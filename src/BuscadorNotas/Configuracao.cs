using System.Text.Json;

namespace BuscadorNotas;

public class Configuracao
{
    public string Cnpj { get; set; } = "";
    public string CertificadoPfx { get; set; } = "";
    public int Ambiente { get; set; } = 1; // 1-Produção, 2-Homologação
    public string CUFAutor { get; set; } = "91";
    public bool Soap12 { get; set; } = true;
    public string UrlDistribuicao { get; set; } =
        "https://www1.nfe.fazenda.gov.br/NFeDistribuicaoDFe/NFeDistribuicaoDFe.asmx";

    /// <summary>URL do NFeConsultaProtocolo4 por código de UF (2 dígitos). Sem valor padrão: preencher conforme o Portal da NF-e.</summary>
    public Dictionary<string, string> UrlsConsultaProtocolo { get; set; } = new();

    public string PastaXml { get; set; } = "./xmls";
    public string BancoSqlite { get; set; } = "./notas.db";
    public int EsperaSemNovosMinutos { get; set; } = 90;
    public int EsperaConsumoIndevidoMinutos { get; set; } = 65;
    public int PausaEntreRequisicoesSegundos { get; set; } = 2;

    /// <summary>A senha do certificado nunca fica no arquivo: vem da variável de ambiente NFE_PFX_SENHA.</summary>
    public string SenhaCertificado =>
        Environment.GetEnvironmentVariable("NFE_PFX_SENHA") ?? "";

    public static Configuracao Carregar(string? caminho = null)
    {
        caminho ??= Environment.GetEnvironmentVariable("BUSCADOR_CONFIG") ?? "appsettings.json";
        if (!File.Exists(caminho))
            throw new FileNotFoundException(
                $"Arquivo de configuração '{caminho}' não encontrado. Copie appsettings.exemplo.json para appsettings.json e preencha.");

        var cfg = JsonSerializer.Deserialize<Configuracao>(File.ReadAllText(caminho),
                      new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                  ?? throw new InvalidDataException("Configuração inválida.");

        cfg.Cnpj = new string(cfg.Cnpj.Where(char.IsDigit).ToArray());
        return cfg;
    }

    public void ValidarParaSefaz()
    {
        if (Cnpj.Length != 14) throw new InvalidOperationException("Cnpj deve ter 14 dígitos.");
        if (string.IsNullOrWhiteSpace(CertificadoPfx)) throw new InvalidOperationException("CertificadoPfx não informado.");
        if (string.IsNullOrEmpty(SenhaCertificado)) throw new InvalidOperationException("Defina a variável de ambiente NFE_PFX_SENHA.");
    }
}
