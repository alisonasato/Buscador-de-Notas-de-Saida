using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuscadorNotas;

public class Configuracao
{
    public string Cnpj { get; set; } = "";
    public string CertificadoPfx { get; set; } = "";
    public int Ambiente { get; set; } = 1; // 1-Produção, 2-Homologação
    /// <summary>
    /// Código IBGE da UF da empresa (ex.: 35 = SP), enviado como cUFAutor na distribuição. Vazio = tenta inferir das
    /// chaves já importadas; se não for possível, o campo (opcional no esquema, ao que consta) é omitido.
    /// O valor "91" (Ambiente Nacional) do guia original NÃO é uma UF válida e é ignorado.
    /// </summary>
    public string CUFAutor { get; set; } = "";

    /// <summary>Valor realmente usado na última consulta (configurado ou inferido); null = omitido.</summary>
    [JsonIgnore] public string? CUFAutorEfetivo { get; set; }

    public static readonly IReadOnlyDictionary<string, string> Ufs = new Dictionary<string, string>
    {
        ["11"] = "RO", ["12"] = "AC", ["13"] = "AM", ["14"] = "RR", ["15"] = "PA", ["16"] = "AP", ["17"] = "TO",
        ["21"] = "MA", ["22"] = "PI", ["23"] = "CE", ["24"] = "RN", ["25"] = "PB", ["26"] = "PE", ["27"] = "AL", ["28"] = "SE", ["29"] = "BA",
        ["31"] = "MG", ["32"] = "ES", ["33"] = "RJ", ["35"] = "SP", ["41"] = "PR", ["42"] = "SC", ["43"] = "RS",
        ["50"] = "MS", ["51"] = "MT", ["52"] = "GO", ["53"] = "DF",
    };

    public static bool UfValida(string? codigo) => codigo != null && Ufs.ContainsKey(codigo);
    public bool Soap12 { get; set; } = true;
    public string UrlDistribuicao { get; set; } =
        "https://www1.nfe.fazenda.gov.br/NFeDistribuicaoDFe/NFeDistribuicaoDFe.asmx";

    /// <summary>EXPERIMENTAL: também busca CT-e na distribuição DF-e do CT-e (desligado por padrão; formato de memória, não testado na Sefaz).</summary>
    public bool DistribuirCte { get; set; }
    public string UrlDistribuicaoCte { get; set; } = "https://www1.cte.fazenda.gov.br/CTeDistribuicaoDFe/CTeDistribuicaoDFe.asmx";

    /// <summary>EXPERIMENTAL: também busca MDF-e. Sem URL padrão: não sei ao certo se há serviço de distribuição de MDF-e; preencha conforme o Portal.</summary>
    public bool DistribuirMdfe { get; set; }
    public string UrlDistribuicaoMdfe { get; set; } = "";

    /// <summary>URL do NFeConsultaProtocolo4 por código de UF (2 dígitos). Sem valor padrão: preencher conforme o Portal da NF-e.</summary>
    public Dictionary<string, string> UrlsConsultaProtocolo { get; set; } = new();

    /// <summary>Pasta de destino do comando <c>backup</c> (preferencialmente outro disco/compartilhamento).</summary>
    public string PastaBackup { get; set; } = "";

    /// <summary>Pasta do arquivo de configuração; logs e dados relativos ficam aqui (não depende do diretório de trabalho).</summary>
    [JsonIgnore] public string PastaConfig { get; set; } = ".";

    /// <summary>
    /// Pasta monitorada pelo servidor: XMLs, ZIPs de XMLs, arquivos SPED (.txt) e listas de chaves (.csv) colocados aqui
    /// são importados sozinhos e movidos para "processados" ou "rejeitados". Vazio = desligado.
    /// </summary>
    public string PastaEntrada { get; set; } = "";
    public int EntradaIntervaloSegundos { get; set; } = 60;
    /// <summary>Só importa arquivos sem alteração há pelo menos este tempo (evita ler arquivo ainda sendo gravado).</summary>
    public int EntradaEstabilidadeSegundos { get; set; } = 5;
    /// <summary>Aceita notas/eventos cujo emitente não é o CNPJ configurado.</summary>
    public bool EntradaAceitarOutrosCnpjs { get; set; }

    public string PastaXml { get; set; } = "./xmls";
    public string BancoSqlite { get; set; } = "./notas.db";
    public int EsperaSemNovosMinutos { get; set; } = 90;
    public int EsperaConsumoIndevidoMinutos { get; set; } = 65;

    /// <summary>
    /// Intervalo mínimo entre consultas quando a anterior chegou ao fim do acervo (cStat 137, ou 138 com ultNSU = maxNSU).
    /// Consultar de novo antes disso faz a Sefaz responder 656 (consumo indevido) e bloquear o CNPJ. Padrão: 60 min.
    /// </summary>
    public int IntervaloMinimoMinutos { get; set; } = 60;
    public int PausaEntreRequisicoesSegundos { get; set; } = 2;

    /// <summary>Endereço do servidor web (comando <c>serve</c>). Fora de loopback exige <see cref="ApiToken"/>.</summary>
    public string ApiUrl { get; set; } = "http://127.0.0.1:5080";

    /// <summary>Se preenchido, toda chamada a /api deve enviar "Authorization: Bearer &lt;token&gt;".</summary>
    public string ApiToken { get; set; } = "";

    /// <summary>Liga o agendador do servidor (consulta à Sefaz sem ação do usuário). Pode ser alterado pela interface.</summary>
    public bool SincronizacaoAutomatica { get; set; }

