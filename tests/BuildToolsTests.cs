using ToolsMcp;
using Xunit;
using static TestIds;

public class BuildToolsTests : IDisposable
{
    private readonly FakeToolsApi _api = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tools-builds-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    private BuildTools Tools() => new(_api, new FakeTokens());

    [Fact]
    public async Task BuildKitSavesTheKitAndSaysWhatItHolds()
    {
        string reply = await Tools().BuildKit("Turning", [G("c1"), G("c2")],
            [new CustomToolInput("drill", 7.3, 90)], folder: _folder);

        string path = Path.Combine(_folder, "Turning.mKit");
        Assert.Equal($"Saved: {path} - 2 components and 1 custom tools. Import it into ENCY's tool library.", reply);
        Assert.True(File.Exists(path));
        Assert.Equal([G("c1"), G("c2")], _api.LastBuild!.Value.Ids);
        Assert.Equal(new CustomTool("drill", 7.3, 90), _api.LastBuild.Value.Custom.Single());
    }

    /**
     * A socket's connections as a list, under the version 3 the site's kits declare, made two formats in one kit: on
     * 13.11 (SA-12) the nightly ENCY refused such a kit and every library with it. The site writes the one shape the
     * version names, so the tools offer no other.
     */
    [Fact]
    public void NoKitToolOffersASocketShape()
    {
        Assert.DoesNotContain(typeof(BuildTools).GetMethod(nameof(BuildTools.BuildKit))!.GetParameters(),
            p => p.Name == "socketShape");
        Assert.DoesNotContain(typeof(LibraryTools).GetMethod(nameof(LibraryTools.ExportLibrary))!.GetParameters(),
            p => p.Name == "socketShape");
    }

    [Fact]
    public async Task TheServersReasonComesThrough()
    {
        _api.Fail[nameof(IToolsApi.BuildKit)] =
            new ToolsHttpException(400, """{"status":400,"message":"select between 1 and 500 components"}""");

        Assert.Equal("ERROR: select between 1 and 500 components", await Tools().BuildKit("Kit", [], folder: _folder));
    }

    [Fact]
    public async Task CustomToolIsSavedUnderTheServersName()
    {
        string reply = await Tools().BuildCustomTool("drill", 7.5, 60, folder: _folder);

        Assert.Equal("Saved: " + Path.Combine(_folder, "7.5mm Drill.mKit") + ". Import it into ENCY's tool library.", reply);
        Assert.Equal(new CustomTool("drill", 7.5, 60), _api.LastCustomTool);
    }

    /** The picks as GTC, for a system that reads it; what has no place in GTC is counted, not silently dropped. */
    [Fact]
    public async Task BuildGtcPackageSavesThePackageAndSaysWhatHadNoPlaceInIt()
    {
        _api.GtcLeftOut = 1;

        string reply = await Tools().BuildGtcPackage("Spindle", [G("c1"), G("c2")], _folder);

        string path = Path.Combine(_folder, "Spindle-gtc.zip");
        Assert.Equal($"Saved: {path} - a GTC package of 1 of the 2 parts; 1 has no place in GTC (our own components "
                     + "and catalogue rows with no GTC class are left out). " + BuildTools.GtcHint, reply);
        Assert.True(File.Exists(path));
        Assert.Equal([G("c1"), G("c2")], _api.LastGtc!.Value.Ids);
    }

    [Fact]
    public async Task AGtcPackageOfEveryPartSaysSo()
    {
        string reply = await Tools().BuildGtcPackage("Spindle", [G("c1"), G("c2")], _folder);

        Assert.Equal($"Saved: {Path.Combine(_folder, "Spindle-gtc.zip")} - a GTC package of 2 parts. " + BuildTools.GtcHint,
            reply);
    }

    [Fact]
    public async Task BuildGtcPackageRefusesWhatIsNoComponentId()
    {
        Assert.Equal("ERROR: 'ER16 collet' is not a component id - find_components gives them",
            await Tools().BuildGtcPackage("Spindle", [G("c1"), "ER16 collet"], _folder));
        Assert.False(_api.Called(nameof(IToolsApi.BuildGtcPackage)));
    }

    [Fact]
    public async Task AGtcPackageOfOurOwnPartsIsRefusedInTheServersWords()
    {
        _api.Fail[nameof(IToolsApi.BuildGtcPackage)] = new ToolsHttpException(400,
            """{"status":400,"message":"none of these parts comes from a supplier's catalogue with a GTC class"}""");

        Assert.Equal("ERROR: none of these parts comes from a supplier's catalogue with a GTC class",
            await Tools().BuildGtcPackage("Ours", [G("c1")], _folder));
    }

    [Fact]
    public async Task BuildKitWithNoComponentIdsStillBuildsItsCustomTools()
    {
        Assert.StartsWith("Saved:", await Tools().BuildKit("Kit", null!, [new CustomToolInput("drill", 7.3, 90)], folder: _folder));
        Assert.Empty(_api.LastBuild!.Value.Ids);
    }
}
