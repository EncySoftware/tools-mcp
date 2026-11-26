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
}
