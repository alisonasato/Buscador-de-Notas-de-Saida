using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using BuscadorNotas;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BuscadorNotas.Tests;

/// <summary>Servidor SOAP falso (Kestrel em porta livre) que devolve respostas prontas e guarda os pedidos recebidos.</summary>
public sealed class SefazFalsa : IAsyncDisposable
{
    public Dictionary<string, string> Respostas { get; } = new();
    public Dictionary<string, string> Pedidos { get; } = new();
    private WebApplication _app = null!;
    public string Base { get; private set; } = "";

    public static string Resposta(string cStat, string motivo, string ult = "000000000000000", string max = "000000000000000", params (string nsu, string xml)[] docs)
    {
        static string Z(string xml)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionMode.Compress, true)) gz.Write(Encoding.UTF8.GetBytes(xml));
            return Convert.ToBase64String(ms.ToArray());
        }
        var dz = string.Concat(docs.Select(d => $"<docZip NSU=\"{d.nsu}\" schema=\"x\">{Z(d.xml)}</docZip>"));
        return $"<soap:Envelope xmlns:soap=\"http://www.w3.org/2003/05/soap-envelope\"><soap:Body><r xmlns=\"x\"><retDistDFeInt xmlns=\"http://www.portalfiscal.inf.br/x\"><cStat>{cStat}</cStat><xMotivo>{motivo}</xMotivo><ultNSU>{ult}</ultNSU><maxNSU>{max}</maxNSU><loteDistDFeInt>{dz}</loteDistDFeInt></retDistDFeInt></r></soap:Body></soap:Envelope>";
    }

    public async Task<SefazFalsa> IniciarAsync(params string[] caminhos)
    {
        var b = WebApplication.CreateBuilder();
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        b.WebHost.UseUrls("http://127.0.0.1:0");
        _app = b.Build();
        foreach (var c in caminhos)
            _app.MapPost("/" + c, async (HttpRequest req) =>
            {
                using var sr = new StreamReader(req.Body);
                Pedidos[c] = await sr.ReadToEndAsync();
                return Results.Content(Respostas.GetValueOrDefault(c) ?? Resposta("137", "nada"), "application/soap+xml");
            });
        await _app.StartAsync();
        Base = _app.Urls.First();
        return this;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

public class DistribuicaoExtraTestes
{
    private const string Eu = "11222333000181";
    private const string Outro = "99888777000166";
    private static string Dv(string b) => b + NfeXml.CalcularDv(b);
    private static string Chave(string mod, int n, string cnpj = Eu) => Dv($"352610{cnpj}{mod}001{n:D9}1{n:D8}");

    private static string Cte(string k, string emit) =>
        $"""<cteProc><CTe><infCte Id="CTe{k}"><ide><mod>57</mod><serie>1</serie><nCT>8</nCT><dhEmi>2026-10-06T08:00:00-03:00</dhEmi></ide><emit><CNPJ>{emit}</CNPJ></emit><dest><CNPJ>{Outro}</CNPJ><xNome>Cli</xNome></dest><vPrest><vTPrest>100.00</vTPrest></vPrest></infCte></CTe><protCTe><infProt><chCTe>{k}</chCTe><cStat>100</cStat></infProt></protCTe></cteProc>""";

    private static async Task<(ApiFixture f, SefazFalsa sefaz, Robo robo)> Montar(bool cte, bool mdfe, string urlMdfe = "")
    {
        var f = new ApiFixture();
        var sefaz = await new SefazFalsa().IniciarAsync("nfe", "cte", "mdfe");
        f.Cfg.UrlDistribuicao = sefaz.Base + "/nfe";
        f.Cfg.UrlDistribuicaoCte = sefaz.Base + "/cte";
        f.Cfg.UrlDistribuicaoMdfe = urlMdfe;
        f.Cfg.DistribuirCte = cte;
        f.Cfg.DistribuirMdfe = mdfe;
        f.Cfg.PausaEntreRequisicoesSegundos = 0;
        f.Cfg.CUFAutor = "35";
        return (f, sefaz, new Robo(f.Cfg, f.Repo));
    }

    [Fact]
    public void Pedido_de_cte_usa_namespace_e_versao_do_cte()
    {
        var cfg = new Configuracao { Cnpj = Eu, CUFAutorEfetivo = "35" };
        var x = SefazDistribuicao.MontarDistDFeInt(cfg, "9", ServicosDistribuicao.Cte(cfg));
        Assert.Equal("http://www.portalfiscal.inf.br/cte", x.Name.NamespaceName);
        Assert.Equal("1.00", (string?)x.Attribute("versao"));
        Assert.Equal(new[] { "tpAmb", "cUFAutor", "CNPJ", "distNSU" }, x.Elements().Select(e => e.Name.LocalName).ToArray());
        // NF-e continua igual ao que já existia
        var n = SefazDistribuicao.MontarDistDFeInt(cfg, "9");
        Assert.Equal(("http://www.portalfiscal.inf.br/nfe", "1.01"), (n.Name.NamespaceName, (string?)n.Attribute("versao")));
    }

    [Fact]
    public async Task Com_cte_ligado_busca_nfe_e_cte_importa_so_o_proprio_e_guarda_nsu_separado()
    {
        var (f, sefaz, robo) = await Montar(cte: true, mdfe: false);
        await using var _f = f; await using var _s = sefaz;
        var meu = Chave("57", 1); var alheio = Chave("57", 2, Outro);
        sefaz.Respostas["nfe"] = SefazFalsa.Resposta("137", "nada", "000000000000005", "000000000000005");
        sefaz.Respostas["cte"] = SefazFalsa.Resposta("138", "ok", "000000000000002", "000000000000002",
            ("000000000000001", Cte(meu, Eu)), ("000000000000002", Cte(alheio, Outro)));

        using var http = new HttpClient();
        var r = await robo.ExecutarCicloComAsync(http, null, default);

        Assert.Equal("137", r.CStat);
        Assert.Equal(1, r.NovasNotas);                                   // só o CT-e do próprio CNPJ
        Assert.Equal(TipoDoc.Cte, f.Repo.ObterNota(meu)!.Tipo);
        Assert.Null(f.Repo.ObterNota(alheio));
        Assert.Equal("000000000000005", f.Repo.ObterUltimoNsu(Eu));       // NSU da NF-e
        Assert.Equal("000000000000002", f.Repo.ObterUltimoNsu($"{Eu}:CTE")); // NSU do CT-e, separado

        // o pedido ao CT-e usa o formato do CT-e
        var pedido = XDocument.Parse(sefaz.Pedidos["cte"]);
        Assert.Contains(pedido.Descendants(), e => e.Name.LocalName == "cteDistDFeInteresse" && e.Name.NamespaceName.EndsWith("CTeDistribuicaoDFe"));
        Assert.Contains(pedido.Descendants(), e => e.Name.LocalName == "cteDadosMsg");
        Assert.DoesNotContain("nfeDadosMsg", sefaz.Pedidos["cte"]);
        Assert.Contains("nfeDadosMsg", sefaz.Pedidos["nfe"]);
    }

    [Fact]
    public async Task Erro_no_cte_nao_derruba_a_nfe_e_aparece_na_mensagem()
    {
        var (f, sefaz, robo) = await Montar(cte: true, mdfe: false);
        await using var _f = f; await using var _s = sefaz;
        sefaz.Respostas["nfe"] = SefazFalsa.Resposta("137", "nada");
        sefaz.Respostas["cte"] = SefazFalsa.Resposta("215", "Rejeicao: Falha no esquema XML");

        var r = await robo.ExecutarCicloComAsync(new HttpClient(), null, default);
        Assert.Equal("137", r.CStat);                       // resultado da NF-e preservado
        Assert.Contains("CT-e", r.Mensagem);
        Assert.Contains("215", r.Mensagem);
    }

    [Fact]
    public async Task Mdfe_ligado_sem_url_gera_aviso_claro_e_nao_tenta_chamar_nada()
    {
        var (f, sefaz, robo) = await Montar(cte: false, mdfe: true, urlMdfe: "");
        await using var _f = f; await using var _s = sefaz;
        var r = await robo.ExecutarCicloComAsync(new HttpClient(), null, default);
        Assert.Equal("137", r.CStat);
        Assert.Contains("MDF-e", r.Mensagem);
        Assert.Contains("URL", r.Mensagem);
        Assert.False(sefaz.Pedidos.ContainsKey("mdfe"));
    }

    [Fact]
    public async Task Desligados_por_padrao_so_a_nfe_e_consultada()
    {
        var (f, sefaz, robo) = await Montar(cte: false, mdfe: false);
        await using var _f = f; await using var _s = sefaz;
        await robo.ExecutarCicloComAsync(new HttpClient(), null, default);
        Assert.True(sefaz.Pedidos.ContainsKey("nfe"));
        Assert.False(sefaz.Pedidos.ContainsKey("cte"));
        Assert.False(new Configuracao().DistribuirCte || new Configuracao().DistribuirMdfe);
    }

    [Fact]
    public async Task Bloqueio_656_na_nfe_nao_consulta_os_outros_servicos()
    {
        var (f, sefaz, robo) = await Montar(cte: true, mdfe: false);
        await using var _f = f; await using var _s = sefaz;
        sefaz.Respostas["nfe"] = SefazFalsa.Resposta("656", "Consumo Indevido");
        var r = await robo.ExecutarCicloComAsync(new HttpClient(), null, default);
        Assert.Equal("656", r.CStat);
        Assert.False(sefaz.Pedidos.ContainsKey("cte"));
    }

    // ---------------- intervalo mínimo entre consultas (evita o 656 provocado por nós mesmos) ----------------

    [Fact]
    public async Task Depois_de_137_a_proxima_consulta_so_e_liberada_apos_o_intervalo_minimo()
    {
        var (f, sefaz, robo) = await Montar(cte: false, mdfe: false);
        await using var _f = f; await using var _s = sefaz;
        Assert.Null(robo.EsperaRestante());                                   // nunca consultou: livre

        sefaz.Respostas["nfe"] = SefazFalsa.Resposta("137", "nada");
        await robo.ExecutarCicloComAsync(new HttpClient(), null, default);

        var espera = robo.EsperaRestante()!.Value;
        Assert.InRange(espera.Restante.TotalMinutes, 59, 60);

        // a tela/agendador recusam em vez de arriscar o 656
        var sync = new SyncService(f.Cfg, f.Repo, robo);
        var motivo = sync.TentarIniciar("manual");
        Assert.Contains("liberada às", motivo);
        Assert.Single(sefaz.Pedidos);                                         // nenhuma consulta nova foi feita
        var status = System.Text.Json.JsonSerializer.SerializeToElement(sync.Status(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, status.GetProperty("liberadoEm").ValueKind);

        // passado o intervalo, libera
        Assert.Null(robo.EsperaRestante(DateTimeOffset.Now.AddMinutes(61)));
    }

    [Fact]
    public async Task Depois_de_656_a_espera_e_a_de_consumo_indevido()
    {
        var (f, sefaz, robo) = await Montar(cte: false, mdfe: false);
        await using var _f = f; await using var _s = sefaz;
        f.Repo.SalvarPref("ultimaConsulta", $"{DateTimeOffset.Now.AddMinutes(-61):o}|656");
        var espera = robo.EsperaRestante()!.Value;                            // 65 - 61 = 4 min
        Assert.InRange(espera.Restante.TotalMinutes, 3, 4.1);

        f.Repo.SalvarPref("ultimaConsulta", $"{DateTimeOffset.Now.AddMinutes(-61):o}|137");
        Assert.Null(robo.EsperaRestante());                                   // 137 há 61 min: já pode
    }

    [Fact]
    public async Task Erro_de_formato_ou_cancelamento_nao_inicia_espera()
    {
        var (f, sefaz, robo) = await Montar(cte: false, mdfe: false);
        await using var _f = f; await using var _s = sefaz;
        sefaz.Respostas["nfe"] = SefazFalsa.Resposta("215", "Rejeicao: Falha no esquema XML");
        await Assert.ThrowsAsync<InvalidOperationException>(() => robo.ExecutarCicloComAsync(new HttpClient(), null, default));
        Assert.Null(robo.EsperaRestante());                                   // rejeição de formato não conta como consulta bem-sucedida
    }
}
