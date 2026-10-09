using System.Globalization;
using Microsoft.Data.Sqlite;

namespace BuscadorNotas;

public class Repositorio
{
    private readonly string _connectionString;

    public Repositorio(string caminhoBanco)
    {
        _connectionString = $"Data Source={caminhoBanco}";
        Inicializar();
    }

    private SqliteConnection Abrir()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void Inicializar()
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS ControleNsuSaida (
                Cnpj TEXT PRIMARY KEY,
                UltimoNsu TEXT NOT NULL,
                UltimaAtualizacao TEXT DEFAULT CURRENT_TIMESTAMP
            );
            CREATE TABLE IF NOT EXISTS NotasSaidaBaixadas (
                ChaveAcesso TEXT PRIMARY KEY,
                CnpjEmitente TEXT,
                NumeroNota TEXT,
                Serie TEXT,
                DataEmissao TEXT,
                CnpjCpfDestinatario TEXT,
                NomeDestinatario TEXT,
                ValorTotal REAL,
                CaminhoXmlLocal TEXT,
                Status TEXT NOT NULL DEFAULT 'PENDENTE',
                SituacaoSefaz TEXT,
                Origem TEXT,
                Tentativas INTEGER NOT NULL DEFAULT 0,
                UltimoErro TEXT
            );
            CREATE TABLE IF NOT EXISTS SincronizacaoLog (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                IniciadoEm TEXT NOT NULL,
                FimEm TEXT,
                Origem TEXT NOT NULL,
                Resultado TEXT,
                NovasNotas INTEGER NOT NULL DEFAULT 0,
                Mensagem TEXT
            );
            CREATE TABLE IF NOT EXISTS Preferencias (
                Chave TEXT PRIMARY KEY,
                Valor TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Notas_Data ON NotasSaidaBaixadas(DataEmissao);
            CREATE INDEX IF NOT EXISTS IX_Notas_Numero ON NotasSaidaBaixadas(NumeroNota);
            CREATE INDEX IF NOT EXISTS IX_Notas_Status ON NotasSaidaBaixadas(Status);
            """;
        cmd.ExecuteNonQuery();
    }

    // ---------- Controle de NSU ----------

    public string ObterUltimoNsu(string cnpj)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT UltimoNsu FROM ControleNsuSaida WHERE Cnpj = $cnpj";
        cmd.Parameters.AddWithValue("$cnpj", cnpj);
        return cmd.ExecuteScalar() as string ?? "000000000000000";
    }

    public void SalvarUltimoNsu(string cnpj, string nsu)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ControleNsuSaida (Cnpj, UltimoNsu, UltimaAtualizacao)
            VALUES ($cnpj, $nsu, CURRENT_TIMESTAMP)
            ON CONFLICT(Cnpj) DO UPDATE SET UltimoNsu = $nsu, UltimaAtualizacao = CURRENT_TIMESTAMP
            """;
        cmd.Parameters.AddWithValue("$cnpj", cnpj);
        cmd.Parameters.AddWithValue("$nsu", nsu);
        cmd.ExecuteNonQuery();
    }

    // ---------- Notas ----------

