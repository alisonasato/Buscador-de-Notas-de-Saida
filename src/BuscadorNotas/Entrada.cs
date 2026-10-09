using System.IO.Compression;
using System.Globalization;
using System.Text;

namespace BuscadorNotas;

/// <summary>
/// Importa automaticamente o que for colocado em <see cref="Configuracao.PastaEntrada"/> (varredura periódica, que funciona
/// também em pastas de rede). Sucesso/ignorado vão para "processados/aaaa-mm"; inválidos para "rejeitados" com um .motivo.txt.
/// Nada é apagado: os arquivos originais são apenas movidos.
/// </summary>
public class EntradaService : BackgroundService
{
    private const int MaxArquivosPorVarredura = 2000;
    private const int MaxEntradasZip = 50_000;
    private const int MaxBytesPorXml = 5 * 1024 * 1024;

    private readonly Configuracao _cfg;
    private readonly Repositorio _repo;
    private readonly Robo _robo;
    private readonly SemaphoreSlim _varredura = new(1, 1);
    private DateTimeOffset? _ultima;
    private string? _erro;

    public EntradaService(Configuracao cfg, Repositorio repo, Robo robo) { _cfg = cfg; _repo = repo; _robo = robo; }

    public bool Ativa => !string.IsNullOrWhiteSpace(_cfg.PastaEntrada);
    private string Processados => Path.Combine(_cfg.PastaEntrada, "processados");
    private string Rejeitados => Path.Combine(_cfg.PastaEntrada, "rejeitados");

    public object Status() => new
    {
        ativa = Ativa,
        pasta = Ativa ? _cfg.PastaEntrada : null,
        intervaloSegundos = _cfg.EntradaIntervaloSegundos,
        varrendo = _varredura.CurrentCount == 0,
        ultimaVarredura = _ultima,
        aguardando = Ativa && _erro == null ? ArquivosPendentes().Count() : 0,
        erro = _erro,
        recentes = _repo.UltimasImportacoes(12),
    };

