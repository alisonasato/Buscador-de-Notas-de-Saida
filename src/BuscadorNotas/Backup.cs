using System.Globalization;
using Microsoft.Data.Sqlite;

namespace BuscadorNotas;

public record ResultadoBackup(string ArquivoDb, int XmlsCopiados, int XmlsJaExistentes, int BackupsRemovidos);

/// <summary>
/// Backup consistente com o servidor rodando: o banco usa a API de backup do SQLite (não é cópia de arquivo aberto);
/// os XMLs são imutáveis, então a cópia é incremental. Nunca apaga nada da origem.
/// </summary>
public static class BackupService
{
    public static ResultadoBackup Executar(Configuracao cfg, string destino, int manter = 14, DateTime? agora = null)
    {
        if (string.IsNullOrWhiteSpace(destino))
            throw new ArgumentException("Informe a pasta de backup (--destino ou PastaBackup no appsettings.json).");
        if (manter < 1) throw new ArgumentException("--manter deve ser pelo menos 1.");
        if (!File.Exists(cfg.BancoSqlite)) throw new FileNotFoundException($"Banco não encontrado: {cfg.BancoSqlite}");

        destino = Path.GetFullPath(destino);
        var xmlsOrigem = Path.GetFullPath(cfg.PastaXml);
        if (EstaDentro(destino, xmlsOrigem) || EstaDentro(xmlsOrigem, destino))
            throw new ArgumentException("A pasta de backup não pode conter nem estar dentro da pasta de XMLs.");

        var dbDir = Path.Combine(destino, "db");
        var xmlDir = Path.Combine(destino, "xmls");
        Directory.CreateDirectory(dbDir);
        Directory.CreateDirectory(xmlDir);

        var carimbo = (agora ?? DateTime.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var final = Path.Combine(dbDir, $"notas-{carimbo}.db");
        var temp = final + ".tmp";

        try
        {
            using (var origem = new SqliteConnection($"Data Source={cfg.BancoSqlite};Mode=ReadOnly"))
            using (var dest = new SqliteConnection($"Data Source={temp}"))
            {
                origem.Open();
                dest.Open();
                origem.BackupDatabase(dest);
                using var cmd = dest.CreateCommand();
                cmd.CommandText = "PRAGMA integrity_check";
                var r = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (r != "ok") throw new InvalidDataException($"A cópia do banco falhou na verificação de integridade: {r}");
            }
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
        SqliteConnection.ClearAllPools(); // libera o arquivo antes de renomear (Windows)
        File.Move(temp, final, overwrite: true);

        int copiados = 0, existentes = 0;
        if (Directory.Exists(xmlsOrigem))
            foreach (var arq in Directory.EnumerateFiles(xmlsOrigem, "*.xml", SearchOption.AllDirectories))
            {
                var alvo = Path.Combine(xmlDir, Path.GetRelativePath(xmlsOrigem, arq));
                if (File.Exists(alvo) && new FileInfo(alvo).Length == new FileInfo(arq).Length) { existentes++; continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(alvo)!);
                File.Copy(arq, alvo + ".tmp", overwrite: true);
                File.Move(alvo + ".tmp", alvo, overwrite: true);
                copiados++;
            }

        // Retenção: mantém os N backups de banco mais recentes (o carimbo no nome ordena cronologicamente).
        var antigos = Directory.GetFiles(dbDir, "notas-*.db").OrderByDescending(f => f, StringComparer.Ordinal).Skip(manter).ToList();
        foreach (var f in antigos) File.Delete(f);

        return new ResultadoBackup(final, copiados, existentes, antigos.Count);
    }

    private static bool EstaDentro(string filho, string pai)
    {
        var p = Path.GetFullPath(pai).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var f = Path.GetFullPath(filho).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return f.StartsWith(p, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}

/// <summary>Redireciona Console.Out/Error para um arquivo por dia (logs/buscador-aaaammdd.log), útil quando roda como serviço.</summary>
public sealed class LogDiario : TextWriter
{
    private readonly string _pasta;
    private readonly int _diasGuardados;
    private readonly Func<DateTime> _agora;
    private readonly object _lock = new();
    private StreamWriter? _atual;
    private string? _dia;

    public LogDiario(string pasta, int diasGuardados = 30, Func<DateTime>? agora = null)
    {
        _pasta = pasta;
        _diasGuardados = diasGuardados;
        _agora = agora ?? (() => DateTime.Now);
        Directory.CreateDirectory(pasta);
    }

    public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

    public static LogDiario Iniciar(string pasta)
    {
        var log = new LogDiario(pasta);
        Console.SetOut(log);
        Console.SetError(log);
        return log;
    }

    private StreamWriter Escritor()
    {
        var agora = _agora();
        var dia = agora.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (_atual == null || dia != _dia)
        {
            _atual?.Dispose();
            _atual = new StreamWriter(new FileStream(Path.Combine(_pasta, $"buscador-{dia}.log"), FileMode.Append, FileAccess.Write, FileShare.Read),
                new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            _dia = dia;
            foreach (var velho in Directory.GetFiles(_pasta, "buscador-*.log"))
                if (File.GetLastWriteTime(velho) < agora.AddDays(-_diasGuardados)) { try { File.Delete(velho); } catch (IOException) { } }
        }
        return _atual;
    }

    public override void Write(char value) { lock (_lock) Escritor().Write(value); }
    public override void Write(string? value) { lock (_lock) Escritor().Write(value); }
    public override void WriteLine(string? value)
    {
        lock (_lock) Escritor().WriteLine($"{_agora():yyyy-MM-dd HH:mm:ss} {value}");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) lock (_lock) { _atual?.Dispose(); _atual = null; }
        base.Dispose(disposing);
    }
}
