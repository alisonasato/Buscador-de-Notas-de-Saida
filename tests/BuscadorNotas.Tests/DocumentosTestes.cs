using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using BuscadorNotas;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BuscadorNotas.Tests;

public class DocumentosTestes
{
    private const string Eu = "11222333000181";
    private const string Outro = "99888777000166";

    private static string Chave(string modelo, int n, string cnpj = Eu) =>
        Dv($"352610{cnpj}{modelo}001{n:D9}1{n:D8}");
    private static string Dv(string b43) => b43 + NfeXml.CalcularDv(b43);

    // XMLs de exemplo escritos de memória (layouts não confirmados com arquivos reais)
    private static string Cte(string k, string emit = Eu) =>
        $"""<cteProc xmlns="http://www.portalfiscal.inf.br/cte" versao="3.00"><CTe><infCte Id="CTe{k}" versao="3.00"><ide><mod>57</mod><serie>1</serie><nCT>77</nCT><dhEmi>2026-10-06T08:00:00-03:00</dhEmi></ide><emit><CNPJ>{emit}</CNPJ><xNome>Transportadora</xNome></emit><rem><CNPJ>12345678000195</CNPJ><xNome>Remetente</xNome></rem><dest><CNPJ>{Outro}</CNPJ><xNome>Cliente CTe</xNome></dest><vPrest><vTPrest>350.25</vTPrest></vPrest></infCte></CTe><protCTe><infProt><chCTe>{k}</chCTe><cStat>100</cStat></infProt></protCTe></cteProc>""";

    private static string Mdfe(string k) =>
        $"""<mdfeProc xmlns="http://www.portalfiscal.inf.br/mdfe" versao="3.00"><MDFe><infMDFe Id="MDFe{k}" versao="3.00"><ide><mod>58</mod><serie>1</serie><nMDF>5</nMDF><dhEmi>2026-10-07T07:00:00-03:00</dhEmi></ide><emit><CNPJ>{Eu}</CNPJ><xNome>Transportadora</xNome></emit><tot><qCTe>2</qCTe><vCarga>15000.00</vCarga></tot></infMDFe></MDFe><protMDFe><infProt><chMDFe>{k}</chMDFe><cStat>100</cStat></infProt></protMDFe></mdfeProc>""";

    private static string Cfe(string k) =>
        $"""<CFe><infCFe Id="CFe{k}" versao="0.07"><ide><mod>59</mod><nserieSAT>900000001</nserieSAT><nCFe>000123</nCFe><dEmi>20261007</dEmi><hEmi>101530</hEmi></ide><emit><CNPJ>{Eu}</CNPJ><xNome>Loja</xNome></emit><dest><CPF>12345678909</CPF></dest><total><vCFe>45.90</vCFe></total></infCFe></CFe>""";

    private const string Nfse =
        """<CompNfse xmlns="http://www.abrasf.org.br/nfse.xsd"><Nfse><InfNfse><Numero>000045</Numero><CodigoVerificacao>ABC123</CodigoVerificacao><DataEmissao>2026-10-08T09:00:00</DataEmissao><Servico><Valores><ValorServicos>1200.00</ValorServicos><ValorLiquidoNfse>1100.00</ValorLiquidoNfse></Valores></Servico><PrestadorServico><IdentificacaoPrestador><Cnpj>11222333000181</Cnpj></IdentificacaoPrestador><RazaoSocial>Minha Empresa</RazaoSocial></PrestadorServico><TomadorServico><IdentificacaoTomador><CpfCnpj><Cnpj>99888777000166</Cnpj></CpfCnpj></IdentificacaoTomador><RazaoSocial>Tomador SA</RazaoSocial></TomadorServico><OrgaoGerador><CodigoMunicipio>3550308</CodigoMunicipio></OrgaoGerador></InfNfse></Nfse></CompNfse>""";

    private const string NfseId = "NFSE-11222333000181-3550308-000045";

    private static string Nfe(string k, int n, string mod = "55") =>
        $"""<nfeProc xmlns="http://www.portalfiscal.inf.br/nfe"><NFe><infNFe Id="NFe{k}"><ide><mod>{mod}</mod><serie>1</serie><nNF>{n}</nNF><dhEmi>2026-10-05T10:00:00-03:00</dhEmi></ide><emit><CNPJ>{Eu}</CNPJ></emit><dest><CNPJ>{Outro}</CNPJ><xNome>Cliente</xNome></dest><total><ICMSTot><vNF>100.00</vNF></ICMSTot></total></infNFe></NFe><protNFe><infProt><chNFe>{k}</chNFe><cStat>100</cStat></infProt></protNFe></nfeProc>""";

