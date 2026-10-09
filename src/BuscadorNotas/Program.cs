using System.Globalization;
using BuscadorNotas;
using Microsoft.Extensions.Hosting.WindowsServices;

const string Ajuda = """
    Buscador de Notas de Saída

    Uso: BuscadorNotas <comando> [opções]
    Sem comando (duplo clique): equivale a "serve --abrir".

    Comandos:
      backup [--destino pasta] [--manter N]
                                  Backup do banco (consistente, online) + cópia incremental dos XMLs. Padrão: PastaBackup, 14 backups.
      serve [--abrir] [--log-arquivo] [--url http://127.0.0.1:5080]
                                  Sobe a interface web e a API HTTP (/api). Veja ApiUrl/ApiToken em appsettings.json.
      sync [--loop]               Robô de NSU (nfeDistDFeInteresse). Com --loop roda continuamente.
      sync --desde-nsu N          Volta o NSU salvo para N e sincroniza (reprocessa histórico; operação idempotente).
      sync --diagnostico [--max-paginas N] [--nsu-inicial N]
                                  Teste: lista em que papel (emitente/destinatário) seu CNPJ aparece, sem gravar nada.
      importar-xml <pasta> [--todos]
                                  Indexa XMLs de NF-e de uma pasta (subpastas incluídas) e copia para ano/mes.
      importar-chaves <arquivo> [--todos]
                                  Lê chaves de 44 dígitos de uma lista/CSV e cria pendências.
                                  (--todos: não exige que o emitente seja o CNPJ configurado)
      importar-sped <arq...>      Lê registros C100 de saída de arquivos SPED Fiscal e cria pendências.
      baixar-pendentes [--max N]  Consulta por chave (nfeConsultaProtocolo) as notas PENDENTES.
      buscar [filtros]            Pesquisa no banco local.
          --chave <44 dígitos>    --numero <n>        --serie <s>
          --de <aaaa-mm-dd>       --ate <aaaa-mm-dd>
          --destinatario <texto>  (nome ou CNPJ/CPF)
          --status <BAIXADO|PENDENTE|RESUMO|CONSULTADA_SEM_XML|ERRO>
          --vmin <valor>          --vmax <valor>      --limite <n>

    Opção global: --config <arquivo>.
    Configuração: appsettings.json (ou --config / variável BUSCADOR_CONFIG). Senha do .pfx em NFE_PFX_SENHA.
    """;

if (args.Length > 0 && args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Ajuda);
    return 0;
}

// Sem argumentos (duplo clique no .exe): sobe a interface e abre o navegador.
var duploClique = args.Length == 0;
if (duploClique) args = new[] { "serve", "--abrir" };

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Opção global: --config <arquivo> (alternativa à variável BUSCADOR_CONFIG; usada pela tarefa agendada de backup).
string? arquivoConfig = null;
for (var i = 0; i < args.Length - 1; i++)
    if (args[i] == "--config") { arquivoConfig = args[i + 1]; args = args.Take(i).Concat(args.Skip(i + 2)).ToArray(); break; }

