using System.Text.Json.Nodes;
using ToolsMcp;
using Xunit;

public sealed class OperationBriefTests
{
    static OperationBrief Read(string json) => OperationBrief.FromContext(JsonNode.Parse(json)!.AsObject(),
        null, null, null, 0, null, null, "", null, 0, 20);

    [Fact] public void ThroughHoleUsesAllAssignedDepths() {
        var brief = Read("""{"holes":[{"diameterMm":10,"depthMm":32,"bottom":"through"},{"diameterMm":10,"depthMm":40,"bottom":"through"}]}""");
        Assert.Equal(10d,brief.Diameter);
        Assert.Equal(40d,brief.Depth);
        Assert.Equal("through",brief.Bottom);
    }

    [Fact] public void UnresolvedFacesDoNotSupplyKnownGeometry() {
        var b = Read("""{"features":[{"Kind":"shoulder","Height":15,"Corner":6},null]}""");
        Assert.Equal("milling", b.Operation);
        Assert.Null(b.Depth);
        Assert.Null(b.InnerRadius);
        Assert.Null(b.Width);
    }
    [Fact] public void PocketFloorPreservesAnExplicitZeroAndProvenance() {
        var flat=Read("""{"features":[{"Kind":"pocket","Height":4,"Corner":3,"BottomRadius":0}],"bottomRadiusMm":0,"bottomRadiusSource":"recognizedPocketFeature"}""");
        Assert.Equal("pocket",flat.Operation);
        Assert.Equal(0,flat.BottomRadius);
        Assert.Equal("recognizedPocketFeature",flat.BottomRadiusSource);
        var mixed=Read("""{"features":[{"Kind":"pocket","Height":4,"BottomRadius":2},null]}""");
        Assert.Null(mixed.BottomRadius);
        Assert.NotEqual("pocket",mixed.Operation);
        Assert.NotEqual("pocket",Read("""{"features":[{"Kind":"boss","Height":4}]}""").Operation);
    }
}
