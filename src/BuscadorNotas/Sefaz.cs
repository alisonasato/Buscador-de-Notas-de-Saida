using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;

namespace BuscadorNotas;

public record DocumentoDistribuido(string Nsu, string Schema, string Xml);

public record RetornoDistribuicao(string CStat, string XMotivo, string UltNsu, string MaxNsu, List<DocumentoDistribuido> Documentos);

public record RetornoConsulta(string CStat, string XMotivo, string XmlResposta, XDocument? NfeProc);

public static class SefazHttp
{
    public static HttpClient CriarClient(X509Certificate2 cert)
    {
        var handler = new HttpClientHandler();
        handler.ClientCertificates.Add(cert);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
    }

    public static Task<XDocument> EnviarSoapAsync(HttpClient http, string url, bool soap12,
        XNamespace nsWsdl, string? metodo, XElement dadosMsg, CancellationToken ct) =>
        EnviarSoapAsync(http, url, soap12, nsWsdl, metodo, "nfeDadosMsg", dadosMsg, ct);

    /// <param name="elementoDados">nfeDadosMsg (NF-e), cteDadosMsg (CT-e), mdfeDadosMsg (MDF-e)...</param>
    public static async Task<XDocument> EnviarSoapAsync(HttpClient http, string url, bool soap12,
        XNamespace nsWsdl, string? metodo, string elementoDados, XElement dadosMsg, CancellationToken ct)
    {
        XNamespace env = soap12
            ? "http://www.w3.org/2003/05/soap-envelope"
            : "http://schemas.xmlsoap.org/soap/envelope/";

        var envelope = new XDocument(
            new XElement(env + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soap", env.NamespaceName),
                new XElement(env + "Body",
                    metodo == null
                        ? new XElement(nsWsdl + elementoDados, dadosMsg)
                        : new XElement(nsWsdl + metodo,
                            new XElement(nsWsdl + elementoDados, dadosMsg)))));

        var conteudo = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8);
        if (soap12)
        {
            conteudo.Headers.ContentType = MediaTypeHeaderValue.Parse("application/soap+xml; charset=utf-8");
        }
        else
        {
            conteudo.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=utf-8");
            conteudo.Headers.Add("SOAPAction", $"\"{nsWsdl.NamespaceName}/{metodo ?? elementoDados}\"");
        }

        using var resp = await http.PostAsync(url, conteudo, ct);
        var texto = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} da Sefaz: {Resumir(texto)}");
        return XDocument.Parse(texto);
    }

    private static string Resumir(string s) => s.Length <= 300 ? s : s[..300] + "...";

    public static string? Valor(XContainer raiz, string nomeLocal) =>
        raiz.Descendants().FirstOrDefault(e => e.Name.LocalName == nomeLocal)?.Value.Trim();
}

/// <summary>Descreve um serviço de distribuição DF-e (NF-e, CT-e, MDF-e): só mudam URL, namespaces, operação e versão.</summary>
public record ServicoDistribuicao(string Chave, string Rotulo, string Url, string NsWsdl, string NsMsg, string Operacao, string ElementoDados, string Versao);

/// <remarks>
/// ATENÇÃO: os dados de CT-e e MDF-e (namespaces, operação, versão) são de MEMÓRIA, não confirmados em manual, e
/// nunca foram testados contra a Sefaz. As URLs podem ser trocadas no appsettings.json.
/// </remarks>
public static class ServicosDistribuicao
{
    public static ServicoDistribuicao Nfe(Configuracao cfg) => new("NFE", "NF-e", cfg.UrlDistribuicao,
        "http://www.portalfiscal.inf.br/nfe/wsdl/NFeDistribuicaoDFe", "http://www.portalfiscal.inf.br/nfe",
        "nfeDistDFeInteresse", "nfeDadosMsg", "1.01");

    public static ServicoDistribuicao Cte(Configuracao cfg) => new("CTE", "CT-e", cfg.UrlDistribuicaoCte,
        "http://www.portalfiscal.inf.br/cte/wsdl/CTeDistribuicaoDFe", "http://www.portalfiscal.inf.br/cte",
        "cteDistDFeInteresse", "cteDadosMsg", "1.00");

