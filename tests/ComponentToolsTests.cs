using System.Text.Json.Nodes;
using ToolsMcp;
using Xunit;
using static TestIds;

public class ComponentToolsTests : IDisposable
{
    private readonly FakeToolsApi _api = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tools-files-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    private ComponentTools Tools() => new(_api, new FakeTokens());

    private static Component Holder(string id, string name) => new(id, name, "External turning tool", "TRNGEI", null, 150,
        "sandvik-gtc", "Sandvik Coromant catalogue", 3, true, true, false, null, true, "Sandvik Coromant");

    [Fact]
    public async Task DescribeComponentListsItsParameters()
    {
        _api.Components[G("c1")] = new ComponentDetail(Holder(G("c1"), "C6-DCLNL"),
            JsonNode.Parse("""{"OAL":"150 mm","CCMS":"C6"}""")!.AsObject());
        _api.Compats[G("c1")] = new Compat("cap:C6", "Capto C6", [], 4, [], [], 12, null);

        string reply = await Tools().DescribeComponent(G("c1"));

        Assert.Contains($"C6-DCLNL [id {G("c1")}]", reply);
        Assert.Contains("CCMS: C6", reply);
        Assert.True(reply.IndexOf("CCMS: C6", StringComparison.Ordinal) < reply.IndexOf("OAL: 150 mm", StringComparison.Ordinal),
            "parameters sorted by key");
        Assert.Contains("Connection: Capto C6; goes into 4, takes 12", reply);
    }

    [Fact]
    public async Task CuttingDataKeepsTheMaterialSourceUnitsAndEngagementTogether()
    {
        var feeds = new Dictionary<string, double> { ["Slot"] = 0.0239 };
        var speeds = new Dictionary<string, long> { ["Slot"] = 191 };
        _api.Cutting = new CuttingData("SER", "maker.pdf", 6, 5.9, 2, ["Slot"],
            [new("STEEL", "180 HB", ["1.0503"], 100, 4000, feeds, speeds),
             new("ALUMINIUM", null, [], 200, 8000, feeds, speeds)], new Dictionary<string,string> { ["Slot"] = "Full slot" }, null);
        var reply = await Tools().GetCuttingData(G("c1"), "1.0503");
        foreach (var term in new[]{"maker.pdf", "STEEL", "180 HB", "0.0239 mm/tooth", "191 mm/min", "Full slot", "4000 rpm"})
            Assert.Contains(term, reply);
        Assert.DoesNotContain("ALUMINIUM", reply);
        Assert.Contains("No matching material", await Tools().GetCuttingData(G("c1"), "Titanium"));
    }

    [Fact]
    public async Task AdapterChainReportsTheOrderedIdsAndUnmatchedComponents()
    {
        string a = G("c1"), b = G("c2"), adapter = G("h1");
        _api.AdapterChain = [Holder(a, "First"), Holder(adapter, "Bridge"), Holder(b, "Last")];
        string reply = await Tools().FindAdapterChain(b, a);
        Assert.Contains("1 intermediate adapter", reply);
        Assert.True(reply.IndexOf(a, StringComparison.Ordinal) < reply.IndexOf(adapter, StringComparison.Ordinal));
        Assert.True(reply.IndexOf(adapter, StringComparison.Ordinal) < reply.IndexOf(b, StringComparison.Ordinal));
        _api.AdapterChain = [];
        Assert.Contains("No chain", await Tools().FindAdapterChain(a, b));
    }

    /** An id that names nothing and an expired sign-in call for different things: a new search, or a new sign-in. */
    [Fact]
    public async Task AnUnknownIdIsNotFoundNotASignInProblem()
    {
        Assert.Equal($"ERROR: no component {G("nope")} - the id is wrong or it is in a catalogue this account cannot see",
            await Tools().DescribeComponent(G("nope")));

        _api.Fail[nameof(IToolsApi.GetComponent)] = new ToolsHttpException(401, "");
        Assert.Equal("ERROR: the sign-in has expired or was refused - run tools-mcp login",
            await Tools().DescribeComponent(G("nope")));
    }

    [Fact]
    public async Task WhatFitsShowsTotalsAndHowToSeeTheRest()
    {
        _api.Compats[G("c2")] = new Compat("seat", "this insert pocket", [Holder(G("h1"), "DCLNL 2525M 12")], 37, [], [], 0, null);

        string reply = await Tools().WhatFits(G("c2"), "holders");

        Assert.Contains("Goes into 37", reply);
        Assert.Contains($"DCLNL 2525M 12 [id {G("h1")}]", reply);
        Assert.EndsWith($"All of them: find_components(fitsComponentId: \"{G("c2")}\", fitDirection: \"holders\")", reply);
        Assert.Equal("ERROR: direction is both, holders or accepts", await Tools().WhatFits(G("c2"), "sideways"));
    }

    [Fact]
    public async Task AnAssemblyThatStopsSaysWhy()
    {
        _api.Assemblies[G("c3")] = new AssemblyChain([new AssemblyStep(Holder(G("h1"), "C6-DCLNL"), 3)], false,
            "nothing in the catalogue holds that");

        string reply = await Tools().AssembleToSpindle(G("c3"));

        Assert.Contains($"1. C6-DCLNL [id {G("h1")}]", reply);
        Assert.Contains("+3 other choices", reply);
        Assert.Contains("Stopped: nothing in the catalogue holds that", reply);
    }

