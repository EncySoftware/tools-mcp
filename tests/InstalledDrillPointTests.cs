using ToolsMcp;
using Xunit;

public class InstalledDrillPointTests
{
    [Theory]
    [InlineData("TSTMillAxisTool.Drill", true)]
    [InlineData("DrillMill", true)]
    [InlineData("SpotDrillMill", false)]
    [InlineData("CenterDrillMill", false)]
    public void RecognizesOnlyStandardDrills(string type, bool expected) =>
        Assert.Equal(expected, InstalledDrillPoint.IsStandardDrill(type));

    [Fact] public void ReadsOnlyAdvertisedDrillParameters()
    {
        Assert.Null(InstalledDrillPoint.ReadAngle(["HelixAngle", "PL"], _ => throw new Exception()));
        Assert.Equal(118, InstalledDrillPoint.ReadAngle(["Angle"], key => key == "Angle" ? 118 : throw new Exception()));
        var parameters = new Dictionary<string,double> { ["Angle"] = 0, ["sig"] = 130 };
        Assert.Equal(130, InstalledDrillPoint.ReadAngle(parameters.Keys.ToList(), key => parameters[key]));
    }

    [Fact] public void PointAllowanceIsUsedForFilteringAndAssignment()
    {
        Assert.Equal(46, InstalledDrillPoint.RequiredDepth(10, 40, 1, 90)!.Value, 8);
        Assert.Equal(41, InstalledDrillPoint.RequiredDepth(10, 40, 1, 180));
        foreach (var missing in new double?[] { null, 0, -1, 181, double.NaN, double.PositiveInfinity })
            Assert.Null(InstalledDrillPoint.RequiredDepth(10, 40, 1, missing));
    }
}
