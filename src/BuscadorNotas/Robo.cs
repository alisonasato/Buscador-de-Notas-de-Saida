using System.Xml.Linq;

namespace BuscadorNotas;

public record ProgressoSync(int Pagina, string UltNsu, string MaxNsu, int NovasNotas);

/// <param name="CStat">138 (docs), 137 (nada novo), 656 (bloqueio) ou CANCELADO.</param>
public record ResultadoCiclo(string CStat, int NovasNotas, TimeSpan Espera, string? Mensagem);

public partial class Robo
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

    public async Task DiagnosticarAsync(int maxPaginas, string nsuInicial, CancellationToken ct)
    {
        _cfg.ValidarParaSefaz();
        using var cert = CertificadoService.ObterCertificado(_cfg.CertificadoPfx, _cfg.SenhaCertificado);
        using var http = SefazHttp.CriarClient(cert);
        await Diagnostico.ExecutarAsync(_cfg, new SefazDistribuicao(http, _cfg), maxPaginas, nsuInicial, ct);
    }

    /// <summary>Roda o robô de NSU. Com <paramref name="repetir"/>, fica em loop respeitando o throttling.</summary>
    public async Task SincronizarAsync(bool repetir, CancellationToken ct)
    {
        do
        {
            var r = await ExecutarCicloAsync(null, ct);
            if (!repetir) break;
            Console.WriteLine($"Aguardando {r.Espera.TotalMinutes:0} min até a próxima consulta...");
            await Task.Delay(r.Espera, ct);
        } while (!ct.IsCancellationRequested);
    }

    /// <summary>Um ciclo completo: consulta até alcançar maxNSU (ou até a Sefaz pedir para parar).</summary>
    public async Task<ResultadoCiclo> ExecutarCicloAsync(Action<ProgressoSync>? progresso, CancellationToken ct)
    {
        _cfg.ValidarParaSefaz();
        using var cert = CertificadoService.ObterCertificado(_cfg.CertificadoPfx, _cfg.SenhaCertificado);
        using var http = SefazHttp.CriarClient(cert);
        return await UmCicloAsync(new SefazDistribuicao(http, _cfg), progresso, ct);
    }

    private async Task<ResultadoCiclo> UmCicloAsync(SefazDistribuicao dist, Action<ProgressoSync>? progresso, CancellationToken ct)
    {
        var ultNsu = _repo.ObterUltimoNsu(_cfg.Cnpj);
        int pagina = 0, novas = 0;

        while (!ct.IsCancellationRequested)
        {
            pagina++;
            Console.WriteLine($"Consultando a partir do NSU {ultNsu}...");
            var ret = await dist.ConsultarAsync(ultNsu, ct);
            Console.WriteLine($"  cStat={ret.CStat} ({ret.XMotivo}) ultNSU={ret.UltNsu} maxNSU={ret.MaxNsu} docs={ret.Documentos.Count}");

            switch (ret.CStat)
            {
                case "138": // documentos localizados
                    foreach (var d in ret.Documentos) if (ProcessarDocumento(d)) novas++;
                    _repo.SalvarUltimoNsu(_cfg.Cnpj, ret.UltNsu);
                    ultNsu = ret.UltNsu;
                    progresso?.Invoke(new ProgressoSync(pagina, ret.UltNsu, ret.MaxNsu, novas));
                    if (string.CompareOrdinal(ret.UltNsu, ret.MaxNsu) >= 0)
                        return new ResultadoCiclo("138", novas, TimeSpan.FromMinutes(_cfg.EsperaSemNovosMinutos), ret.XMotivo);
                    await Task.Delay(TimeSpan.FromSeconds(_cfg.PausaEntreRequisicoesSegundos), ct);
                    break;

                case "137": // nenhum documento localizado
                    _repo.SalvarUltimoNsu(_cfg.Cnpj, ret.UltNsu);
                    return new ResultadoCiclo("137", novas, TimeSpan.FromMinutes(_cfg.EsperaSemNovosMinutos), ret.XMotivo);

                case "656": // consumo indevido: bloqueio temporário
                    Console.WriteLine("  Sefaz informou consumo indevido; aguardando antes de tentar de novo.");
                    if (ret.UltNsu != "000000000000000") _repo.SalvarUltimoNsu(_cfg.Cnpj, ret.UltNsu);
                    return new ResultadoCiclo("656", novas, TimeSpan.FromMinutes(_cfg.EsperaConsumoIndevidoMinutos), ret.XMotivo);

                default:
                    throw new InvalidOperationException($"Retorno inesperado da Sefaz: {ret.CStat} - {ret.XMotivo}");
            }
        }
        return new ResultadoCiclo("CANCELADO", novas, TimeSpan.Zero, "Sincronização cancelada.");
    }

    /// <summary>Retorna true quando o documento é uma nota de saída nova (ou que ainda não tinha XML).</summary>
    public bool ProcessarDocumento(DocumentoDistribuido d)
    {
        XDocument doc;
        try { doc = XDocument.Parse(d.Xml); }
        catch (System.Xml.XmlException ex)
        {
            Console.WriteLine($"  NSU {d.Nsu}: XML inválido ({ex.Message}); ignorado.");
            return false;
        }

        switch (NfeXml.TipoDocumento(doc))
        {
            case "nfeProc":
            case "NFe":
            {
                var nota = NfeXml.LerNotaCompleta(doc, "NSU");
                if (nota == null || nota.CnpjEmitente != _cfg.Cnpj) return false; // só notas emitidas por nós (saída)
                var nova = _repo.ObterNota(nota.ChaveAcesso)?.Status != StatusNota.Baixado;
                nota.CaminhoXmlLocal = _storage.Salvar(nota.ChaveAcesso, d.Xml);
                _repo.SalvarNotaCompleta(nota);
                Console.WriteLine($"  NSU {d.Nsu}: nota {nota.NumeroNota} salva.");
                return nova;
            }
            case "resNFe":
            {
                var nota = NfeXml.LerResumo(doc, "NSU");
                if (nota == null || nota.CnpjEmitente != _cfg.Cnpj) return false;
                return _repo.InserirSeNaoExiste(nota);
            }
            case "resEvento":
            case "procEventoNFe":
            {
                var ev = NfeXml.LerEvento(doc);
                // Só eventos de notas emitidas por nós; o CNPJ vem da própria chave de acesso.
                if (ev == null || NfeXml.CnpjDaChave(ev.Chave) != _cfg.Cnpj) return false;
                if (!NfeXml.EventosCancelamento.Contains(ev.Tipo)) return false; // CC-e e demais: não tratados
                if (_repo.RegistrarEvento(ev, "NSU"))
                    Console.WriteLine($"  NSU {d.Nsu}: evento {ev.Tipo} (cancelamento) da NF-e {NfeXml.NumeroDaChave(ev.Chave)}.");
                return false; // não é nota nova
            }
            default:
                return false;
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
                    Serie = n.Serie.TrimStart('0') is { Length: > 0 } sr ? sr : "0",
                    DataEmissao = n.DataDoc,
                    ValorTotal = n.Valor,
                    Status = StatusNota.Pendente,
                    SituacaoSefaz = $"SPED COD_SIT={n.CodSit}",
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
