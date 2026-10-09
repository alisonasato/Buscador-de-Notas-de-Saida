using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BuscadorNotas;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BuscadorNotas.Tests;

public class EntradaTestes
{
    private static string Dv(string b43) => b43 + NfeXml.CalcularDv(b43);
    private static string Chave(string cnpj, int n) => Dv($"352610{cnpj}55001{n:D9}1{n:D8}");

    private static string XmlNfe(string chave, string emitente, int n) =>
        $"""<nfeProc xmlns="http://www.portalfiscal.inf.br/nfe"><NFe><infNFe Id="NFe{chave}"><ide><serie>1</serie><nNF>{n}</nNF><dhEmi>2026-10-05T10:00:00-03:00</dhEmi></ide><emit><CNPJ>{emitente}</CNPJ></emit><dest><CNPJ>99888777000166</CNPJ><xNome>Cliente {n}</xNome></dest><total><ICMSTot><vNF>{n}.00</vNF></ICMSTot></total></infNFe></NFe><protNFe><infProt><chNFe>{chave}</chNFe><cStat>100</cStat></infProt></protNFe></nfeProc>""";

    private const string Eu = "11222333000181";

    private static async Task<(ApiFixture f, EntradaService svc, string entrada)> Montar(int estabilidade = 0)
    {
        var f = new ApiFixture();
        f.Cfg.PastaEntrada = Path.Combine(f.Dir, "entrada");
        f.Cfg.EntradaEstabilidadeSegundos = estabilidade;
        await f.IniciarAsync();
        return (f, f.App.Services.GetRequiredService<EntradaService>(), f.Cfg.PastaEntrada);
    }

    private static void Envelhecer(string arq) => File.SetLastWriteTimeUtc(arq, DateTime.UtcNow.AddMinutes(-5));

    [Fact]
    public async Task Importa_xml_zip_sped_lista_e_evento_e_move_os_arquivos()
    {
        var (f, svc, entrada) = await Montar();
        await using var _ = f;
        Directory.CreateDirectory(entrada);

        var c1 = Chave(Eu, 1); var c2 = Chave(Eu, 2); var c3 = Chave(Eu, 3); var c4 = Chave(Eu, 4); var c5 = Chave(Eu, 5);
        File.WriteAllText(Path.Combine(entrada, "n1.xml"), XmlNfe(c1, Eu, 1));

        // ZIP com 2 notas nossas, 1 de outro emitente e 1 XML quebrado
        using (var zip = ZipFile.Open(Path.Combine(entrada, "lote.zip"), ZipArchiveMode.Create))
        {
            void Add(string nome, string txt) { using var w = new StreamWriter(zip.CreateEntry(nome).Open()); w.Write(txt); }
            Add("a.xml", XmlNfe(c2, Eu, 2)); Add("sub/b.xml", XmlNfe(c3, Eu, 3));
            Add("outro.xml", XmlNfe(Chave("99888777000166", 9), "99888777000166", 9)); Add("quebrado.xml", "<nfeProc><x>");
        }

        // SPED com 1 saída (c4), lista de chaves com c5
        File.WriteAllText(Path.Combine(entrada, "sped.txt"),
            $"|0000|017|\n|C100|1|0||55|00|001|000000004|{c4}|05102026|05102026|10,00|\n", Encoding.Latin1);
        File.WriteAllText(Path.Combine(entrada, "chaves.csv"), $"{c5};x\n");

        // cancelamento da nota 1 (evento completo)
        File.WriteAllText(Path.Combine(entrada, "cancel.xml"),
            $"""<procEventoNFe xmlns="http://www.portalfiscal.inf.br/nfe"><evento><infEvento><chNFe>{c1}</chNFe><tpEvento>110111</tpEvento><nSeqEvento>1</nSeqEvento></infEvento></evento><retEvento><infEvento><cStat>135</cStat></infEvento></retEvento></procEventoNFe>""");

        File.WriteAllText(Path.Combine(entrada, "lixo.txt"), "isso nao e SPED nem tem chaves\n");
        File.WriteAllText(Path.Combine(entrada, "quebrado.xml"), "<a><b>");

        var tratados = await svc.VarrerAsync();
        Assert.Equal(7, tratados);

        // XML solto + zip + cancelamento
        Assert.Equal(StatusNota.Baixado, f.Repo.ObterNota(c1)!.Status);
        Assert.Equal("CANCELADA", Situacao.Classificar(f.Repo.ObterNota(c1)!.SituacaoSefaz)); // evento aplicado (arquivos em ordem qualquer: reaplicado)
        Assert.Equal(StatusNota.Baixado, f.Repo.ObterNota(c2)!.Status);
        Assert.Equal(StatusNota.Baixado, f.Repo.ObterNota(c3)!.Status);
        Assert.True(File.Exists(Path.Combine(f.Cfg.PastaXml, "2026", "10", c2 + ".xml")));
        Assert.Null(f.Repo.ObterNota(Chave("99888777000166", 9)));
        // SPED e lista viram pendentes
        Assert.Equal(StatusNota.Pendente, f.Repo.ObterNota(c4)!.Status);
        Assert.Equal(StatusNota.Pendente, f.Repo.ObterNota(c5)!.Status);

        // arquivos movidos, nada apagado
        Assert.Empty(Directory.GetFiles(entrada));
        Assert.Equal(5, Directory.GetFiles(Path.Combine(entrada, "processados"), "*", SearchOption.AllDirectories).Length);
        var rej = Directory.GetFiles(Path.Combine(entrada, "rejeitados"));
        Assert.Equal(4, rej.Length); // lixo.txt + quebrado.xml e seus .motivo.txt
        Assert.Contains("XML inválido", File.ReadAllText(Path.Combine(entrada, "rejeitados", "quebrado.xml.motivo.txt")));

        var log = f.Repo.UltimasImportacoes(20);
        Assert.Contains(log, l => l.Arquivo == "lote.zip" && l.Resultado == "IMPORTADO" && l.Detalhe!.Contains("2 novo(s)") && l.Detalhe.Contains("1 ignorado(s)") && l.Detalhe.Contains("1 inválido(s)"));
        Assert.Contains(log, l => l.Arquivo == "lixo.txt" && l.Resultado == "REJEITADO");
    }

