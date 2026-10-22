using ToolsMcp;
using Xunit;

public class CatalogueToolsTests : IDisposable
{
    private readonly FakeToolsApi _api = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tools-kits-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    private CatalogueTools Tools(string? token = "t1") => new(_api, new FakeTokens(token));

    private static KitDetail Kit(string slug, string name, string format) =>
        new(slug, name, "Category", "MIXED", 3, format, "About it.", 2,
            [new SectionCount("INSI", 2, "ISO insert", true, null)], []);

    [Fact]
    public async Task NotSignedInSaysHowToSignIn()
    {
        var tools = Tools(token: null);
        const string expected = "ERROR: not signed in - run tools-mcp login";

        Assert.Equal(expected, await tools.WhoAmI());
        Assert.Equal(expected, await tools.ListKits());
        Assert.Equal(expected, await tools.DescribeKit("drills"));
        Assert.Equal(expected, await tools.DownloadKit("drills"));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task WhoAmINamesTheAccount()
    {
        Assert.Equal("Signed in to tools.encycam.com as alice (alice@example.com).", await Tools().WhoAmI());
    }

    [Fact]
    public async Task ListKitsMarksSupplierCatalogues()
    {
        _api.Kits.Add(new KitSummary("drills", "Drills Ø1–20 mm", "Hole-making tools", "MILLING", 3, "mkit", "Generic", 37, 20, null));
        _api.Kits.Add(new KitSummary("iscar-catalogue", "Iscar catalogue", "Supplier catalogue · Iscar", "MIXED", 3, "gtc", "Iscar", 24333, 9000, null));

        string reply = await Tools().ListKits();

        string iscar = reply.Split('\n').Single(l => l.Contains("slug iscar-catalogue"));
        string drills = reply.Split('\n').Single(l => l.Contains("slug drills"));
        Assert.Contains("supplier catalogue - pick from it with find_components", iscar);
        Assert.DoesNotContain("supplier catalogue", drills);
    }

    /** A supplier catalogue's file is its raw data: ENCY cannot open it, and handing it out is wholesale export. */
    [Fact]
    public async Task DownloadKitRefusesASupplierCatalogue()
    {
        _api.KitDetails["iscar-catalogue"] = Kit("iscar-catalogue", "Iscar catalogue", "gtc");

        string reply = await Tools().DownloadKit("iscar-catalogue", _folder);

        Assert.Equal("ERROR: Iscar catalogue is a supplier catalogue - its file is the catalogue's own data, which ENCY "
                     + "cannot open. Pick its components with find_components, then build_kit or add_to_library.", reply);
        Assert.False(_api.Called(nameof(IToolsApi.DownloadKit)));
    }

    [Fact]
    public async Task DownloadKitSavesOurKitAndSaysWhere()
    {
        _api.KitDetails["drills"] = Kit("drills", "Drills Ø1–20 mm", "mkit");
        _api.KitFiles["drills"] = new DownloadedFile("Drills_D1-20.mKit", [1, 2]);

        string reply = await Tools().DownloadKit("drills", _folder);

        string path = Path.Combine(_folder, "Drills_D1-20.mKit");
        Assert.Contains("Saved: " + path, reply);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task AnExpiredSignInAsksToSignInAgain()
    {
        _api.Fail[nameof(IToolsApi.ListKits)] = new ToolsHttpException(401, "");

        Assert.Equal("ERROR: the sign-in has expired or was refused - run tools-mcp login", await Tools().ListKits());
    }

    [Fact]
    public async Task TheServersReasonIsPassedThrough()
    {
        _api.Fail[nameof(IToolsApi.GetKit)] = new ToolsHttpException(400, """{"status":400,"message":"kit not found"}""");

        Assert.Equal("ERROR: kit not found", await Tools().DescribeKit("x"));
    }
}
