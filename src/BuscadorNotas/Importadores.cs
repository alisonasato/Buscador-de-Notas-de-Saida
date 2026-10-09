using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BuscadorNotas;

public record ResultadoImportacao(int Lidos, int Novos, int Ignorados, List<string> Avisos);

public static class ExtratorChaves
{
    private static readonly Regex Chave = new(@"(?<!\d)\d{44}(?!\d)", RegexOptions.Compiled);

    /// <summary>
    /// Extrai chaves de 44 dígitos de linhas de texto/CSV (qualquer coluna). Se a linha não tiver uma chave contínua,
    /// tenta de novo sem espaços/pontos/hífens (formato impresso no DANFE, em grupos de 4 dígitos).
    /// </summary>
    public static IEnumerable<string> Extrair(IEnumerable<string> linhas)
    {
        foreach (var linha in linhas)
        {
            var achou = false;
            foreach (Match m in Chave.Matches(linha)) { achou = true; yield return m.Value; }
            if (achou) continue;

            var compacta = Regex.Replace(linha, @"[\s.\-]", "");
            foreach (Match m in Chave.Matches(compacta)) yield return m.Value;
        }
    }
}

public partial class Robo
{
    // ---------- Importar chaves de planilha/lista ----------

    public ResultadoImportacao ImportarChaves(string arquivo, bool incluirOutrosCnpjs)
    {
        var avisos = new List<string>();
        int lidos = 0, novos = 0, ignorados = 0;
        var vistas = new HashSet<string>();
        var linhas = File.ReadLines(arquivo, System.Text.Encoding.UTF8);

        foreach (var chave in ExtratorChaves.Extrair(linhas))
        {
            if (!vistas.Add(chave)) continue;
            lidos++;
            if (!NfeXml.DigitoVerificadorOk(chave))
            {
                ignorados++; avisos.Add($"{chave}: dígito verificador inválido (provável erro de digitação).");
                continue;
            }
            if (!incluirOutrosCnpjs && _cfg.Cnpj.Length == 14 && NfeXml.CnpjDaChave(chave) != _cfg.Cnpj)
            {
                ignorados++; avisos.Add($"{chave}: emitente da chave ({NfeXml.CnpjDaChave(chave)}) não é o seu CNPJ.");
                continue;
            }
            if (_repo.InserirSeNaoExiste(new NotaSaida
            {
                ChaveAcesso = chave,
                CnpjEmitente = NfeXml.CnpjDaChave(chave),
                NumeroNota = NfeXml.NumeroDaChave(chave),
                Serie = NfeXml.SerieDaChave(chave),
                Status = StatusNota.Pendente,
                Origem = "LISTA",
            })) novos++;
        }
        return new ResultadoImportacao(lidos, novos, ignorados, avisos);
    }

    // ---------- Importar XMLs de uma pasta ----------

    public ResultadoImportacao ImportarXmls(string pasta, bool incluirOutrosCnpjs)
    {
        if (!Directory.Exists(pasta)) throw new DirectoryNotFoundException($"Pasta não encontrada: {pasta}");

        var avisos = new List<string>();
        int lidos = 0, novos = 0, ignorados = 0;

        foreach (var arq in Directory.EnumerateFiles(pasta, "*.xml", SearchOption.AllDirectories))
        {
            lidos++;
            try
            {
                var doc = XDocument.Load(arq);
                var tipo = NfeXml.TipoDocumento(doc);
                if (tipo is not ("nfeProc" or "NFe"))
                {
                    ignorados++; avisos.Add($"{arq}: não é uma NF-e (raiz '{tipo}').");
                    continue;
                }
                var nota = NfeXml.LerNotaCompleta(doc, "XML");
                if (nota == null) { ignorados++; avisos.Add($"{arq}: NF-e sem chave válida."); continue; }
                if (!incluirOutrosCnpjs && _cfg.Cnpj.Length == 14 && nota.CnpjEmitente != _cfg.Cnpj)
                {
                    ignorados++; avisos.Add($"{arq}: emitente {nota.CnpjEmitente} não é o seu CNPJ.");
                    continue;
                }
                var existia = _repo.Buscar(new FiltroBusca { Chave = nota.ChaveAcesso, Limite = 1 })
                    .Any(n => n.Status == StatusNota.Baixado);
                nota.CaminhoXmlLocal = _storage.Copiar(nota.ChaveAcesso, arq);
                _repo.SalvarNotaCompleta(nota);
                if (!existia) novos++;
            }
            catch (System.Xml.XmlException ex)
            {
                ignorados++; avisos.Add($"{arq}: XML inválido ({ex.Message}).");
            }
        }
        return new ResultadoImportacao(lidos, novos, ignorados, avisos);
    }
}
