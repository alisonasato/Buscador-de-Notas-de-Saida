using System.Text;

namespace BuscadorNotas;

public record NotaSped(string Chave, string NumeroDoc, string Serie, string? DataDoc, decimal? Valor, string CodSit);

/// <summary>
/// Lê registros C100 do EFD-ICMS/IPI. Campos (separados por '|', índice 0 é vazio):
/// 1 REG, 2 IND_OPER (0 entrada / 1 saída), 3 IND_EMIT, 4 COD_PART, 5 COD_MOD, 6 COD_SIT,
/// 7 SER, 8 NUM_DOC, 9 CHV_NFE, 10 DT_DOC (ddMMaaaa), 11 DT_E_S, 12 VL_DOC.
/// Confirme o layout no Guia Prático da EFD da versão do seu arquivo.
/// </summary>
public static class SpedParser
{
    public static IEnumerable<NotaSped> LerSaidas(string caminho)
    {
        // SPED é normalmente ISO-8859-1.
        using var reader = new StreamReader(caminho, Encoding.Latin1);
        return LerSaidas(reader).ToList();
    }

    public static IEnumerable<NotaSped> LerSaidas(TextReader reader)
    {
        string? linha;
        while ((linha = reader.ReadLine()) != null)
        {
            if (!linha.StartsWith("|C100|", StringComparison.Ordinal)) continue;
            var c = linha.Split('|');
            if (c.Length < 13) continue;
            if (c[2] != "1") continue;                       // só saídas
            if (!NfeXml.ChaveValida(c[9])) continue;         // sem chave (ex.: modelo 01/papel)

            string? data = null;
            if (c[10].Length == 8 &&
                DateTime.TryParseExact(c[10], "ddMMyyyy", null, System.Globalization.DateTimeStyles.None, out var d))
                data = d.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

            var valor = decimal.TryParse(c[12].Replace(',', '.'), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : (decimal?)null;

            yield return new NotaSped(c[9], c[8], c[7], data, valor, c[6]);
        }
    }
}
