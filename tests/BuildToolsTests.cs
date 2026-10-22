using ToolsMcp;
using Xunit;

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
        string reply = await Tools().BuildKit("Turning", ["c1", "c2"],
            [new CustomToolInput("drill", 7.3, 90)], folder: _folder);

        string path = Path.Combine(_folder, "Turning.mKit");
        Assert.Equal($"Saved: {path} - 2 components and 1 custom tools. Import it into ENCY's tool library.", reply);
        Assert.True(File.Exists(path));
        Assert.Equal(["c1", "c2"], _api.LastBuild!.Value.Ids);
        Assert.Equal(new CustomTool("drill", 7.3, 90), _api.LastBuild.Value.Custom.Single());
    }

    /** ENCY changed how a socket names its connections on 1 September 2026; each build refuses the other's shape. */
    [Fact]
    public async Task TheSocketShapeIsListOrNothing()
    {
        Assert.Equal("ERROR: socketShape is empty (released ENCY) or \"list\" (ENCY builds after 1 September 2026)",
            await Tools().BuildKit("Kit", ["c1"], socketShape: "tree", folder: _folder));
        Assert.False(_api.Called(nameof(IToolsApi.BuildKit)));

        await Tools().BuildKit("Kit", ["c1"], socketShape: "list", folder: _folder);
        Assert.Equal("list", _api.LastBuild!.Value.Sockets);
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
}
