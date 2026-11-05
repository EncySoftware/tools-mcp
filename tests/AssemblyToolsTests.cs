using ToolsMcp;
using Xunit;
using static TestIds;

/// <summary>
/// A tool as it stands built at the machine - cutter in collet in chuck, its T number, where it stands, the spindle it
/// works on and its measured gauge length - kept on a shelf as one item. Without it an agent worked the chain out and
/// ENCY still got loose parts with no reach.
/// </summary>
public class AssemblyToolsTests : IDisposable
{
    private readonly FakeToolsApi _api = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tools-assemblies-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly DateTimeOffset Updated = DateTimeOffset.Parse("2026-11-06T08:00:00Z");

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    private LibraryTools Tools() => new(_api, new FakeTokens());

    private void Shelf()
    {
        _api.Libraries[G("L1")] = new Library(G("L1"), "DMU 50", Updated, Updated, []);
        // the chuck takes the collet, the collet takes the drill
        _api.Accepts[G("k1")] = [G("c1")];
        _api.Accepts[G("c1")] = [G("d1")];
    }

    [Fact]
    public async Task AnAssemblyGoesOnTheShelfAsOneItemInTheOrderItFits()
    {
        Shelf();

        string reply = await Tools().CreateAssembly(G("L1"), [G("d1"), G("k1"), G("c1")], number: "T12",
            place: "magazine, pocket 12", spindle: "sub", gaugeLengthMm: 105);

        var sent = _api.LastAdded!.Single().Assembly!;
        Assert.Equal([G("k1"), G("c1"), G("d1")], sent.Parts.Select(p => p.Component));
        Assert.Equal("T12", sent.Number);
        Assert.Equal("SUB", sent.Spindle);
        Assert.Equal(105, sent.GaugeLengthMm);
        Assert.Equal("magazine, pocket 12", sent.Place);
        Assert.Contains("T12", reply);
        Assert.Contains("'DMU 50'", reply);
    }

    /** add_to_library takes assemblies beside components, ordered the same way. */
    [Fact]
    public async Task AddToLibraryTakesAssemblies()
    {
        Shelf();

        await Tools().AddToLibrary(G("L1"), assemblies:
        [
            new AssemblyInput([G("c1"), G("k1"), G("d1")], Number: "T3"),
            new AssemblyInput([G("k1")], Number: "T4", CustomTool: new CustomToolInput("endmill", 6, 50)),
        ]);

        var sent = _api.LastAdded!;
        Assert.Equal([G("k1"), G("c1"), G("d1")], sent[0].Assembly!.Parts.Select(p => p.Component));
        // a custom tool is a cutter: it goes at the cutting end
        Assert.Equal(G("k1"), sent[1].Assembly!.Parts[0].Component);
        Assert.Equal("endmill", sent[1].Assembly!.Parts[1].Custom!.Family);
    }

    [Fact]
    public async Task AWrongSpindleOrGaugeLengthIsRefusedBeforeItGoesOut()
    {
        Shelf();

        Assert.StartsWith("ERROR:", await Tools().CreateAssembly(G("L1"), [G("d1")], spindle: "left"));
        Assert.StartsWith("ERROR:", await Tools().CreateAssembly(G("L1"), [G("d1")], gaugeLengthMm: -3));
        Assert.StartsWith("ERROR:", await Tools().CreateAssembly(G("L1"), []));
        Assert.False(_api.Called(nameof(IToolsApi.AddToLibrary)));
    }

    /** Two parts next to each other that do not mate: kept as asked, but the agent is told where the chain breaks. */
    [Fact]
    public async Task AChainThatBreaksIsSaidToBreak()
    {
        Shelf();
        _api.Accepts.Remove(G("c1"));

        string reply = await Tools().CreateAssembly(G("L1"), [G("k1"), G("c1"), G("d1")], number: "T5");

        Assert.Contains("do not fit", reply);
    }

    [Fact]
    public async Task ShowLibraryListsAnAssemblyWithItsParts()
    {
        Shelf();
        await Tools().CreateAssembly(G("L1"), [G("k1"), G("c1"), G("d1")], number: "T12", spindle: "MAIN", gaugeLengthMm: 105);

        string reply = await Tools().ShowLibrary(G("L1"));

        Assert.Contains("T12", reply);
        Assert.Contains("MAIN spindle", reply);
        Assert.Contains("gauge 105 mm", reply);
        Assert.Contains("[assembly id ", reply);
    }

    [Fact]
    public async Task AnAssemblyIsTakenOutByItsId()
    {
        Shelf();
        await Tools().CreateAssembly(G("L1"), [G("k1"), G("c1"), G("d1")], number: "T12");
        string id = _api.Libraries[G("L1")].Items.Single().Assembly!.Id;

        string reply = await Tools().RemoveFromLibrary(G("L1"), assemblyIds: [id]);

        Assert.Contains("Removed 1", reply);
        Assert.Empty(_api.Libraries[G("L1")].Items);
    }

    /** The shelf as ENCY's tool list: each assembly one tool, built, numbered, reaching as far as measured. */
    [Fact]
    public async Task TheShelfGoesToEncyAsItsToolList()
    {
        Shelf();

        string reply = await new LibraryTools(_api, new FakeTokens()).ExportToolList(G("L1"), folder: _folder);

        Assert.Contains("DMU 50.tom", reply);
        Assert.Contains("Load existing tool list", reply);
        Assert.True(File.Exists(Path.Combine(_folder, "DMU 50.tom")));
    }
}
