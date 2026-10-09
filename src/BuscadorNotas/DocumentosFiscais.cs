using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace BuscadorNotas;

/// <summary>
/// Leitores de XML para os demais documentos fiscais, todos ignorando namespaces.
/// <para>
/// ATENÇÃO: os nomes dos elementos de CT-e, MDF-e, CF-e SAT e NFS-e abaixo foram escritos de MEMÓRIA, sem acesso aos
/// manuais oficiais, e testados apenas com XMLs montados à mão. Valide com arquivos reais do seu emissor. A NFS-e em
/// particular não tem layout único (cada município/versão tem o seu): o leitor é "melhor esforço".
/// </para>
/// </summary>
public static class DocumentosFiscais
{
    /// <summary>Tipo do documento pelo XML, ou null se a raiz não for um documento fiscal reconhecido.</summary>
    public static string? Identificar(XDocument doc)
    {
        var raiz = doc.Root?.Name.LocalName;
        switch (raiz)
        {
            case "nfeProc" or "NFe": return TipoDoc.Nfe;       // NFC-e (mod 65) usa o mesmo layout; o tipo exato sai de ide/mod
            case "cteProc" or "CTe" or "cteOSProc" or "CTeOS": return TipoDoc.Cte;
            case "mdfeProc" or "MDFe": return TipoDoc.Mdfe;
            case "CFe": return TipoDoc.Cfe;
        }
        // NFS-e: ABRASF (CompNfse/Nfse/InfNfse) ou padrão nacional (infNFSe), em qualquer envelope
        if (NfeXml.Primeiro(doc, "InfNfse") != null || NfeXml.Primeiro(doc, "infNFSe") != null) return TipoDoc.Nfse;
        return null;
    }

    /// <summary>Lê o documento. Retorna null se o tipo é reconhecido mas faltam os dados mínimos (identificador).</summary>
    public static NotaSaida? Ler(XDocument doc, string origem) => Identificar(doc) switch
    {
        TipoDoc.Nfe => NfeXml.LerNotaCompleta(doc, origem),
        TipoDoc.Cte => LerCte(doc, origem),
        TipoDoc.Mdfe => LerMdfe(doc, origem),
        TipoDoc.Cfe => LerCfe(doc, origem),
        TipoDoc.Nfse => LerNfse(doc, origem),
        _ => null,
    };

    private static string? T(XContainer? raiz, string nome) => raiz == null ? null : NfeXml.Texto(raiz, nome);

    private static XElement? Filho(XContainer? pai, string nome) =>
        pai?.Elements().FirstOrDefault(e => e.Name.LocalName == nome);

    /// <summary>Chave de 44 dígitos do atributo Id (ex.: "CTe3524...") ou, na falta, do elemento do protocolo.</summary>
    private static string ChaveDe(XElement? inf, XDocument doc, string elementoProtocolo)
    {
        var chave = NfeXml.SoDigitos(inf?.Attribute("Id")?.Value);
        if (!NfeXml.ChaveValida(chave)) chave = NfeXml.SoDigitos(NfeXml.Texto(doc, elementoProtocolo));
        return chave;
    }

    private static string? SituacaoDoProtocolo(XDocument doc, string protocolo) =>
        NfeXml.Primeiro(doc, protocolo) is { } p ? T(p, "cStat") : null;

    // ---------------------------------------------------------------- CT-e (mod 57) e CT-e OS (mod 67)

    public static NotaSaida? LerCte(XDocument doc, string origem)
    {
        var inf = NfeXml.Primeiro(doc, "infCte");
        if (inf == null) return null;
        var chave = ChaveDe(inf, doc, "chCTe");
        if (!NfeXml.ChaveValida(chave)) return null;

        var ide = Filho(inf, "ide");
        var emit = Filho(inf, "emit");
        // Quem contratou o frete: destinatário, ou tomador (CT-e OS), ou remetente
        var cliente = Filho(inf, "dest") ?? Filho(inf, "toma") ?? Filho(inf, "rem");

        return new NotaSaida
        {
            ChaveAcesso = chave,
            Tipo = TipoDoc.Cte,
            CnpjEmitente = T(emit, "CNPJ") ?? T(emit, "CPF"),
            NomeEmitente = T(emit, "xNome"),
            NumeroNota = T(ide, "nCT"),
            Serie = T(ide, "serie"),
            DataEmissao = NfeXml.NormalizarData(T(ide, "dhEmi")),
            CnpjCpfDestinatario = T(cliente, "CNPJ") ?? T(cliente, "CPF"),
            NomeDestinatario = T(cliente, "xNome"),
            ValorTotal = NfeXml.ParseDecimal(T(Filho(inf, "vPrest"), "vTPrest")),
            Status = StatusNota.Baixado,
            SituacaoSefaz = SituacaoDoProtocolo(doc, "protCTe"),
            Origem = origem,
        };
    }

    // ---------------------------------------------------------------- MDF-e (mod 58)

    public static NotaSaida? LerMdfe(XDocument doc, string origem)
    {
        var inf = NfeXml.Primeiro(doc, "infMDFe");
        if (inf == null) return null;
        var chave = ChaveDe(inf, doc, "chMDFe");
        if (!NfeXml.ChaveValida(chave)) return null;

        var ide = Filho(inf, "ide");
        var emit = Filho(inf, "emit");
        return new NotaSaida
        {
            ChaveAcesso = chave,
            Tipo = TipoDoc.Mdfe,
            CnpjEmitente = T(emit, "CNPJ") ?? T(emit, "CPF"),
            NomeEmitente = T(emit, "xNome"),
            NumeroNota = T(ide, "nMDF"),
            Serie = T(ide, "serie"),
            DataEmissao = NfeXml.NormalizarData(T(ide, "dhEmi")),
            // Manifesto não tem destinatário; o valor é o da CARGA transportada (não é faturamento)
            ValorTotal = NfeXml.ParseDecimal(T(Filho(inf, "tot"), "vCarga")),
            Status = StatusNota.Baixado,
            SituacaoSefaz = SituacaoDoProtocolo(doc, "protMDFe"),
            Origem = origem,
        };
    }

