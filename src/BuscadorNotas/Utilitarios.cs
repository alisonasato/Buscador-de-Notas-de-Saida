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
}
