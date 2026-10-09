using System.IO.Compression;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace BuscadorNotas;

public static class CertificadoService
{
    public static X509Certificate2 ObterCertificado(string caminhoPfx, string senha) =>
        new(caminhoPfx, senha, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
}

public static class DescompactadorXml
{
    /// <summary>docZip: Base64 de um fluxo GZip contendo o XML.</summary>
    public static string ExtrairXmlDeDocZip(string base64Gzip)
    {
        var dados = Convert.FromBase64String(base64Gzip);
        using var ms = new MemoryStream(dados);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

public class Armazenamento
{
    private readonly string _raiz;

    public Armazenamento(string raiz) => _raiz = raiz;

    /// <summary>Salva em {raiz}/{Ano}/{Mes}/{chave}.xml. A chave é validada (44 dígitos) para evitar path traversal.</summary>
    public string Salvar(string chave, string xml)
    {
        if (!NfeXml.ChaveValida(chave)) throw new ArgumentException("Chave de acesso inválida.", nameof(chave));
        var pasta = Path.Combine(_raiz, NfeXml.AnoDaChave(chave), NfeXml.MesDaChave(chave));
        Directory.CreateDirectory(pasta);
        var caminho = Path.Combine(pasta, chave + ".xml");
        File.WriteAllText(caminho, xml, new UTF8Encoding(false));
        return caminho;
    }

    /// <summary>Copia o arquivo original (bytes intactos) para a estrutura {raiz}/{Ano}/{Mes}/{chave}.xml.</summary>
    public string Copiar(string chave, string arquivoOrigem)
    {
        if (!NfeXml.ChaveValida(chave)) throw new ArgumentException("Chave de acesso inválida.", nameof(chave));
        var pasta = Path.Combine(_raiz, NfeXml.AnoDaChave(chave), NfeXml.MesDaChave(chave));
        Directory.CreateDirectory(pasta);
        var destino = Path.Combine(pasta, chave + ".xml");
        if (Path.GetFullPath(destino) != Path.GetFullPath(arquivoOrigem))
            File.Copy(arquivoOrigem, destino, overwrite: true);
        return destino;
    }

    /// <summary>Caminho completo do arquivo se ele estiver dentro da pasta de XMLs e existir; senão null (evita path traversal).</summary>
    public string? ResolverSeguro(string? caminho)
    {
        if (string.IsNullOrWhiteSpace(caminho)) return null;
        var raiz = Path.GetFullPath(_raiz).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var completo = Path.GetFullPath(caminho);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return completo.StartsWith(raiz, cmp) && File.Exists(completo) ? completo : null;
    }
}
