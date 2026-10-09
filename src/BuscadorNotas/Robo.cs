using System.Xml.Linq;

namespace BuscadorNotas;

/// <summary>O que a Sefaz entregou num ciclo, por papel do seu CNPJ (para explicar "0 notas novas").</summary>
public class EstatisticaCiclo
{
    public int Recebidos, Saidas, Entradas, ComoDestinatario, Resumos, Eventos, Outros;

    public string Texto()
    {
        if (Recebidos == 0) return "";
        var partes = new List<string>();
        void Add(int n, string rotulo) { if (n > 0) partes.Add($"{n} {rotulo}"); }
        Add(Saidas, "emitido(s) por você (saída)");
        Add(Entradas, "de entrada guardada(s) (você é destinatário)");
        Add(ComoDestinatario, "em que você é destinatário (entrada, não guardada)");
        Add(Resumos, "resumo(s) de notas de terceiros");
        Add(Eventos, "evento(s)");
        Add(Outros, "outro(s)");
        return $"{Recebidos} documento(s) recebido(s): {string.Join(", ", partes)}";
    }
}

/// <summary>Outro processo (ou o serviço) já está consultando a Sefaz para este banco; não é erro de configuração nem da Sefaz.</summary>
public class ConsultaEmAndamentoException(string mensagem) : InvalidOperationException(mensagem);

