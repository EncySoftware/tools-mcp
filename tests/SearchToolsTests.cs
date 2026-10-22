using ToolsMcp;
using Xunit;

public class SearchToolsTests
{
    private readonly FakeToolsApi _api = new();

    private SearchTools Tools() => new(_api, new FakeTokens());

    private static Component Mill(string id) => new(id, "TE90AP 120-W20-17", "End mill", "MIL", 20, 120,
        "taegutec-catalogue", "TaeguTec catalogue", 3, true, true, true, null, true, "TaeguTec");

    /** A page of 1000 rows is a catalogue a few calls at a time - the export the catalogues are closed against. */
    [Fact]
    public async Task PageSizeIsCappedAtFifty()
    {
        string reply = await Tools().FindComponents(query: "mill", pageSize: 1000);

        Assert.Equal(50, _api.LastQuery!.Size);
        Assert.Contains("at most 50", reply);
    }

    [Fact]
    public async Task FiltersReachTheApiUnderTheirNames()
    {
        await Tools().FindComponents(query: "q", kit: "k", classCode: "MIL", family: "Milling cutter", machineType: "mill",
            maker: "TaeguTec", coating: "TiAlN", coolant: "true", shank: "cyl", minDiameterMm: 10, maxDiameterMm: 12,
            fitsComponentId: "c1", buildableOnly: true, encyVersion: 3, sort: "diameter", descending: true, page: 2,
            pageSize: 10);

        Assert.Equal(new ComponentQuery("q", "k", "MIL", "Milling cutter", "mill", "TaeguTec", "TiAlN", "true", "cyl",
            10, 12, "c1", true, 3, "diameter", true, 2, 10), _api.LastQuery);

        // "not only buildable" is no filter at all, not "only what cannot be built"
        await Tools().FindComponents(query: "q");
        Assert.Null(_api.LastQuery!.Buildable);
    }

    [Fact]
    public async Task EachRowSaysWhatItGoesIntoAndTakes()
    {
        _api.Page = new ComponentsPage(1, 0, 20, [Mill("c1")]);
        _api.Counts["c1"] = new CompatCounts(3, 2);

        string reply = await Tools().FindComponents(kit: "taegutec-catalogue");

        Assert.StartsWith("1 found, page 1 of 1", reply);
        Assert.Contains("TE90AP 120-W20-17 [id c1]", reply);
        Assert.Contains("goes into 3, takes 2", reply);
    }

    [Fact]
    public async Task AnUnknownSortIsRefused()
    {
        Assert.Equal("ERROR: sort is name, diameter or length", await Tools().FindComponents(sort: "price"));
        Assert.False(_api.Called(nameof(IToolsApi.FindComponents)));
    }

    [Fact]
    public async Task SearchOptionsNamesClassesWithTheirCodes()
    {
        _api.Filters = new FilterValues(
            [new SectionCount("MIL", 193, "Milling cutter", true, null)],
            [new SectionCount("Milling cutter", 193, null, false, null)],
            [new SectionCount("TaeguTec", 2137, null, false, null)], [], [],
            new SizeRange(1, 315), []);

        string reply = await Tools().SearchOptions(kit: "taegutec-catalogue");

        Assert.Contains("Milling cutter (MIL): 193", reply);
        Assert.Contains("Ø 1–315 mm", reply);
        Assert.Equal("taegutec-catalogue", _api.LastQuery!.Kit);
    }
}
