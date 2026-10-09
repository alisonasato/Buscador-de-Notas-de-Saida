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

    public List<NotaSaida> Buscar(FiltroBusca f)
    {
        var where = new List<string>();
        using var c = Abrir();
        using var cmd = c.CreateCommand();

        void Add(string cond, string nome, object valor)
        {
            where.Add(cond);
            cmd.Parameters.AddWithValue(nome, valor);
        }

        if (!string.IsNullOrWhiteSpace(f.Chave)) Add("ChaveAcesso = $chave", "$chave", f.Chave.Trim());
        if (!string.IsNullOrWhiteSpace(f.Numero))
            Add("CAST(NumeroNota AS INTEGER) = CAST($num AS INTEGER)", "$num", f.Numero.Trim());
        if (!string.IsNullOrWhiteSpace(f.Serie)) Add("CAST(Serie AS INTEGER) = CAST($serie AS INTEGER)", "$serie", f.Serie.Trim());
        if (!string.IsNullOrWhiteSpace(f.De)) Add("DataEmissao >= $de", "$de", f.De);
        if (!string.IsNullOrWhiteSpace(f.Ate)) Add("DataEmissao < date($ate, '+1 day')", "$ate", f.Ate);
        if (!string.IsNullOrWhiteSpace(f.Destinatario))
            Add("(NomeDestinatario LIKE $dest OR CnpjCpfDestinatario LIKE $dest)", "$dest", $"%{f.Destinatario.Trim()}%");
        if (!string.IsNullOrWhiteSpace(f.Status)) Add("Status = $status", "$status", f.Status.Trim().ToUpperInvariant());
        if (f.ValorMin.HasValue) Add("ValorTotal >= $vmin", "$vmin", (double)f.ValorMin.Value);
        if (f.ValorMax.HasValue) Add("ValorTotal <= $vmax", "$vmax", (double)f.ValorMax.Value);

        cmd.CommandText = "SELECT ChaveAcesso, CnpjEmitente, NumeroNota, Serie, DataEmissao, CnpjCpfDestinatario, " +
                          "NomeDestinatario, ValorTotal, CaminhoXmlLocal, Status, SituacaoSefaz, Origem, Tentativas, UltimoErro " +
                          "FROM NotasSaidaBaixadas" +
                          (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") +
                          " ORDER BY DataEmissao DESC, ChaveAcesso LIMIT $limite";
        cmd.Parameters.AddWithValue("$limite", f.Limite);

        var lista = new List<NotaSaida>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            lista.Add(new NotaSaida
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
            });
        }
        return lista;
    }
}