    public static ServicoDistribuicao Mdfe(Configuracao cfg) => new("MDFE", "MDF-e", cfg.UrlDistribuicaoMdfe,
        "http://www.portalfiscal.inf.br/mdfe/wsdl/MDFeDistribuicaoDFe", "http://www.portalfiscal.inf.br/mdfe",
        "mdfeDistDFeInteresse", "mdfeDadosMsg", "1.00");

    public static ServicoDistribuicao Por(string? chave, Configuracao cfg) => (chave ?? "NFE").ToUpperInvariant() switch
    {
        "CTE" => Cte(cfg),
        "MDFE" => Mdfe(cfg),
        _ => Nfe(cfg),
    };
}

/// <summary>Serviço de distribuição DF-e (Ambiente Nacional / SVRS): NFeDistribuicaoDFe por padrão; CT-e e MDF-e por <see cref="ServicoDistribuicao"/>.</summary>
public class SefazDistribuicao
{
    private readonly HttpClient _http;
    private readonly Configuracao _cfg;
    private readonly ServicoDistribuicao _servico;

    public SefazDistribuicao(HttpClient http, Configuracao cfg, ServicoDistribuicao? servico = null)
    {
        _http = http;
        _cfg = cfg;
        _servico = servico ?? ServicosDistribuicao.Nfe(cfg);
    }

    /// <summary>Último distDFeInt enviado (para diagnóstico quando a Sefaz rejeita).</summary>
    public string? UltimaRequisicao { get; private set; }

    /// <summary>distDFeInt: tpAmb, [cUFAutor], CNPJ, distNSU/ultNSU (nesta ordem). cUFAutor só entra se for uma UF válida.</summary>
    public static XElement MontarDistDFeInt(Configuracao cfg, string ultNsu, ServicoDistribuicao? servico = null) =>
        MontarDistDFeInt(cfg, ultNsu, servico, null);

    /// <param name="chave">Se informada, consulta essa chave (consChNFe) em vez de ler por NSU (distNSU).</param>
    public static XElement MontarDistDFeInt(Configuracao cfg, string ultNsu, ServicoDistribuicao? servico, string? chave)
    {
        servico ??= ServicosDistribuicao.Nfe(cfg);
        XNamespace ns = servico.NsMsg;
        var el = new XElement(ns + "distDFeInt",
            new XAttribute("versao", servico.Versao),
            new XElement(ns + "tpAmb", cfg.Ambiente));
        if (Configuracao.UfValida(cfg.CUFAutorEfetivo)) el.Add(new XElement(ns + "cUFAutor", cfg.CUFAutorEfetivo));
        el.Add(new XElement(ns + "CNPJ", cfg.Cnpj));
        el.Add(chave == null
            ? new XElement(ns + "distNSU", new XElement(ns + "ultNSU", ultNsu.PadLeft(15, '0')))
            : new XElement(ns + "consChNFe", new XElement(ns + "chNFe", chave)));
        return el;
    }