    /// <summary>Grava/atualiza a nota com os dados de um XML completo (sempre vence sobre PENDENTE/RESUMO).</summary>
    public void SalvarNotaCompleta(NotaSaida n)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO NotasSaidaBaixadas
              (ChaveAcesso, CnpjEmitente, NumeroNota, Serie, DataEmissao, CnpjCpfDestinatario,
               NomeDestinatario, ValorTotal, CaminhoXmlLocal, Status, SituacaoSefaz, Origem, Tentativas, UltimoErro)
            VALUES
              ($chave, $emit, $num, $serie, $data, $dest, $nome, $valor, $caminho, $status, $sit, $origem, 0, NULL)
            ON CONFLICT(ChaveAcesso) DO UPDATE SET
              CnpjEmitente = excluded.CnpjEmitente, NumeroNota = excluded.NumeroNota, Serie = excluded.Serie,
              DataEmissao = excluded.DataEmissao, CnpjCpfDestinatario = excluded.CnpjCpfDestinatario,
              NomeDestinatario = excluded.NomeDestinatario, ValorTotal = excluded.ValorTotal,
              CaminhoXmlLocal = excluded.CaminhoXmlLocal, Status = excluded.Status,
              SituacaoSefaz = COALESCE(excluded.SituacaoSefaz, SituacaoSefaz),
              Origem = excluded.Origem, UltimoErro = NULL
            """;
        Preencher(cmd, n);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Insere uma nota conhecida só por metadados (SPED/resumo); não sobrescreve dado existente.</summary>
    public bool InserirSeNaoExiste(NotaSaida n)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO NotasSaidaBaixadas
              (ChaveAcesso, CnpjEmitente, NumeroNota, Serie, DataEmissao, CnpjCpfDestinatario,
               NomeDestinatario, ValorTotal, CaminhoXmlLocal, Status, SituacaoSefaz, Origem, Tentativas, UltimoErro)
            VALUES
              ($chave, $emit, $num, $serie, $data, $dest, $nome, $valor, $caminho, $status, $sit, $origem, 0, NULL)
            """;
        Preencher(cmd, n);
        return cmd.ExecuteNonQuery() > 0;
    }

    private static void Preencher(SqliteCommand cmd, NotaSaida n)
    {
        cmd.Parameters.AddWithValue("$chave", n.ChaveAcesso);
        cmd.Parameters.AddWithValue("$emit", (object?)n.CnpjEmitente ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$num", (object?)n.NumeroNota ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$serie", (object?)n.Serie ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$data", (object?)n.DataEmissao ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dest", (object?)n.CnpjCpfDestinatario ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$nome", (object?)n.NomeDestinatario ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$valor", n.ValorTotal.HasValue ? (object)(double)n.ValorTotal.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$caminho", (object?)n.CaminhoXmlLocal ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", n.Status);
        cmd.Parameters.AddWithValue("$sit", (object?)n.SituacaoSefaz ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$origem", (object?)n.Origem ?? DBNull.Value);
    }

    public List<string> ListarChavesPendentes(int limite)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT ChaveAcesso FROM NotasSaidaBaixadas
            WHERE Status IN ('PENDENTE', 'ERRO') AND Tentativas < 5
            ORDER BY Tentativas, DataEmissao LIMIT $lim
            """;
        cmd.Parameters.AddWithValue("$lim", limite);
        var lista = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) lista.Add(r.GetString(0));
        return lista;
    }

    public void RegistrarConsulta(string chave, string status, string? situacaoSefaz, string? erro)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE NotasSaidaBaixadas
            SET Status = $status, SituacaoSefaz = COALESCE($sit, SituacaoSefaz),
                UltimoErro = $erro, Tentativas = Tentativas + 1
            WHERE ChaveAcesso = $chave
            """;
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$sit", (object?)situacaoSefaz ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$erro", (object?)erro ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$chave", chave);
        cmd.ExecuteNonQuery();
    }

    // ---------- Busca ----------

    private const string ColunasNota = "ChaveAcesso, CnpjEmitente, NumeroNota, Serie, DataEmissao, CnpjCpfDestinatario, " +
        "NomeDestinatario, ValorTotal, CaminhoXmlLocal, Status, SituacaoSefaz, Origem, Tentativas, UltimoErro";

    private static string EscaparLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static string Aspas(IEnumerable<string> prefixos) =>
        string.Join(" OR ", prefixos.Select(p => $"SituacaoSefaz LIKE '{p}%'"));

    /// <summary>Monta o WHERE e registra os parâmetros em <paramref name="cmd"/>.</summary>
    private static string MontarWhere(FiltroBusca f, SqliteCommand cmd)
    {
        var where = new List<string>();
        void Add(string cond, string nome, object valor) { where.Add(cond); cmd.Parameters.AddWithValue(nome, valor); }

        if (!string.IsNullOrWhiteSpace(f.Chave)) Add("ChaveAcesso = $chave", "$chave", f.Chave.Trim());
        if (!string.IsNullOrWhiteSpace(f.Numero))
            Add("CAST(NumeroNota AS INTEGER) = CAST($num AS INTEGER)", "$num", f.Numero.Trim());
        if (!string.IsNullOrWhiteSpace(f.Serie)) Add("CAST(Serie AS INTEGER) = CAST($serie AS INTEGER)", "$serie", f.Serie.Trim());
        if (!string.IsNullOrWhiteSpace(f.De)) Add("DataEmissao >= $de", "$de", f.De);
        if (!string.IsNullOrWhiteSpace(f.Ate)) Add("DataEmissao < date($ate, '+1 day')", "$ate", f.Ate);
        if (!string.IsNullOrWhiteSpace(f.Destinatario))
            Add("(NomeDestinatario LIKE $dest ESCAPE '\\' OR CnpjCpfDestinatario LIKE $dest ESCAPE '\\')", "$dest", $"%{EscaparLike(f.Destinatario.Trim())}%");
        if (!string.IsNullOrWhiteSpace(f.Status)) Add("Status = $status", "$status", f.Status.Trim().ToUpperInvariant());
        if (f.ValorMin.HasValue) Add("ValorTotal >= $vmin", "$vmin", (double)f.ValorMin.Value);
        if (f.ValorMax.HasValue) Add("ValorTotal <= $vmax", "$vmax", (double)f.ValorMax.Value);

        if (!string.IsNullOrWhiteSpace(f.Busca))
        {
            var q = f.Busca.Trim();
            var digitos = new string(q.Where(char.IsDigit).ToArray());
            cmd.Parameters.AddWithValue("$q", $"%{EscaparLike(q)}%");
            cmd.Parameters.AddWithValue("$qd", $"%{EscaparLike(digitos.Length > 0 ? digitos : q)}%");
            where.Add("(ChaveAcesso LIKE $q ESCAPE '\\' OR NumeroNota LIKE $q ESCAPE '\\' OR NomeDestinatario LIKE $q ESCAPE '\\' OR CnpjCpfDestinatario LIKE $qd ESCAPE '\\')");
        }

        switch (f.Situacao?.Trim().ToUpperInvariant())
        {
            case Situacao.Autorizada: where.Add("(" + Aspas(Situacao.PrefixosAutorizada) + ")"); break;
            case Situacao.Cancelada: where.Add("(" + Aspas(Situacao.PrefixosCancelada) + ")"); break;
            case Situacao.Denegada: where.Add("(" + Aspas(Situacao.PrefixosDenegada) + ")"); break;
            case Situacao.Desconhecida:
                where.Add("(SituacaoSefaz IS NULL OR NOT (" + Aspas(Situacao.PrefixosAutorizada.Concat(Situacao.PrefixosCancelada).Concat(Situacao.PrefixosDenegada)) + "))");
                break;
        }

        switch (f.Xml?.Trim().ToUpperInvariant())
        {
            case "BAIXADO": where.Add("Status = 'BAIXADO'"); break;
            case "PENDENTE": where.Add("Status <> 'BAIXADO'"); break;
        }

        return where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";
    }

    private static NotaSaida LerNota(SqliteDataReader r) => new()
    {
        ChaveAcesso = r.GetString(0),
        CnpjEmitente = r.IsDBNull(1) ? null : r.GetString(1),
        NumeroNota = r.IsDBNull(2) ? null : r.GetString(2),
        Serie = r.IsDBNull(3) ? null : r.GetString(3),
        DataEmissao = r.IsDBNull(4) ? null : r.GetString(4),
        CnpjCpfDestinatario = r.IsDBNull(5) ? null : r.GetString(5),
        NomeDestinatario = r.IsDBNull(6) ? null : r.GetString(6),
        ValorTotal = r.IsDBNull(7) ? null : (decimal)r.GetDouble(7),
        CaminhoXmlLocal = r.IsDBNull(8) ? null : r.GetString(8),
        Status = r.GetString(9),
        SituacaoSefaz = r.IsDBNull(10) ? null : r.GetString(10),
        Origem = r.IsDBNull(11) ? null : r.GetString(11),
        Tentativas = r.GetInt32(12),
        UltimoErro = r.IsDBNull(13) ? null : r.GetString(13),
    };

    public List<NotaSaida> Buscar(FiltroBusca f)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {ColunasNota} FROM NotasSaidaBaixadas" + MontarWhere(f, cmd) +
                          " ORDER BY DataEmissao DESC, ChaveAcesso LIMIT $limite OFFSET $offset";
        cmd.Parameters.AddWithValue("$limite", f.Limite);
        cmd.Parameters.AddWithValue("$offset", f.Offset);

        var lista = new List<NotaSaida>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) lista.Add(LerNota(r));
        return lista;
    }

    public int Contar(FiltroBusca f)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM NotasSaidaBaixadas" + MontarWhere(f, cmd);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Página 1-based, com o total de registros que casam com o filtro (ignora Limite/Offset do filtro).</summary>
    public PaginaNotas BuscarPagina(FiltroBusca f, int pagina, int tamanho)
    {
        tamanho = Math.Clamp(tamanho, 1, 200);
        pagina = Math.Max(1, pagina);
        var total = Contar(f);
        var ultima = Math.Max(1, (int)Math.Ceiling(total / (double)tamanho));
        pagina = Math.Min(pagina, ultima);
        f.Limite = tamanho;
        f.Offset = (pagina - 1) * tamanho;
        return new PaginaNotas(Buscar(f), total, pagina, tamanho);
    }

    public NotaSaida? ObterNota(string chave) =>
        Buscar(new FiltroBusca { Chave = chave, Limite = 1 }).FirstOrDefault();

    // ---------- Dashboard ----------

    /// <param name="mes">Formato aaaa-MM.</param>
    public ResumoDashboard ObterResumo(string mes)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT
              COALESCE(SUM(CASE WHEN DataEmissao LIKE $mes AND NOT ({Aspas(Situacao.PrefixosCancelada)}) THEN 1 ELSE 0 END), 0),
              COALESCE(SUM(CASE WHEN DataEmissao LIKE $mes AND NOT ({Aspas(Situacao.PrefixosCancelada)}) THEN ValorTotal ELSE 0 END), 0),
              COALESCE(SUM(CASE WHEN Status = 'BAIXADO' THEN 1 ELSE 0 END), 0),
              COALESCE(SUM(CASE WHEN Status <> 'BAIXADO' THEN 1 ELSE 0 END), 0),
              COUNT(*)
            FROM NotasSaidaBaixadas
            """;
        cmd.Parameters.AddWithValue("$mes", mes + "%");
        using var r = cmd.ExecuteReader();
        r.Read();
        return new ResumoDashboard(mes, r.GetInt32(0), (decimal)r.GetDouble(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4));
    }

    // ---------- Histórico de sincronização ----------

    public long IniciarLog(string origem, DateTimeOffset inicio)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO SincronizacaoLog (IniciadoEm, Origem) VALUES ($i, $o); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$i", inicio.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$o", origem);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void FinalizarLog(long id, DateTimeOffset fim, string resultado, int novasNotas, string? mensagem)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE SincronizacaoLog SET FimEm = $f, Resultado = $r, NovasNotas = $n, Mensagem = $m WHERE Id = $id";
        cmd.Parameters.AddWithValue("$f", fim.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$r", resultado);
        cmd.Parameters.AddWithValue("$n", novasNotas);
        cmd.Parameters.AddWithValue("$m", (object?)mensagem ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public List<LogSincronizacao> UltimosLogs(int n)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id, IniciadoEm, FimEm, Origem, Resultado, NovasNotas, Mensagem FROM SincronizacaoLog ORDER BY Id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", n);
        var lista = new List<LogSincronizacao>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            lista.Add(new LogSincronizacao(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5), r.IsDBNull(6) ? null : r.GetString(6)));
        return lista;
    }

    /// <summary>Logs que ficaram "em andamento" porque o processo caiu: marca como interrompidos.</summary>
    public void FecharLogsAbertos(DateTimeOffset agora)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE SincronizacaoLog SET FimEm = $f, Resultado = 'INTERROMPIDO' WHERE FimEm IS NULL";
        cmd.Parameters.AddWithValue("$f", agora.ToString("o", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    // ---------- Preferências (chave/valor) ----------

    public string? ObterPref(string chave)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Valor FROM Preferencias WHERE Chave = $k";
        cmd.Parameters.AddWithValue("$k", chave);
        return cmd.ExecuteScalar() as string;
    }

    public void SalvarPref(string chave, string valor)
    {
        using var c = Abrir();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO Preferencias (Chave, Valor) VALUES ($k, $v) ON CONFLICT(Chave) DO UPDATE SET Valor = $v";
        cmd.Parameters.AddWithValue("$k", chave);
        cmd.Parameters.AddWithValue("$v", valor);
        cmd.ExecuteNonQuery();
    }
}
