using System.Security.Cryptography;
using System.Threading.Channels;
using System.Text.Json;

namespace BuscadorNotas;

/// <summary>
/// Orquestra a sincronização em segundo plano para a interface web: uma execução por vez, cancelável,
/// com estado consultável, eventos em tempo real (SSE) e histórico no banco.
/// Estados: idle | running | blocked (Sefaz respondeu 656) | error.
/// </summary>
public class SyncService
{
    private readonly Configuracao _cfg;
    private readonly Repositorio _repo;
    private readonly Robo _robo;
    private readonly object _lock = new();
    private readonly List<Channel<string>> _assinantes = new();

    private string _estado = "idle";
    private string? _origem;
    private DateTimeOffset? _iniciadoEm;
    private ProgressoSync? _progresso;
    private DateTimeOffset? _retomarEm;
    private (string Codigo, string Mensagem)? _erro;
    private (string CStat, int Novas, DateTimeOffset FimEm)? _ultimo;
    private DateTimeOffset? _proxima;
    private CancellationTokenSource? _cts;

    public SyncService(Configuracao cfg, Repositorio repo, Robo robo)
    {
        _cfg = cfg;
        _repo = repo;
        _robo = robo;
        Inicializar();
    }

    private void Inicializar()
    {
        var agora = DateTimeOffset.Now;
        _repo.FecharLogsAbertos(agora); // execução interrompida por queda do processo
        var ultimo = _repo.UltimosLogs(1).FirstOrDefault();
        if (ultimo?.FimEm != null && DateTimeOffset.TryParse(ultimo.FimEm, out var fim))
        {
            _ultimo = (ultimo.Resultado ?? "", ultimo.NovasNotas, fim);
            if (ultimo.Resultado == "656" && fim.AddMinutes(_cfg.EsperaConsumoIndevidoMinutos) > agora)
            {
                _estado = "blocked";
                _retomarEm = fim.AddMinutes(_cfg.EsperaConsumoIndevidoMinutos);
            }
        }
        RecalcularProxima();
    }

    // ---------- Consulta de estado ----------

    public object Status()
    {
        lock (_lock)
        {
            ExpirarBloqueio();
            return new
            {
                estado = _estado,
                origem = _origem,
                iniciadoEm = _iniciadoEm,
                progresso = _progresso == null ? null : new
                {
                    pagina = _progresso.Pagina,
                    ultNsu = _progresso.UltNsu,
                    maxNsu = _progresso.MaxNsu,
                    percentual = Percentual(_progresso),
                    novasNotas = _progresso.NovasNotas,
                },
                retomarEm = _retomarEm,
                erro = _erro == null ? null : new { codigo = _erro.Value.Codigo, mensagem = _erro.Value.Mensagem },
                ultimoResultado = _ultimo == null ? null : new { cStat = _ultimo.Value.CStat, novasNotas = _ultimo.Value.Novas, fimEm = _ultimo.Value.FimEm },
                proximaExecucao = _cfg.SincronizacaoAutomatica && _cfg.EsperaSemNovosMinutos > 0 ? _proxima : null,
                automatica = _cfg.SincronizacaoAutomatica,
                intervaloMinutos = _cfg.EsperaSemNovosMinutos,
            };
        }
    }

    private static int Percentual(ProgressoSync p)
    {
        if (!long.TryParse(p.UltNsu, out var u) || !long.TryParse(p.MaxNsu, out var m) || m <= 0) return 0;
        return (int)Math.Clamp(u * 100 / m, 0, 100);
    }

    private void ExpirarBloqueio()
    {
        if (_estado == "blocked" && _retomarEm is { } r && r <= DateTimeOffset.Now)
        {
            _estado = "idle";
            _retomarEm = null;
        }
    }

    // ---------- Controle ----------

    /// <returns>null se iniciou; caso contrário o motivo da recusa.</returns>
    public string? TentarIniciar(string origem)
    {
        lock (_lock)
        {
            ExpirarBloqueio();
            if (_estado == "running") return "Já existe uma sincronização em andamento.";
            if (_estado == "blocked") return "A Sefaz pediu para aguardar; tente novamente após o fim da espera.";
            try { _cfg.ValidarParaSefaz(); }
            catch (InvalidOperationException ex) { Registrar(origem, "CONFIGURACAO_INVALIDA", ex.Message); return null; }

            _estado = "running";
            _origem = origem;
            _iniciadoEm = DateTimeOffset.Now;
            _progresso = null;
            _erro = null;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            var logId = _repo.IniciarLog(origem, _iniciadoEm.Value);
            Publicar(new { tipo = "inicio", iniciadoEm = _iniciadoEm, origem });
            _ = Task.Run(() => ExecutarAsync(logId, ct));
            return null;
        }
    }

    public bool Cancelar()
    {
        lock (_lock)
        {
            if (_estado != "running" || _cts == null) return false;
            _cts.Cancel();
            return true;
        }
    }

