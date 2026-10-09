using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BuscadorNotas;

/// <summary>Leitura dos campos que interessam da NF-e (procNFe/NFe) e do resumo (resNFe). Ignora namespaces.</summary>
public record EventoNfe(string Chave, string Tipo, int Seq, string? CStat, string? Descricao, string? DataEvento);

public static class NfeXml
{
    private static readonly Regex Chave44 = new(@"^\d{44}$", RegexOptions.Compiled);

    public static bool ChaveValida(string? chave) => chave != null && Chave44.IsMatch(chave);

    /// <summary>Dígito verificador da chave (módulo 11, pesos 2..9 da direita para a esquerda sobre os 43 primeiros dígitos).</summary>
    public static int CalcularDv(string chave43)
    {
        int soma = 0, peso = 2;
        for (int i = chave43.Length - 1; i >= 0; i--)
        {
            soma += (chave43[i] - '0') * peso;
            peso = peso == 9 ? 2 : peso + 1;
        }
        var resto = soma % 11;
        return resto < 2 ? 0 : 11 - resto;
    }

    public static bool DigitoVerificadorOk(string chave) =>
        ChaveValida(chave) && CalcularDv(chave[..43]) == chave[43] - '0';

    private static XElement? Primeiro(XContainer raiz, string nomeLocal) =>
        raiz.Descendants().FirstOrDefault(e => e.Name.LocalName == nomeLocal);

    private static string? Texto(XContainer? raiz, string nomeLocal) =>
        raiz == null ? null : Primeiro(raiz, nomeLocal)?.Value.Trim();

    /// <summary>Tipo do documento pelo elemento raiz: procNFe, NFe, resNFe, procEventoNFe, resEvento...</summary>
    public static string TipoDocumento(XDocument doc) => doc.Root?.Name.LocalName ?? "";

    public static NotaSaida? LerNotaCompleta(XDocument doc, string origem)
    {
        var infNFe = Primeiro(doc, "infNFe");
        if (infNFe == null) return null;

        var chave = infNFe.Attribute("Id")?.Value.Replace("NFe", "") ?? "";
        if (!ChaveValida(chave))
            chave = Texto(doc, "chNFe") ?? "";   // fallback: chave do protocolo
        if (!ChaveValida(chave)) return null;

        var ide = infNFe.Elements().FirstOrDefault(e => e.Name.LocalName == "ide");
        var emit = infNFe.Elements().FirstOrDefault(e => e.Name.LocalName == "emit");
        var dest = infNFe.Elements().FirstOrDefault(e => e.Name.LocalName == "dest");
        var total = infNFe.Elements().FirstOrDefault(e => e.Name.LocalName == "total");

        return new NotaSaida
        {
            ChaveAcesso = chave,
            CnpjEmitente = Texto(emit, "CNPJ") ?? Texto(emit, "CPF"),
            NumeroNota = Texto(ide, "nNF"),
            Serie = Texto(ide, "serie"),
            DataEmissao = NormalizarData(Texto(ide, "dhEmi") ?? Texto(ide, "dEmi")),
            CnpjCpfDestinatario = Texto(dest, "CNPJ") ?? Texto(dest, "CPF") ?? Texto(dest, "idEstrangeiro"),
            NomeDestinatario = Texto(dest, "xNome"),
            ValorTotal = ParseDecimal(Texto(total, "vNF")),
            Status = StatusNota.Baixado,
            SituacaoSefaz = Texto(doc, "cStat") is { } cs && Primeiro(doc, "protNFe") != null ? cs : null,
            Origem = origem,
        };
    }

    public static NotaSaida? LerResumo(XDocument doc, string origem)
    {
        var chave = Texto(doc, "chNFe") ?? "";
        if (!ChaveValida(chave)) return null;
        return new NotaSaida
        {
            ChaveAcesso = chave,
            CnpjEmitente = Texto(doc, "CNPJ") ?? Texto(doc, "CPF"),
            NumeroNota = NumeroDaChave(chave),
            Serie = SerieDaChave(chave),
            DataEmissao = NormalizarData(Texto(doc, "dhEmi")),
            NomeDestinatario = null,
            ValorTotal = ParseDecimal(Texto(doc, "vNF")),
            Status = StatusNota.ResumoApenas,
            SituacaoSefaz = Texto(doc, "cSitNFe"),
            Origem = origem,
        };
    }

    public static readonly string[] EventosCancelamento = { "110111", "110112" }; // cancelamento / cancelamento por substituição

    /// <summary>Lê resEvento (resumo) ou procEventoNFe (evento completo). Retorna null se não for um evento reconhecível.</summary>
    public static EventoNfe? LerEvento(XDocument doc)
    {
        if (doc.Root?.Name.LocalName is not ("resEvento" or "procEventoNFe" or "evento" or "retEvento")) return null;
        var chave = Texto(doc, "chNFe") ?? "";
        var tipo = Texto(doc, "tpEvento") ?? "";
        if (!ChaveValida(chave) || tipo.Length == 0) return null;
        _ = int.TryParse(Texto(doc, "nSeqEvento"), out var seq);
        return new EventoNfe(chave, tipo, seq == 0 ? 1 : seq,
            Texto(doc, "cStat"), Texto(doc, "descEvento") ?? Texto(doc, "xEvento"),
            NormalizarData(Texto(doc, "dhEvento") ?? Texto(doc, "dhRegEvento")));
    }

    /// <summary>Cancelamento válido: tipo 110111/110112 e (sem retorno de status, ou 135/155 = registrado/extemporâneo).</summary>
    public static bool EhCancelamentoEfetivo(EventoNfe e) =>
        EventosCancelamento.Contains(e.Tipo) && (e.CStat is null or "135" or "155");

    // Layout da chave: cUF(2) AAMM(4) CNPJ(14) mod(2) serie(3) nNF(9) tpEmis(1) cNF(8) cDV(1)
    public static string CnpjDaChave(string chave) => chave.Substring(6, 14);
    public static string SerieDaChave(string chave) => chave.Substring(22, 3).TrimStart('0') is { Length: > 0 } s ? s : "0";
    public static string NumeroDaChave(string chave) => chave.Substring(25, 9).TrimStart('0') is { Length: > 0 } s ? s : "0";
    public static string AnoDaChave(string chave) => "20" + chave.Substring(2, 2);
    public static string MesDaChave(string chave) => chave.Substring(4, 2);

    public static decimal? ParseDecimal(string? s) =>
        decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Mantém o relógio local gravado na nota (ignora o fuso) em formato ISO sem offset.</summary>
    public static string? NormalizarData(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return dto.DateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        return null;
    }
}