    /// <summary>Senha informada pela interface (só em memória, nunca gravada). Tem prioridade sobre a variável de ambiente.</summary>
    [JsonIgnore] public string? SenhaEmMemoria { get; set; }

    /// <summary>A senha do certificado nunca fica no arquivo: vem da interface (memória) ou da variável NFE_PFX_SENHA.</summary>
    [JsonIgnore]
    public string SenhaCertificado =>
        SenhaEmMemoria ?? Environment.GetEnvironmentVariable("NFE_PFX_SENHA") ?? "";

    /// <summary>Caminho do arquivo: argumento, BUSCADOR_CONFIG, appsettings.json no diretório atual ou ao lado do executável.</summary>
    public static string LocalizarArquivo(string? caminho = null)
    {
        caminho ??= Environment.GetEnvironmentVariable("BUSCADOR_CONFIG");
        if (!string.IsNullOrWhiteSpace(caminho)) return Path.GetFullPath(caminho);
        foreach (var c in new[] { Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"), Path.Combine(PastaDoExecutavel(), "appsettings.json") })
            if (File.Exists(c)) return c;
        // Ainda não existe: o executável publicado guarda tudo ao lado dele; em desenvolvimento (dotnet run), no diretório atual.
        return Path.Combine(ExecutavelPublicado() ? PastaDoExecutavel() : Directory.GetCurrentDirectory(), "appsettings.json");
    }

    private static bool ExecutavelPublicado() =>
        !string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pasta do .exe (arquivo único) ou da DLL (dotnet run).</summary>
    public static string PastaDoExecutavel() =>
        ExecutavelPublicado() && Environment.ProcessPath is { } p ? Path.GetDirectoryName(p)! : AppContext.BaseDirectory;

    /// <summary>Cria um arquivo de configuração inicial (sem CNPJ: informe pela tela de Configurações).</summary>
    public static void CriarPadrao(string caminho)
    {
        var padrao = new
        {
            Cnpj = "",
            CertificadoPfx = "",
            Ambiente = 1,
            PastaXml = "xmls",
            BancoSqlite = "notas.db",
            PastaEntrada = "entrada",
            PastaBackup = "",
            SincronizacaoAutomatica = false,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(caminho)!);
        File.WriteAllText(caminho, JsonSerializer.Serialize(padrao, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static Configuracao Carregar(string? caminho = null)
    {
        caminho = LocalizarArquivo(caminho);
        if (!File.Exists(caminho))
            throw new FileNotFoundException(
                $"Arquivo de configuração '{caminho}' não encontrado. Copie appsettings.exemplo.json para appsettings.json e preencha.");

        var cfg = JsonSerializer.Deserialize<Configuracao>(File.ReadAllText(caminho),
                      new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                  ?? throw new InvalidDataException("Configuração inválida.");

        cfg.Cnpj = new string(cfg.Cnpj.Where(char.IsDigit).ToArray());
        cfg.PastaConfig = Path.GetDirectoryName(caminho) ?? ".";
        // Caminhos relativos valem a partir da pasta do arquivo (um serviço do Windows roda em System32).
        string Resolver(string p) => string.IsNullOrWhiteSpace(p) ? p : Path.GetFullPath(p, cfg.PastaConfig);
        cfg.PastaXml = Resolver(cfg.PastaXml);
        cfg.BancoSqlite = Resolver(cfg.BancoSqlite);
        cfg.CertificadoPfx = Resolver(cfg.CertificadoPfx);
        cfg.PastaBackup = Resolver(cfg.PastaBackup);
        cfg.PastaEntrada = Resolver(cfg.PastaEntrada);
        return cfg;
    }

    /// <summary>Aplica as escolhas feitas na interface (guardadas no banco) por cima do arquivo de configuração.</summary>
    public void AplicarPreferencias(Repositorio repo)
    {
        if (repo.ObterPref("cufAutor") is { } uf && (uf == "" || UfValida(uf))) CUFAutor = uf;
        if (bool.TryParse(repo.ObterPref("distribuirCte"), out var dc)) DistribuirCte = dc;
        if (bool.TryParse(repo.ObterPref("distribuirMdfe"), out var dm)) DistribuirMdfe = dm;
        if (repo.ObterPref("cnpj") is { } c && Documento.CnpjValido(c)) Cnpj = new string(c.Where(char.IsDigit).ToArray());
        if (int.TryParse(repo.ObterPref("intervaloMinutos"), out var i) && i >= 0) EsperaSemNovosMinutos = i;
        if (bool.TryParse(repo.ObterPref("automatica"), out var a)) SincronizacaoAutomatica = a;
        if (int.TryParse(repo.ObterPref("ambiente"), out var amb) && amb is 1 or 2) Ambiente = amb;
        if (repo.ObterPref("certificadoPfx") is { Length: > 0 } pfx && File.Exists(pfx)) CertificadoPfx = pfx;
    }

    public void ValidarParaSefaz()
    {
        if (Cnpj.Length != 14) throw new InvalidOperationException("Cnpj deve ter 14 dígitos.");
        if (string.IsNullOrWhiteSpace(CertificadoPfx)) throw new InvalidOperationException("CertificadoPfx não informado.");
        if (string.IsNullOrEmpty(SenhaCertificado)) throw new InvalidOperationException("Informe a senha do certificado (variável de ambiente NFE_PFX_SENHA ou pela tela de configurações).");
    }
}
