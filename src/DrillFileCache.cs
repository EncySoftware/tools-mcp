using System.IO;
using System.Security.Cryptography;

namespace ToolsMcp;

internal static class DrillFileCache
{
    // ENCY uses the full source path as file provenance. Identical geometry needs a stable path
    // across searches and retries; a changed assembly has its own file and project tool.
    internal static string Store(byte[] bytes)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EncyToolsCatalog", "drill-files");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Convert.ToHexString(SHA256.HashData(bytes)) + ".sc12tool");
        if (!File.Exists(path))
        {
            string temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".part");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { /* Another caller completed the same export. */ }
            }
            finally { File.Delete(temporary); }
        }
        using var stored = File.OpenRead(path);
        if (stored.Length != bytes.Length || !SHA256.HashData(stored).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
            throw new IOException("The cached drill file did not match the catalogue export.");
        return path;
    }
}
