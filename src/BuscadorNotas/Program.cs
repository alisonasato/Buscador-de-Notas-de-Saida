using System.Globalization;
using BuscadorNotas;

const string Ajuda = """
    Buscador de Notas de Saída

    Uso: BuscadorNotas <comando> [opções]

    Comandos:
      sync [--loop]               Robô de NSU (nfeDistDFeInteresse). Com --loop roda continuamente.
      importar-sped <arq...>      Lê registros C100 de saída de arquivos SPED Fiscal e cria pendências.
      baixar-pendentes [--max N]  Consulta por chave (nfeConsultaProtocolo) as notas PENDENTES.
      buscar [filtros]            Pesquisa no banco local.
          --chave <44 dígitos>    --numero <n>        --serie <s>
          --de <aaaa-mm-dd>       --ate <aaaa-mm-dd>
          --destinatario <texto>  (nome ou CNPJ/CPF)
          --status <BAIXADO|PENDENTE|RESUMO|CONSULTADA_SEM_XML|ERRO>
          --vmin <valor>          --vmax <valor>      --limite <n>

    Configuração: appsettings.json (ou caminho em BUSCADOR_CONFIG). Senha do .pfx em NFE_PFX_SENHA.
    """;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Ajuda);
    return 0;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    var cfg = Configuracao.Carregar();
    var repo = new Repositorio(cfg.BancoSqlite);
    var robo = new Robo(cfg, repo);
    var resto = args.Skip(1).ToArray();

    switch (args[0])
    {
        case "sync":
            await robo.SincronizarAsync(resto.Contains("--loop"), cts.Token);
            break;

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
