using ToolsMcp;
using Xunit;

public class LibraryToolsTests : IDisposable
{
    private readonly FakeToolsApi _api = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tools-libraries-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly DateTimeOffset Updated = DateTimeOffset.Parse("2026-10-21T12:00:00Z");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    private LibraryTools Tools() => new(_api, new FakeTokens());

    private static Library Lib(string id, string name, params LibraryItem[] items) => new(id, name, Updated, Updated, items);

    private static LibraryItem Item(string component, string name, bool present) =>
        new(component, name, "drills", present, "Drill", "Drills Ø1–20 mm", 10, 70, null);

    /** A second "Turning" beside the first is a duplicate the person did not ask for. */
    [Fact]
    public async Task CreateLibraryStopsAtASameNamedOne()
    {
        _api.Libraries["L9"] = Lib("L9", "Turning");

        Assert.Equal("ERROR: you already have a library named 'Turning' [id L9] - add_to_library adds to it; "
                     + "force=true makes a second one", await Tools().CreateLibrary(" turning "));
        Assert.False(_api.Called(nameof(IToolsApi.CreateLibrary)));

        string forced = await Tools().CreateLibrary("Turning", force: true);
        Assert.Contains("Created 'Turning'", forced);
        Assert.Equal(2, _api.Libraries.Count);
    }

    [Fact]
    public async Task AddingSaysHowToOpenItInEncy()
    {
        _api.Libraries["L1"] = Lib("L1", "Milling", Item("c1", "10mm Drill", true));

        string reply = await Tools().AddToLibrary("L1", componentIds: ["c2"]);

        Assert.Contains("now holds 2 items (was 1)", reply);
        Assert.EndsWith("In ENCY: the Tools catalogue extension lists your libraries - open it there.", reply);
        Assert.StartsWith("ERROR:", await Tools().AddToLibrary("L1"));
    }

    /** Taking one item out keeps the rest - one the supplier has since withdrawn included. */
    [Fact]
    public async Task RemoveKeepsWhatWasNotNamed()
    {
        _api.Libraries["L1"] = Lib("L1", "Kept", Item("c1", "10mm Drill", false), Item("c2", "12mm Drill", true));

        string reply = await Tools().RemoveFromLibrary("L1", componentIds: ["c2"]);

        Assert.Contains("10mm Drill", reply);
        Assert.Contains("GONE from the catalogue", reply);
        Assert.DoesNotContain("12mm Drill", reply);
        Assert.Single(_api.Libraries["L1"].Items);
    }

    [Fact]
    public async Task ShowLibraryMarksItemsGoneFromTheCatalogue()
    {
        _api.Libraries["L1"] = Lib("L1", "Kept", Item("c1", "10mm Drill", false),
            new LibraryItem(null, null, null, true, "drill", "Custom tool", 7.5, 60, new CustomTool("drill", 7.5, 60)));

        string reply = await Tools().ShowLibrary("L1");

        Assert.Contains("10mm Drill", reply);
        Assert.Contains("GONE from the catalogue", reply);
        Assert.Contains("custom drill Ø7.5 L 60 mm", reply);
    }

    [Fact]
    public async Task ListLibrariesCountsWhatIsGone()
    {
        _api.Libraries["L1"] = Lib("L1", "Kept", Item("c1", "10mm Drill", false), Item("c2", "12mm Drill", true));

        Assert.Equal("Kept [id L1] - 2 items (1 gone from the catalogue), updated 2026-10-21", await Tools().ListLibraries());
        _api.Libraries.Clear();
        Assert.Equal("No libraries yet - create_library makes one.", await Tools().ListLibraries());
    }

    [Fact]
    public async Task DeleteSaysWhatWasDeleted()
    {
        _api.Libraries["L1"] = Lib("L1", "Old");

        Assert.Equal("Deleted 'Old'.", await Tools().DeleteLibrary("L1"));
        Assert.Empty(_api.Libraries);
        Assert.Equal("ERROR: no library L1", await Tools().DeleteLibrary("L1"));
    }

    [Fact]
    public async Task TheLimitsComeFromTheServer()
    {
        _api.Fail[nameof(IToolsApi.CreateLibrary)] = new ToolsHttpException(400,
            """{"status":400,"message":"an account holds up to 50 libraries — delete one first"}""");

        Assert.Equal("ERROR: an account holds up to 50 libraries — delete one first", await Tools().CreateLibrary("New"));
    }

    [Fact]
    public async Task ExportSavesTheLibraryAsAKit()
    {
        _api.Libraries["L1"] = Lib("L1", "Turning");

        string reply = await Tools().ExportLibrary("L1", folder: _folder);

        Assert.Contains("Saved: " + Path.Combine(_folder, "Turning.mKit"), reply);
        Assert.StartsWith("ERROR: socketShape", await Tools().ExportLibrary("L1", socketShape: "tree", folder: _folder));
    }
}
