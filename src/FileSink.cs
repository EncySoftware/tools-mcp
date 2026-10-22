namespace ToolsMcp;

/// <summary>
/// Where a tool's file goes. At home it is saved to the person's disk (<see cref="LocalFiles"/>); on the hosted server it
/// is kept for an hour behind a link (<see cref="LinkFiles"/>), since an agent there has no disk of the person's to write
/// to. A refusal throws, and the tool's reply is then the refusal (Replies.WithToken).
/// </summary>
public interface IFileSink
{
    /** Throws when nothing can be delivered into that folder. Called before the site is asked for the file, so a refusal
     *  costs no build and no download. */
    void Check(string? folder);

    /** Delivers the file and says where it is: "Saved: &lt;path&gt;" or "Download (1 hour): &lt;link&gt;". */
    string Deliver(DownloadedFile file, string? folder);
}

/// <summary>The person's disk: their Downloads, or the folder they name (<see cref="SavedFiles"/>).</summary>
public sealed class LocalFiles : IFileSink
{
    public static readonly LocalFiles Instance = new();

    public void Check(string? folder) { }

    public string Deliver(DownloadedFile file, string? folder) => "Saved: " + SavedFiles.Save(file, folder);
}

/// <summary>The hosted server: a link for an hour, the file kept as the calling person's (<see cref="DownloadStore"/>).</summary>
public sealed class LinkFiles(DownloadStore store, string publicUrl) : IFileSink
{
    public void Check(string? folder)
    {
        // The server never writes where an agent points.
        if (!string.IsNullOrWhiteSpace(folder))
            throw new ArgumentException("this server cannot save into a folder - leave folder out, and a link comes back");
    }

    public string Deliver(DownloadedFile file, string? folder)
    {
        Check(folder);
        var owner = Caller.Current?.Owner ?? throw new InvalidOperationException("a file is kept only for a signed-in person");
        var id = store.Keep(file, owner);
        return $"Download (1 hour): {publicUrl}/mcp/files/{id}/{Uri.EscapeDataString(file.FileName)}";
    }
}
