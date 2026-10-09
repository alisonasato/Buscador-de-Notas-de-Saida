using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using BuscadorNotas;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace BuscadorNotas.Tests;

public sealed class ApiFixture : IAsyncDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"api-{Guid.NewGuid():N}");
    public Configuracao Cfg { get; }
    public Repositorio Repo { get; }
    public Armazenamento Storage { get; }
    public WebApplication App { get; private set; } = null!;
    public HttpClient Http { get; private set; } = null!;
    public string Base { get; private set; } = "";

    public ApiFixture(string token = "")
    {
        Directory.CreateDirectory(Dir);
        Cfg = new Configuracao
        {
            Cnpj = "11222333000181",
            PastaXml = Path.Combine(Dir, "xmls"),
            BancoSqlite = Path.Combine(Dir, "t.db"),
            ApiToken = token,
        };
        Repo = new Repositorio(Cfg.BancoSqlite);
        Storage = new Armazenamento(Cfg.PastaXml);
    }

    public async Task<ApiFixture> IniciarAsync()
    {
        App = ApiServer.Construir(Cfg, Repo, new Robo(Cfg, Repo), "http://127.0.0.1:0");
        await App.StartAsync();
        Base = App.Urls.First();
        Http = new HttpClient { BaseAddress = new Uri(Base) };
        return this;
    }

    private static string Dv(string b) => b + NfeXml.CalcularDv(b);

    /// <summary>Cria uma nota; com <paramref name="comXml"/> grava o arquivo e marca BAIXADO.</summary>
    public NotaSaida Nota(int n, string nome, string doc, string data, decimal valor, string sit, bool comXml)
    {
        var chave = Dv($"352410" + "11222333000181" + "55001" + n.ToString("D9") + "1" + n.ToString("D8"));
        var nota = new NotaSaida
        {
            ChaveAcesso = chave, CnpjEmitente = Cfg.Cnpj, NumeroNota = n.ToString(), Serie = "1", DataEmissao = data + "T10:00:00",
            NomeDestinatario = nome, CnpjCpfDestinatario = doc, ValorTotal = valor, SituacaoSefaz = sit, Origem = "TESTE",
            Status = comXml ? StatusNota.Baixado : StatusNota.Pendente,
        };
        if (comXml) nota.CaminhoXmlLocal = Storage.Salvar(chave, $"<nfeProc><x>{n}</x></nfeProc>");
        if (comXml) Repo.SalvarNotaCompleta(nota); else Repo.InserirSeNaoExiste(nota);
        return nota;
    }

    public async ValueTask DisposeAsync()
    {
        Http?.Dispose();
        if (App != null) await App.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}

public class ApiTestes
{
    private static async Task<ApiFixture> Semeada()
    {
        var f = new ApiFixture();
        f.Nota(1, "Comercial Alfa Ltda", "99888777000166", "2026-10-02", 100m, "100", true);
        f.Nota(2, "Distribuidora Beta", "12345678000195", "2026-10-05", 200m, "100", false);
        f.Nota(3, "Mercado 100% Central", "45678912000134", "2026-10-07", 300m, "101", true);   // cancelada
        f.Nota(4, "Alfa Norte", "99888777000166", "2026-09-20", 50m, "110", true);               // denegada, mês anterior
        f.Nota(5, "=HYPERLINK(\"http://x\")", "11111111111", "2026-10-08", 10m, "SPED COD_SIT=00", false);
        return await f.IniciarAsync();
    }

    [Fact]
    public void Situacao_classifica_cStat_e_sped()
    {
        Assert.Equal("AUTORIZADA", Situacao.Classificar("100"));
        Assert.Equal("AUTORIZADA", Situacao.Classificar("100 - Autorizado o uso da NF-e"));
        Assert.Equal("CANCELADA", Situacao.Classificar("101"));
        Assert.Equal("CANCELADA", Situacao.Classificar("SPED COD_SIT=02"));
        Assert.Equal("DENEGADA", Situacao.Classificar("110"));
        Assert.Equal("AUTORIZADA", Situacao.Classificar("SPED COD_SIT=00"));
        Assert.Equal("DESCONHECIDA", Situacao.Classificar(null));
        Assert.Equal("DESCONHECIDA", Situacao.Classificar("217 - nao consta"));
    }

