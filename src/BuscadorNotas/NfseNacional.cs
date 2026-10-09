using System.Text.Json;

namespace BuscadorNotas;

public record RetornoNfse(int Http, string Status, string UltNsu, List<DocumentoDistribuido> Documentos, List<string> Erros);

/// <summary>
/// EXPERIMENTAL: distribuição de NFS-e do Ambiente de Dados Nacional (ADN, padrão nacional), por NSU, via HTTPS + certificado.
/// ATENÇÃO: não consegui acessar o manual oficial (o domínio gov.br está bloqueado neste ambiente). Só sei, por fonte secundária
/// (resultado de busca citando o manual de contribuintes), que existe um GET /DFe/{NSU}. O host, os nomes dos campos do JSON
/// e os códigos de status abaixo são de MEMÓRIA e de relatos em fóruns: confirme no manual/Swagger oficial antes de usar.
/// </summary>
public class NfseNacional
{
    private readonly HttpClient _http;
    private readonly Configuracao _cfg;

    public NfseNacional(HttpClient http, Configuracao cfg) { _http = http; _cfg = cfg; }

    public string UltimaUrl { get; private set; } = "";

    /// <summary>Monta a URL trocando {nsu} (aceita também {NSU}); sem o marcador, acrescenta "/{nsu}".</summary>
    public static string MontarUrl(string modelo, string nsu)
    {
        var n = nsu.TrimStart('0');
        if (n.Length == 0) n = "0";
        if (modelo.Contains("{nsu}", StringComparison.OrdinalIgnoreCase))
            return System.Text.RegularExpressions.Regex.Replace(modelo, @"\{nsu\}", n, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return modelo.TrimEnd('/') + "/" + n;
    }

    public async Task<RetornoNfse> ConsultarAsync(string nsuInicial, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_cfg.UrlDistribuicaoNfse))
            throw new InvalidOperationException("URL da distribuição de NFS-e não configurada (UrlDistribuicaoNfse; veja o manual do ADN no portal gov.br/nfse).");
        UltimaUrl = MontarUrl(_cfg.UrlDistribuicaoNfse, nsuInicial);
        using var resp = await _http.GetAsync(UltimaUrl, ct);
        var corpo = await resp.Content.ReadAsStringAsync(ct);
        return Interpretar((int)resp.StatusCode, corpo);
    }

    private static JsonElement? Prop(JsonElement el, params string[] nomes)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in el.EnumerateObject())
            foreach (var n in nomes)
                if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static string? Texto(JsonElement el, params string[] nomes) =>
        Prop(el, nomes) is { ValueKind: JsonValueKind.String or JsonValueKind.Number } v ? v.ToString() : null;

    /// <summary>Interpreta de forma tolerante (nomes sem diferenciar maiúsculas): lote de documentos, status e erros.</summary>
    public static RetornoNfse Interpretar(int http, string corpo)
    {
        var docs = new List<DocumentoDistribuido>();
        var erros = new List<string>();
        string status = "";
        var ultimo = "";

        if (!string.IsNullOrWhiteSpace(corpo))
        {
            JsonDocument json;
            try { json = JsonDocument.Parse(corpo); }
            catch (JsonException)
            {
                if (http is >= 200 and < 300) throw new InvalidDataException("A resposta do portal de NFS-e não é JSON.");
                return new RetornoNfse(http, "", "", docs, new List<string> { $"HTTP {http}" });
            }
            using (json)
            {
                var raiz = json.RootElement;
                status = Texto(raiz, "StatusProcessamento", "status") ?? "";
                if (Prop(raiz, "Erros", "erros") is { ValueKind: JsonValueKind.Array } arr)
                    foreach (var e in arr.EnumerateArray())
                        erros.Add(e.ValueKind == JsonValueKind.String ? e.GetString()! :
                            $"{Texto(e, "Codigo", "codigo")} {Texto(e, "Descricao", "descricao", "mensagem")}".Trim());

                if (Prop(raiz, "LoteDFe", "lote", "documentos") is { ValueKind: JsonValueKind.Array } lote)
                    foreach (var d in lote.EnumerateArray())
                    {
                        var nsu = (Texto(d, "NSU", "nsu") ?? "").PadLeft(15, '0');
                        var tipo = Texto(d, "TipoDocumento", "tipoDocumento", "tipo") ?? "NFSE";
                        var arquivo = Texto(d, "ArquivoXml", "arquivoXml", "xml");
                        if (string.IsNullOrEmpty(arquivo)) continue;
                        // base64 de GZip (como na distribuição da Sefaz); se vier XML puro, usa como está
                        var xml = arquivo.TrimStart().StartsWith('<') ? arquivo : DescompactadorXml.ExtrairXmlDeDocZip(arquivo);
                        docs.Add(new DocumentoDistribuido(nsu, tipo, xml));
                        if (string.CompareOrdinal(nsu, ultimo) > 0) ultimo = nsu;
                    }
            }
        }
        return new RetornoNfse(http, status, ultimo, docs, erros);
    }
}

