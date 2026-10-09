using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using BuscadorNotas;
using Xunit;

namespace BuscadorNotas.Tests;

public class Testes
{
    // Chave fictícia: UF 35, 2410, CNPJ 11222333000181, mod 55, série 001, nNF 000000123
    private const string Chave = "35241011222333000181550010000001231000000019";

    private const string NfeProcXml = """
        <nfeProc xmlns="http://www.portalfiscal.inf.br/nfe" versao="4.00">
          <NFe><infNFe Id="NFe35241011222333000181550010000001231000000019" versao="4.00">
            <ide><serie>1</serie><nNF>123</nNF><dhEmi>2024-10-15T10:30:00-03:00</dhEmi></ide>
            <emit><CNPJ>11222333000181</CNPJ><xNome>Empresa</xNome></emit>
            <dest><CNPJ>99888777000166</CNPJ><xNome>Cliente Teste</xNome></dest>
            <total><ICMSTot><vNF>1234.56</vNF></ICMSTot></total>
          </infNFe></NFe>
          <protNFe><infProt><chNFe>35241011222333000181550010000001231000000019</chNFe><cStat>100</cStat></infProt></protNFe>
        </nfeProc>
        """;

    [Fact]
    public void ChaveValida_exige_44_digitos()
    {
        Assert.True(NfeXml.ChaveValida(Chave));
        Assert.False(NfeXml.ChaveValida("../../etc/passwd"));
        Assert.False(NfeXml.ChaveValida(Chave + "0"));
    }

    [Fact]
    public void LerNotaCompleta_extrai_campos()
    {
        var n = NfeXml.LerNotaCompleta(XDocument.Parse(NfeProcXml), "NSU")!;
        Assert.Equal(Chave, n.ChaveAcesso);
        Assert.Equal("11222333000181", n.CnpjEmitente);
        Assert.Equal("123", n.NumeroNota);
        Assert.Equal("2024-10-15T10:30:00", n.DataEmissao);
        Assert.Equal("Cliente Teste", n.NomeDestinatario);
        Assert.Equal(1234.56m, n.ValorTotal);
        Assert.Equal("100", n.SituacaoSefaz);
    }

    [Fact]
    public void Chave_decomposicao()
    {
        Assert.Equal("11222333000181", NfeXml.CnpjDaChave(Chave));
        Assert.Equal("123", NfeXml.NumeroDaChave(Chave));
        Assert.Equal("1", NfeXml.SerieDaChave(Chave));
    }

