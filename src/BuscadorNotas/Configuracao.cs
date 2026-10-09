using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>Aplica as escolhas feitas na interface (guardadas no banco) por cima do arquivo de configuração.</summary>
    public void AplicarPreferencias(Repositorio repo)
    {
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