try
{
    var caminhoConfig = Configuracao.LocalizarArquivo(arquivoConfig);
    if (!File.Exists(caminhoConfig) && args[0] == "serve")
    {
        Configuracao.CriarPadrao(caminhoConfig);
        Console.WriteLine($"Primeira execução: criei {caminhoConfig}. Informe o CNPJ em Configurações.");
    }
    var cfg = Configuracao.Carregar(arquivoConfig);
    var repo = new Repositorio(cfg.BancoSqlite);
    cfg.AplicarPreferencias(repo);
    var robo = new Robo(cfg, repo);
    var resto = args.Skip(1).ToArray();

    switch (args[0])
    {
        case "backup":
        {
            var r = BackupService.Executar(cfg, Opcao(resto, "--destino") ?? cfg.PastaBackup,
                int.Parse(Opcao(resto, "--manter") ?? "14", CultureInfo.InvariantCulture));
            Console.WriteLine($"Banco salvo em {r.ArquivoDb}");
            Console.WriteLine($"XMLs: {r.XmlsCopiados} copiados, {r.XmlsJaExistentes} já existiam. Backups de banco antigos removidos: {r.BackupsRemovidos}.");
            break;
        }

        case "serve":
            if (WindowsServiceHelpers.IsWindowsService() || resto.Contains("--log-arquivo"))
                LogDiario.Iniciar(Path.Combine(cfg.PastaConfig, "logs"));
            await ApiServer.ServirAsync(cfg, repo, robo, Opcao(resto, "--url") ?? cfg.ApiUrl, cts.Token, resto.Contains("--abrir"));
            break;

        case "sync":
            if (resto.Contains("--diagnostico"))
                await robo.DiagnosticarAsync(
                    int.Parse(Opcao(resto, "--max-paginas") ?? "20", CultureInfo.InvariantCulture),
                    (Opcao(resto, "--nsu-inicial") ?? "0").PadLeft(15, '0'), cts.Token);
            else
            {
                // Reprocessar histórico (ex.: para aplicar eventos de cancelamento que passaram antes desta versão):
                // volta o NSU salvo; é seguro repetir, pois gravar nota/evento é idempotente.
                if (Opcao(resto, "--desde-nsu") is { } desde)
                {
                    if (desde.Length is 0 or > 15 || !desde.All(char.IsDigit))
                        throw new ArgumentException("--desde-nsu deve ser um número de até 15 dígitos.");
                    repo.SalvarUltimoNsu(cfg.Cnpj, desde.PadLeft(15, '0'));
                    Console.WriteLine($"NSU salvo reposicionado para {desde.PadLeft(15, '0')}.");
                }
                await robo.SincronizarAsync(resto.Contains("--loop"), cts.Token);
            }
            break;

        case "importar-xml":
        case "importar-chaves":
        {
            var alvo = resto.FirstOrDefault(a => !a.StartsWith("--"));
            if (alvo == null) { Console.Error.WriteLine("Informe o caminho."); return 2; }
            var todos = resto.Contains("--todos");
            var res = args[0] == "importar-xml" ? robo.ImportarXmls(alvo, todos) : robo.ImportarChaves(alvo, todos);
            foreach (var aviso in res.Avisos.Take(50)) Console.WriteLine("  aviso: " + aviso);
            if (res.Avisos.Count > 50) Console.WriteLine($"  ... e mais {res.Avisos.Count - 50} aviso(s).");
            Console.WriteLine($"Lidos: {res.Lidos} | novos: {res.Novos} | ignorados: {res.Ignorados}");
            break;
        }

        case "importar-sped":
            if (resto.Length == 0) { Console.Error.WriteLine("Informe ao menos um arquivo SPED."); return 2; }
            var novas = robo.ImportarSped(resto);
            Console.WriteLine($"{novas} chaves novas adicionadas às pendências.");
            break;

        case "baixar-pendentes":
            await robo.BaixarPendentesAsync(int.Parse(Opcao(resto, "--max") ?? "50", CultureInfo.InvariantCulture), cts.Token);
            break;

        case "buscar":
            Buscar(repo, resto);
            break;

        default:
            Console.Error.WriteLine($"Comando desconhecido: {args[0]}\n");
            Console.WriteLine(Ajuda);
            return 2;
    }
    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine("Interrompido.");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Erro: {ex.Message}");
    if (duploClique && !Console.IsInputRedirected)
    {
        Console.WriteLine("Pressione qualquer tecla para fechar...");
        Console.ReadKey(true);
    }
    return 1;
}

static string? Opcao(string[] a, string nome)
{
    var i = Array.IndexOf(a, nome);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
}

static void Buscar(Repositorio repo, string[] a)
{
    var f = new FiltroBusca
    {
        Chave = Opcao(a, "--chave"),
        Numero = Opcao(a, "--numero"),
        Serie = Opcao(a, "--serie"),
        De = Opcao(a, "--de"),
        Ate = Opcao(a, "--ate"),
        Destinatario = Opcao(a, "--destinatario"),
        Status = Opcao(a, "--status"),
        ValorMin = NfeXml.ParseDecimal(Opcao(a, "--vmin")),
        ValorMax = NfeXml.ParseDecimal(Opcao(a, "--vmax")),
        Limite = int.TryParse(Opcao(a, "--limite"), out var l) ? l : 50,
    };

    var notas = repo.Buscar(f);
    Console.WriteLine($"{"Data",-10} {"Nº",-9} {"Sér",-3} {"Valor",12} {"Status",-18} {"Destinatário",-30} Chave");
    foreach (var n in notas)
    {
        var data = n.DataEmissao is { Length: >= 10 } d ? d[..10] : "";
        var dest = n.NomeDestinatario ?? n.CnpjCpfDestinatario ?? "";
        if (dest.Length > 30) dest = dest[..30];
        Console.WriteLine(string.Format(CultureInfo.GetCultureInfo("pt-BR"),
            "{0,-10} {1,-9} {2,-3} {3,12:N2} {4,-18} {5,-30} {6}",
            data, n.NumeroNota, n.Serie, n.ValorTotal, n.Status, dest, n.ChaveAcesso));
    }
    Console.WriteLine($"{notas.Count} resultado(s).");
}