    [Fact]
    public void DocZip_roundtrip()
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionMode.Compress, true))
            gz.Write(Encoding.UTF8.GetBytes("<a>ação</a>"));
        Assert.Equal("<a>ação</a>", DescompactadorXml.ExtrairXmlDeDocZip(Convert.ToBase64String(ms.ToArray())));
    }

    [Fact]
    public void Sped_le_apenas_saidas_com_chave()
    {
        var sped = string.Join("\n",
            "|0000|017|0|01012024|31012024|EMPRESA|11222333000181|",
            $"|C100|1|0||55|00|001|000000123|{Chave}|15102024|15102024|1234,56|",
            $"|C100|0|1|F1|55|00|001|000000124|{Chave[..43]}8|15102024|15102024|10,00|",
            "|C100|1|0||55|00|001|000000125||15102024|15102024|10,00|");
        var r = SpedParser.LerSaidas(new StringReader(sped)).ToList();
        var nota = Assert.Single(r);
        Assert.Equal(Chave, nota.Chave);
        Assert.Equal(1234.56m, nota.Valor);
        Assert.Equal("2024-10-15T00:00:00", nota.DataDoc);
    }

    [Fact]
    public void Repositorio_busca_e_nao_rebaixa_nota_completa()
    {
        var db = Path.Combine(Path.GetTempPath(), $"notas-{Guid.NewGuid():N}.db");
        try
        {
            var repo = new Repositorio(db);
            Assert.True(repo.InserirSeNaoExiste(new NotaSaida { ChaveAcesso = Chave, NumeroNota = "123", Status = StatusNota.Pendente, DataEmissao = "2024-10-15T00:00:00" }));
            repo.SalvarNotaCompleta(NfeXml.LerNotaCompleta(XDocument.Parse(NfeProcXml), "NSU")!);
            Assert.False(repo.InserirSeNaoExiste(new NotaSaida { ChaveAcesso = Chave, Status = StatusNota.Pendente }));

            var achou = Assert.Single(repo.Buscar(new FiltroBusca { Numero = "123", Ate = "2024-10-15", Destinatario = "cliente" }));
            Assert.Equal(StatusNota.Baixado, achou.Status);
            Assert.Empty(repo.Buscar(new FiltroBusca { De = "2024-10-16" }));
            Assert.Empty(repo.ListarChavesPendentes(10));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(db); }
    }

    [Fact]
    public void Envelope_distribuicao_tem_ultNSU_com_15_digitos()
    {
        var cfg = new Configuracao { Cnpj = "11222333000181" };
        var x = SefazDistribuicao.MontarDistDFeInt(cfg, "42");
        Assert.Equal("000000000000042", x.Descendants().First(e => e.Name.LocalName == "ultNSU").Value);
    }

    private static string ChaveComDv(string chave43) => chave43 + NfeXml.CalcularDv(chave43);

    [Fact]
    public void DigitoVerificador_confere()
    {
        var boa = ChaveComDv("3524101122233300018155001000000123100000001");
        Assert.True(NfeXml.DigitoVerificadorOk(boa));
        var ruim = boa[..43] + ((boa[43] - '0' + 1) % 10);
        Assert.False(NfeXml.DigitoVerificadorOk(ruim));
    }

    [Fact]
    public void ExtratorChaves_acha_em_csv_e_no_formato_do_danfe()
    {
        var boa = ChaveComDv("3524101122233300018155001000000123100000001");
        var agrupada = string.Join(" ", Enumerable.Range(0, 11).Select(i => boa.Substring(i * 4, 4)));
        var r = ExtratorChaves.Extrair(new[] { $"123;15/10/2024;{boa};100,00", agrupada, "sem chave aqui 12345" }).ToList();
        Assert.Equal(new[] { boa, boa }, r);
    }

    [Fact]
    public void ImportarChaves_e_ImportarXmls_integracao()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"imp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var db = Path.Combine(dir, "t.db");
        try
        {
            var cfg = new Configuracao { Cnpj = "11222333000181", PastaXml = Path.Combine(dir, "saida") };
            var repo = new Repositorio(db);
            var robo = new Robo(cfg, repo);

            var boa = ChaveComDv("3524101122233300018155001000000123100000001");
            var outroCnpj = ChaveComDv("3524109999999900019955001000000124100000001");
            var lista = Path.Combine(dir, "chaves.csv");
            File.WriteAllLines(lista, new[] { boa, outroCnpj, boa[..43] + "X" });
            var r1 = robo.ImportarChaves(lista, false);
            Assert.Equal(1, r1.Novos);
            Assert.Equal(1, r1.Ignorados);

            var pastaXml = Path.Combine(dir, "xmls");
            Directory.CreateDirectory(pastaXml);
            File.WriteAllText(Path.Combine(pastaXml, "a.xml"), NfeProcXml);
            File.WriteAllText(Path.Combine(pastaXml, "lixo.xml"), "<outra/>");
            var r2 = robo.ImportarXmls(pastaXml, false);
            Assert.Equal(1, r2.Novos);
            Assert.Equal(1, r2.Ignorados);
            Assert.True(File.Exists(Path.Combine(dir, "saida", "2024", "10", Chave + ".xml")));
            Assert.Equal(StatusNota.Baixado, Assert.Single(repo.Buscar(new FiltroBusca { Chave = Chave })).Status);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }
}
