using System.Xml.Linq;

namespace BuscadorNotas;

/// <summary>
/// Modo de teste do robô de NSU: percorre a distribuição SEM filtrar, SEM gravar XML/banco e SEM mexer no NSU salvo,
/// só para mostrar em que papel (emitente/destinatário) o seu CNPJ aparece nos documentos devolvidos pela Sefaz.
/// </summary>
public static class Diagnostico
{
    public static async Task ExecutarAsync(Configuracao cfg, SefazDistribuicao dist, int maxPaginas,
        string nsuInicial, CancellationToken ct)
    {
        var porSchema = new Dictionary<string, int>();
        int totalDocs = 0, nfeComoEmitente = 0, nfeComoDestinatario = 0, nfeOutroPapel = 0, resumoComoEmitente = 0, semLeitura = 0;
        var exemplosEmitente = new List<string>();
        var ultNsu = nsuInicial;
        var paginas = 0;

        while (paginas < maxPaginas && !ct.IsCancellationRequested)
        {
            paginas++;
            var ret = await dist.ConsultarAsync(ultNsu, ct);
            Console.WriteLine($"Página {paginas}: cStat={ret.CStat} ({ret.XMotivo}) ultNSU={ret.UltNsu} maxNSU={ret.MaxNsu} docs={ret.Documentos.Count}");
            if (ret.CStat != "138") break; // 137 = nada; 656 = bloqueio; outros = erro

            foreach (var d in ret.Documentos)
            {
                totalDocs++;
                porSchema[d.Schema] = porSchema.GetValueOrDefault(d.Schema) + 1;

                XDocument doc;
                try { doc = XDocument.Parse(d.Xml); } catch (System.Xml.XmlException) { semLeitura++; continue; }

                switch (NfeXml.TipoDocumento(doc))
                {
                    case "nfeProc":
                    case "NFe":
                    {
                        var n = NfeXml.LerNotaCompleta(doc, "NSU");
                        if (n == null) { semLeitura++; break; }
                        var emit = n.CnpjEmitente == cfg.Cnpj;
                        var dest = n.CnpjCpfDestinatario == cfg.Cnpj;
                        if (emit) { nfeComoEmitente++; if (exemplosEmitente.Count < 5) exemplosEmitente.Add(n.ChaveAcesso); }
                        if (dest) nfeComoDestinatario++;
                        if (!emit && !dest) nfeOutroPapel++;
                        break;
                    }
                    case "resNFe":
                    {
                        var n = NfeXml.LerResumo(doc, "NSU");
                        if (n == null) { semLeitura++; break; }
                        if (n.CnpjEmitente == cfg.Cnpj) { resumoComoEmitente++; if (exemplosEmitente.Count < 5) exemplosEmitente.Add(n.ChaveAcesso); }
                        break;
                    }
                }
            }

            ultNsu = ret.UltNsu;
            if (string.CompareOrdinal(ret.UltNsu, ret.MaxNsu) >= 0) break;
            await Task.Delay(TimeSpan.FromSeconds(cfg.PausaEntreRequisicoesSegundos), ct);
        }

        Console.WriteLine();
        Console.WriteLine($"=== Resumo do diagnóstico (CNPJ {cfg.Cnpj}; {paginas} página(s); último NSU lido {ultNsu}) ===");
        Console.WriteLine($"Documentos recebidos: {totalDocs} (não lidos: {semLeitura})");
        foreach (var kv in porSchema.OrderByDescending(k => k.Value)) Console.WriteLine($"  schema {kv.Key}: {kv.Value}");
        Console.WriteLine($"NF-e completas em que o CNPJ é EMITENTE (saída): {nfeComoEmitente}");
        Console.WriteLine($"Resumos (resNFe) em que o CNPJ é EMITENTE:        {resumoComoEmitente}");
        Console.WriteLine($"NF-e completas em que o CNPJ é DESTINATÁRIO:      {nfeComoDestinatario}");
        Console.WriteLine($"NF-e completas em outro papel (ex.: autorizado):  {nfeOutroPapel}");
        if (exemplosEmitente.Count > 0) Console.WriteLine("Exemplos de chaves como emitente: " + string.Join(", ", exemplosEmitente));
        Console.WriteLine();
        Console.WriteLine(nfeComoEmitente + resumoComoEmitente > 0
            ? "A distribuição devolveu documentos em que o seu CNPJ é emitente: o 'sync' normal deve capturar saídas."
            : "Nenhum documento como emitente nesta amostra. Isso NÃO prova que nunca aparece (a amostra pode ser parcial: aumente --max-paginas), mas é indício de que as saídas virão de outra fonte.");
        Console.WriteLine("Nada foi gravado no banco nem o NSU salvo foi alterado.");
    }
}