    private static byte[] B(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    private static async Task<ApiFixture> Fixture()
    {
        var f = new ApiFixture();
        return await f.IniciarAsync();
    }

    [Fact]
    public void Leitores_extraem_os_campos_de_cada_tipo()
    {
        var kc = Chave("57", 1); var km = Chave("58", 2); var kf = Chave("59", 3);

        var cte = DocumentosFiscais.Ler(XDocument.Parse(Cte(kc)), "T")!;
        Assert.Equal((TipoDoc.Cte, kc, "77", "1", "2026-10-06T08:00:00"), (cte.Tipo, cte.ChaveAcesso, cte.NumeroNota, cte.Serie, cte.DataEmissao));
        Assert.Equal((Eu, Outro, "Cliente CTe", 350.25m, "100"), (cte.CnpjEmitente, cte.CnpjCpfDestinatario, cte.NomeDestinatario, cte.ValorTotal, cte.SituacaoSefaz));

        var mdfe = DocumentosFiscais.Ler(XDocument.Parse(Mdfe(km)), "T")!;
        Assert.Equal((TipoDoc.Mdfe, "5", 15000m, null), (mdfe.Tipo, mdfe.NumeroNota, mdfe.ValorTotal, mdfe.NomeDestinatario));

        var cfe = DocumentosFiscais.Ler(XDocument.Parse(Cfe(kf)), "T")!;
        Assert.Equal((TipoDoc.Cfe, kf, "123", "900000001", "2026-10-07T10:15:30", 45.90m), (cfe.Tipo, cfe.ChaveAcesso, cfe.NumeroNota, cfe.Serie, cfe.DataEmissao, cfe.ValorTotal));
        Assert.Equal("12345678909", cfe.CnpjCpfDestinatario);

        var nfse = DocumentosFiscais.Ler(XDocument.Parse(Nfse), "T")!;
        Assert.Equal((TipoDoc.Nfse, NfseId, "45", Eu, "Tomador SA", 1200m), (nfse.Tipo, nfse.ChaveAcesso, nfse.NumeroNota, nfse.CnpjEmitente, nfse.NomeDestinatario, nfse.ValorTotal));
        Assert.Equal(Outro, nfse.CnpjCpfDestinatario);

        // NFC-e usa o layout da NF-e, mas o tipo vem do modelo
        var nfce = DocumentosFiscais.Ler(XDocument.Parse(Nfe(Chave("65", 4), 4, "65")), "T")!;
        Assert.Equal(TipoDoc.Nfce, nfce.Tipo);
        Assert.Equal(TipoDoc.Nfe, DocumentosFiscais.Ler(XDocument.Parse(Nfe(Chave("55", 5), 5)), "T")!.Tipo);

        Assert.Null(DocumentosFiscais.Identificar(XDocument.Parse("<qualquer/>")));
    }

    [Fact]
    public void NfseId_e_chave_sao_ids_validos_e_o_resto_nao()
    {
        Assert.True(NfeXml.IdValido(NfseId));
        Assert.True(NfeXml.IdValido(Chave("55", 1)));
        Assert.False(NfeXml.IdValido("NFSE-../../etc/passwd"));
        Assert.False(NfeXml.IdValido("NFSE-a/b"));
        Assert.False(NfeXml.IdValido("../x"));
        Assert.False(NfeXml.IdValido(""));
    }

    [Fact]
    public async Task Importa_cada_tipo_guarda_o_xml_no_lugar_certo_e_filtra_outro_emitente()
    {
        await using var f = await Fixture();
        var robo = new Robo(f.Cfg, f.Repo);
        var kc = Chave("57", 1); var km = Chave("58", 2); var kf = Chave("59", 3); var kn = Chave("55", 4);

        foreach (var xml in new[] { Cte(kc), Mdfe(km), Cfe(kf), Nfse, Nfe(kn, 4) })
            Assert.Equal(Desfecho.Importado, robo.ImportarXmlBytes(B(xml), false).Desfecho);

        // emitente de outro CNPJ é ignorado, e documento sem identificador é rejeitado
        Assert.Equal(Desfecho.Ignorado, robo.ImportarXmlBytes(B(Cte(Chave("57", 9, Outro), Outro)), false).Desfecho);
        Assert.Equal(Desfecho.Rejeitado, robo.ImportarXmlBytes(B("<CompNfse><Nfse><InfNfse><Valor>1</Valor></InfNfse></Nfse></CompNfse>"), false).Desfecho);
        Assert.Equal(Desfecho.Ignorado, robo.ImportarXmlBytes(B("<html/>"), false).Desfecho);

        Assert.Equal(TipoDoc.Cte, f.Repo.ObterNota(kc)!.Tipo);
        Assert.True(File.Exists(Path.Combine(f.Cfg.PastaXml, "cte", "2026", "10", kc + ".xml")));
        Assert.True(File.Exists(Path.Combine(f.Cfg.PastaXml, "mdfe", "2026", "10", km + ".xml")));
        Assert.True(File.Exists(Path.Combine(f.Cfg.PastaXml, "cfe", "2026", "10", kf + ".xml")));
        Assert.True(File.Exists(Path.Combine(f.Cfg.PastaXml, "nfse", "2026", "10", NfseId + ".xml")));
        Assert.True(File.Exists(Path.Combine(f.Cfg.PastaXml, "2026", "10", kn + ".xml"))); // NF-e: caminho antigo, inalterado
        Assert.Equal(TipoDoc.Nfse, f.Repo.ObterNota(NfseId)!.Tipo);

        // reimportar não duplica
        Assert.False(robo.ImportarXmlBytes(B(Cte(kc)), false).Novo);
        Assert.Equal(5, f.Repo.Contar(new FiltroBusca()));
    }

    [Fact]
    public async Task Cancelamento_respeita_o_tipo_de_documento()
    {
        await using var f = await Fixture();
        var robo = new Robo(f.Cfg, f.Repo);
        var kc = Chave("57", 1); var km = Chave("58", 2); var kf = Chave("59", 3); var kn = Chave("55", 4);
        foreach (var xml in new[] { Cte(kc), Mdfe(km), Cfe(kf), Nfe(kn, 4) }) robo.ImportarXmlBytes(B(xml), false);

        string Evento(string raiz, string chaveTag, string chave, string tipo) =>
            $"""<{raiz} xmlns="http://www.portalfiscal.inf.br/x"><evento><infEvento><{chaveTag}>{chave}</{chaveTag}><tpEvento>{tipo}</tpEvento><nSeqEvento>1</nSeqEvento></infEvento></evento><retEvento><infEvento><cStat>135</cStat></infEvento></retEvento></{raiz}>""";
        string Sit(string k) => Situacao.Classificar(f.Repo.ObterNota(k)!.SituacaoSefaz);

        // MDF-e: 110112 é ENCERRAMENTO e não pode cancelar
        robo.ImportarXmlBytes(B(Evento("procEventoMDFe", "chMDFe", km, "110112")), false);
        Assert.Equal("AUTORIZADA", Sit(km));
        robo.ImportarXmlBytes(B(Evento("procEventoMDFe", "chMDFe", km, "110111")), false);
        Assert.Equal("CANCELADA", Sit(km));

        // CT-e: 110111 cancela; 110112 não existe como cancelamento
        robo.ImportarXmlBytes(B(Evento("procEventoCTe", "chCTe", kc, "110112")), false);
        Assert.Equal("AUTORIZADA", Sit(kc));
        robo.ImportarXmlBytes(B(Evento("procEventoCTe", "chCTe", kc, "110111")), false);
        Assert.Equal("CANCELADA", Sit(kc));

        // NF-e: 110112 (cancelamento por substituição) cancela
        robo.ImportarXmlBytes(B(Evento("procEventoNFe", "chNFe", kn, "110112")), false);
        Assert.Equal("CANCELADA", Sit(kn));

        // CF-e SAT: documento CFeCanc aponta o original em chCanc
        var kcan = Chave("59", 33);
        robo.ImportarXmlBytes(B($"""<CFeCanc><infCFe Id="CFe{kcan}" chCanc="CFe{kf}"><ide><mod>59</mod></ide><emit><CNPJ>{Eu}</CNPJ></emit></infCFe></CFeCanc>"""), false);
        Assert.Equal("CANCELADA", Sit(kf));
    }

    [Fact]
    public async Task Api_filtra_por_tipo_e_o_dashboard_nao_soma_mdfe_no_faturamento()
    {
        await using var f = await Fixture();
        var robo = new Robo(f.Cfg, f.Repo);
        foreach (var xml in new[] { Cte(Chave("57", 1)), Mdfe(Chave("58", 2)), Cfe(Chave("59", 3)), Nfse, Nfe(Chave("55", 4), 4) })
            robo.ImportarXmlBytes(B(xml), false);

        var cte = await f.Http.GetFromJsonAsync<JsonElement>("/api/notas?tipo=CTE");
        Assert.Equal(1, cte.GetProperty("total").GetInt32());
        Assert.Equal("CTE", cte.GetProperty("itens")[0].GetProperty("tipo").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Http.GetAsync("/api/notas?tipo=XYZ")).StatusCode);
        Assert.Equal(5, (await f.Http.GetFromJsonAsync<JsonElement>("/api/notas")).GetProperty("total").GetInt32());

        var d = (await f.Http.GetFromJsonAsync<JsonElement>("/api/dashboard?mes=2026-10")).GetProperty("resumo");
        Assert.Equal(4, d.GetProperty("notasNoMes").GetInt32());                 // sem o MDF-e
        Assert.Equal(350.25m + 45.90m + 1200m + 100m, d.GetProperty("valorNoMes").GetDecimal()); // sem os 15.000 da carga
        var porTipo = d.GetProperty("porTipo").EnumerateArray().ToDictionary(x => x.GetProperty("tipo").GetString()!, x => x.GetProperty("quantidade").GetInt32());
        Assert.Equal(5, porTipo.Count);
        Assert.Equal(1, porTipo["MDFE"]);

        var csv = await f.Http.GetStringAsync("/api/notas/export.csv");
        Assert.Contains("Tipo;Numero", csv);
        Assert.Contains("\"CT-e\";", csv);
        Assert.Contains("\"NFS-e\";", csv);
    }

