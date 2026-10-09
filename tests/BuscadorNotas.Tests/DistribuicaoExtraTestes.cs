using System.Net.Http.Json;
using System.Text.Json;
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
    /// <summary>Se definido, monta a resposta a partir do corpo do pedido (caminho, corpo).</summary>
    public Func<string, string, string>? Dinamica { get; set; }
    public int TotalPedidos;
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
                var corpo = await sr.ReadToEndAsync();
                Pedidos[c] = corpo;
                Interlocked.Increment(ref TotalPedidos);
                if (Dinamica != null) return Results.Content(Dinamica(c, corpo), "application/soap+xml");
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
        var espera = robo.EsperaRestante()!.Value;                            // 120 - 61 = 59 min
        Assert.InRange(espera.Restante.TotalMinutes, 58, 59.1);

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

    // ---------------- o que a Sefaz entregou (explica "0 notas novas") ----------------

    private static string XmlNfeDoTeste(string k, string emit, string dest) =>
        $"""<nfeProc xmlns="http://www.portalfiscal.inf.br/nfe"><NFe><infNFe Id="NFe{k}"><ide><mod>55</mod><serie>1</serie><nNF>1</nNF><dhEmi>2026-10-05T10:00:00-03:00</dhEmi></ide><emit><CNPJ>{emit}</CNPJ></emit><dest><CNPJ>{dest}</CNPJ><xNome>X</xNome></dest><total><ICMSTot><vNF>10.00</vNF></ICMSTot></total></infNFe></NFe><protNFe><infProt><chNFe>{k}</chNFe><cStat>100</cStat></infProt></protNFe></nfeProc>""";

    [Fact]
    public async Task Resultado_explica_o_que_a_sefaz_entregou_por_papel_do_cnpj()
    {
        var (f, sefaz, robo) = await Montar(cte: false, mdfe: false);
        await using var _f = f; await using var _s = sefaz;
        var terceiro = "12345678000195";
        var kSaida = Chave("55", 1); var kEntrada = Chave("55", 2, terceiro); var kAlheia = Chave("55", 3, terceiro); var kResumo = Chave("55", 4, terceiro);

        sefaz.Respostas["nfe"] = SefazFalsa.Resposta("138", "Documento localizado", "000000000000005", "000000000000005",
            ("000000000000001", XmlNfeDoTeste(kSaida, Eu, Outro)),                      // emitida por mim
            ("000000000000002", XmlNfeDoTeste(kEntrada, terceiro, Eu)),                 // sou o destinatário
            ("000000000000003", XmlNfeDoTeste(kAlheia, terceiro, Outro)),               // nem emitente nem destinatário
            ("000000000000004", $"""<resNFe xmlns="http://www.portalfiscal.inf.br/nfe"><chNFe>{kResumo}</chNFe><CNPJ>{terceiro}</CNPJ><xNome>T</xNome><vNF>5.00</vNF><dhEmi>2026-10-05T10:00:00-03:00</dhEmi><cSitNFe>1</cSitNFe></resNFe>"""),
            ("000000000000005", $"""<procEventoNFe xmlns="http://www.portalfiscal.inf.br/nfe"><evento><infEvento><chNFe>{kSaida}</chNFe><tpEvento>110110</tpEvento><nSeqEvento>1</nSeqEvento></infEvento></evento></procEventoNFe>"""));

        var r = await robo.ExecutarCicloComAsync(new HttpClient(), null, default);

        Assert.Equal("138", r.CStat);
        Assert.Equal(1, r.NovasNotas);
        Assert.Contains("5 documento(s) recebido(s)", r.Mensagem);
        Assert.Contains("1 emitido(s) por você", r.Mensagem);
        Assert.Contains("1 de entrada guardada(s)", r.Mensagem);
        Assert.Contains("1 resumo(s) de notas de terceiros", r.Mensagem);
        Assert.Contains("1 evento(s)", r.Mensagem);
        Assert.Contains("1 outro(s)", r.Mensagem);
        Assert.Equal(1, f.Repo.Contar(new FiltroBusca { Direcao = Direcao.Saida }));
        Assert.Equal(2, f.Repo.Contar(new FiltroBusca { Direcao = Direcao.Entrada }));   // nota completa + resumo de terceiro
        Assert.Equal(3, f.Repo.Contar(new FiltroBusca()));                                // a nota "nem emitente nem destinatário" não é guardada
    }

    [Fact]
    public void Texto_da_estatistica_omite_zeros_e_fica_vazio_sem_documentos()
    {
        Assert.Equal("", new EstatisticaCiclo().Texto());
        var e = new EstatisticaCiclo { Recebidos = 8955, ComoDestinatario = 8955 };
        Assert.Equal("8955 documento(s) recebido(s): 8955 em que você é destinatário (entrada, não guardada)", e.Texto());
    }

    [Fact]
    public async Task Entradas_ficam_separadas_das_saidas_e_fora_do_painel()
    {
        var (f, sefaz, robo) = await Montar(cte: false, mdfe: false);
        await using var _f = f; await using var _s = sefaz;
        var terceiro = "12345678000195";
        var kSaida = Chave("55", 1); var kEntrada = Chave("55", 2, terceiro);
        sefaz.Respostas["nfe"] = SefazFalsa.Resposta("138", "Documento localizado", "000000000000003", "000000000000003",
            ("000000000000001", XmlNfeDoTeste(kSaida, Eu, Outro)),
            ("000000000000002", XmlNfeDoTeste(kEntrada, terceiro, Eu)),
            ("000000000000003", $"""<procEventoNFe xmlns="http://www.portalfiscal.inf.br/nfe"><evento><infEvento><chNFe>{kEntrada}</chNFe><tpEvento>110111</tpEvento><nSeqEvento>1</nSeqEvento></infEvento></evento><retEvento><infEvento><cStat>135</cStat></infEvento></retEvento></procEventoNFe>"""));

        var r = await robo.ExecutarCicloComAsync(new HttpClient(), null, default);

        Assert.Equal(1, r.NovasNotas);                                          // entrada nunca conta como saída nova
        var ent = f.Repo.ObterNota(kEntrada)!;
        Assert.Equal(Direcao.Entrada, ent.Direcao);
        Assert.Equal(StatusNota.Baixado, ent.Status);
        Assert.Equal(Situacao.Cancelada, Situacao.Classificar(ent.SituacaoSefaz));
        Assert.Equal(Direcao.Saida, f.Repo.ObterNota(kSaida)!.Direcao);

        var resumo = f.Repo.ObterResumo("2026-10");
        Assert.Equal(1, resumo.Total);                                          // painel só conta saídas
        Assert.Equal(1, resumo.TotalEntradas);
        Assert.Equal(1, resumo.NotasNoMes);

        await f.IniciarAsync();                                                 // mesma base de dados
        // API: sem parâmetro só saídas; direcao=ENTRADA e TODAS
        var padrao = await f.Http.GetFromJsonAsync<JsonElement>("/api/notas");
        Assert.Equal(1, padrao.GetProperty("total").GetInt32());
        var entradas = await f.Http.GetFromJsonAsync<JsonElement>("/api/notas?direcao=ENTRADA");
        Assert.Equal(1, entradas.GetProperty("total").GetInt32());
        Assert.Equal("ENTRADA", entradas.GetProperty("itens")[0].GetProperty("direcao").GetString());
        var todas = await f.Http.GetFromJsonAsync<JsonElement>("/api/notas?direcao=TODAS");
        Assert.Equal(2, todas.GetProperty("total").GetInt32());
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await f.Http.GetAsync("/api/notas?direcao=XYZ")).StatusCode);
    }

    [Fact]
    public async Task Com_guardar_entradas_desligado_so_as_saidas_sao_guardadas()
    {
        var (f, sefaz, robo) = await Montar(cte: false, mdfe: false);
        await using var _f = f; await using var _s = sefaz;
        f.Cfg.GuardarEntradas = false;
        var terceiro = "12345678000195";
        var kEntrada = Chave("55", 2, terceiro);
        sefaz.Respostas["nfe"] = SefazFalsa.Resposta("138", "Documento localizado", "000000000000001", "000000000000001",
            ("000000000000001", XmlNfeDoTeste(kEntrada, terceiro, Eu)));

        await robo.ExecutarCicloComAsync(new HttpClient(), null, default);

        Assert.Null(f.Repo.ObterNota(kEntrada));
    }

    [Fact]
    public async Task Importacao_de_xml_guarda_entrada_e_banco_antigo_e_migrado()
    {
        var (f, sefaz, robo) = await Montar(cte: false, mdfe: false);
        await using var _f = f; await using var _s = sefaz;
        var terceiro = "12345678000195";
        var k = Chave("55", 7, terceiro);
        var r = robo.ImportarXmlBytes(System.Text.Encoding.UTF8.GetBytes(XmlNfeDoTeste(k, terceiro, Eu)), incluirOutrosCnpjs: false);
        Assert.Equal(Desfecho.Importado, r.Desfecho);
        Assert.Equal(Direcao.Entrada, f.Repo.ObterNota(k)!.Direcao);

        var kAlheia = Chave("55", 8, terceiro);
        var r2 = robo.ImportarXmlBytes(System.Text.Encoding.UTF8.GetBytes(XmlNfeDoTeste(kAlheia, terceiro, Outro)), incluirOutrosCnpjs: false);
        Assert.Equal(Desfecho.Ignorado, r2.Desfecho);

        // banco criado antes das colunas Direcao/NomeEmitente
        var antigo = Path.Combine(f.Dir, "antigo.db");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={antigo}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE NotasSaidaBaixadas (ChaveAcesso TEXT PRIMARY KEY, CnpjEmitente TEXT, NumeroNota TEXT, Serie TEXT, DataEmissao TEXT, CnpjCpfDestinatario TEXT, NomeDestinatario TEXT, ValorTotal REAL, CaminhoXmlLocal TEXT, Status TEXT NOT NULL, SituacaoSefaz TEXT, Origem TEXT, Tentativas INTEGER NOT NULL DEFAULT 0, UltimoErro TEXT, Tipo TEXT NOT NULL DEFAULT 'NFE'); INSERT INTO NotasSaidaBaixadas (ChaveAcesso, Status) VALUES ('" + k + "', 'BAIXADO');";
            cmd.ExecuteNonQuery();
        }
        var migrado = new Repositorio(antigo);
        Assert.Equal(Direcao.Saida, migrado.ObterNota(k)!.Direcao);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    // ---------------- busca de chaves pendentes (consChNFe) ----------------

    [Fact]
    public async Task Busca_por_chave_obtem_o_xml_marca_indisponivel_e_respeita_o_intervalo()
    {
        var (f, sefaz, robo) = await Montar(cte: false, mdfe: false);
        await using var _f = f; await using var _s = sefaz;
        var kOk = Chave("55", 1); var kNao = Chave("55", 2);
        foreach (var k in new[] { kOk, kNao })
            f.Repo.InserirSeNaoExiste(new NotaSaida { ChaveAcesso = k, CnpjEmitente = Eu, NumeroNota = "1", Serie = "1", Status = StatusNota.Pendente, Origem = "SPED" });

        sefaz.Dinamica = (_, corpo) => corpo.Contains(kOk)
            ? SefazFalsa.Resposta("138", "Documento localizado", "000000000000000", "000000000000000", ("000000000000009", XmlNfeDoTeste(kOk, Eu, Outro)))
            : SefazFalsa.Resposta("137", "Nenhum documento localizado");

        var r = await robo.BuscarPendentesPorChaveAsync(new HttpClient(), 20, forcar: false, default);

        Assert.Equal(2, r.Consultadas); Assert.Equal(1, r.Obtidas); Assert.Equal(1, r.Indisponiveis);
        Assert.Contains("<consChNFe", sefaz.Pedidos["nfe"]);
        Assert.Equal(StatusNota.Baixado, f.Repo.ObterNota(kOk)!.Status);
        Assert.True(File.Exists(f.Repo.ObterNota(kOk)!.CaminhoXmlLocal));
        Assert.Equal(StatusNota.Indisponivel, f.Repo.ObterNota(kNao)!.Status);

        // nova busca logo em seguida é recusada sem tocar na Sefaz
        var antes = sefaz.TotalPedidos;
        f.Repo.InserirSeNaoExiste(new NotaSaida { ChaveAcesso = Chave("55", 3), CnpjEmitente = Eu, NumeroNota = "3", Status = StatusNota.Pendente, Origem = "SPED" });
        var r2 = await robo.BuscarPendentesPorChaveAsync(new HttpClient(), 20, forcar: false, default);
        Assert.Equal(0, r2.Consultadas);
        Assert.Equal(antes, sefaz.TotalPedidos);
        Assert.Contains("liberada", r2.Mensagem);
        Assert.Equal(1, (await robo.BuscarPendentesPorChaveAsync(new HttpClient(), 20, forcar: true, default)).Consultadas);
    }

    [Fact]
    public async Task Busca_por_chave_para_ao_receber_656_e_bloqueia_novas_consultas()
    {
        var (f, sefaz, robo) = await Montar(cte: false, mdfe: false);
        await using var _f = f; await using var _s = sefaz;
        for (int i = 1; i <= 3; i++)
            f.Repo.InserirSeNaoExiste(new NotaSaida { ChaveAcesso = Chave("55", i), CnpjEmitente = Eu, NumeroNota = i.ToString(), Status = StatusNota.Pendente, Origem = "SPED" });
        sefaz.Dinamica = (_, _) => SefazFalsa.Resposta("656", "Consumo Indevido");

        var r = await robo.BuscarPendentesPorChaveAsync(new HttpClient(), 20, forcar: false, default);

        Assert.Equal(1, r.Consultadas);                       // parou na primeira
        Assert.Contains("656", r.Mensagem);
        Assert.NotNull(robo.EsperaRestante());                // bloqueio compartilhado com a sincronização
        var r2 = await robo.BuscarPendentesPorChaveAsync(new HttpClient(), 20, forcar: true, default);
        Assert.Equal(0, r2.Consultadas);                      // nem --forcar ignora o 656
        Assert.Equal(1, sefaz.TotalPedidos);
    }

    [Fact]
    public async Task Api_de_pendentes_informa_contagens_e_valida_a_configuracao()
    {
        await using var f = await new ApiFixture().IniciarAsync();
        f.Repo.InserirSeNaoExiste(new NotaSaida { ChaveAcesso = Chave("55", 1), CnpjEmitente = Eu, NumeroNota = "1", Status = StatusNota.Pendente, Origem = "SPED" });
        var estado = await f.Http.GetFromJsonAsync<JsonElement>("/api/pendentes");
        Assert.Equal(1, estado.GetProperty("pendentes").GetInt32());
        Assert.Equal(20, estado.GetProperty("maximoPorBusca").GetInt32());
        // sem certificado configurado a busca é recusada com mensagem, sem erro 500
        var resp = await f.Http.PostAsync("/api/pendentes/buscar", null);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private static string Chave(string mod, int n) => Chave(mod, n, Eu);

    [Fact]
    public async Task Trava_entre_processos_impede_duas_consultas_simultaneas()
    {
        var (f, sefaz, robo) = await Montar(cte: false, mdfe: false);
        await using var _f = f; await using var _s = sefaz;
        using (var primeira = robo.AdquirirTravaConsulta())
        {
            var ex = Assert.Throws<InvalidOperationException>(() => robo.AdquirirTravaConsulta());   // "outro processo"
            Assert.Contains("já está consultando", ex.Message);
        }
        using var depois = robo.AdquirirTravaConsulta();                                              // liberada ao fechar
    }
}
