using System.Net.Http;

namespace BuscadorNotas;

/// <summary>
/// <c>testar-certificado [--modo maquina|temporaria]</c>: carrega o certificado no modo escolhido, mostra se sobraram arquivos de
/// chave no Windows e confere o handshake TLS com o certificado (GET no ?wsdl do serviço — não é uma consulta de distribuição).
/// </summary>
public static class TesteCertificado
{
    private static IEnumerable<string> PastasDeChaves()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        var comum = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Path.Combine(comum, "Microsoft", "Crypto", "RSA", "MachineKeys");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Crypto", "RSA");
        yield return Path.Combine(comum, "Microsoft", "Crypto", "Keys");
    }

    public static int ContarArquivosDeChave()
    {
        var total = 0;
        foreach (var p in PastasDeChaves())
        {
            try { if (Directory.Exists(p)) total += Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Count(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* sem permissão: ignora esta pasta */ }
        }
        return total;
    }

    public static async Task<int> ExecutarAsync(Configuracao cfg, string modo, CancellationToken ct)
    {
        cfg.ValidarParaSefaz();
        if (!CertificadoService.ModoValido(modo)) throw new ArgumentException("--modo deve ser maquina ou temporaria.");
        Console.WriteLine($"Modo de carga da chave: {modo}");

        var antes = ContarArquivosDeChave();
        for (var i = 1; i <= 3; i++)
        {
            using var c = CertificadoService.ObterCertificado(cfg.CertificadoPfx, cfg.SenhaCertificado, modo);
            if (!c.HasPrivateKey) { Console.WriteLine("O certificado não tem chave privada."); return 1; }
        }
        var depois = ContarArquivosDeChave();
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine($"Arquivos de chave do Windows: antes {antes}, depois de carregar 3 vezes {depois} (diferença {depois - antes}).");
            Console.WriteLine(depois - antes >= 3
                ? "  -> cada carga deixou arquivo(s) de chave: o modo 'maquina' acumula. Considere ModoChaveCertificado = \"temporaria\" SE o teste de conexão abaixo passar."
                : "  -> não houve acúmulo neste modo (outros programas também mexem nessas pastas, então a contagem é aproximada).");
        }
        else Console.WriteLine("Contagem de arquivos de chave só se aplica ao Windows.");

        // Handshake TLS com o certificado: GET do WSDL (metadados), sem enviar nenhuma consulta de distribuição.
        using var cert = CertificadoService.ObterCertificado(cfg.CertificadoPfx, cfg.SenhaCertificado, modo);
        using var http = SefazHttp.CriarClient(cert);
        var url = cfg.UrlDistribuicao + "?wsdl";
        try
        {
            using var resp = await http.GetAsync(url, ct);
            Console.WriteLine($"Conexão com {new Uri(url).Host}: HTTP {(int)resp.StatusCode}. " +
                (resp.IsSuccessStatusCode ? "O certificado foi aceito no handshake." : "Houve resposta (o handshake funcionou), mas não é 200; isso não prova o certificado."));
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"Falha de conexão/handshake neste modo: {ex.Message}");
            if (ex.InnerException != null) Console.WriteLine($"  Detalhe: {ex.InnerException.Message}");
            return 1;
        }
    }
}
