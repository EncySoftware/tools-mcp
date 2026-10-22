using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using ToolsMcp;
using Xunit;

/** The hosted server's files: an hour each behind a random id, within a file's, a person's and the server's limits. */
public sealed class DownloadStoreTests : IDisposable
{
    private const long MB = 1024 * 1024;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tools-files-" + Guid.NewGuid().ToString("N")[..8]);
    private DateTimeOffset _now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private DownloadStore Store() => new(_root, () => _now);

    /** Megabytes, not hundreds of them: 1 MB a file, 3 MB a person, 5 MB in all. */
    private DownloadStore Small() => new(_root, () => _now, 1 * MB, 3 * MB, 5 * MB);

    private static DownloadedFile Mb(string name) => new(name, new byte[MB]);

    private int Kept() => Directory.Exists(_root) ? Directory.GetDirectories(_root).Length : 0;

    [Fact]
    public void KeepsAFileUnderARandomId()
    {
        var store = Store();
        var id = store.Keep(new DownloadedFile("Mills.mKit", [1, 2, 3]), "user:anna");
        Assert.Matches("^[0-9a-f]{32}$", id);
        var found = store.Find(id);
        Assert.NotNull(found);
        Assert.Equal("Mills.mKit", found.Value.Name);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(found.Value.Path));
        Assert.NotEqual(id, store.Keep(new DownloadedFile("Mills.mKit", [1, 2, 3]), "user:anna"));
    }

    [Fact]
    public void ALinkLivesAnHour()
    {
        var store = Store();
        var id = store.Keep(new DownloadedFile("Mills.mKit", [1]), "user:anna");
        _now += TimeSpan.FromMinutes(59);
        Assert.NotNull(store.Find(id));
        _now += TimeSpan.FromMinutes(2);
        Assert.Null(store.Find(id));
        Assert.Equal(1, store.Purge());
        Assert.Equal(0, Kept());
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../../etc")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public void AnUnknownOrMalformedIdFindsNothing(string id) => Assert.Null(Store().Find(id));

    [Fact]
    public void AFileOverTheLimitIsRefusedAndNotKept()
    {
        var e = Assert.Throws<DownloadStore.FullException>(() =>
            Store().Keep(new DownloadedFile("big.mKit", new byte[DownloadStore.MaxFile + 1]), "user:anna"));
        Assert.Equal("a file holds at most 100 MB", e.Message);
        Assert.Equal(0, Kept());
    }

    [Fact]
    public void APersonOverTheirShareIsRefused()
    {
        var store = Small();
        for (var i = 0; i < 3; i++) store.Keep(Mb($"k{i}.mKit"), "user:anna");
        var e = Assert.Throws<DownloadStore.FullException>(() => store.Keep(new DownloadedFile("one.mKit", [1]), "user:anna"));
        Assert.Equal("your links hold 3 MB - wait for older ones to expire (1 hour)", e.Message);
        Assert.Equal(3, Kept());
        store.Keep(new DownloadedFile("one.mKit", [1]), "user:boris"); // someone else's share is untouched

        _now += TimeSpan.FromMinutes(61); // the hour passes, and the share is free again
        store.Keep(Mb("k3.mKit"), "user:anna");
    }

    [Fact]
    public void AFullStoreRefusesEveryone()
    {
        var store = Small();
        foreach (var owner in new[] { "user:a", "user:a", "user:a", "user:b", "user:b" }) store.Keep(Mb("k.mKit"), owner);
        var e = Assert.Throws<DownloadStore.FullException>(() => store.Keep(new DownloadedFile("one.mKit", [1]), "user:c"));
        Assert.Equal("the server's file space is full - try again later", e.Message);
    }

    /** A dot-name is the store's own (".owner", ".expires"): a file named like one must not overwrite it. */
    [Fact]
    public void AFileNamedLikeTheStoresOwnKeepsItsLink()
    {
        var store = Store();
        var id = store.Keep(new DownloadedFile(".expires", [7]), "user:anna");
        var found = store.Find(id);
        Assert.NotNull(found);
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(found.Value.Path));
    }

    [Fact]
    public async Task TheJanitorSweepsAsSoonAsItStarts()
    {
        var store = Store();
        store.Keep(new DownloadedFile("Mills.mKit", [1]), "user:anna");
        _now += TimeSpan.FromHours(2);
        using var janitor = new DownloadJanitor(store, NullLogger<DownloadJanitor>.Instance);
        await janitor.StartAsync(CancellationToken.None);
        for (var i = 0; i < 100 && Kept() > 0; i++) await Task.Delay(20);
        await janitor.StopAsync(CancellationToken.None);
        Assert.Equal(0, Kept());
        Assert.Equal(TimeSpan.FromMinutes(10), DownloadJanitor.Every);
    }
}
