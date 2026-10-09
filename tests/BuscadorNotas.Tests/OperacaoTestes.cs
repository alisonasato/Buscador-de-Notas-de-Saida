using BuscadorNotas;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BuscadorNotas.Tests;

public class OperacaoTestes
{
    private static string NovaPasta()
    {
        var d = Path.Combine(Path.GetTempPath(), $"op-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Limpar(string d)
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(d, true); } catch (IOException) { }
    }

    private static (Configuracao cfg, Repositorio repo) Ambiente(string dir)
    {
        var cfg = new Configuracao { Cnpj = "11222333000181", BancoSqlite = Path.Combine(dir, "dados", "notas.db"), PastaXml = Path.Combine(dir, "dados", "xmls") };
        Directory.CreateDirectory(Path.GetDirectoryName(cfg.BancoSqlite)!);
        return (cfg, new Repositorio(cfg.BancoSqlite));
    }

    private const string Chave1 = "35261011222333000181550010000000011000000012";
    private const string Chave2 = "35261011222333000181550010000000021000000025";

    [Fact]
    public void Backup_copia_banco_integro_e_xmls_de_forma_incremental()
    {
        var dir = NovaPasta();
        try
        {
            var (cfg, repo) = Ambiente(dir);
            var storage = new Armazenamento(cfg.PastaXml);
            repo.SalvarNotaCompleta(new NotaSaida { ChaveAcesso = Chave1, NumeroNota = "1", Status = StatusNota.Baixado, CaminhoXmlLocal = storage.Salvar(Chave1, "<a/>") });
            var destino = Path.Combine(dir, "bkp");

            var r1 = BackupService.Executar(cfg, destino, manter: 14, agora: new DateTime(2026, 10, 9, 2, 0, 0));
            Assert.Equal(1, r1.XmlsCopiados);
            Assert.True(File.Exists(Path.Combine(destino, "xmls", "2026", "10", Chave1 + ".xml")));
            Assert.Empty(Directory.GetFiles(Path.Combine(destino, "db"), "*.tmp"));

            // o banco copiado abre e contém a nota
            var copia = new Repositorio(r1.ArquivoDb);
            Assert.Equal("1", copia.ObterNota(Chave1)!.NumeroNota);

            // segunda execução: só copia o que é novo
            repo.SalvarNotaCompleta(new NotaSaida { ChaveAcesso = Chave2, NumeroNota = "2", Status = StatusNota.Baixado, CaminhoXmlLocal = storage.Salvar(Chave2, "<b/>") });
            var r2 = BackupService.Executar(cfg, destino, manter: 14, agora: new DateTime(2026, 10, 10, 2, 0, 0));
            Assert.Equal(1, r2.XmlsCopiados);
            Assert.Equal(1, r2.XmlsJaExistentes);
        }
        finally { Limpar(dir); }
    }

    [Fact]
    public void Backup_mantem_so_os_N_mais_recentes_e_nao_toca_na_origem()
    {
        var dir = NovaPasta();
        try
        {
            var (cfg, _) = Ambiente(dir);
            var destino = Path.Combine(dir, "bkp");
            for (int i = 1; i <= 4; i++) BackupService.Executar(cfg, destino, manter: 2, agora: new DateTime(2026, 10, i, 2, 0, 0));
            var arquivos = Directory.GetFiles(Path.Combine(destino, "db"), "notas-*.db").Select(Path.GetFileName).OrderBy(x => x).ToList();
            Assert.Equal(new[] { "notas-20261003-020000.db", "notas-20261004-020000.db" }, arquivos);
            Assert.True(File.Exists(cfg.BancoSqlite));
        }
        finally { Limpar(dir); }
    }

    [Fact]
    public void Backup_recusa_destino_dentro_da_pasta_de_xmls_ou_vazio()
    {
        var dir = NovaPasta();
        try
        {
            var (cfg, _) = Ambiente(dir);
            Assert.Throws<ArgumentException>(() => BackupService.Executar(cfg, Path.Combine(cfg.PastaXml, "bkp")));
            Assert.Throws<ArgumentException>(() => BackupService.Executar(cfg, ""));
            Assert.Throws<ArgumentException>(() => BackupService.Executar(cfg, Path.Combine(dir, "b"), manter: 0));
        }
        finally { Limpar(dir); }
    }

    [Fact]
    public void LogDiario_escreve_com_data_gira_por_dia_e_apaga_antigos()
    {
        var dir = NovaPasta();
        try
        {
            var agora = new DateTime(2026, 10, 9, 10, 0, 0);
            var velho = Path.Combine(dir, "buscador-20250101.log");
            File.WriteAllText(velho, "antigo");
            File.SetLastWriteTime(velho, new DateTime(2025, 1, 1));

            using (var log = new LogDiario(dir, diasGuardados: 30, agora: () => agora))
            {
                log.WriteLine("primeira");
                agora = agora.AddDays(1);
                log.WriteLine("segunda");
            }
            Assert.Contains("2026-10-09 10:00:00 primeira", File.ReadAllText(Path.Combine(dir, "buscador-20261009.log")));
            Assert.Contains("segunda", File.ReadAllText(Path.Combine(dir, "buscador-20261010.log")));
            Assert.False(File.Exists(velho));
        }
        finally { Limpar(dir); }
    }

