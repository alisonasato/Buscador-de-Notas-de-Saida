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
}
