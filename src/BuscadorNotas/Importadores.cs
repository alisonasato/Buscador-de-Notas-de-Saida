using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BuscadorNotas;

public enum Desfecho { Importado, Ignorado, Rejeitado }
public record ResultadoArquivo(Desfecho Desfecho, string Detalhe, bool Novo = false);

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

    // ---------- Importar XMLs ----------

    /// <summary>
    /// Importa um XML de NF-e/NFC-e/CT-e/MDF-e/CF-e SAT/NFS-e ou um evento de cancelamento, a partir dos bytes originais
    /// (guardados intactos). Só documentos emitidos pelo CNPJ configurado, salvo <paramref name="incluirOutrosCnpjs"/>.
    /// </summary>
    public ResultadoArquivo ImportarXmlBytes(byte[] dados, bool incluirOutrosCnpjs)
    {
        XDocument doc;
        try
        {
            using var ms = new MemoryStream(dados);
            doc = XDocument.Load(ms);
        }
        catch (System.Xml.XmlException ex) { return new(Desfecho.Rejeitado, $"XML inválido ({ex.Message})"); }

        bool NossoCnpj(string? cnpj) => incluirOutrosCnpjs || _cfg.Cnpj.Length != 14 || cnpj == _cfg.Cnpj;

        // Eventos de cancelamento (NF-e, CT-e, MDF-e) e CFeCanc
        var raiz = NfeXml.TipoDocumento(doc);
        if (raiz is "resEvento" or "procEventoNFe" or "procEventoCTe" or "procEventoMDFe" or "CFeCanc")
        {
            var ev = NfeXml.LerEvento(doc);
            if (ev == null) return new(Desfecho.Rejeitado, "evento sem chave/tipo válidos");
            if (!NossoCnpj(NfeXml.CnpjDaChave(ev.Chave)) && !(_cfg.GuardarEntradas && _repo.ObterNota(ev.Chave) != null))
                return new(Desfecho.Ignorado, "evento de documento de outro emitente");
            if (!NfeXml.TipoCancelaDocumento(ev.Chave, ev.Tipo)) return new(Desfecho.Ignorado, $"evento {ev.Tipo} não é tratado (só cancelamento)");
            var novo = _repo.RegistrarEvento(ev, "XML");
            return new(Desfecho.Importado, $"cancelamento do documento {NfeXml.NumeroDaChave(ev.Chave)}", Novo: novo);
        }

        var tipoDoc = DocumentosFiscais.Identificar(doc);
        if (tipoDoc == null) return new(Desfecho.Ignorado, $"não é um documento fiscal reconhecido (raiz '{raiz}')");

        var nota = DocumentosFiscais.Ler(doc, "XML");
        if (nota == null) return new(Desfecho.Rejeitado, $"{TipoDoc.Rotulo(tipoDoc)} sem identificador válido ou em layout não reconhecido");
        nota.Direcao = Direcao.De(nota, _cfg.Cnpj);
        var aceita = nota.Direcao == Direcao.Saida || incluirOutrosCnpjs || (nota.Direcao == Direcao.Entrada && _cfg.GuardarEntradas);
        if (!aceita)
            return new(Desfecho.Ignorado, nota.Direcao == Direcao.Entrada
                ? "nota de entrada (guardar entradas está desligado)"
                : $"emitente {nota.CnpjEmitente} não é o seu CNPJ");

        var existia = _repo.ObterNota(nota.ChaveAcesso)?.Status == StatusNota.Baixado;
        nota.CaminhoXmlLocal = _storage.SalvarDocumento(nota.Tipo, nota.ChaveAcesso, nota.DataEmissao, dados);
        _repo.SalvarNotaCompleta(nota);
        return new(Desfecho.Importado, $"{TipoDoc.Rotulo(nota.Tipo)} {nota.NumeroNota}" + (nota.Direcao == Direcao.Entrada ? " (entrada)" : ""), Novo: !existia && nota.Direcao != Direcao.Entrada);
    }

    public ResultadoImportacao ImportarXmls(string pasta, bool incluirOutrosCnpjs)
    {
        if (!Directory.Exists(pasta)) throw new DirectoryNotFoundException($"Pasta não encontrada: {pasta}");

        var avisos = new List<string>();
        int lidos = 0, novos = 0, ignorados = 0;

        foreach (var arq in Directory.EnumerateFiles(pasta, "*.xml", SearchOption.AllDirectories))
        {
            lidos++;
            var r = ImportarXmlBytes(File.ReadAllBytes(arq), incluirOutrosCnpjs);
            if (r.Desfecho == Desfecho.Importado) { if (r.Novo) novos++; }
            else { ignorados++; avisos.Add($"{arq}: {r.Detalhe}."); }
        }
        return new ResultadoImportacao(lidos, novos, ignorados, avisos);
    }
}