    [Fact]
    public void Configuracao_resolve_caminhos_relativos_a_partir_da_pasta_do_arquivo()
    {
        var dir = NovaPasta();
        try
        {
            var arq = Path.Combine(dir, "appsettings.json");
            File.WriteAllText(arq, """{"Cnpj":"11.222.333/0001-81","PastaXml":"./xmls","BancoSqlite":"notas.db","PastaBackup":"../bkp"}""");
            var cfg = Configuracao.Carregar(arq);
            Assert.Equal("11222333000181", cfg.Cnpj);
            Assert.Equal(Path.Combine(dir, "xmls"), cfg.PastaXml);
            Assert.Equal(Path.Combine(dir, "notas.db"), cfg.BancoSqlite);
            Assert.Equal(Path.GetFullPath(Path.Combine(dir, "..", "bkp")), cfg.PastaBackup);
            Assert.Equal(dir, cfg.PastaConfig);
        }
        finally { Limpar(dir); }
    }

    [Theory]
    [InlineData("11.222.333/0001-81", true)]
    [InlineData("11222333000181", true)]
    [InlineData("11222333000182", false)]   // dígito verificador errado
    [InlineData("00000000000000", false)]
    [InlineData("1122233300018", false)]    // 13 dígitos
    [InlineData("", false)]
    public void Cnpj_valido(string cnpj, bool esperado) => Assert.Equal(esperado, Documento.CnpjValido(cnpj));

    [Fact]
    public void Configuracao_padrao_criada_na_primeira_execucao_e_carregavel()
    {
        var dir = NovaPasta();
        try
        {
            var arq = Path.Combine(dir, "novo", "appsettings.json");
            Configuracao.CriarPadrao(arq);
            var cfg = Configuracao.Carregar(arq);
            Assert.Equal("", cfg.Cnpj);
            Assert.Equal(Path.Combine(dir, "novo", "notas.db"), cfg.BancoSqlite);
            Assert.Equal(Path.Combine(dir, "novo", "entrada"), cfg.PastaEntrada);
        }
        finally { Limpar(dir); }
    }

    [Theory]
    [InlineData("35", true)]
    [InlineData("91", false)]   // "Ambiente Nacional" do guia original: não é UF
    [InlineData("", false)]
    [InlineData("SP", false)]
    public void Uf_valida(string codigo, bool esperado) => Assert.Equal(esperado, Configuracao.UfValida(codigo));

    [Fact]
    public void DistDFeInt_so_envia_cUFAutor_valido_e_na_ordem_do_esquema()
    {
        var cfg = new Configuracao { Cnpj = "11222333000181", Ambiente = 1 };

        cfg.CUFAutorEfetivo = "91"; // valor do guia: não pode ir
        var sem = SefazDistribuicao.MontarDistDFeInt(cfg, "7");
        Assert.DoesNotContain("cUFAutor", sem.ToString());
        Assert.Equal(new[] { "tpAmb", "CNPJ", "distNSU" }, sem.Elements().Select(e => e.Name.LocalName).ToArray());

        cfg.CUFAutorEfetivo = "35";
        var com = SefazDistribuicao.MontarDistDFeInt(cfg, "7");
        Assert.Equal(new[] { "tpAmb", "cUFAutor", "CNPJ", "distNSU" }, com.Elements().Select(e => e.Name.LocalName).ToArray());
        Assert.Equal("35", com.Elements().First(e => e.Name.LocalName == "cUFAutor").Value);
        Assert.Equal("000000000000007", com.Descendants().First(e => e.Name.LocalName == "ultNSU").Value);
    }

    [Fact]
    public void Robo_resolve_uf_configurada_inferida_ou_omitida()
    {
        var dir = NovaPasta();
        try
        {
            var (cfg, repo) = Ambiente(dir);
            var robo = new Robo(cfg, repo);

            cfg.CUFAutor = "91";                       // valor inválido do guia
            robo.ResolverUf();
            Assert.Null(cfg.CUFAutorEfetivo);          // sem notas para inferir: omite

            repo.InserirSeNaoExiste(new NotaSaida { ChaveAcesso = Chave1, Status = StatusNota.Pendente });
            repo.InserirSeNaoExiste(new NotaSaida { ChaveAcesso = Chave2, Status = StatusNota.Pendente });
            robo.ResolverUf();
            Assert.Equal("35", cfg.CUFAutorEfetivo);   // inferida das chaves (35...)

            cfg.CUFAutor = "41";                       // configurada vence
            robo.ResolverUf();
            Assert.Equal("41", cfg.CUFAutorEfetivo);
        }
        finally { Limpar(dir); }
    }
}