    [Fact]
    public async Task Lista_pagina_com_total_e_filtros()
    {
        await using var f = await Semeada();
        var p1 = await f.Http.GetFromJsonAsync<JsonElement>("/api/notas?tamanho=2&pagina=1");
        Assert.Equal(5, p1.GetProperty("total").GetInt32());
        Assert.Equal(3, p1.GetProperty("totalPaginas").GetInt32());
        Assert.Equal(2, p1.GetProperty("itens").GetArrayLength());
        Assert.Equal("5", p1.GetProperty("itens")[0].GetProperty("numero").GetString()); // mais recente primeiro

        var p3 = await f.Http.GetFromJsonAsync<JsonElement>("/api/notas?tamanho=2&pagina=3");
        Assert.Equal(1, p3.GetProperty("itens").GetArrayLength());
        var alem = await f.Http.GetFromJsonAsync<JsonElement>("/api/notas?tamanho=2&pagina=99");
        Assert.Equal(3, alem.GetProperty("pagina").GetInt32()); // página fora do intervalo vai para a última

        Assert.Equal(1, (await f.Http.GetFromJsonAsync<JsonElement>("/api/notas?situacao=CANCELADA")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await f.Http.GetFromJsonAsync<JsonElement>("/api/notas?situacao=DENEGADA")).GetProperty("total").GetInt32());
        Assert.Equal(2, (await f.Http.GetFromJsonAsync<JsonElement>("/api/notas?xml=PENDENTE")).GetProperty("total").GetInt32());
        Assert.Equal(3, (await f.Http.GetFromJsonAsync<JsonElement>("/api/notas?xml=BAIXADO")).GetProperty("total").GetInt32());
        Assert.Equal(4, (await f.Http.GetFromJsonAsync<JsonElement>("/api/notas?de=2026-10-01&ate=2026-10-31")).GetProperty("total").GetInt32());
        // CNPJ com pontuação e nome, sem diferenciar maiúsculas
        Assert.Equal(2, (await f.Http.GetFromJsonAsync<JsonElement>("/api/notas?busca=99.888.777")).GetProperty("total").GetInt32());
        Assert.Equal(2, (await f.Http.GetFromJsonAsync<JsonElement>("/api/notas?busca=alfa")).GetProperty("total").GetInt32());
        // '%' digitado pelo usuário é literal, não curinga
        Assert.Equal(1, (await f.Http.GetFromJsonAsync<JsonElement>("/api/notas?busca=100%25")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Parametros_invalidos_retornam_400()
    {
        await using var f = await Semeada();
        foreach (var q in new[] { "de=01/10/2026", "situacao=XYZ", "xml=TALVEZ" })
            Assert.Equal(HttpStatusCode.BadRequest, (await f.Http.GetAsync("/api/notas?" + q)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Http.GetAsync("/api/dashboard?mes=2026-13")).StatusCode);
    }

    [Fact]
    public async Task Detalhe_e_download_de_xml()
    {
        await using var f = await Semeada();
        var chave = f.Repo.Buscar(new FiltroBusca { Numero = "1" })[0].ChaveAcesso;
        var det = await f.Http.GetFromJsonAsync<JsonElement>($"/api/notas/{chave}");
        Assert.Equal("BAIXADO", det.GetProperty("xml").GetString());
        Assert.False(det.TryGetProperty("caminhoXmlLocal", out _)); // não vaza caminho do servidor

        var xml = await f.Http.GetAsync($"/api/notas/{chave}/xml");
        Assert.Equal(HttpStatusCode.OK, xml.StatusCode);
        Assert.Contains("<x>1</x>", await xml.Content.ReadAsStringAsync());

        var pendente = f.Repo.Buscar(new FiltroBusca { Numero = "2" })[0].ChaveAcesso;
        Assert.Equal(HttpStatusCode.NotFound, (await f.Http.GetAsync($"/api/notas/{pendente}/xml")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Http.GetAsync("/api/notas/abc/xml")).StatusCode);
    }

    [Fact]
    public async Task Download_nao_sai_da_pasta_de_xmls()
    {
        await using var f = await Semeada();
        var segredo = Path.Combine(f.Dir, "segredo.txt");
        File.WriteAllText(segredo, "SEGREDO");
        var n = f.Repo.Buscar(new FiltroBusca { Numero = "1" })[0];
        n.CaminhoXmlLocal = segredo; // banco adulterado apontando para fora de PastaXml
        f.Repo.SalvarNotaCompleta(n);

        var r = await f.Http.GetAsync($"/api/notas/{n.ChaveAcesso}/xml");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.DoesNotContain("SEGREDO", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Zip_inclui_so_notas_com_xml_e_informa_as_demais()
    {
        await using var f = await Semeada();
        var todas = f.Repo.Buscar(new FiltroBusca { Limite = 10 }).Select(n => n.ChaveAcesso).ToList();
        var r = await f.Http.PostAsJsonAsync("/api/notas/zip", new { chaves = todas });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("3", r.Headers.GetValues("X-Incluidas").Single());
        Assert.Equal("2", r.Headers.GetValues("X-Sem-Xml").Single());
        using var zip = new System.IO.Compression.ZipArchive(await r.Content.ReadAsStreamAsync());
        Assert.Equal(3, zip.Entries.Count);

        var vazio = await f.Http.PostAsJsonAsync("/api/notas/zip", new { chaves = new[] { "x" } });
        Assert.Equal(HttpStatusCode.BadRequest, vazio.StatusCode);
    }

    [Fact]
    public async Task Csv_exporta_resultado_filtrado_e_neutraliza_formulas()
    {
        await using var f = await Semeada();
        var csv = await f.Http.GetStringAsync("/api/notas/export.csv?de=2026-10-01");
        var linhas = csv.TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, linhas.Length); // cabeçalho + 4 de outubro
        Assert.Contains("\"'=HYPERLINK(", csv);
        Assert.DoesNotContain(";\"=HYPERLINK", csv);
        Assert.Contains(";300,00;", csv); // decimal pt-BR
    }

    [Fact]
    public async Task Dashboard_soma_o_mes_sem_canceladas()
    {
        await using var f = await Semeada();
        var d = await f.Http.GetFromJsonAsync<JsonElement>("/api/dashboard?mes=2026-10");
        var r = d.GetProperty("resumo");
        Assert.Equal(3, r.GetProperty("notasNoMes").GetInt32());          // 1, 2 e 5 (a 3 está cancelada)
        Assert.Equal(310m, r.GetProperty("valorNoMes").GetDecimal());
        Assert.Equal(3, r.GetProperty("xmlBaixados").GetInt32());
        Assert.Equal(2, r.GetProperty("xmlPendentes").GetInt32());
        Assert.Equal("idle", d.GetProperty("sync").GetProperty("estado").GetString());
    }

    [Fact]
    public async Task Config_valida_intervalo_e_persiste()
    {
        await using var f = await Semeada();
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Http.PutAsJsonAsync("/api/config", new { intervaloMinutos = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Http.PutAsJsonAsync("/api/config", new { ambiente = 3 })).StatusCode);

        var ok = await f.Http.PutAsJsonAsync("/api/config", new { intervaloMinutos = 120, automatica = true, ambiente = 2 });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("120", f.Repo.ObterPref("intervaloMinutos"));

        var outra = new Configuracao { BancoSqlite = f.Cfg.BancoSqlite };
        outra.AplicarPreferencias(f.Repo);
        Assert.Equal(120, outra.EsperaSemNovosMinutos);
        Assert.True(outra.SincronizacaoAutomatica);
        Assert.Equal(2, outra.Ambiente);
    }

    [Fact]
    public async Task Seguranca_host_origem_e_token()
    {
        await using var f = await Semeada();

        var rebinding = new HttpRequestMessage(HttpMethod.Get, "/api/notas") { Headers = { Host = "evil.example.com" } };
        Assert.Equal((HttpStatusCode)421, (await f.Http.SendAsync(rebinding)).StatusCode);

        var csrf = new HttpRequestMessage(HttpMethod.Post, "/api/sync/cancel") { Headers = { { "Origin", "http://evil.example.com" } } };
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Http.SendAsync(csrf)).StatusCode);

        await using var t = new ApiFixture("segredo123");
        await t.IniciarAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await t.Http.GetAsync("/api/notas")).StatusCode);
        t.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "errado");
        Assert.Equal(HttpStatusCode.Unauthorized, (await t.Http.GetAsync("/api/notas")).StatusCode);
        t.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "segredo123");
        Assert.Equal(HttpStatusCode.OK, (await t.Http.GetAsync("/api/notas")).StatusCode);
    }

    [Fact]
    public void Exposicao_fora_de_localhost_exige_token()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"api-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var cfg = new Configuracao { BancoSqlite = Path.Combine(dir, "t.db"), PastaXml = Path.Combine(dir, "x") };
            var repo = new Repositorio(cfg.BancoSqlite);
            Assert.Throws<InvalidOperationException>(() => ApiServer.Construir(cfg, repo, new Robo(cfg, repo), "http://0.0.0.0:5080"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Sync_sem_certificado_vira_erro_visivel_e_cancel_sem_execucao_da_409()
    {
        await using var f = await Semeada();
        Assert.Equal(HttpStatusCode.Conflict, (await f.Http.PostAsync("/api/sync/cancel", null)).StatusCode);

        var r = await f.Http.PostAsync("/api/sync/start", null);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var s = await f.Http.GetFromJsonAsync<JsonElement>("/api/sync/status");
        Assert.Equal("error", s.GetProperty("estado").GetString());
        Assert.False(string.IsNullOrEmpty(s.GetProperty("erro").GetProperty("mensagem").GetString()));
        Assert.Equal("ERRO", f.Repo.UltimosLogs(1)[0].Resultado);
    }

    [Fact]
    public async Task Bloqueio_656_persiste_apos_reinicio_e_impede_nova_execucao()
    {
        await using var f = await Semeada();
        var id = f.Repo.IniciarLog("manual", DateTimeOffset.Now.AddMinutes(-5));
        f.Repo.FinalizarLog(id, DateTimeOffset.Now.AddMinutes(-4), "656", 0, "consumo indevido");

        var sync = new SyncService(f.Cfg, f.Repo, new Robo(f.Cfg, f.Repo)); // simula novo processo
        Assert.Contains("aguardar", sync.TentarIniciar("manual"));
    }

    [Fact]
    public async Task Eventos_sse_enviam_o_estado_inicial()
    {
        await using var f = await Semeada();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/sync/events");
        using var resp = await f.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
        var linha = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.StartsWith("data: ", linha);
        Assert.Contains("\"tipo\":\"estado\"", linha);
    }

    [Fact]
    public async Task Certificado_upload_valida_senha_e_informa_validade()
    {
        await using var f = await Semeada();
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=EMPRESA TESTE LTDA:11222333000181", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(400));
        var pfx = cert.Export(X509ContentType.Pfx, "abc123");

        HttpContent Form(string senha) => new MultipartFormDataContent
        {
            { new ByteArrayContent(pfx), "arquivo", "c.pfx" },
            { new StringContent(senha), "senha" },
        };

        var ruim = await f.Http.PostAsync("/api/certificado", Form("errada"));
        Assert.Equal(HttpStatusCode.BadRequest, ruim.StatusCode);

        var ok = await f.Http.PostAsync("/api/certificado", Form("abc123"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var info = await f.Http.GetFromJsonAsync<JsonElement>("/api/certificado");
        Assert.True(info.GetProperty("valido").GetBoolean());
        Assert.Equal("EMPRESA TESTE LTDA", info.GetProperty("titular").GetString());
        Assert.Equal("11222333000181", info.GetProperty("cnpj").GetString());
        Assert.InRange(info.GetProperty("diasRestantes").GetInt32(), 398, 400);
        Assert.DoesNotContain("abc123", File.ReadAllText(Path.Combine(f.Dir, "certificado.pfx"), Encoding.Latin1)); // senha não vai pro disco
        Assert.Equal("abc123", f.Cfg.SenhaEmMemoria);
        Assert.DoesNotContain("abc123", await f.Http.GetStringAsync("/api/certificado"));
    }

    // ---------------- eventos de cancelamento ----------------

    private const string EventoCompleto = """
        <procEventoNFe xmlns="http://www.portalfiscal.inf.br/nfe" versao="1.00">
          <evento versao="1.00"><infEvento Id="ID1101113524101122233300018155001000000001110000000011">
            <cOrgao>35</cOrgao><chNFe>{CHAVE}</chNFe><dhEvento>2026-10-10T09:00:00-03:00</dhEvento>
            <tpEvento>110111</tpEvento><nSeqEvento>1</nSeqEvento><detEvento versao="1.00"><descEvento>Cancelamento</descEvento></detEvento>
          </infEvento></evento>
          <retEvento versao="1.00"><infEvento><cStat>135</cStat><chNFe>{CHAVE}</chNFe></infEvento></retEvento>
        </procEventoNFe>
        """;

    private const string EventoResumo = """
        <resEvento xmlns="http://www.portalfiscal.inf.br/nfe" versao="1.01">
          <cOrgao>91</cOrgao><CNPJ>11222333000181</CNPJ><chNFe>{CHAVE}</chNFe><dhEvento>2026-10-10T09:00:00-03:00</dhEvento>
          <tpEvento>110111</tpEvento><nSeqEvento>1</nSeqEvento><xEvento>Cancelamento registrado</xEvento><dhRecbto>2026-10-10T09:00:05-03:00</dhRecbto>
        </resEvento>
        """;

    [Fact]
    public void LerEvento_le_resumo_e_evento_completo()
    {
        var chave = "35261011222333000181550010000000011000000012";
        var completo = NfeXml.LerEvento(System.Xml.Linq.XDocument.Parse(EventoCompleto.Replace("{CHAVE}", chave)))!;
        Assert.Equal(chave, completo.Chave);
        Assert.Equal("110111", completo.Tipo);
        Assert.Equal("135", completo.CStat);
        Assert.True(NfeXml.EhCancelamentoEfetivo(completo));

        var resumo = NfeXml.LerEvento(System.Xml.Linq.XDocument.Parse(EventoResumo.Replace("{CHAVE}", chave)))!;
        Assert.Equal("Cancelamento registrado", resumo.Descricao);
        Assert.True(NfeXml.EhCancelamentoEfetivo(resumo));

        Assert.False(NfeXml.EhCancelamentoEfetivo(completo with { Tipo = "110110" })); // CC-e não cancela
        Assert.False(NfeXml.EhCancelamentoEfetivo(completo with { CStat = "573" }));   // evento rejeitado
        Assert.Null(NfeXml.LerEvento(System.Xml.Linq.XDocument.Parse("<nfeProc/>")));
    }

    [Fact]
    public async Task Cancelamento_marca_nota_e_vence_sobre_reprocessamento_do_xml()
    {
        await using var f = await Semeada();
        var nota = f.Repo.Buscar(new FiltroBusca { Numero = "1" })[0];
        Assert.Equal("AUTORIZADA", Situacao.Classificar(nota.SituacaoSefaz));

        var ev = new EventoNfe(nota.ChaveAcesso, "110111", 1, "135", "Cancelamento", "2026-10-10T09:00:00");
        Assert.True(f.Repo.RegistrarEvento(ev, "TESTE"));
        Assert.False(f.Repo.RegistrarEvento(ev, "TESTE")); // idempotente
        Assert.Equal("CANCELADA", Situacao.Classificar(f.Repo.ObterNota(nota.ChaveAcesso)!.SituacaoSefaz));

        // reprocessar o XML original (cStat 100) não "descancela"
        nota.SituacaoSefaz = "100";
        f.Repo.SalvarNotaCompleta(nota);
        Assert.Equal("CANCELADA", Situacao.Classificar(f.Repo.ObterNota(nota.ChaveAcesso)!.SituacaoSefaz));

        var cancel = await f.Http.GetFromJsonAsync<JsonElement>("/api/notas?situacao=CANCELADA");
        Assert.Equal(2, cancel.GetProperty("total").GetInt32()); // a nota 3 já era cancelada + a 1
    }

    [Fact]
    public async Task Evento_que_chega_antes_da_nota_e_reaplicado_quando_ela_entra()
    {
        await using var f = await Semeada();
        var chave = "35261011222333000181550010000000991000000999";
        f.Repo.RegistrarEvento(new EventoNfe(chave, "110111", 1, null, null, null), "TESTE");
        Assert.Null(f.Repo.ObterNota(chave));

        f.Repo.InserirSeNaoExiste(new NotaSaida { ChaveAcesso = chave, CnpjEmitente = f.Cfg.Cnpj, NumeroNota = "99", Status = StatusNota.Pendente, SituacaoSefaz = "100" });
        Assert.Equal("CANCELADA", Situacao.Classificar(f.Repo.ObterNota(chave)!.SituacaoSefaz));
    }

    [Fact]
    public async Task Evento_de_outro_tipo_ou_de_outro_emitente_nao_altera_notas()
    {
        await using var f = await Semeada();
        var robo = new Robo(f.Cfg, f.Repo);
        var nota = f.Repo.Buscar(new FiltroBusca { Numero = "1" })[0];

        // carta de correção (110110) da nossa nota: ignorada
        var cce = EventoCompleto.Replace("{CHAVE}", nota.ChaveAcesso).Replace("110111", "110110");
        robo.ProcessarDocumento(new DocumentoDistribuido("1", "procEventoNFe_v1.00.xsd", cce));
        Assert.Equal("AUTORIZADA", Situacao.Classificar(f.Repo.ObterNota(nota.ChaveAcesso)!.SituacaoSefaz));
        Assert.Equal(0, f.Repo.ContarEventos(nota.ChaveAcesso));

        // cancelamento de nota de OUTRO emitente (CNPJ na chave diferente do configurado): ignorado
        var alheia = "35261099888777000166550010000000011000000011";
        robo.ProcessarDocumento(new DocumentoDistribuido("2", "resEvento_v1.01.xsd", EventoResumo.Replace("{CHAVE}", alheia)));
        Assert.Equal(0, f.Repo.ContarEventos(alheia));

        // cancelamento da nossa nota, como resumo: aplicado
        robo.ProcessarDocumento(new DocumentoDistribuido("3", "resEvento_v1.01.xsd", EventoResumo.Replace("{CHAVE}", nota.ChaveAcesso)));
        Assert.Equal("CANCELADA", Situacao.Classificar(f.Repo.ObterNota(nota.ChaveAcesso)!.SituacaoSefaz));
        Assert.Equal(1, f.Repo.ContarEventos(nota.ChaveAcesso));
    }

    [Fact]
    public async Task Interface_embutida_e_servida_sem_pasta_wwwroot()
    {
        await using var f = await Semeada();
        var raiz = await f.Http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, raiz.StatusCode);
        Assert.Contains("<title>Notas de Saída</title>", await raiz.Content.ReadAsStringAsync());
        var css = await f.Http.GetAsync("/app.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Cnpj_pela_interface_valida_persiste_e_vale_na_proxima_execucao()
    {
        await using var f = await Semeada();
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Http.PutAsJsonAsync("/api/config", new { cnpj = "11222333000182" })).StatusCode);

        var ok = await f.Http.PutAsJsonAsync("/api/config", new { cnpj = "99.888.777/0001-00" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("99888777000100", f.Cfg.Cnpj);
        Assert.Equal("99888777000100", (await f.Http.GetFromJsonAsync<JsonElement>("/api/config")).GetProperty("cnpj").GetString());

        var nova = new Configuracao { Cnpj = "" };
        nova.AplicarPreferencias(f.Repo);
        Assert.Equal("99888777000100", nova.Cnpj);
    }
}
