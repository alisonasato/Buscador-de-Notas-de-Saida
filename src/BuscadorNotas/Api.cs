using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;

namespace BuscadorNotas;

public record ConfigRequisicao(int? IntervaloMinutos, bool? Automatica, int? Ambiente, string? Cnpj, string? CufAutor = null, bool? BuscarCte = null, bool? BuscarMdfe = null, bool? GuardarEntradas = null);
public record ZipRequisicao(List<string>? Chaves);

/// <summary>API HTTP (Minimal API) + arquivos estáticos da interface (wwwroot). Reutiliza Repositorio, Robo e SyncService.</summary>
public static class ApiServer
{
    public const string ServiceName = "BuscadorNotasSaida";
    private const int MaxChavesZip = 1000;
    private const int MaxLinhasCsv = 50_000;
    private static readonly Regex MesRegex = new(@"^\d{4}-(0[1-9]|1[0-2])$", RegexOptions.Compiled);

    public static async Task ServirAsync(Configuracao cfg, Repositorio repo, Robo robo, string url, CancellationToken ct, bool abrirNavegador = false)
    {
        var app = Construir(cfg, repo, robo, url);
        if (abrirNavegador)
            app.Lifetime.ApplicationStarted.Register(() =>
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception) { /* sem navegador padrão: o endereço já foi impresso */ }
            });
        Console.WriteLine($"Interface em {url}  (Ctrl+C para encerrar)");
        if (string.IsNullOrEmpty(cfg.ApiToken))
            Console.WriteLine("  Sem ApiToken: acesso permitido apenas por localhost.");
        await app.RunAsync(ct);
    }

    private static bool EhLoopback(string url) =>
        Uri.TryCreate(url.Replace("*", "0.0.0.0").Replace("+", "0.0.0.0"), UriKind.Absolute, out var u) &&
        (u.IsLoopback || u.Host == "localhost");

    public static WebApplication Construir(Configuracao cfg, Repositorio repo, Robo robo, string url)
    {
        if (!EhLoopback(url) && string.IsNullOrEmpty(cfg.ApiToken))
            throw new InvalidOperationException(
                "Para expor a interface fora de localhost defina ApiToken no appsettings.json (a API entrega dados fiscais).");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls(url);
        builder.Host.UseWindowsService(o => o.ServiceName = ServiceName); // sem efeito fora do Windows Service

        var sync = new SyncService(cfg, repo, robo);
        var storage = new Armazenamento(cfg.PastaXml);
        builder.Services.AddSingleton(sync);
        builder.Services.AddHostedService<SyncScheduler>();
        var entrada = new EntradaService(cfg, repo, robo);
        builder.Services.AddSingleton(entrada);
        builder.Services.AddHostedService(_ => entrada);

        var app = builder.Build();

        app.UseExceptionHandler(e => e.Run(async ctx =>
        {
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsJsonAsync(new { erro = "Erro interno no servidor." });
        }));

        app.Use(async (ctx, next) =>
        {
            var req = ctx.Request;
            var token = cfg.ApiToken;

            // Sem token, só aceita Host de loopback (bloqueia DNS rebinding).
            if (string.IsNullOrEmpty(token) && req.Host.Host is not ("localhost" or "127.0.0.1" or "::1" or "[::1]"))
            {
                ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                return;
            }

            // Requisições que alteram estado só valem se vierem da própria origem (defesa contra CSRF).
            if (!HttpMethods.IsGet(req.Method) && !HttpMethods.IsHead(req.Method) && !HttpMethods.IsOptions(req.Method) &&
                req.Headers.TryGetValue("Origin", out var origem) &&
                (!Uri.TryCreate(origem.ToString(), UriKind.Absolute, out var o) ||
                 !string.Equals(o.Authority, req.Host.Value, StringComparison.OrdinalIgnoreCase)))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { erro = "Origem não permitida." });
                return;
            }

            if (req.Path.StartsWithSegments("/api") && !string.IsNullOrEmpty(token))
            {
                string? recebido = null;
                var auth = req.Headers.Authorization.ToString();
                if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) recebido = auth[7..].Trim();
                // EventSource não envia cabeçalhos: só o endpoint de eventos aceita o token na query.
                else if (req.Path.Equals("/api/sync/events", StringComparison.OrdinalIgnoreCase)) recebido = req.Query["token"];

                if (!TokenOk(token, recebido))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.Response.Headers.WWWAuthenticate = "Bearer";
                    await ctx.Response.WriteAsJsonAsync(new { erro = "Token ausente ou inválido." });
                    return;
                }
            }
            await next();
        });

        // A interface fica embutida no assembly (funciona em executável de arquivo único).
        var arquivos = new EmbeddedFileProvider(typeof(ApiServer).Assembly, "BuscadorNotas.wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = arquivos });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = arquivos });

        var api = app.MapGroup("/api");
        MapearNotas(api, repo, storage);
        MapearDashboard(api, repo, sync);
        MapearSync(api, sync);
        MapearEntrada(api, entrada);
        MapearConfiguracao(api, cfg, repo, sync);
        MapearPendentes(api, cfg, repo, robo);
        return app;
    }

    private static bool TokenOk(string esperado, string? recebido)
    {
        if (recebido == null) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(esperado));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(recebido));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    // ---------------------------------------------------------------- Notas

    private static object Dto(NotaSaida n) => new
    {
        chave = n.ChaveAcesso,
        tipo = n.Tipo,
        direcao = n.Direcao,
        emitenteNome = n.NomeEmitente,
        emitenteDoc = n.CnpjEmitente,
        numero = n.NumeroNota,
        serie = n.Serie,
        destinatarioNome = n.NomeDestinatario,
        destinatarioDoc = n.CnpjCpfDestinatario,
        emissao = n.DataEmissao,
        valor = n.ValorTotal,
        situacao = Situacao.Classificar(n.SituacaoSefaz),
        situacaoCodigo = n.SituacaoSefaz,
        xml = n.Status == StatusNota.Baixado ? "BAIXADO" : "PENDENTE",
        status = n.Status,
        origem = n.Origem,
    };

    /// <summary>Lê e valida os filtros da query string. Devolve a mensagem de erro, se houver.</summary>
    private static string? LerFiltro(IQueryCollection q, out FiltroBusca f)
    {
        f = new FiltroBusca { Busca = q["busca"], Situacao = q["situacao"], Xml = q["xml"], Tipo = q["tipo"] };

        static bool DataOk(string? s) => DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        foreach (var (nome, valor) in new[] { ("de", (string?)q["de"]), ("ate", (string?)q["ate"]) })
            if (!string.IsNullOrEmpty(valor) && !DataOk(valor)) return $"Parâmetro '{nome}' deve estar no formato aaaa-mm-dd.";
        f.De = q["de"]; f.Ate = q["ate"];

        if (!string.IsNullOrEmpty(f.Situacao) &&
            !new[] { Situacao.Autorizada, Situacao.Cancelada, Situacao.Denegada, Situacao.Desconhecida }.Contains(f.Situacao.ToUpperInvariant()))
            return "Parâmetro 'situacao' inválido.";
        // Sem o parâmetro, só saídas (comportamento original); "TODAS" mostra tudo.
        var dir = ((string?)q["direcao"])?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(dir)) f.Direcao = Direcao.Saida;
        else if (dir == "TODAS") f.Direcao = null;
        else if (Direcao.Todas.Contains(dir)) f.Direcao = dir;
        else return "Parâmetro 'direcao' inválido (SAIDA, ENTRADA, OUTRA ou TODAS).";
        if (!string.IsNullOrEmpty(f.Tipo) && !TipoDoc.Todos.Contains(f.Tipo.ToUpperInvariant()))
            return "Parâmetro 'tipo' inválido (NFE, NFCE, CTE, MDFE, CFE ou NFSE).";
        if (!string.IsNullOrEmpty(f.Xml) && f.Xml.ToUpperInvariant() is not ("BAIXADO" or "PENDENTE"))
            return "Parâmetro 'xml' deve ser BAIXADO ou PENDENTE.";
        return null;
    }

    private static void MapearNotas(RouteGroupBuilder api, Repositorio repo, Armazenamento storage)
    {
        api.MapGet("/notas", (HttpRequest req) =>
        {
            var erro = LerFiltro(req.Query, out var f);
            if (erro != null) return Results.BadRequest(new { erro });
            int.TryParse(req.Query["pagina"], out var pagina);
            if (!int.TryParse(req.Query["tamanho"], out var tamanho)) tamanho = 25;

            var p = repo.BuscarPagina(f, pagina < 1 ? 1 : pagina, tamanho);
            return Results.Ok(new
            {
                itens = p.Itens.Select(Dto),
                total = p.Total,
                pagina = p.Pagina,
                tamanho = p.Tamanho,
                totalPaginas = Math.Max(1, (int)Math.Ceiling(p.Total / (double)p.Tamanho)),
            });
        });

        api.MapGet("/notas/export.csv", (HttpRequest req) =>
        {
            var erro = LerFiltro(req.Query, out var f);
            if (erro != null) return Results.BadRequest(new { erro });

            var pt = CultureInfo.GetCultureInfo("pt-BR");
            var sb = new StringBuilder("﻿");
            sb.Append("Direcao;Tipo;Numero;Serie;Chave;Emitente;Documento do emitente;Destinatario;Documento;Emissao;Valor;Situacao;XML\r\n");
            const int lote = 1000;
            for (int off = 0; off < MaxLinhasCsv; off += lote)
            {
                f.Limite = lote; f.Offset = off;
                var itens = repo.Buscar(f);
                foreach (var n in itens)
                    sb.Append(string.Join(';', Cel(n.Direcao), Cel(TipoDoc.Rotulo(n.Tipo)), Cel(n.NumeroNota), Cel(n.Serie), Cel(n.ChaveAcesso), Cel(n.NomeEmitente), Cel(n.CnpjEmitente), Cel(n.NomeDestinatario), Cel(n.CnpjCpfDestinatario),
                        Cel(n.DataEmissao is { Length: >= 10 } d ? d[..10] : ""), n.ValorTotal?.ToString("F2", pt) ?? "",
                        Situacao.Classificar(n.SituacaoSefaz), n.Status == StatusNota.Baixado ? "BAIXADO" : "PENDENTE")).Append("\r\n");
                if (itens.Count < lote) break;
            }
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", "notas.csv");
        });

        api.MapPost("/notas/zip", (ZipRequisicao body, HttpResponse resp) =>
        {
            var chaves = (body.Chaves ?? new()).Where(NfeXml.IdValido).Distinct().ToList();
            if (chaves.Count == 0) return Results.BadRequest(new { erro = "Informe ao menos uma chave válida." });
            if (chaves.Count > MaxChavesZip) return Results.BadRequest(new { erro = $"Máximo de {MaxChavesZip} notas por ZIP." });

            var ms = new MemoryStream();
            int incluidas = 0;
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var chave in chaves)
                {
                    var caminho = storage.ResolverSeguro(repo.ObterNota(chave)?.CaminhoXmlLocal);
                    if (caminho == null) continue;
                    zip.CreateEntryFromFile(caminho, chave + ".xml");
                    incluidas++;
                }
            if (incluidas == 0) return Results.NotFound(new { erro = "Nenhuma das notas selecionadas tem XML disponível." });

            resp.Headers["X-Incluidas"] = incluidas.ToString(CultureInfo.InvariantCulture);
            resp.Headers["X-Sem-Xml"] = (chaves.Count - incluidas).ToString(CultureInfo.InvariantCulture);
            return Results.File(ms.ToArray(), "application/zip", "notas-xml.zip");
        });

        api.MapGet("/notas/{chave}", (string chave) =>
        {
            if (!NfeXml.IdValido(chave)) return Results.BadRequest(new { erro = "Identificador inválido." });
            var n = repo.ObterNota(chave);
            return n == null ? Results.NotFound(new { erro = "Nota não encontrada." }) : Results.Ok(Dto(n));
        });

        api.MapGet("/notas/{chave}/xml", (string chave) =>
        {
            if (!NfeXml.IdValido(chave)) return Results.BadRequest(new { erro = "Identificador inválido." });
            var n = repo.ObterNota(chave);
            if (n == null) return Results.NotFound(new { erro = "Nota não encontrada." });
            var caminho = storage.ResolverSeguro(n.CaminhoXmlLocal);
            return caminho == null
                ? Results.NotFound(new { erro = "O XML desta nota ainda não está disponível." })
                : Results.File(caminho, "application/xml", chave + ".xml");
        });
    }

    /// <summary>Escapa para CSV e neutraliza injeção de fórmula (=, +, -, @) em planilhas.</summary>
    private static string Cel(string? v)
    {
        v ??= "";
        if (v.Length > 0 && "=+-@\t\r".Contains(v[0])) v = "'" + v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    // ---------------------------------------------------------------- Dashboard

    private static void MapearDashboard(RouteGroupBuilder api, Repositorio repo, SyncService sync)
    {
        api.MapGet("/dashboard", (HttpRequest req) =>
        {
            string? mes = req.Query["mes"];
            if (string.IsNullOrEmpty(mes)) mes = DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            if (!MesRegex.IsMatch(mes)) return Results.BadRequest(new { erro = "Parâmetro 'mes' deve ser aaaa-mm." });
            return Results.Ok(new { resumo = repo.ObterResumo(mes), historico = repo.UltimosLogs(5), sync = sync.Status() });
        });
    }

    // ---------------------------------------------------------------- Sincronização

    private static void MapearSync(RouteGroupBuilder api, SyncService sync)
    {
        api.MapGet("/sync/status", () => Results.Ok(sync.Status()));

        api.MapPost("/sync/start", () =>
        {
            var motivo = sync.TentarIniciar("manual");
            return motivo == null ? Results.Accepted("/api/sync/status", sync.Status()) : Results.Conflict(new { erro = motivo });
        });

        api.MapPost("/sync/cancel", () =>
            sync.Cancelar() ? Results.Accepted("/api/sync/status", sync.Status()) : Results.Conflict(new { erro = "Não há sincronização em andamento." }));

        api.MapGet("/sync/events", async (HttpContext ctx) =>
        {
            var ct = ctx.RequestAborted;
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            var reader = sync.Assinar(out var canal);
            async Task Escrever(string texto)
            {
                await ctx.Response.WriteAsync(texto, ct);
                await ctx.Response.Body.FlushAsync(ct);
            }

            try
            {
                await Escrever($"data: {sync.SnapshotJson()}\n\n");
                while (!ct.IsCancellationRequested)
                {
                    using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    limite.CancelAfter(TimeSpan.FromSeconds(15));
                    try
                    {
                        if (!await reader.WaitToReadAsync(limite.Token)) break;
                        while (reader.TryRead(out var msg)) await Escrever($"data: {msg}\n\n");
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        await Escrever(": ping\n\n"); // mantém a conexão viva
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally { sync.Desassinar(canal); }
        });
    }

    // ---------------------------------------------------------------- Pasta monitorada

    private static void MapearEntrada(RouteGroupBuilder api, EntradaService entrada)
    {
        api.MapGet("/entrada", () => Results.Ok(entrada.Status()));

        api.MapPost("/entrada/varrer", async () =>
        {
            if (!entrada.Ativa) return Results.BadRequest(new { erro = "Pasta de entrada não configurada (PastaEntrada no appsettings.json)." });
            var n = await entrada.VarrerAsync();
            return n < 0 ? Results.Conflict(new { erro = "Já existe uma verificação em andamento." }) : Results.Ok(new { tratados = n, status = entrada.Status() });
        });
    }

    // ---------------------------------------------------------------- Configurações e certificado

    // ---------------------------------------------------------------- Chaves pendentes (consChNFe, experimental)

    private static void MapearPendentes(RouteGroupBuilder api, Configuracao cfg, Repositorio repo, Robo robo)
    {
        object Estado() => new
        {
            pendentes = repo.ContarPorStatus(StatusNota.Pendente, StatusNota.Erro),
            indisponiveis = repo.ContarPorStatus(StatusNota.Indisponivel),
            maximoPorBusca = Robo.MaxChavesPorBusca,
            liberadaEm = robo.BuscaChavesLiberadaEm(),
        };

        api.MapGet("/pendentes", () => Results.Ok(Estado()));

        api.MapPost("/pendentes/buscar", async (HttpRequest req, CancellationToken ct) =>
        {
            try
            {
                var forcar = string.Equals(req.Query["forcar"], "true", StringComparison.OrdinalIgnoreCase);
                var r = await robo.BuscarPendentesPorChaveAsync(Robo.MaxChavesPorBusca, forcar, ct);
                return Results.Ok(new { resultado = r, estado = Estado() });
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { erro = ex.Message }); }
        });
    }

    private static void MapearConfiguracao(RouteGroupBuilder api, Configuracao cfg, Repositorio repo, SyncService sync)
    {
        object Cfg() => new
        {
            intervaloMinutos = cfg.EsperaSemNovosMinutos,
            automatica = cfg.SincronizacaoAutomatica,
            ambiente = cfg.Ambiente,
            cnpj = cfg.Cnpj,
            cufAutor = Configuracao.UfValida(cfg.CUFAutor) ? cfg.CUFAutor : "",
            cufAutorEfetivo = cfg.CUFAutorEfetivo,
            buscarCte = cfg.DistribuirCte,
            buscarMdfe = cfg.DistribuirMdfe,
            guardarEntradas = cfg.GuardarEntradas,
            urlMdfeConfigurada = !string.IsNullOrWhiteSpace(cfg.UrlDistribuicaoMdfe),
            apiProtegidaPorToken = !string.IsNullOrEmpty(cfg.ApiToken),
        };

        api.MapGet("/config", () => Results.Ok(Cfg()));

        api.MapPut("/config", (ConfigRequisicao b) =>
        {
            if (b.IntervaloMinutos is { } i && i != 0 && (i < 60 || i > 1440))
                return Results.BadRequest(new { erro = "O intervalo deve ser 0 (somente manual) ou entre 60 e 1440 minutos, para evitar bloqueio da Sefaz." });
            if (b.Ambiente is { } a && a is not (1 or 2))
                return Results.BadRequest(new { erro = "Ambiente deve ser 1 (produção) ou 2 (homologação)." });
            if (b.CufAutor is { Length: > 0 } uf0 && !Configuracao.UfValida(uf0))
                return Results.BadRequest(new { erro = "UF inválida: use o código IBGE de 2 dígitos (ex.: 35 para SP)." });
            if (!string.IsNullOrWhiteSpace(b.Cnpj) && !Documento.CnpjValido(b.Cnpj))
                return Results.BadRequest(new { erro = "CNPJ inválido (confira os 14 dígitos)." });

            if (b.IntervaloMinutos is { } iv) { cfg.EsperaSemNovosMinutos = iv; repo.SalvarPref("intervaloMinutos", iv.ToString(CultureInfo.InvariantCulture)); }
            if (b.Automatica is { } au) { cfg.SincronizacaoAutomatica = au; repo.SalvarPref("automatica", au.ToString()); }
            if (b.Ambiente is { } am) { cfg.Ambiente = am; repo.SalvarPref("ambiente", am.ToString(CultureInfo.InvariantCulture)); }
            if (b.BuscarCte is { } bc) { cfg.DistribuirCte = bc; repo.SalvarPref("distribuirCte", bc.ToString()); }
            if (b.GuardarEntradas is { } ge) { cfg.GuardarEntradas = ge; repo.SalvarPref("guardarEntradas", ge.ToString()); }
            if (b.BuscarMdfe is { } bm) { cfg.DistribuirMdfe = bm; repo.SalvarPref("distribuirMdfe", bm.ToString()); }
            if (b.CufAutor is { } uf) { cfg.CUFAutor = uf; repo.SalvarPref("cufAutor", uf); }
            if (!string.IsNullOrWhiteSpace(b.Cnpj))
            {
                var c = new string(b.Cnpj.Where(char.IsDigit).ToArray());
                cfg.Cnpj = c; repo.SalvarPref("cnpj", c);
            }
            sync.RecalcularProxima();
            return Results.Ok(Cfg());
        });

        api.MapGet("/certificado", () => Results.Ok(InfoCertificado(cfg)));

        api.MapPost("/certificado", async (HttpContext ctx) =>
        {
            ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = 512 * 1024;
            if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { erro = "Envie multipart/form-data com 'arquivo' e 'senha'." });

            IFormCollection form;
            try { form = await ctx.Request.ReadFormAsync(ctx.RequestAborted); }
            catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
            { return Results.BadRequest(new { erro = "Arquivo grande demais ou requisição inválida (máximo 512 KB)." }); }

            var arquivo = form.Files["arquivo"];
            string? senha = form["senha"];
            if (arquivo == null || arquivo.Length == 0 || string.IsNullOrEmpty(senha))
                return Results.BadRequest(new { erro = "Informe o arquivo .pfx e a senha." });

            byte[] bytes;
            using (var ms = new MemoryStream()) { await arquivo.CopyToAsync(ms); bytes = ms.ToArray(); }

            try
            {
                using var teste = new X509Certificate2(bytes, senha);
                if (!teste.HasPrivateKey) return Results.BadRequest(new { erro = "O arquivo não contém a chave privada do certificado." });
            }
            catch (CryptographicException)
            {
                return Results.BadRequest(new { erro = "Senha incorreta ou arquivo de certificado inválido." });
            }

            var pasta = Path.GetDirectoryName(Path.GetFullPath(cfg.BancoSqlite)) ?? ".";
            Directory.CreateDirectory(pasta);
            var destino = Path.Combine(pasta, "certificado.pfx");
            await File.WriteAllBytesAsync(destino, bytes);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destino, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            cfg.CertificadoPfx = destino;
            cfg.SenhaEmMemoria = senha; // a senha NUNCA é gravada em disco
            repo.SalvarPref("certificadoPfx", destino);

            var info = InfoCertificado(cfg);
            return Results.Ok(new
            {
                certificado = info,
                aviso = "A senha fica apenas na memória do servidor: após reiniciar, defina NFE_PFX_SENHA ou envie o certificado de novo.",
            });
        });
    }

    private static object InfoCertificado(Configuracao cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.CertificadoPfx) || !File.Exists(cfg.CertificadoPfx))
            return new { configurado = false, senhaDefinida = !string.IsNullOrEmpty(cfg.SenhaCertificado), valido = false, erro = "Nenhum certificado configurado." };
        if (string.IsNullOrEmpty(cfg.SenhaCertificado))
            return new { configurado = true, arquivo = Path.GetFileName(cfg.CertificadoPfx), senhaDefinida = false, valido = false, erro = "Senha do certificado não informada." };

        try
        {
            using var cert = new X509Certificate2(cfg.CertificadoPfx, cfg.SenhaCertificado);
            var cn = cert.GetNameInfo(X509NameType.SimpleName, false);
            var cnpj = Regex.Match(cn, @"\d{14}").Value;
            var validade = new DateTimeOffset(cert.NotAfter);
            var dias = (int)Math.Floor((validade - DateTimeOffset.Now).TotalDays);
            return new
            {
                configurado = true,
                arquivo = Path.GetFileName(cfg.CertificadoPfx),
                senhaDefinida = true,
                valido = dias >= 0,
                titular = cn.Contains(':') ? cn[..cn.IndexOf(':')] : cn,
                cnpj = cnpj.Length == 14 ? cnpj : null,
                cnpjConfere = cnpj.Length == 14 ? (bool?)(cnpj == cfg.Cnpj) : null,
                validade = cert.NotAfter,
                diasRestantes = dias,
            };
        }
        catch (CryptographicException)
        {
            return new { configurado = true, arquivo = Path.GetFileName(cfg.CertificadoPfx), senhaDefinida = true, valido = false, erro = "Senha incorreta ou arquivo inválido." };
        }
    }
}