    /// <summary>Valida a pasta e cria processados/rejeitados. Retorna o motivo se não puder ser usada.</summary>
    private string? Preparar()
    {
        if (!Ativa) return "Pasta de entrada não configurada.";
        var entrada = Path.GetFullPath(_cfg.PastaEntrada);
        var xmls = Path.GetFullPath(_cfg.PastaXml);
        string Norm(string p) => p.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Norm(entrada).StartsWith(Norm(xmls), cmp) || Norm(xmls).StartsWith(Norm(entrada), cmp))
            return "A pasta de entrada não pode ser a pasta de XMLs, nem conter ou estar dentro dela.";
        try
        {
            Directory.CreateDirectory(entrada);
            Directory.CreateDirectory(Processados);
            Directory.CreateDirectory(Rejeitados);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Não foi possível usar a pasta de entrada: {ex.Message}";
        }
    }

    private IEnumerable<string> ArquivosPendentes()
    {
        if (!Directory.Exists(_cfg.PastaEntrada)) return Enumerable.Empty<string>();
        var limite = DateTime.UtcNow.AddSeconds(-Math.Max(0, _cfg.EntradaEstabilidadeSegundos));
        return Directory.EnumerateFiles(_cfg.PastaEntrada, "*", SearchOption.AllDirectories)
            .Where(f => !EmSubpastaReservada(f))
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".xml" or ".zip" or ".txt" or ".csv")
            .Where(f => File.GetLastWriteTimeUtc(f) <= limite);
    }

    private bool EmSubpastaReservada(string arquivo)
    {
        var rel = Path.GetRelativePath(_cfg.PastaEntrada, arquivo);
        var primeira = rel.Split(Path.DirectorySeparatorChar)[0];
        return primeira.Equals("processados", StringComparison.OrdinalIgnoreCase) || primeira.Equals("rejeitados", StringComparison.OrdinalIgnoreCase);
    }

    /// <returns>Número de arquivos tratados, ou -1 se já havia uma varredura em andamento.</returns>
    public async Task<int> VarrerAsync(CancellationToken ct = default)
    {
        if (!await _varredura.WaitAsync(0, ct)) return -1;
        try
        {
            _erro = Preparar();
            if (_erro != null) return 0;

            int tratados = 0;
            foreach (var arq in ArquivosPendentes().Take(MaxArquivosPorVarredura).ToList())
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(arq) || !Estavel(arq)) continue;
                TratarArquivo(arq);
                tratados++;
            }
            _ultima = DateTimeOffset.Now;
            return tratados;
        }
        finally { _varredura.Release(); }
    }

    private static bool Estavel(string arquivo)
    {
        try { using var _ = new FileStream(arquivo, FileMode.Open, FileAccess.Read, FileShare.None); return true; }
        catch (IOException) { return false; } // ainda em uso por outro programa
    }

    private void TratarArquivo(string arq)
    {
        var nome = Path.GetFileName(arq);
        try
        {
            var (desfecho, detalhe) = ProcessarPorTipo(arq);
            _repo.RegistrarImportacao(nome, desfecho.ToString().ToUpperInvariant(), detalhe);
            if (desfecho == Desfecho.Rejeitado) MoverRejeitado(arq, detalhe);
            else Mover(arq, Path.Combine(Processados, DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // erro inesperado neste arquivo não pode parar os demais
            _repo.RegistrarImportacao(nome, "REJEITADO", "erro inesperado: " + ex.Message);
            try { MoverRejeitado(arq, "erro inesperado: " + ex.Message); } catch (IOException) { }
        }
    }

    private (Desfecho, string) ProcessarPorTipo(string arq)
    {
        var ext = Path.GetExtension(arq).ToLowerInvariant();
        var outros = _cfg.EntradaAceitarOutrosCnpjs;

        if (ext == ".xml")
        {
            if (new FileInfo(arq).Length > MaxBytesPorXml) return (Desfecho.Rejeitado, $"XML maior que {MaxBytesPorXml / (1024 * 1024)} MB");
            var r = _robo.ImportarXmlBytes(File.ReadAllBytes(arq), outros);
            return (r.Desfecho, r.Detalhe);
        }
        if (ext == ".zip") return ProcessarZip(arq, outros);

        // .txt / .csv: SPED (começa com |0000|) ou lista de chaves
        var primeira = File.ReadLines(arq, Encoding.Latin1).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
        primeira = primeira.TrimStart('\uFEFF', 'ï', '»', '¿'); // BOM UTF-8 lido como Latin1 ("ï»¿")
        if (primeira.StartsWith("|0000|", StringComparison.Ordinal))
        {
            var novas = _robo.ImportarSped(new[] { arq });
            return (Desfecho.Importado, $"SPED: {novas} chave(s) nova(s) de saída");
        }
        var res = _robo.ImportarChaves(arq, outros);
        if (res.Lidos == 0) return (Desfecho.Rejeitado, "não é SPED nem contém chaves de acesso de 44 dígitos");
        return (Desfecho.Importado, $"lista de chaves: {res.Novos} nova(s), {res.Ignorados} ignorada(s)");
    }

    private (Desfecho, string) ProcessarZip(string arq, bool outros)
    {
        int importados = 0, novos = 0, ignorados = 0, rejeitados = 0, lidos = 0;
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(arq); }
        catch (InvalidDataException) { return (Desfecho.Rejeitado, "ZIP inválido ou corrompido"); }

        using (zip)
        {
            if (zip.Entries.Count > MaxEntradasZip) return (Desfecho.Rejeitado, $"ZIP com mais de {MaxEntradasZip} arquivos");
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                lidos++;
                var dados = LerLimitado(e);
                if (dados == null) { rejeitados++; continue; }
                var r = _robo.ImportarXmlBytes(dados, outros);
                switch (r.Desfecho)
                {
                    case Desfecho.Importado: importados++; if (r.Novo) novos++; break;
                    case Desfecho.Ignorado: ignorados++; break;
                    default: rejeitados++; break;
                }
            }
        }
        if (lidos == 0) return (Desfecho.Rejeitado, "ZIP sem arquivos .xml");
        var detalhe = $"ZIP: {lidos} XML(s): {novos} novo(s), {importados - novos} já existente(s), {ignorados} ignorado(s), {rejeitados} inválido(s)";
        return (importados > 0 || ignorados > 0 ? Desfecho.Importado : Desfecho.Rejeitado, detalhe);
    }

    /// <summary>Lê o conteúdo com teto de tamanho (proteção contra "zip bomb"); null se exceder.</summary>
    private static byte[]? LerLimitado(ZipArchiveEntry e)
    {
        if (e.Length > MaxBytesPorXml) return null;
        using var s = e.Open();
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > MaxBytesPorXml) return null;
        }
        return ms.ToArray();
    }

    private static void Mover(string origem, string pastaDestino)
    {
        Directory.CreateDirectory(pastaDestino);
        File.Move(origem, NomeLivre(pastaDestino, Path.GetFileName(origem)));
    }

    private void MoverRejeitado(string origem, string motivo)
    {
        var destino = NomeLivre(Rejeitados, Path.GetFileName(origem));
        File.Move(origem, destino);
        File.WriteAllText(destino + ".motivo.txt", motivo + Environment.NewLine, new UTF8Encoding(false));
    }

    private static string NomeLivre(string pasta, string nome)
    {
        var alvo = Path.Combine(pasta, nome);
        if (!File.Exists(alvo)) return alvo;
        return Path.Combine(pasta, $"{Path.GetFileNameWithoutExtension(nome)}-{DateTime.Now:yyyyMMddHHmmssfff}{Path.GetExtension(nome)}");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!Ativa) return;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await VarrerAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _erro = ex.Message; }
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _cfg.EntradaIntervaloSegundos)), ct);
            }
        }
        catch (OperationCanceledException) { }
    }
}
