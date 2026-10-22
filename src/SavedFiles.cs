namespace ToolsMcp;

/// <summary>
/// Where kits, libraries and a component's files land on the person's disk: their Downloads,
/// unless they name a folder. Nothing is ever written over: a file they already have is theirs.
/// </summary>
public static class SavedFiles
{
    /** Windows refuses these in a name on every disk; the rest of the world refuses '/'. */
    private static readonly HashSet<char> Invalid =
        new(Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*"));

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", Brand.DownloadsFolder);

    /// <summary>Writes the file under a free name in the folder (created if missing) and returns its full path.
    /// It goes through a ".part" file, so a write cut short leaves nothing behind that looks like a kit.</summary>
    public static string Save(DownloadedFile file, string? folder)
    {
        string dir = string.IsNullOrWhiteSpace(folder) ? DefaultFolder : FolderOf(folder.Trim());
        Directory.CreateDirectory(dir);
        string path = FreeName(dir, Safe(file.FileName));
        string part = path + ".part";
        try
        {
            File.WriteAllBytes(part, file.Bytes);
            File.Move(part, path);
            return path;
        }
        catch
        {
            File.Delete(part);
            throw;
        }
    }

    /// <summary>
    /// A folder the person named: "~" is their home, and anything else must be a full path. A relative one resolved against
    /// the folder the agent's server runs in - often the person's project - and "~/Desktop" made a folder named "~" there
    /// (final review, 2026-10-22).
    /// </summary>
    private static string FolderOf(string folder)
    {
        if (folder == "~" || folder.StartsWith("~/", StringComparison.Ordinal) || folder.StartsWith("~\\", StringComparison.Ordinal))
            folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), folder.Length > 2 ? folder[2..] : "");
        if (!Path.IsPathFullyQualified(folder))
            throw new ArgumentException($@"folder must be a full path, such as C:\Users\you\Desktop, or start with ~ - not '{folder}'");
        return Path.GetFullPath(folder);
    }

    /// <summary>The name with every character a disk refuses replaced by '_', and no trailing dots or spaces,
    /// which Windows strips on its own and then cannot find again.</summary>
    internal static string Safe(string name)
    {
        string safe = new string(name.Select(c => Invalid.Contains(c) ? '_' : c).ToArray()).TrimEnd('.', ' ');
        return safe.Length == 0 ? "download" : safe;
    }

    private static string FreeName(string dir, string name)
    {
        string path = Path.Combine(dir, name);
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(dir, $"{stem} ({n}){ext}");
        return path;
    }
}