public record ResultadoBuscaChaves(int Consultadas, int Obtidas, int Indisponiveis, int Erros, string Mensagem);

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

    /// <summary>Define o cUFAutor a usar: o configurado (se for UF válida), senão o inferido das chaves, senão nenhum.</summary>
    public void ResolverUf()
    {
        var conf = _cfg.CUFAutor?.Trim();
        if (Configuracao.UfValida(conf)) { _cfg.CUFAutorEfetivo = conf; Console.WriteLine($"cUFAutor: {conf} ({Configuracao.Ufs[conf!]}, configurado)."); return; }
        var inferida = _repo.UfMaisFrequente();
        _cfg.CUFAutorEfetivo = Configuracao.UfValida(inferida) ? inferida : null;
        Console.WriteLine(_cfg.CUFAutorEfetivo != null
            ? $"cUFAutor: {_cfg.CUFAutorEfetivo} ({Configuracao.Ufs[_cfg.CUFAutorEfetivo]}, inferido das notas já importadas)."
            : "cUFAutor: omitido (UF não configurada e sem notas para inferir; informe a UF em Configurações).");
    }

    public async Task DiagnosticarAsync(int maxPaginas, string nsuInicial, CancellationToken ct, string? servico = null)
    {
        _cfg.ValidarParaSefaz();
        using var trava = AdquirirTravaConsulta();
        ResolverUf();
        using var cert = CertificadoService.ObterCertificado(_cfg.CertificadoPfx, _cfg.SenhaCertificado);
        using var http = SefazHttp.CriarClient(cert);
        await Diagnostico.ExecutarAsync(_cfg, new SefazDistribuicao(http, _cfg, ServicosDistribuicao.Por(servico, _cfg)), maxPaginas, nsuInicial, ct,
            aoEsgotar: servico is null or "" or "nfe" or "NFE" ? RegistrarFimDeConsulta : null);
    }

    /// <summary>Roda o robô de NSU. Com <paramref name="repetir"/>, fica em loop respeitando o throttling.</summary>
    /// <summary>Espera entre dois ciclos no modo --loop: nunca menos que o intervalo mínimo (intervalo 0 não pode virar laço sem pausa).</summary>
    public TimeSpan EsperaEntreCiclos(ResultadoCiclo r)
    {
        var minimo = TimeSpan.FromMinutes(Math.Max(1, _cfg.IntervaloMinimoMinutos));
        return r.Espera > minimo ? r.Espera : minimo;
    }

    public async Task SincronizarAsync(bool repetir, CancellationToken ct)
    {
        do
        {
            TimeSpan espera;
            try
            {
                var r = await ExecutarCicloAsync(null, ct);
                if (!repetir) break;
                espera = EsperaEntreCiclos(r);
            }
            catch (Exception ex) when (repetir && ex is not OperationCanceledException)
            {
                // no modo contínuo uma falha (rede, Sefaz fora do ar...) não encerra o programa: espera e tenta de novo
                Console.Error.WriteLine($"Falha na consulta: {ex.Message}");
                espera = EsperaEntreCiclos(new ResultadoCiclo("ERRO", 0, TimeSpan.FromMinutes(_cfg.EsperaSemNovosMinutos), null));
            }
            Console.WriteLine($"Aguardando {espera.TotalMinutes:0} min até a próxima consulta...");
            await Task.Delay(espera, ct);
        } while (!ct.IsCancellationRequested);
    }

    /// <summary>Um ciclo completo: consulta até alcançar maxNSU (ou até a Sefaz pedir para parar).</summary>
    public async Task<ResultadoCiclo> ExecutarCicloAsync(Action<ProgressoSync>? progresso, CancellationToken ct)
    {
        _cfg.ValidarParaSefaz();
        using var trava = AdquirirTravaConsulta();
        ResolverUf();
        using var cert = CertificadoService.ObterCertificado(_cfg.CertificadoPfx, _cfg.SenhaCertificado);
        using var http = SefazHttp.CriarClient(cert);
        return await ExecutarCicloComAsync(http, progresso, ct);
    }

    // ---------- Limite de consultas da Sefaz ----------

    /// <summary>
    /// Trava entre PROCESSOS (arquivo ao lado do banco, aberto sem compartilhamento): impede que o serviço do Windows e um
    /// "sync" digitado no terminal consultem a Sefaz ao mesmo tempo. É liberada ao fechar e pelo sistema se o processo morrer.
    /// </summary>
    public IDisposable AdquirirTravaConsulta()
    {
        try
        {
            return new FileStream(_cfg.BancoSqlite + ".consulta.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            throw new ConsultaEmAndamentoException("Outro programa (ou o serviço) já está consultando a Sefaz para este banco de dados. Aguarde ele terminar.");
        }
    }

    private const string PrefUltimaConsulta = "ultimaConsulta";

    /// <summary>Guarda quando e como terminou a última consulta que esgotou o acervo (ou foi bloqueada), para respeitar o intervalo mínimo.</summary>
    public void RegistrarFimDeConsulta(string cStat) =>
        _repo.SalvarPref(PrefUltimaConsulta, $"{DateTimeOffset.Now:o}|{cStat}");

    /// <summary>
    /// Se ainda é cedo para consultar a Sefaz de novo (após 137/138 sem mais nada a baixar: IntervaloMinimoMinutos; após 656:
    /// EsperaConsumoIndevidoMinutos), devolve quanto falta e quando será liberado; senão null. Vale para todo o programa
    /// (tela, agendador e linha de comando), mesmo depois de reiniciar.
    /// </summary>
    public (TimeSpan Restante, DateTimeOffset LiberadoEm)? EsperaRestante(DateTimeOffset? agora = null)
    {
        var partes = (_repo.ObterPref(PrefUltimaConsulta) ?? "").Split('|');
        if (partes.Length != 2 || !DateTimeOffset.TryParse(partes[0], out var fim)) return null;
        var minutos = partes[1] switch
        {
            "656" => _cfg.EsperaConsumoIndevidoMinutos,
            "137" or "138" => _cfg.IntervaloMinimoMinutos,
            _ => 0,
        };
        if (minutos <= 0) return null;
        var libera = fim.AddMinutes(minutos);
        var hoje = agora ?? DateTimeOffset.Now;
        return libera > hoje ? (libera - hoje, libera) : null;
    }

    /// <summary>
    /// NF-e primeiro; depois, se ligados, CT-e e MDF-e. Uma falha nos serviços adicionais (experimentais) nunca derruba
    /// o resultado da NF-e: vira um aviso na mensagem do resultado.
    /// </summary>
    public async Task<ResultadoCiclo> ExecutarCicloComAsync(HttpClient http, Action<ProgressoSync>? progresso, CancellationToken ct)
    {
        var nfe = ServicosDistribuicao.Nfe(_cfg);
        var r = await UmCicloAsync(new SefazDistribuicao(http, _cfg, nfe), nfe, progresso, ct);
        if (r.CStat is "137" or "138" or "656") RegistrarFimDeConsulta(r.CStat);
        if (r.CStat is "CANCELADO" or "656") return r; // não insiste em outros serviços se o usuário cancelou ou a Sefaz pediu espera

        var servicos = new List<ServicoDistribuicao>();
        if (_cfg.DistribuirCte) servicos.Add(ServicosDistribuicao.Cte(_cfg));
        if (_cfg.DistribuirMdfe) servicos.Add(ServicosDistribuicao.Mdfe(_cfg));

        int novas = r.NovasNotas;
        var avisos = new List<string>();
        foreach (var svc in servicos)
        {
            try
            {
                var rx = await UmCicloAsync(new SefazDistribuicao(http, _cfg, svc), svc, null, ct);
                novas += rx.NovasNotas;
                if (rx.CStat == "656") avisos.Add($"{svc.Rotulo}: a Sefaz pediu para aguardar (656)");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Console.WriteLine($"  {svc.Rotulo} (experimental): {ex.Message}");
                avisos.Add($"{svc.Rotulo}: {ex.Message}");
            }
        }
        return avisos.Count == 0 && novas == r.NovasNotas ? r
            : r with { NovasNotas = novas, Mensagem = string.Join(" | ", new[] { r.Mensagem }.Concat(avisos).Where(x => !string.IsNullOrEmpty(x))) };
    }

    private async Task<ResultadoCiclo> UmCicloAsync(SefazDistribuicao dist, ServicoDistribuicao svc, Action<ProgressoSync>? progresso, CancellationToken ct)
    {
        // O controle de NSU de cada serviço é separado: NF-e usa só o CNPJ (compatível com o que já existe); os demais, "CNPJ:TIPO".
        var chaveNsu = svc.Chave == "NFE" ? _cfg.Cnpj : $"{_cfg.Cnpj}:{svc.Chave}";
        var ultNsu = _repo.ObterUltimoNsu(chaveNsu);
        int pagina = 0, novas = 0;
        var est = new EstatisticaCiclo();

        while (!ct.IsCancellationRequested)
        {
            pagina++;
            Console.WriteLine($"Consultando {svc.Rotulo} a partir do NSU {ultNsu}...");
            var anterior = ultNsu;
            var ret = await dist.ConsultarAsync(ultNsu, ct);
            Console.WriteLine($"  cStat={ret.CStat} ({ret.XMotivo}) ultNSU={ret.UltNsu} maxNSU={ret.MaxNsu} docs={ret.Documentos.Count}");

            switch (ret.CStat)
            {
                case "138": // documentos localizados
                    foreach (var d in ret.Documentos) { est.Recebidos++; if (svc.Chave == "NFE" ? ProcessarDocumento(d, est) : ProcessarDocumentoExtra(d)) novas++; }
                    _repo.SalvarUltimoNsu(chaveNsu, ret.UltNsu);
                    ultNsu = ret.UltNsu;
                    progresso?.Invoke(new ProgressoSync(pagina, ret.UltNsu, ret.MaxNsu, novas));
                    if (string.CompareOrdinal(ret.UltNsu, ret.MaxNsu) >= 0)
                    {
                        var resumo = est.Texto();
                        if (resumo.Length > 0) Console.WriteLine("  " + resumo);
                        return new ResultadoCiclo("138", novas, TimeSpan.FromMinutes(_cfg.EsperaSemNovosMinutos), resumo.Length > 0 ? resumo : ret.XMotivo);
                    }
                    if (string.CompareOrdinal(ret.UltNsu, anterior) <= 0)
                    {
                        // Sem avanço do NSU, repetir o pedido seria um laço de consultas iguais (a Sefaz responderia 656).
                        Console.WriteLine("  A Sefaz devolveu o mesmo NSU; consulta interrompida para não repetir o pedido.");
                        var txt = est.Texto();
                        return new ResultadoCiclo("138", novas, TimeSpan.FromMinutes(_cfg.EsperaSemNovosMinutos),
                            "A Sefaz não avançou o NSU; consulta interrompida." + (txt.Length > 0 ? " " + txt : ""));
                    }
                    await Task.Delay(TimeSpan.FromSeconds(_cfg.PausaEntreRequisicoesSegundos), ct);
                    break;

                case "137": // nenhum documento localizado
                    _repo.SalvarUltimoNsu(chaveNsu, ret.UltNsu);
                    return new ResultadoCiclo("137", novas, TimeSpan.FromMinutes(_cfg.EsperaSemNovosMinutos), ret.XMotivo);

                case "656": // consumo indevido: bloqueio temporário
                    Console.WriteLine("  Sefaz informou consumo indevido; aguardando antes de tentar de novo.");
                    if (ret.UltNsu != "000000000000000") _repo.SalvarUltimoNsu(chaveNsu, ret.UltNsu);
                    return new ResultadoCiclo("656", novas, TimeSpan.FromMinutes(_cfg.EsperaConsumoIndevidoMinutos), ret.XMotivo);

                default:
                    Console.WriteLine($"  Requisição enviada: {dist.UltimaRequisicao}");
                    var dica = ret.CStat == "215"
                        ? " Dica: a Sefaz rejeitou o formato do pedido; confira a UF da empresa em Configurações (cUFAutor) e envie o log ao suporte."
                        : "";
                    throw new InvalidOperationException($"Retorno inesperado da Sefaz: {ret.CStat} - {ret.XMotivo}.{dica}");
            }
        }
        return new ResultadoCiclo("CANCELADO", novas, TimeSpan.Zero, "Sincronização cancelada.");
    }

    /// <summary>CT-e/MDF-e vindos da distribuição: mesma importação dos XMLs (filtra emitente, tipo, cancelamentos).</summary>
    private bool ProcessarDocumentoExtra(DocumentoDistribuido d)
    {
        var r = ImportarXmlBytes(System.Text.Encoding.UTF8.GetBytes(d.Xml), incluirOutrosCnpjs: false);
        if (r.Desfecho == Desfecho.Importado) Console.WriteLine($"  NSU {d.Nsu}: {r.Detalhe}.");
        return r.Desfecho == Desfecho.Importado && r.Novo && !r.Entrada;
    }

    /// <summary>Retorna true quando o documento é uma nota de saída nova (ou que ainda não tinha XML).</summary>
    public bool ProcessarDocumento(DocumentoDistribuido d, EstatisticaCiclo? est = null)
    {
        XDocument doc;
        try { doc = XDocument.Parse(d.Xml); }
        catch (System.Xml.XmlException ex)
        {
            Console.WriteLine($"  NSU {d.Nsu}: XML inválido ({ex.Message}); ignorado.");
            if (est != null) est.Outros++;
            return false;
        }

        switch (NfeXml.TipoDocumento(doc))
        {
            case "nfeProc":
            case "NFe":
            {
                var nota = NfeXml.LerNotaCompleta(doc, "NSU");
                if (nota == null) { if (est != null) est.Outros++; return false; }
                if (nota.CnpjEmitente != _cfg.Cnpj) // não emitida por nós: entrada (se for para nós e a opção estiver ligada)
                {
                    if (nota.CnpjCpfDestinatario != _cfg.Cnpj) { if (est != null) est.Outros++; return false; }
                    if (!_cfg.GuardarEntradas) { if (est != null) est.ComoDestinatario++; return false; }
                    nota.Direcao = Direcao.Entrada;
                    nota.CaminhoXmlLocal = _storage.Salvar(nota.ChaveAcesso, d.Xml);
                    _repo.SalvarNotaCompleta(nota);
                    if (est != null) est.Entradas++;
                    return false; // entrada nunca conta como saída nova
                }
                if (est != null) est.Saidas++;
                var nova = _repo.ObterNota(nota.ChaveAcesso)?.Status != StatusNota.Baixado;
                nota.CaminhoXmlLocal = _storage.Salvar(nota.ChaveAcesso, d.Xml);
                _repo.SalvarNotaCompleta(nota);
                Console.WriteLine($"  NSU {d.Nsu}: nota {nota.NumeroNota} salva.");
                return nova;
            }
            case "resNFe":
            {
                var nota = NfeXml.LerResumo(doc, "NSU");
                if (nota == null) { if (est != null) est.Outros++; return false; }
                if (nota.CnpjEmitente != _cfg.Cnpj)
                {
                    // Resumo de nota de terceiro entregue na distribuição: o CNPJ é destinatário (ou interessado) → entrada
                    if (_cfg.GuardarEntradas) { nota.Direcao = Direcao.Entrada; _repo.InserirSeNaoExiste(nota); }
                    if (est != null) est.Resumos++;
                    return false;
                }
                if (est != null) est.Saidas++;
                return _repo.InserirSeNaoExiste(nota);
            }
            case "resEvento":
            case "procEventoNFe":
            {
                var ev = NfeXml.LerEvento(doc);
                // Só eventos de notas emitidas por nós; o CNPJ vem da própria chave de acesso.
                if (est != null) est.Eventos++;
                if (ev == null) return false;
                // eventos de notas nossas ou de entradas que já guardamos
                if (NfeXml.CnpjDaChave(ev.Chave) != _cfg.Cnpj && !(_cfg.GuardarEntradas && _repo.ObterNota(ev.Chave) != null)) return false;
                if (!NfeXml.TipoCancelaDocumento(ev.Chave, ev.Tipo)) return false; // CC-e e demais: não tratados
                if (_repo.RegistrarEvento(ev, "NSU"))
                    Console.WriteLine($"  NSU {d.Nsu}: evento {ev.Tipo} (cancelamento) da NF-e {NfeXml.NumeroDaChave(ev.Chave)}.");
                return false; // não é nota nova
            }
            default:
                if (est != null) est.Outros++;
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
                    Tipo = TipoDoc.DoModelo(NfeXml.ModeloDaChave(n.Chave)) ?? TipoDoc.Nfe,
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

    // ---------- Busca de chaves pendentes na distribuição (consChNFe, EXPERIMENTAL) ----------

    private const string PrefUltimaBuscaChaves = "ultimaBuscaChaves";
    public const int MaxChavesPorBusca = 20;

    /// <summary>Quando a busca por chaves pode rodar de novo (mínimo de IntervaloMinimoMinutos entre buscas), ou null se já pode.</summary>
    public DateTimeOffset? BuscaChavesLiberadaEm(DateTimeOffset? agora = null)
    {
        if (!DateTimeOffset.TryParse(_repo.ObterPref(PrefUltimaBuscaChaves), out var ultima)) return null;
        var libera = ultima.AddMinutes(_cfg.IntervaloMinimoMinutos);
        return libera > (agora ?? DateTimeOffset.Now) ? libera : null;
    }

    /// <summary>
    /// EXPERIMENTAL. Para cada chave pendente (até <see cref="MaxChavesPorBusca"/>), pergunta à distribuição DF-e pelo documento.
    /// Pelo que sei, a Sefaz só entrega o XML se o seu CNPJ for parte do documento (emitente, destinatário...); caso contrário
    /// a chave fica INDISPONIVEL (não tenho fonte verificada para o comportamento exato). Respeita um intervalo mínimo entre
    /// buscas e interrompe tudo ao receber 656.
    /// </summary>
    public async Task<ResultadoBuscaChaves> BuscarPendentesPorChaveAsync(int limite, bool forcar, CancellationToken ct)
    {
        _cfg.ValidarParaSefaz();
        using var trava = AdquirirTravaConsulta();
        using var cert = CertificadoService.ObterCertificado(_cfg.CertificadoPfx, _cfg.SenhaCertificado);
        using var http = SefazHttp.CriarClient(cert);
        return await BuscarPendentesPorChaveAsync(http, limite, forcar, ct);
    }

    private readonly SemaphoreSlim _buscaChaves = new(1, 1);

    public async Task<ResultadoBuscaChaves> BuscarPendentesPorChaveAsync(HttpClient http, int limite, bool forcar, CancellationToken ct)
    {
        if (_cfg.Cnpj.Length != 14) throw new InvalidOperationException("Cnpj deve ter 14 dígitos.");
        if (!await _buscaChaves.WaitAsync(0, ct)) return new ResultadoBuscaChaves(0, 0, 0, 0, "Já existe uma busca por chaves em andamento.");
        try { return await BuscarChavesInternoAsync(http, limite, forcar, ct); }
        finally { _buscaChaves.Release(); }
    }

    private async Task<ResultadoBuscaChaves> BuscarChavesInternoAsync(HttpClient http, int limite, bool forcar, CancellationToken ct)
    {
        if (EsperaRestante() is { } e && e.Restante > TimeSpan.Zero && _repo.ObterPref(PrefUltimaConsulta)?.EndsWith("|656") == true)
            return new ResultadoBuscaChaves(0, 0, 0, 0, $"A Sefaz pediu para aguardar (656); tente após {e.LiberadoEm.LocalDateTime:HH:mm}.");
        if (!forcar && BuscaChavesLiberadaEm() is { } lib)
            return new ResultadoBuscaChaves(0, 0, 0, 0, $"Última busca por chaves foi há pouco; a próxima é liberada às {lib.LocalDateTime:HH:mm} (evita consumo indevido).");

        var chaves = _repo.ListarChavesPendentes(Math.Clamp(limite, 1, MaxChavesPorBusca));
        if (chaves.Count == 0) return new ResultadoBuscaChaves(0, 0, 0, 0, "Nenhuma chave pendente.");

        var dist = new SefazDistribuicao(http, _cfg, ServicosDistribuicao.Nfe(_cfg));
        int consultadas = 0, obtidas = 0, indisponiveis = 0, erros = 0;
        string? msg = null;
        _repo.SalvarPref(PrefUltimaBuscaChaves, DateTimeOffset.Now.ToString("o"));

        foreach (var chave in chaves)
        {
            if (ct.IsCancellationRequested) { msg = "Cancelada."; break; }
            consultadas++;
            try
            {
                var ret = await dist.ConsultarPorChaveAsync(chave, ct);
                Console.WriteLine($"  {chave}: cStat={ret.CStat} ({ret.XMotivo}) docs={ret.Documentos.Count}");
                if (ret.CStat == "656")
                {
                    RegistrarFimDeConsulta("656");
                    msg = "A Sefaz pediu para aguardar (656); a busca foi interrompida.";
                    break;
                }
                if (ret.CStat == "138" && ret.Documentos.Count > 0)
                {
                    foreach (var d in ret.Documentos) ProcessarDocumento(d);
                    if (_repo.ObterNota(chave)?.Status == StatusNota.Baixado) { obtidas++; }
                    else { _repo.RegistrarConsulta(chave, StatusNota.Indisponivel, $"{ret.CStat} - {ret.XMotivo}", "A Sefaz devolveu só resumo/evento"); indisponiveis++; }
                }
                else if (ret.CStat == "137")
                {
                    _repo.RegistrarConsulta(chave, StatusNota.Indisponivel, $"{ret.CStat} - {ret.XMotivo}", "A Sefaz não entregou o XML desta chave (seu CNPJ pode não fazer parte do documento)");
                    indisponiveis++;
                }
                else
                {
                    _repo.RegistrarConsulta(chave, StatusNota.Erro, $"{ret.CStat} - {ret.XMotivo}", $"{ret.CStat} - {ret.XMotivo}");
                    erros++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _repo.RegistrarConsulta(chave, StatusNota.Erro, null, ex.Message);
                erros++;
                Console.WriteLine($"  {chave}: erro - {ex.Message}");
            }
            await Task.Delay(TimeSpan.FromSeconds(_cfg.PausaEntreRequisicoesSegundos), ct);
        }
        return new ResultadoBuscaChaves(consultadas, obtidas, indisponiveis, erros,
            msg ?? $"{consultadas} chave(s) consultada(s): {obtidas} XML obtido(s), {indisponiveis} indisponível(is), {erros} com erro.");
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
