using System.Text.Json.Nodes;
using ToolsMcp;
using Xunit;

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
        _api.Components["c1"] = new ComponentDetail(Holder("c1", "C6-DCLNL"),
            JsonNode.Parse("""{"OAL":"150 mm","CCMS":"C6"}""")!.AsObject());
        _api.Compats["c1"] = new Compat("cap:C6", "Capto C6", [], 4, [], [], 12, null);

        string reply = await Tools().DescribeComponent("c1");

        Assert.Contains("C6-DCLNL [id c1]", reply);
        Assert.Contains("CCMS: C6", reply);
        Assert.True(reply.IndexOf("CCMS: C6", StringComparison.Ordinal) < reply.IndexOf("OAL: 150 mm", StringComparison.Ordinal),
            "parameters sorted by key");
        Assert.Contains("Connection: Capto C6; goes into 4, takes 12", reply);
    }

    /** An id that names nothing and an expired sign-in call for different things: a new search, or a new sign-in. */
    [Fact]
    public async Task AnUnknownIdIsNotFoundNotASignInProblem()
    {
        Assert.Equal("ERROR: no component nope - the id is wrong or it is in a catalogue this account cannot see",
            await Tools().DescribeComponent("nope"));

        _api.Fail[nameof(IToolsApi.GetComponent)] = new ToolsHttpException(401, "");
        Assert.Equal("ERROR: the sign-in has expired or was refused - run tools-mcp login",
            await Tools().DescribeComponent("nope"));
    }

    [Fact]
    public async Task WhatFitsShowsTotalsAndHowToSeeTheRest()
    {
        _api.Compats["c2"] = new Compat("seat", "this insert pocket", [Holder("h1", "DCLNL 2525M 12")], 37, [], [], 0, null);

        string reply = await Tools().WhatFits("c2", "holders");

        Assert.Contains("Goes into 37", reply);
        Assert.Contains("DCLNL 2525M 12 [id h1]", reply);
        Assert.EndsWith("All of them: find_components(fitsComponentId: \"c2\")", reply);
        Assert.Equal("ERROR: direction is both, holders or accepts", await Tools().WhatFits("c2", "sideways"));
    }

    [Fact]
    public async Task AnAssemblyThatStopsSaysWhy()
    {
        _api.Assemblies["c3"] = new AssemblyChain([new AssemblyStep(Holder("h1", "C6-DCLNL"), 3)], false,
            "nothing in the catalogue holds that");

        string reply = await Tools().AssembleToSpindle("c3");

        Assert.Contains("1. C6-DCLNL [id h1]", reply);
        Assert.Contains("+3 other choices", reply);
        Assert.Contains("Stopped: nothing in the catalogue holds that", reply);
    }

    /** Names are looked up one by one, so the ids are capped: 51 would be 51 lookups for one answer. */
    [Fact]
    public async Task CheckFitNeedsTwoToFiftyIds()
    {
        const string refused = "ERROR: check_fit takes 2 to 50 component ids";

        Assert.Equal(refused, await Tools().CheckFit(["c1"]));
        Assert.Equal(refused, await Tools().CheckFit(Enumerable.Range(0, 51).Select(i => "c" + i).ToArray()));
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task CheckFitSaysWhatTakesWhat()
    {
        _api.Components["h1"] = new ComponentDetail(Holder("h1", "C6-DCLNL"), null);
        _api.Components["i1"] = new ComponentDetail(Holder("i1", "CNMG 120408"), null);
        _api.Components["x1"] = new ComponentDetail(Holder("x1", "R390 end mill"), null);
        _api.Accepts["h1"] = ["i1"];

        string reply = await Tools().CheckFit(["h1", "i1", "x1"]);

        Assert.Contains("C6-DCLNL takes CNMG 120408", reply);
        Assert.Contains("Fit with none of the others: R390 end mill [id x1]", reply);
    }

    [Fact]
    public async Task SaveComponentFileNamesTheFileAfterTheComponent()
    {
        _api.Components["c1"] = new ComponentDetail(Holder("c1", "C6-DCLNL/R 00090-12"), null);
        _api.ComponentFiles[("c1", ComponentFileKind.Model)] = new DownloadedFile("", [7]);

        string reply = await Tools().SaveComponentFile("c1", "model", _folder);

        string path = Path.Combine(_folder, "C6-DCLNL_R 00090-12.stp");
        Assert.Contains("Saved: " + path, reply);
        Assert.True(File.Exists(path));
        Assert.Equal("ERROR: C6-DCLNL/R 00090-12 has no drawing on tools.encycam.com",
            await Tools().SaveComponentFile("c1", "drawing", _folder));
    }
}