    [Fact]
    public async Task ZeroAdaptersMeansDirectMountOrTheBackendsReason()
    {
        _api.Assemblies[G("c3")] = new AssemblyChain([], true, "mounts in a Capto C3 spindle");
        Assert.Equal("This component mounts directly in a Capto C3 spindle.", await Tools().AssembleToSpindle(G("c3")));
        _api.Assemblies[G("c3")] = new AssemblyChain([], false, "no chain reaches SK50");
        Assert.Equal("Stopped: no chain reaches SK50", await Tools().AssembleToSpindle(G("c3")));
    }

    /** Groshev, 2026-10-22: the chain for the machine at hand - the spindle goes to the site, and the reply names it. */
    [Fact]
    public async Task AnAssemblyToTheSpindleAskedNamesIt()
    {
        _api.Assemblies[G("c3")] = new AssemblyChain([new AssemblyStep(Holder(G("h1"), "HSK63 ER16 chuck"), 2)], true,
            "mounts in a HSK63 spindle");

        string reply = await Tools().AssembleToSpindle(G("c3"), "HSK63");

        Assert.Equal("HSK63", _api.LastSpindle);
        Assert.EndsWith("Reaches the spindle: mounts in a HSK63 spindle.", reply);
        await Tools().AssembleToSpindle(G("c3"), "  ");
        Assert.Null(_api.LastSpindle);
    }

    /** An insert is no ENCY component of its own: it goes into a kit with the holder it seats in. */
    [Fact]
    public void AnInsertSaysItGoesWithItsHolder()
    {
        var insert = Holder(G("i1"), "R390-11 T3 08M-PM 4240") with { Section = "INSCMIL", Pullable = false };
        Assert.Contains("goes into a kit with its holder", Replies.Line(insert));
        Assert.Contains("cannot go into a kit", Replies.Line(insert with { Section = "VENDOR" }));
    }

    /** The site takes a chain of parts that all go into a kit where one exists: one that cannot is said, not hidden. */
    [Fact]
    public async Task AChainThroughAPartNoKitTakesIsSaidToBeOne()
    {
        var chuck = Holder(G("h2"), "HSK-F63 ER16 chuck") with { Pullable = false };
        _api.Assemblies[G("c4")] = new AssemblyChain([new AssemblyStep(chuck, 0)], true, "mounts in a HSK-F63 spindle");

        string reply = await Tools().AssembleToSpindle(G("c4"), "HSK-F63");

        Assert.Contains("cannot go into a kit", reply);
        Assert.Contains("NOTE: no chain of parts that all go into a kit reaches it", reply);
    }

    /** Names are looked up one by one, so the ids are capped: 51 would be 51 lookups for one answer. */
    [Fact]
    public async Task CheckFitNeedsTwoToFiftyIds()
    {
        const string refused = "ERROR: check_fit takes 2 to 50 component ids";

        Assert.Equal(refused, await Tools().CheckFit([G("c1")]));
        Assert.Equal(refused, await Tools().CheckFit(Enumerable.Range(0, 51).Select(i => "c" + i).ToArray()));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task CheckFitSaysWhatTakesWhat()
    {
        _api.Components[G("h1")] = new ComponentDetail(Holder(G("h1"), "C6-DCLNL"), null);
        _api.Components[G("i1")] = new ComponentDetail(Holder(G("i1"), "CNMG 120408"), null);
        _api.Components[G("x1")] = new ComponentDetail(Holder(G("x1"), "R390 end mill"), null);
        _api.Accepts[G("h1")] = [G("i1")];

        string reply = await Tools().CheckFit([G("h1"), G("i1"), G("x1")]);

        Assert.Contains("C6-DCLNL takes CNMG 120408", reply);
        Assert.Contains($"Fit with none of the others: R390 end mill [id {G("x1")}]", reply);
    }

    [Fact]
    public async Task SaveComponentFileNamesTheFileAfterTheComponent()
    {
        _api.Components[G("c1")] = new ComponentDetail(Holder(G("c1"), "C6-DCLNL/R 00090-12"), null);
        _api.ComponentFiles[(G("c1"), ComponentFileKind.Model)] = new DownloadedFile("", [7]);

        string reply = await Tools().SaveComponentFile(G("c1"), "model", _folder);

        string path = Path.Combine(_folder, "C6-DCLNL_R 00090-12.stp");
        Assert.Contains("Saved: " + path, reply);
        Assert.True(File.Exists(path));
        Assert.Equal("ERROR: C6-DCLNL/R 00090-12 has no drawing on tools.encycam.com",
            await Tools().SaveComponentFile(G("c1"), "drawing", _folder));
    }

    /** An agent passes a designation where an id goes; the server answered that with a raw 400 body. */
    [Fact]
    public async Task AMalformedIdIsRefusedWithoutACall()
    {
        Assert.Equal("ERROR: 'CNMG 120408' is not a component id - find_components gives them",
            await Tools().DescribeComponent("CNMG 120408"));
        Assert.Empty(_api.Calls);
    }

    /** The server keys its answer by lower-case ids; upper-case ones crashed the lookup. */
    [Fact]
    public async Task CheckFitAcceptsUpperCaseIds()
    {
        _api.Components[G("h1")] = new ComponentDetail(Holder(G("h1"), "C6-DCLNL"), null);
        _api.Components[G("i1")] = new ComponentDetail(Holder(G("i1"), "CNMG 120408"), null);
        _api.Accepts[G("h1")] = [G("i1")];

        string reply = await Tools().CheckFit([G("h1").ToUpperInvariant(), G("i1").ToUpperInvariant()]);

        Assert.Contains("C6-DCLNL takes CNMG 120408", reply);
    }
}