    private async Task ExecutarAsync(long logId, CancellationToken ct)
    {
        try
        {
            var r = await _robo.ExecutarCicloAsync(p =>
            {
                lock (_lock) _progresso = p;
                Publicar(new { tipo = "lote", pagina = p.Pagina, ultNsu = p.UltNsu, maxNsu = p.MaxNsu, percentual = Percentual(p), novasNotas = p.NovasNotas });
            }, ct);
            Finalizar(logId, r.CStat, r.NovasNotas, r.Mensagem, r.Espera, erro: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Finalizar(logId, "CANCELADO", _progresso?.NovasNotas ?? 0, "Sincronização cancelada.", TimeSpan.Zero, erro: null);
        }
        catch (Exception ex)
        {
            Finalizar(logId, "ERRO", _progresso?.NovasNotas ?? 0, ex.Message, TimeSpan.FromMinutes(Math.Max(1, _cfg.EsperaSemNovosMinutos)), (CodigoErro(ex), ex.Message));
        }
    }

    private static string CodigoErro(Exception ex) => ex switch
    {
        CryptographicException => "CERTIFICADO_INVALIDO",
        FileNotFoundException => "CERTIFICADO_AUSENTE",
        InvalidOperationException => "RETORNO_INESPERADO",
        HttpRequestException => "REDE",
        TaskCanceledException => "REDE_TIMEOUT",
        _ => "ERRO_INESPERADO",
    };

    private void Finalizar(long logId, string resultado, int novas, string? mensagem, TimeSpan espera, (string, string)? erro)
    {
        var fim = DateTimeOffset.Now;
        _repo.FinalizarLog(logId, fim, resultado, novas, mensagem);
        lock (_lock)
        {
            _cts?.Dispose();
            _cts = null;
            _iniciadoEm = null;
            _origem = null;
            _progresso = null;
            _ultimo = (resultado, novas, fim);
            _erro = erro;
            _retomarEm = null;
            _estado = erro != null ? "error" : "idle";
            if (resultado == "656")
            {
                _estado = "blocked";
                _retomarEm = fim.Add(espera);
            }
            _proxima = resultado == "CANCELADO" ? _proxima : fim.Add(espera);

            if (resultado == "656")
                Publicar(new { tipo = "fim", resultado, retomarEm = _retomarEm });
            else if (erro != null)
                Publicar(new { tipo = "erro", codigo = erro.Value.Item1, mensagem = erro.Value.Item2 });
            else
                Publicar(new { tipo = "fim", resultado, novasNotas = novas, proximaExecucao = _cfg.SincronizacaoAutomatica ? _proxima : null });
        }
    }

    /// <summary>Registra um erro imediato (sem chegar a chamar a Sefaz), por exemplo configuração incompleta.</summary>
    private void Registrar(string origem, string codigo, string mensagem)
    {
        var agora = DateTimeOffset.Now;
        var id = _repo.IniciarLog(origem, agora);
        _repo.FinalizarLog(id, agora, "ERRO", 0, mensagem);
        _estado = "error";
        _erro = (codigo, mensagem);
        _ultimo = ("ERRO", 0, agora);
        _proxima = agora.AddMinutes(Math.Max(1, _cfg.EsperaSemNovosMinutos));
        Publicar(new { tipo = "erro", codigo, mensagem });
    }

    // ---------- Agendamento ----------

    public void RecalcularProxima()
    {
        lock (_lock)
        {
            var baseHora = _ultimo?.FimEm ?? DateTimeOffset.Now.AddMinutes(-_cfg.EsperaSemNovosMinutos);
            _proxima = baseHora.AddMinutes(_cfg.EsperaSemNovosMinutos);
        }
    }

    /// <summary>Chamado periodicamente pelo agendador.</summary>
    public void VerificarAgendamento()
    {
        DateTimeOffset? proxima;
        string estado;
        lock (_lock) { ExpirarBloqueio(); proxima = _proxima; estado = _estado; }
        if (!_cfg.SincronizacaoAutomatica || _cfg.EsperaSemNovosMinutos <= 0) return;
        if (estado is "running" or "blocked") return;
        if (proxima is { } p && p <= DateTimeOffset.Now) TentarIniciar("agendada");
    }

    // ---------- Eventos em tempo real (SSE) ----------

    public ChannelReader<string> Assinar(out Channel<string> canal)
    {
        canal = Channel.CreateBounded<string>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_lock) _assinantes.Add(canal);
        return canal.Reader;
    }

    public void Desassinar(Channel<string> canal)
    {
        lock (_lock) _assinantes.Remove(canal);
        canal.Writer.TryComplete();
    }

    private void Publicar(object evento)
    {
        var json = JsonSerializer.Serialize(evento, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        lock (_lock) foreach (var c in _assinantes) c.Writer.TryWrite(json);
    }

    public string SnapshotJson() =>
        JsonSerializer.Serialize(new { tipo = "estado", estado = Status() }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

/// <summary>Verifica a cada 30 s se a sincronização automática deve rodar.</summary>
public class SyncScheduler : BackgroundService
{
    private readonly SyncService _sync;
    public SyncScheduler(SyncService sync) => _sync = sync;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) _sync.VerificarAgendamento();
        }
        catch (OperationCanceledException) { }
    }
}