    // ---------------------------------------------------------------- CF-e SAT (mod 59)

    public static NotaSaida? LerCfe(XDocument doc, string origem)
    {
        var inf = NfeXml.Primeiro(doc, "infCFe");
        if (inf == null) return null;
        var chave = NfeXml.SoDigitos(inf.Attribute("Id")?.Value);
        if (!NfeXml.ChaveValida(chave)) return null;

        var ide = Filho(inf, "ide");
        var emit = Filho(inf, "emit");
        var dest = Filho(inf, "dest");
        // dEmi = aaaaMMdd, hEmi = HHmmss
        string? data = null;
        if (DateTime.TryParseExact(T(ide, "dEmi") + T(ide, "hEmi"), "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            data = dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        return new NotaSaida
        {
            ChaveAcesso = chave,
            Tipo = TipoDoc.Cfe,
            CnpjEmitente = T(emit, "CNPJ") ?? T(emit, "CPF"),
            NomeEmitente = T(emit, "xNome"),
            NumeroNota = (T(ide, "nCFe") ?? "").TrimStart('0') is { Length: > 0 } n ? n : "0",
            Serie = T(ide, "nserieSAT") is { } s ? (s.TrimStart('0') is { Length: > 0 } s2 ? s2 : "0") : null,
            DataEmissao = data,
            CnpjCpfDestinatario = T(dest, "CNPJ") ?? T(dest, "CPF"),
            NomeDestinatario = T(dest, "xNome"),
            ValorTotal = NfeXml.ParseDecimal(T(Filho(inf, "total"), "vCFe")),
            Status = StatusNota.Baixado,
            // O SAT não tem autorização prévia da Sefaz: o XML válido do equipamento equivale a "emitido".
            SituacaoSefaz = "100 - CF-e-SAT emitido",
            Origem = origem,
        };
    }

    // ---------------------------------------------------------------- NFS-e (melhor esforço; sem layout único)

    /// <summary>Primeiro valor não vazio entre os elementos (por nome local) dentro de <paramref name="raiz"/>.</summary>
    private static string? Qualquer(XContainer? raiz, params string[] nomes)
    {
        if (raiz == null) return null;
        foreach (var n in nomes)
            if (NfeXml.Texto(raiz, n) is { Length: > 0 } v) return v;
        return null;
    }

    public static NotaSaida? LerNfse(XDocument doc, string origem)
    {
        // ABRASF: InfNfse; padrão nacional: infNFSe
        var inf = NfeXml.Primeiro(doc, "InfNfse") ?? NfeXml.Primeiro(doc, "infNFSe");
        if (inf == null) return null;

        var prestador = NfeXml.Primeiro(inf, "PrestadorServico") ?? NfeXml.Primeiro(inf, "emit") ?? NfeXml.Primeiro(inf, "Prestador");
        var tomador = NfeXml.Primeiro(inf, "TomadorServico") ?? NfeXml.Primeiro(inf, "toma") ?? NfeXml.Primeiro(inf, "Tomador");

        var cnpj = SoDigitos(Qualquer(prestador, "Cnpj", "CNPJ", "Cpf", "CPF"));
        var numero = (Qualquer(inf, "Numero", "nNFSe") ?? "").Trim();
        if (cnpj.Length is not (11 or 14) || numero.Length == 0) return null;

        var municipio = SoDigitos(Qualquer(inf, "CodigoMunicipio", "cLocEmi", "cMunIncid", "CodigoMunicipioGerador", "OrgaoGerador"));
        var id = $"NFSE-{cnpj}-{(municipio.Length > 0 ? municipio : "0")}-{Sanitizar(numero)}";
        if (!NfeXml.IdValido(id)) return null;

        return new NotaSaida
        {
            ChaveAcesso = id,
            Tipo = TipoDoc.Nfse,
            CnpjEmitente = cnpj,
            NomeEmitente = Qualquer(prestador, "RazaoSocial", "xNome", "NomeFantasia"),
            NumeroNota = numero.TrimStart('0') is { Length: > 0 } n ? n : "0",
            Serie = Qualquer(inf, "Serie", "serie"),
            DataEmissao = NfeXml.NormalizarData(Qualquer(inf, "DataEmissao", "dhEmi", "dhProc", "DataEmissaoNfse")),
            CnpjCpfDestinatario = SoDigitos(Qualquer(tomador, "Cnpj", "CNPJ", "Cpf", "CPF")) is { Length: > 0 } d ? d : null,
            NomeDestinatario = Qualquer(tomador, "RazaoSocial", "xNome", "Nome"),
            ValorTotal = NfeXml.ParseDecimal(Qualquer(inf, "ValorServicos", "ValorLiquidoNfse", "vServ", "vLiq", "vTotal")),
            Status = StatusNota.Baixado,
            // NFS-e é validada pela prefeitura; um XML emitido é tratado como "autorizado"
            SituacaoSefaz = Cancelada(doc) ? "101 - NFS-e cancelada" : "100 - NFS-e emitida",
            Origem = origem,
        };
    }

    private static bool Cancelada(XDocument doc) =>
        NfeXml.Primeiro(doc, "NfseCancelamento") != null || NfeXml.Primeiro(doc, "InfPedidoCancelamento") != null;

    private static string SoDigitos(string? s) => NfeXml.SoDigitos(s);

    private static string Sanitizar(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }
}