public partial class Robo
{
    private const string PrefUltimaConsultaNfse = "ultimaConsultaNfse";

    public DateTimeOffset? NfseLiberadaEm(DateTimeOffset? agora = null)
    {
        if (!DateTimeOffset.TryParse(_repo.ObterPref(PrefUltimaConsultaNfse), out var ultima)) return null;
        var libera = ultima.AddMinutes(_cfg.IntervaloMinimoMinutos);
        return libera > (agora ?? DateTimeOffset.Now) ? libera : null;
    }

    /// <summary>
    /// EXPERIMENTAL: lê a distribuição de NFS-e do portal nacional a partir do último NSU salvo ("CNPJ:NFSE"), importando cada
    /// documento como o das demais fontes (saída se o prestador é você, entrada se o tomador é você).
    /// </summary>
    public async Task<(int Novas, string Mensagem)> ConsultarNfseAsync(HttpClient http, CancellationToken ct, int maxPaginas = 200)
    {
        if (NfseLiberadaEm() is { } lib)
            return (0, $"NFS-e: próxima consulta liberada às {lib.LocalDateTime:HH:mm}.");

        var cliente = new NfseNacional(http, _cfg);
        var chaveNsu = $"{_cfg.Cnpj}:NFSE";
        var ultNsu = _repo.ObterUltimoNsu(chaveNsu);
        int novas = 0, recebidos = 0, entradas = 0;
        string fim = "";

        for (var pagina = 1; pagina <= maxPaginas && !ct.IsCancellationRequested; pagina++)
        {
            // Pelo que sei, o parâmetro é o NSU a partir do qual ler: pede o seguinte ao último guardado (não confirmado no manual).
            var proximo = (long.TryParse(ultNsu, out var u) ? u + 1 : 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Console.WriteLine($"Consultando NFS-e (portal nacional) a partir do NSU {proximo}...");
            var ret = await cliente.ConsultarAsync(proximo, ct);
            Console.WriteLine($"  HTTP {ret.Http} status={ret.Status} docs={ret.Documentos.Count}");

            if (ret.Http == 429)
            {
                _repo.SalvarPref(PrefUltimaConsultaNfse, DateTimeOffset.Now.ToString("o"));
                return (novas, "NFS-e: o portal pediu para aguardar (HTTP 429); nova consulta só após o intervalo mínimo.");
            }
            if (ret.Documentos.Count == 0)
            {
                // "nenhum documento" (em alguns serviços vem como 404 com corpo) é o fim da fila; outra coisa é erro
                var nenhum = ret.Status.Contains("NENHUM", StringComparison.OrdinalIgnoreCase) || ret.Http == 404 && ret.Erros.Count == 0 || ret.Http is >= 200 and < 300 && ret.Erros.Count == 0;
                if (!nenhum)
                    throw new InvalidOperationException($"Portal de NFS-e respondeu HTTP {ret.Http}: {string.Join("; ", ret.Erros)}".TrimEnd(' ', ':'));
                fim = "fim da fila";
                break;
            }

            foreach (var d in ret.Documentos)
            {
                recebidos++;
                var r = ImportarXmlBytes(System.Text.Encoding.UTF8.GetBytes(d.Xml), incluirOutrosCnpjs: false);
                if (r.Desfecho == Desfecho.Importado) { if (r.Entrada) entradas++; else if (r.Novo) novas++; }
            }
            if (string.CompareOrdinal(ret.UltNsu, ultNsu) <= 0) { fim = "o portal não avançou o NSU"; break; }
            ultNsu = ret.UltNsu;
            _repo.SalvarUltimoNsu(chaveNsu, ultNsu);
            await Task.Delay(TimeSpan.FromSeconds(_cfg.PausaEntreRequisicoesSegundos), ct);
        }

        if (!ct.IsCancellationRequested) _repo.SalvarPref(PrefUltimaConsultaNfse, DateTimeOffset.Now.ToString("o"));
        return (novas, $"NFS-e: {recebidos} documento(s) recebido(s), {novas} saída(s) nova(s), {entradas} entrada(s)" + (fim.Length > 0 ? $" ({fim})." : "."));
    }
}
