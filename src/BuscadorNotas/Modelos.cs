namespace BuscadorNotas;

public static class StatusNota
{
    public const string Pendente = "PENDENTE";                 // chave conhecida (SPED), XML ainda não obtido
    public const string Baixado = "BAIXADO";                   // XML completo salvo em disco
    public const string ResumoApenas = "RESUMO";               // só resNFe (sem XML completo)
    public const string ConsultadaSemXml = "CONSULTADA_SEM_XML"; // situação obtida via consulta, sem XML
    public const string Erro = "ERRO";
}

public class NotaSaida
{
    public string ChaveAcesso { get; set; } = "";
    public string? CnpjEmitente { get; set; }
    public string? NumeroNota { get; set; }
    public string? Serie { get; set; }
    public string? DataEmissao { get; set; }      // ISO: yyyy-MM-ddTHH:mm:ss
    public string? CnpjCpfDestinatario { get; set; }
    public string? NomeDestinatario { get; set; }
    public decimal? ValorTotal { get; set; }
    public string? CaminhoXmlLocal { get; set; }
    public string Status { get; set; } = StatusNota.Pendente;
    public string? SituacaoSefaz { get; set; }
    public string? Origem { get; set; }           // NSU | SPED | CONSULTA
    public int Tentativas { get; set; }
    public string? UltimoErro { get; set; }
}

public class FiltroBusca
{
    public string? Chave { get; set; }
    public string? Numero { get; set; }
    public string? Serie { get; set; }
    public string? De { get; set; }               // yyyy-MM-dd
    public string? Ate { get; set; }              // yyyy-MM-dd
    public string? Destinatario { get; set; }     // trecho do nome ou CNPJ/CPF
    public string? Status { get; set; }
    public decimal? ValorMin { get; set; }
    public decimal? ValorMax { get; set; }
    public int Limite { get; set; } = 50;
    public int Offset { get; set; }

    /// <summary>Texto livre: trecho da chave, número, nome ou CNPJ/CPF do destinatário.</summary>
    public string? Busca { get; set; }
    /// <summary>AUTORIZADA | CANCELADA | DENEGADA | DESCONHECIDA (ver <see cref="Situacao"/>).</summary>
    public string? Situacao { get; set; }
    /// <summary>BAIXADO (tem XML local) | PENDENTE (qualquer outro status, sem XML).</summary>
    public string? Xml { get; set; }
}

public record PaginaNotas(List<NotaSaida> Itens, int Total, int Pagina, int Tamanho);

public record ResumoDashboard(string Mes, int NotasNoMes, decimal ValorNoMes, int XmlBaixados, int XmlPendentes, int Total);

public record LogSincronizacao(long Id, string IniciadoEm, string? FimEm, string Origem, string? Resultado, int NovasNotas, string? Mensagem);

/// <summary>Situação fiscal exibida na interface, derivada do texto guardado em SituacaoSefaz (cStat da Sefaz ou código do SPED).</summary>
public static class Situacao
{
    public const string Autorizada = "AUTORIZADA", Cancelada = "CANCELADA", Denegada = "DENEGADA", Desconhecida = "DESCONHECIDA";

    // Prefixos de cStat (Sefaz) e códigos COD_SIT do SPED (00 regular, 01 extemporâneo, 02/03 cancelado, 04 denegado, 06-08 complementar/regime especial).
    internal static readonly string[] PrefixosAutorizada = { "100", "150", "SPED COD_SIT=00", "SPED COD_SIT=01", "SPED COD_SIT=06", "SPED COD_SIT=07", "SPED COD_SIT=08" };
    internal static readonly string[] PrefixosCancelada = { "101", "135", "155", "SPED COD_SIT=02", "SPED COD_SIT=03" };
    internal static readonly string[] PrefixosDenegada = { "110", "301", "302", "SPED COD_SIT=04" };

    public static string Classificar(string? situacaoSefaz)
    {
        if (string.IsNullOrWhiteSpace(situacaoSefaz)) return Desconhecida;
        bool Tem(string[] pref) => pref.Any(p => situacaoSefaz.StartsWith(p, StringComparison.Ordinal));
        if (Tem(PrefixosCancelada)) return Cancelada;
        if (Tem(PrefixosDenegada)) return Denegada;
        if (Tem(PrefixosAutorizada)) return Autorizada;
        return Desconhecida;
    }
}
