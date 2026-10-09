namespace BuscadorNotas;

public static class StatusNota
{
    public const string Pendente = "PENDENTE";                 // chave conhecida (SPED), XML ainda não obtido
    public const string Baixado = "BAIXADO";                   // XML completo salvo em disco
    public const string ResumoApenas = "RESUMO";               // só resNFe (sem XML completo)
    public const string ConsultadaSemXml = "CONSULTADA_SEM_XML"; // situação obtida via consulta, sem XML
    public const string Erro = "ERRO";
    public const string Indisponivel = "INDISPONIVEL";         // a Sefaz não devolveu o XML ao ser consultada pela chave
}

/// <summary>Em que papel o CNPJ configurado aparece no documento.</summary>
public static class Direcao
{
    public const string Saida = "SAIDA", Entrada = "ENTRADA", Outra = "OUTRA";
    public static readonly string[] Todas = { Saida, Entrada, Outra };

    /// <summary>Saída = você é o emitente; entrada = você é o destinatário/tomador; outra = nenhum dos dois.</summary>
    public static string De(NotaSaida n, string cnpj) =>
        cnpj.Length != 14 || n.CnpjEmitente == cnpj ? Saida : n.CnpjCpfDestinatario == cnpj ? Entrada : Outra;
}

/// <summary>Tipos de documento fiscal tratados. NFE e NFCE compartilham o layout; os demais têm leitor próprio.</summary>
public static class TipoDoc
{
    public const string Nfe = "NFE", Nfce = "NFCE", Cte = "CTE", Mdfe = "MDFE", Cfe = "CFE", Nfse = "NFSE";
    public static readonly string[] Todos = { Nfe, Nfce, Cte, Mdfe, Cfe, Nfse };

    public static string Rotulo(string tipo) => tipo switch
    {
        Nfe => "NF-e", Nfce => "NFC-e", Cte => "CT-e", Mdfe => "MDF-e", Cfe => "CF-e SAT", Nfse => "NFS-e", _ => tipo,
    };

    /// <summary>Modelo fiscal (2 dígitos, posições 20-21 da chave) → tipo.</summary>
    public static string? DoModelo(string? modelo) => modelo switch
    {
        "55" => Nfe, "65" => Nfce, "57" or "67" => Cte, "58" => Mdfe, "59" => Cfe, _ => null,
    };
}

public class NotaSaida
{
    /// <summary>NFE | NFCE | CTE | MDFE | CFE | NFSE (ver <see cref="TipoDoc"/>).</summary>
    public string Tipo { get; set; } = TipoDoc.Nfe;
    /// <summary>SAIDA | ENTRADA | OUTRA (ver <see cref="BuscadorNotas.Direcao"/>).</summary>
    public string Direcao { get; set; } = BuscadorNotas.Direcao.Saida;
    public string? NomeEmitente { get; set; }
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
    /// <summary>NFE | NFCE | CTE | MDFE | CFE | NFSE.</summary>
    public string? Tipo { get; set; }
    /// <summary>SAIDA | ENTRADA | OUTRA; vazio = todas.</summary>
    public string? Direcao { get; set; }
}

public record ImportacaoRegistro(string Quando, string Arquivo, string Resultado, string? Detalhe);

public record PaginaNotas(List<NotaSaida> Itens, int Total, int Pagina, int Tamanho);

public record ContagemTipo(string Tipo, int Quantidade, decimal Valor);

/// <param name="NotasNoMes">Documentos do mês, exceto cancelados e exceto MDF-e (manifesto de transporte, não é nota de faturamento).</param>
/// <param name="PorTipo">Quantidade e valor por tipo no mês (cancelados excluídos; o valor do MDF-e é o da carga, não faturamento).</param>
public record ResumoDashboard(string Mes, int NotasNoMes, decimal ValorNoMes, int XmlBaixados, int XmlPendentes, int Total, List<ContagemTipo> PorTipo, int TotalEntradas);

public record LogSincronizacao(long Id, string IniciadoEm, string? FimEm, string Origem, string? Resultado, int NovasNotas, string? Mensagem);

/// <summary>Situação fiscal exibida na interface, derivada do texto guardado em SituacaoSefaz (cStat da Sefaz ou código do SPED).</summary>
public static class Situacao
{
    public const string Autorizada = "AUTORIZADA", Cancelada = "CANCELADA", Denegada = "DENEGADA", Desconhecida = "DESCONHECIDA";

    /// <summary>Gravado em SituacaoSefaz quando um evento de cancelamento (110111/110112) é aplicado à nota (começa com 135, tratado como cancelada).</summary>
    public const string CanceladaPorEvento = "135 - Cancelamento registrado (evento)";

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
