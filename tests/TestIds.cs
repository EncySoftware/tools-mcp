using System.Security.Cryptography;
using System.Text;

/** Readable names for the ids the server hands out: G("c1") is always the same lower-case GUID, as the server writes them. */
internal static class TestIds
{
    public static string G(string name) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(name))).ToString("D");
}