    public async Task<RetornoDistribuicao> ConsultarAsync(string ultNsu, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_servico.Url))
            throw new InvalidOperationException($"URL do serviço de distribuição de {_servico.Rotulo} não configurada (veja o Portal e preencha no appsettings.json).");
        var msg = MontarDistDFeInt(_cfg, ultNsu, _servico);
        UltimaRequisicao = msg.ToString(SaveOptions.DisableFormatting);
        XNamespace wsdl = _servico.NsWsdl;
        var resp = await SefazHttp.EnviarSoapAsync(_http, _servico.Url, _cfg.Soap12, wsdl,
            _servico.Operacao, _servico.ElementoDados, msg, ct);
        return InterpretarRetorno(resp);
    }

    /// <summary>EXPERIMENTAL: pede à distribuição o documento de uma chave (consChNFe). Layout de memória; não testado na Sefaz real.</summary>
    public async Task<RetornoDistribuicao> ConsultarPorChaveAsync(string chave, CancellationToken ct)
    {
        if (!NfeXml.ChaveValida(chave)) throw new ArgumentException("Chave de acesso inválida.", nameof(chave));
        var msg = MontarDistDFeInt(_cfg, "0", _servico, chave);
        UltimaRequisicao = msg.ToString(SaveOptions.DisableFormatting);
        XNamespace wsdl = _servico.NsWsdl;
        var resp = await SefazHttp.EnviarSoapAsync(_http, _servico.Url, _cfg.Soap12, wsdl,
            _servico.Operacao, _servico.ElementoDados, msg, ct);
        return InterpretarRetorno(resp);
    }

    public static RetornoDistribuicao InterpretarRetorno(XDocument resp)
    {
        var ret = resp.Descendants().FirstOrDefault(e => e.Name.LocalName == "retDistDFeInt")
                  ?? throw new InvalidDataException("Resposta sem retDistDFeInt.");

        var docs = new List<DocumentoDistribuido>();
        foreach (var dz in ret.Descendants().Where(e => e.Name.LocalName == "docZip"))
        {
            var xml = DescompactadorXml.ExtrairXmlDeDocZip(dz.Value.Trim());
            docs.Add(new DocumentoDistribuido(
                dz.Attribute("NSU")?.Value ?? "",
                dz.Attribute("schema")?.Value ?? "",
                xml));
        }

        return new RetornoDistribuicao(
            SefazHttp.Valor(ret, "cStat") ?? "",
            SefazHttp.Valor(ret, "xMotivo") ?? "",
            (SefazHttp.Valor(ret, "ultNSU") ?? "").PadLeft(15, '0'),   // NSU sempre com 15 dígitos: a comparação é textual
            (SefazHttp.Valor(ret, "maxNSU") ?? "").PadLeft(15, '0'),
            docs);
    }
}

/// <summary>Serviço NFeConsultaProtocolo4 (SEFAZ da UF emitente) — método nfeConsultaNF.</summary>
public class SefazConsultaProtocolo
{
    private static readonly XNamespace NsNfe = "http://www.portalfiscal.inf.br/nfe";
    private static readonly XNamespace NsWsdl = "http://www.portalfiscal.inf.br/nfe/wsdl/NFeConsultaProtocolo4";

    private readonly HttpClient _http;
    private readonly Configuracao _cfg;

    public SefazConsultaProtocolo(HttpClient http, Configuracao cfg)
    {
        _http = http;
        _cfg = cfg;
    }

    public static XElement MontarConsSitNFe(int ambiente, string chave) =>
        new(NsNfe + "consSitNFe",
            new XAttribute("versao", "4.00"),
            new XElement(NsNfe + "tpAmb", ambiente),
            new XElement(NsNfe + "xServ", "CONSULTAR"),
            new XElement(NsNfe + "chNFe", chave));

    public async Task<RetornoConsulta> ConsultarAsync(string chave, CancellationToken ct)
    {
        var cuf = chave[..2];
        if (!_cfg.UrlsConsultaProtocolo.TryGetValue(cuf, out var url) || string.IsNullOrWhiteSpace(url) || url.StartsWith("PREENCHER"))
            throw new InvalidOperationException($"Configure UrlsConsultaProtocolo[\"{cuf}\"] em appsettings.json.");

        var resp = await SefazHttp.EnviarSoapAsync(_http, url, _cfg.Soap12, NsWsdl, null,
            MontarConsSitNFe(_cfg.Ambiente, chave), ct);

        var ret = resp.Descendants().FirstOrDefault(e => e.Name.LocalName == "retConsSitNFe")
                  ?? throw new InvalidDataException("Resposta sem retConsSitNFe.");

        // Sem elemento-operação no corpo SOAP (confirme no WSDL da sua UF). A resposta só traz o XML completo (nfeProc) em alguns cenários; geralmente vem só o protocolo/situação.
        var nfeProc = ret.Descendants().FirstOrDefault(e => e.Name.LocalName == "nfeProc");
        return new RetornoConsulta(
            ret.Elements().FirstOrDefault(e => e.Name.LocalName == "cStat")?.Value.Trim() ?? "",
            ret.Elements().FirstOrDefault(e => e.Name.LocalName == "xMotivo")?.Value.Trim() ?? "",
            ret.ToString(SaveOptions.DisableFormatting),
            nfeProc == null ? null : new XDocument(nfeProc));
    }
}
