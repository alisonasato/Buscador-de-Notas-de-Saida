using System.Xml.Linq;

namespace BuscadorNotas;

public class Robo
{
    private readonly Configuracao _cfg;
    private readonly Repositorio _repo;
    private readonly Armazenamento _storage;

    public Robo(Configuracao cfg, Repositorio repo)
    {
        _cfg = cfg;
        _repo = repo;
        _storage = new Armazenamento(cfg.PastaXml);
    }

    // ---------- Robô de NSU ----------

    /// <summary>Roda o robô de NSU. Com <paramref name="repetir"/>, fica em loop respeitando o throttling.</summary>
    public async Task SincronizarAsync(bool repetir, CancellationToken ct)
    {
        _cfg.ValidarParaSefaz();
        using var cert = CertificadoService.ObterCertificado(_cfg.CertificadoPfx, _cfg.SenhaCertificado);
        using var http = SefazHttp.CriarClient(cert);
        var dist = new SefazDistribuicao(http, _cfg);

        do
        {
            var espera = await UmCicloAsync(dist, ct);
            if (!repetir) break;
            Console.WriteLine($"Aguardando {espera.TotalMinutes:0} min até a próxima consulta...");
            await Task.Delay(espera, ct);
        } while (!ct.IsCancellationRequested);
    }

    /// <summary>Consulta até alcançar maxNSU. Retorna quanto esperar antes do próximo ciclo.</summary>
    private async Task<TimeSpan> UmCicloAsync(SefazDistribuicao dist, CancellationToken ct)
    {
        var ultNsu = _repo.ObterUltimoNsu(_cfg.Cnpj);

        while (!ct.IsCancellationRequested)
        {
            Console.WriteLine($"Consultando a partir do NSU {ultNsu}...");
            var ret = await dist.ConsultarAsync(ultNsu, ct);
            Console.WriteLine($"  cStat={ret.CStat} ({ret.XMotivo}) ultNSU={ret.UltNsu} maxNSU={ret.MaxNsu} docs={ret.Documentos.Count}");

            switch (ret.CStat)
            {
                case "138": // documentos localizados
                    foreach (var d in ret.Documentos) ProcessarDocumento(d);
                    _repo.SalvarUltimoNsu(_cfg.Cnpj, ret.UltNsu);
                    ultNsu = ret.UltNsu;
                    if (string.CompareOrdinal(ret.UltNsu, ret.MaxNsu) >= 0)
                        return TimeSpan.FromMinutes(_cfg.EsperaSemNovosMinutos);
                    await Task.Delay(TimeSpan.FromSeconds(_cfg.PausaEntreRequisicoesSegundos), ct);
                    break;

                case "137": // nenhum documento localizado
                    _repo.SalvarUltimoNsu(_cfg.Cnpj, ret.UltNsu);
                    return TimeSpan.FromMinutes(_cfg.EsperaSemNovosMinutos);

                case "656": // consumo indevido: bloqueio temporário
                    Console.WriteLine("  Sefaz informou consumo indevido; aguardando antes de tentar de novo.");
                    if (ret.UltNsu != "000000000000000") _repo.SalvarUltimoNsu(_cfg.Cnpj, ret.UltNsu);
                    return TimeSpan.FromMinutes(_cfg.EsperaConsumoIndevidoMinutos);

                default:
                    throw new InvalidOperationException($"Retorno inesperado da Sefaz: {ret.CStat} - {ret.XMotivo}");
            }
        }
        return TimeSpan.Zero;
    }

    private void ProcessarDocumento(DocumentoDistribuido d)
    {
        XDocument doc;
        try { doc = XDocument.Parse(d.Xml); }
        catch (System.Xml.XmlException ex)
        {
            Console.WriteLine($"  NSU {d.Nsu}: XML inválido ({ex.Message}); ignorado.");
            return;
        }

        switch (NfeXml.TipoDocumento(doc))
        {
            case "nfeProc":
            case "NFe":
            {
                var nota = NfeXml.LerNotaCompleta(doc, "NSU");
                if (nota == null || nota.CnpjEmitente != _cfg.Cnpj) return; // só notas emitidas por nós (saída)
                nota.CaminhoXmlLocal = _storage.Salvar(nota.ChaveAcesso, d.Xml);
                _repo.SalvarNotaCompleta(nota);
                Console.WriteLine($"  NSU {d.Nsu}: nota {nota.NumeroNota} salva.");
                break;
            }
            case "resNFe":
            {
                var nota = NfeXml.LerResumo(doc, "NSU");
                if (nota == null || nota.CnpjEmitente != _cfg.Cnpj) return;
                _repo.InserirSeNaoExiste(nota);
                break;
            }
            // eventos (resEvento/procEventoNFe) não são tratados nesta versão
        }
    }

    // ---------- Resgate histórico via SPED ----------

    public int ImportarSped(IEnumerable<string> arquivos)
    {
        var novas = 0;
        foreach (var arq in arquivos)
        {
            var total = 0;
            foreach (var n in SpedParser.LerSaidas(arq))
            {
                total++;
                var inserida = _repo.InserirSeNaoExiste(new NotaSaida
                {
                    ChaveAcesso = n.Chave,
                    CnpjEmitente = NfeXml.CnpjDaChave(n.Chave),
                    NumeroNota = n.NumeroDoc.TrimStart('0') is { Length: > 0 } s ? s : "0",
                    Serie = n.Serie,
                    DataEmissao = n.DataDoc,
                    ValorTotal = n.Valor,
                    Status = StatusNota.Pendente,
                    SituacaoSefaz = n.CodSit == "00" ? null : $"SPED COD_SIT={n.CodSit}",
                    Origem = "SPED",
                });
                if (inserida) novas++;
            }
            Console.WriteLine($"{Path.GetFileName(arq)}: {total} notas de saída com chave.");
        }
        return novas;
    }

    // ---------- Consulta por chave (nfeConsultaProtocolo) ----------

    public async Task BaixarPendentesAsync(int limite, CancellationToken ct)
    {
        _cfg.ValidarParaSefaz();
        using var cert = CertificadoService.ObterCertificado(_cfg.CertificadoPfx, _cfg.SenhaCertificado);
        using var http = SefazHttp.CriarClient(cert);
        var consulta = new SefazConsultaProtocolo(http, _cfg);

        foreach (var chave in _repo.ListarChavesPendentes(limite))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var r = await consulta.ConsultarAsync(chave, ct);
                if (r.NfeProc != null &&
                    NfeXml.LerNotaCompleta(r.NfeProc, "CONSULTA") is { } nota)
                {
                    nota.CaminhoXmlLocal = _storage.Salvar(chave, r.NfeProc.ToString(SaveOptions.DisableFormatting));
                    _repo.SalvarNotaCompleta(nota);
                    Console.WriteLine($"{chave}: XML salvo (cStat {r.CStat}).");
                }
                else
                {
                    _repo.RegistrarConsulta(chave, StatusNota.ConsultadaSemXml, $"{r.CStat} - {r.XMotivo}", null);
                    Console.WriteLine($"{chave}: situação {r.CStat} - {r.XMotivo} (resposta sem XML completo).");
                }
            }
            catch (InvalidOperationException) { throw; } // configuração ausente: não adianta continuar
            catch (Exception ex)
            {
                _repo.RegistrarConsulta(chave, StatusNota.Erro, null, ex.Message);
                Console.WriteLine($"{chave}: erro - {ex.Message}");
            }
            await Task.Delay(TimeSpan.FromSeconds(_cfg.PausaEntreRequisicoesSegundos), ct);
        }
    }
}