    [Fact]
    public async Task Cancelamento_vale_mesmo_se_o_evento_for_processado_antes_da_nota()
    {
        var (f, svc, entrada) = await Montar();
        await using var _ = f;
        Directory.CreateDirectory(entrada);
        var c = Chave(Eu, 7);
        // nome do evento ordena antes da nota
        File.WriteAllText(Path.Combine(entrada, "a-evento.xml"),
            $"""<resEvento xmlns="http://www.portalfiscal.inf.br/nfe"><chNFe>{c}</chNFe><tpEvento>110111</tpEvento><nSeqEvento>1</nSeqEvento></resEvento>""");
        File.WriteAllText(Path.Combine(entrada, "z-nota.xml"), XmlNfe(c, Eu, 7));
        await svc.VarrerAsync();
        Assert.Equal("CANCELADA", Situacao.Classificar(f.Repo.ObterNota(c)!.SituacaoSefaz));
    }

    [Fact]
    public async Task Arquivo_recem_gravado_espera_e_reimportar_o_mesmo_nao_duplica()
    {
        var (f, svc, entrada) = await Montar(estabilidade: 5);
        await using var _ = f;
        Directory.CreateDirectory(entrada);
        var c = Chave(Eu, 8);
        var arq = Path.Combine(entrada, "n8.xml");
        File.WriteAllText(arq, XmlNfe(c, Eu, 8));

        Assert.Equal(0, await svc.VarrerAsync());       // ainda "quente"
        Assert.True(File.Exists(arq));

        Envelhecer(arq);
        Assert.Equal(1, await svc.VarrerAsync());
        Assert.False(File.Exists(arq));

        // o mesmo arquivo de novo (nome igual): não quebra, não duplica, vai para processados com nome livre
        File.WriteAllText(arq, XmlNfe(c, Eu, 8)); Envelhecer(arq);
        Assert.Equal(1, await svc.VarrerAsync());
        Assert.Equal(1, f.Repo.Contar(new FiltroBusca { Chave = c }));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(entrada, "processados"), "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task Pasta_de_entrada_nao_pode_ser_a_pasta_de_xmls()
    {
        var (f, svc, _) = await Montar();
        await using var _ = f;
        f.Cfg.PastaEntrada = f.Cfg.PastaXml;
        Assert.Equal(0, await svc.VarrerAsync());
        var st = JsonSerializer.SerializeToElement(svc.Status(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("não pode ser a pasta de XMLs", st.GetProperty("erro").GetString());
    }

    [Fact]
    public async Task Api_mostra_status_e_varre_sob_demanda()
    {
        var (f, _, entrada) = await Montar();
        await using var _ = f;
        Directory.CreateDirectory(entrada);
        var c = Chave(Eu, 11);
        File.WriteAllText(Path.Combine(entrada, "n.xml"), XmlNfe(c, Eu, 11));

        var antes = await f.Http.GetFromJsonAsync<JsonElement>("/api/entrada");
        Assert.True(antes.GetProperty("ativa").GetBoolean());
        Assert.Equal(1, antes.GetProperty("aguardando").GetInt32());

        var r = await f.Http.PostAsync("/api/entrada/varrer", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var depois = await f.Http.GetFromJsonAsync<JsonElement>("/api/entrada");
        Assert.Equal(0, depois.GetProperty("aguardando").GetInt32());
        Assert.Equal("n.xml", depois.GetProperty("recentes")[0].GetProperty("arquivo").GetString());
    }

    [Fact]
    public async Task Api_recusa_varredura_quando_a_pasta_nao_esta_configurada()
    {
        await using var f = new ApiFixture();
        await f.IniciarAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Http.PostAsync("/api/entrada/varrer", null)).StatusCode);
        Assert.False((await f.Http.GetFromJsonAsync<JsonElement>("/api/entrada")).GetProperty("ativa").GetBoolean());
    }
}
