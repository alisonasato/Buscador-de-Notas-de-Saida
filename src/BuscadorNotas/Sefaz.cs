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

    public static async Task<XDocument> EnviarSoapAsync(HttpClient http, string url, bool soap12,
        XNamespace nsWsdl, string? metodo, XElement dadosMsg, CancellationToken ct)
    {
        XNamespace env = soap12
            ? "http://www.w3.org/2003/05/soap-envelope"
            : "http://schemas.xmlsoap.org/soap/envelope/";

        var envelope = new XDocument(
            new XElement(env + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soap", env.NamespaceName),
                new XElement(env + "Body",
                    metodo == null
                        ? new XElement(nsWsdl + "nfeDadosMsg", dadosMsg)
                        : new XElement(nsWsdl + metodo,
                            new XElement(nsWsdl + "nfeDadosMsg", dadosMsg)))));

        var conteudo = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8);
        if (soap12)
        {
            conteudo.Headers.ContentType = MediaTypeHeaderValue.Parse("application/soap+xml; charset=utf-8");
        }
        else
        {
            conteudo.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=utf-8");
            conteudo.Headers.Add("SOAPAction", $"\"{nsWsdl.NamespaceName}/{metodo ?? "nfeDadosMsg"}\"");
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

/// <summary>Serviço NFeDistribuicaoDFe (Ambiente Nacional) — método nfeDistDFeInteresse.</summary>
public class SefazDistribuicao
{
    private static readonly XNamespace NsNfe = "http://www.portalfiscal.inf.br/nfe";
    private static readonly XNamespace NsWsdl = "http://www.portalfiscal.inf.br/nfe/wsdl/NFeDistribuicaoDFe";

    private readonly HttpClient _http;
    private readonly Configuracao _cfg;

    public SefazDistribuicao(HttpClient http, Configuracao cfg)
    {
        _http = http;
        _cfg = cfg;
    }

    public static XElement MontarDistDFeInt(Configuracao cfg, string ultNsu) =>
        new(NsNfe + "distDFeInt",
            new XAttribute("versao", "1.01"),
            new XElement(NsNfe + "tpAmb", cfg.Ambiente),
            new XElement(NsNfe + "cUFAutor", cfg.CUFAutor),
            new XElement(NsNfe + "CNPJ", cfg.Cnpj),
            new XElement(NsNfe + "distNSU",
                new XElement(NsNfe + "ultNSU", ultNsu.PadLeft(15, '0'))));

    public async Task<RetornoDistribuicao> ConsultarAsync(string ultNsu, CancellationToken ct)
    {
        var resp = await SefazHttp.EnviarSoapAsync(_http, _cfg.UrlDistribuicao, _cfg.Soap12, NsWsdl,
            "nfeDistDFeInteresse", MontarDistDFeInt(_cfg, ultNsu), ct);
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
            SefazHttp.Valor(ret, "ultNSU") ?? "000000000000000",
            SefazHttp.Valor(ret, "maxNSU") ?? "000000000000000",
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
