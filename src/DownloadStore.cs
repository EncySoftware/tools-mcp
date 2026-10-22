using System.Globalization;
using System.Security.Cryptography;

namespace ToolsMcp;

/// <summary>
/// The hosted server's files, each kept for an hour under a 128-bit random id. The id is the link's only credential: a
/// person's browser or messenger opening the link sends no token, and fetching it with the token inside the agent would
/// put the token in the model's context (design, 2026-10-22). Laid out as &lt;root&gt;/&lt;id&gt;/&lt;name&gt;, with ".owner"
/// and ".expires" beside the file. A full store refuses a new file; it never takes a link away before its hour.
/// </summary>
public sealed class DownloadStore
{
    private const long MB = 1024 * 1024;
    public const long MaxFile = 100 * MB;
    public const long MaxPerPerson = 300 * MB;
    public const long MaxTotal = 1024 * MB;
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private const string OwnerFile = ".owner", ExpiresFile = ".expires", Part = ".part";

    private readonly string _root;
    private readonly Func<DateTimeOffset> _now;
    private readonly long _maxFile, _maxPerPerson, _maxTotal;
    /** One writer or sweeper at a time: the limits are counted over what is on disk. */
    private readonly object _gate = new();

    public DownloadStore(string root, Func<DateTimeOffset>? clock = null) : this(root, clock, MaxFile, MaxPerPerson, MaxTotal) { }

    /** Smaller limits, for tests that would otherwise write hundreds of megabytes. */
    internal DownloadStore(string root, Func<DateTimeOffset>? clock, long maxFile, long maxPerPerson, long maxTotal)
    {
        _root = root;
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        (_maxFile, _maxPerPerson, _maxTotal) = (maxFile, maxPerPerson, maxTotal);
    }

    /** The store's refusal, in words for the person: which limit, and what to do. */
    public sealed class FullException(string message) : Exception(message);

    /** A file over the limit, refused in the same words wherever it is measured (ToolsApi, before reading it). */
    public static FullException TooLarge(long maxFile) => new($"a file holds at most {Mb(maxFile)}");

    /**
     * Throws when the person's share, or the server's space, is full already: asked before the site builds a file
     * that could not be kept (final review, 2026-10-22).
     */
    public void EnsureRoom(string owner)
    {
        lock (_gate)
        {
            Purge();
            var live = Live();
            if (live.Where(f => f.Owner == owner).Sum(f => f.Size) >= _maxPerPerson) throw ShareFull();
            if (live.Sum(f => f.Size) >= _maxTotal) throw ServerFull();
        }
    }

    private FullException ShareFull() => new($"your links hold {Mb(_maxPerPerson)} - wait for older ones to expire (1 hour)");

    private static FullException ServerFull() => new("the server's file space is full - try again later");

    /** Keeps the file for an hour as owner's ("user:" and a hash) and returns its id. Nothing is left of a refused or failed one. */
    public string Keep(DownloadedFile file, string owner)
    {
        long size = file.Bytes.LongLength;
        if (size > _maxFile) throw TooLarge(_maxFile);
        lock (_gate)
        {
            Purge();
            var live = Live();
            if (live.Where(f => f.Owner == owner).Sum(f => f.Size) + size > _maxPerPerson) throw ShareFull();
            if (live.Sum(f => f.Size) + size > _maxTotal) throw ServerFull();

            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var dir = Path.Combine(_root, id);
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, OwnerFile), owner);
                File.WriteAllText(Path.Combine(dir, ExpiresFile),
                    (_now() + Lifetime).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
                var path = Path.Combine(dir, NameOf(file.FileName));
                File.WriteAllBytes(path + Part, file.Bytes);
                File.Move(path + Part, path);
                return id;
            }
            catch
            {
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }
    }

    /** The file behind an id: null for an id never handed out, a malformed one, or one whose hour has passed. */
    public (string Path, string Name)? Find(string id)
    {
        if (!IsId(id)) return null; // 32 lower-case hex: nothing else reaches the disk, no "..", no separator
        var dir = Path.Combine(_root, id);
        if (!Directory.Exists(dir) || Expired(dir)) return null;
        var file = Payload(dir);
        return file == null ? null : (file, Path.GetFileName(file));
    }

    /** Removes what has expired (and anything that is not a link); returns how many links went. */
    public int Purge()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_root)) return 0;
            var removed = 0;
            foreach (var dir in Directory.GetDirectories(_root))
            {
                if (IsId(Path.GetFileName(dir)) && !Expired(dir)) continue;
                try
                {
                    Directory.Delete(dir, true);
                    removed++;
                }
                catch (IOException) { } // open for a download right now (Windows): the next sweep takes it
                catch (UnauthorizedAccessException) { }
            }
            return removed;
        }
    }

    private List<(string Owner, long Size)> Live() =>
        !Directory.Exists(_root)
            ? new()
            : Directory.GetDirectories(_root)
                .Select(dir => (Owner: ReadText(Path.Combine(dir, OwnerFile)) ?? "",
                    Size: Directory.GetFiles(dir).Where(f => !IsOwn(f)).Sum(f => new FileInfo(f).Length)))
                .ToList();

    private bool Expired(string dir) =>
        !long.TryParse(ReadText(Path.Combine(dir, ExpiresFile)), NumberStyles.None, CultureInfo.InvariantCulture, out var at)
        || _now().ToUnixTimeSeconds() >= at;

    private static string? Payload(string dir) =>
        Directory.GetFiles(dir).FirstOrDefault(f => !IsOwn(f) && !f.EndsWith(Part, StringComparison.Ordinal));

    /** The store's own files start with a dot. */
    private static bool IsOwn(string path) => Path.GetFileName(path).StartsWith('.');

    /** The name as a disk takes it, and never one of the store's own dot-names. */
    private static string NameOf(string fileName)
    {
        var name = SavedFiles.Safe(fileName);
        return name.StartsWith('.') ? "_" + name : name;
    }

    private static string? ReadText(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    internal static bool IsId(string id) => id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /** "100 MB"; a limit that is no whole number of megabytes (the tests' small ones) in bytes. */
    private static string Mb(long bytes) => bytes % MB == 0 ? bytes / MB + " MB" : bytes + " bytes";
}