    [Fact]
    public async Task Api_baixa_xml_de_nfse_pelo_id_e_recusa_ids_perigosos()
    {
        await using var f = await Fixture();
        new Robo(f.Cfg, f.Repo).ImportarXmlBytes(B(Nfse), false);

        var r = await f.Http.GetAsync($"/api/notas/{NfseId}/xml");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("CompNfse", await r.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Http.GetAsync("/api/notas/NFSE-a..b%2F..%2Fx/xml")).StatusCode);

        var zip = await f.Http.PostAsJsonAsync("/api/notas/zip", new { chaves = new[] { NfseId } });
        Assert.Equal(HttpStatusCode.OK, zip.StatusCode);
        using var arq = new ZipArchive(await zip.Content.ReadAsStreamAsync());
        Assert.Equal(NfseId + ".xml", Assert.Single(arq.Entries).Name);
    }

    [Fact]
    public void Banco_antigo_sem_coluna_tipo_e_migrado_sem_perder_dados()
    {
        var db = Path.Combine(Path.GetTempPath(), $"antigo-{Guid.NewGuid():N}.db");
        try
        {
            using (var c = new SqliteConnection($"Data Source={db}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE NotasSaidaBaixadas (ChaveAcesso TEXT PRIMARY KEY, CnpjEmitente TEXT, NumeroNota TEXT, Serie TEXT, DataEmissao TEXT,
                        CnpjCpfDestinatario TEXT, NomeDestinatario TEXT, ValorTotal REAL, CaminhoXmlLocal TEXT, Status TEXT NOT NULL DEFAULT 'PENDENTE',
                        SituacaoSefaz TEXT, Origem TEXT, Tentativas INTEGER NOT NULL DEFAULT 0, UltimoErro TEXT);
                    INSERT INTO NotasSaidaBaixadas (ChaveAcesso, NumeroNota, Status) VALUES ('35261011222333000181550010000000011000000012', '1', 'BAIXADO');
                    """;
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            var repo = new Repositorio(db);   // migra
            var nota = repo.ObterNota("35261011222333000181550010000000011000000012")!;
            Assert.Equal(("1", TipoDoc.Nfe), (nota.NumeroNota, nota.Tipo));
            _ = new Repositorio(db);          // abrir de novo não falha (migração idempotente)
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(db); }
    }

    [Fact]
    public async Task Pasta_de_entrada_importa_zip_com_documentos_de_varios_tipos()
    {
        await using var f = new ApiFixture();
        f.Cfg.PastaEntrada = Path.Combine(f.Dir, "entrada");
        f.Cfg.EntradaEstabilidadeSegundos = 0;
        await f.IniciarAsync();
        Directory.CreateDirectory(f.Cfg.PastaEntrada);
        using (var zip = ZipFile.Open(Path.Combine(f.Cfg.PastaEntrada, "mes.zip"), ZipArchiveMode.Create))
        {
            void Add(string nome, string txt) { using var w = new StreamWriter(zip.CreateEntry(nome).Open()); w.Write(txt); }
            Add("cte.xml", Cte(Chave("57", 1))); Add("mdfe.xml", Mdfe(Chave("58", 2))); Add("nfse.xml", Nfse); Add("sat.xml", Cfe(Chave("59", 3)));
        }
        var svc = f.App.Services.GetService(typeof(EntradaService)) as EntradaService;
        Assert.Equal(1, await svc!.VarrerAsync());
        Assert.Equal(4, f.Repo.Contar(new FiltroBusca()));
        Assert.Contains(f.Repo.UltimasImportacoes(5), l => l.Detalhe!.Contains("4 novo(s)"));
    }
}
