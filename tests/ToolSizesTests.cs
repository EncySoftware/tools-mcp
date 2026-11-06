using ToolsMcp;
using Xunit;

/// <summary>SA-11: a US setup sheet writes a #7 drill and a 1/4-20 tap; a custom tool took millimetres only.</summary>
public class ToolSizesTests
{
    [Theory]
    [InlineData("1/4in", 6.35)]
    [InlineData("1/4\"", 6.35)]
    [InlineData("1-1/4in", 31.75)]
    [InlineData("0.5in", 12.7)]
    [InlineData("#7", 5.1054)]
    [InlineData("No. 7", 5.1054)]
    [InlineData("#80", 0.3429)]
    [InlineData("F", 6.5278)]
    [InlineData("6.8", 6.8)]
    [InlineData("6,8mm", 6.8)]
    public void ASizeIsReadInMillimetres(string size, double mm) => Assert.Equal(mm, ToolSizes.MillimetresOf(size)!.Value, 4);

    [Theory]
    [InlineData("M10", 10.0, null)]
    [InlineData("M10x1.25", 10.0, 1.25)]
    [InlineData("MF14x1.5", 14.0, 1.5)]
    [InlineData("1/4-20 UNC", 6.35, 1.27)]
    [InlineData("UNC1/4-20", 6.35, 1.27)]
    [InlineData("#10-32 UNF", 4.826, 0.79375)]
    [InlineData("6-32", 3.5052, 0.79375)]
    [InlineData("1-8 UNC", 25.4, 3.175)]
    public void AThreadGivesItsDiameterAndPitch(string thread, double d, double? p)
    {
        var t = ToolSizes.Thread(thread)!.Value;
        Assert.Equal(d, t.DiameterMm, 4);
        if (p is null) Assert.Null(t.PitchMm);
        else Assert.Equal(p.Value, t.PitchMm!.Value, 4);
    }

    [Fact]
    public void ACustomToolTakesItsSizeOrItsThread()
    {
        Assert.Equal(new CustomTool("drill", 5.105, 60), new CustomToolInput("drill", null, 60, Size: "#7").ToCustomTool());
        Assert.Equal(new CustomTool("tap", 6.35, 70, PitchMm: 1.27),
            new CustomToolInput("tap", null, 70, Thread: "1/4-20 UNC").ToCustomTool());
        Assert.Equal(6.0, new CustomToolInput("drill", 6, 60, Size: "#7").ToCustomTool().DiameterMm);
        Assert.Throws<ArgumentException>(() => new CustomToolInput("drill", null, 60).ToCustomTool());
        Assert.Throws<ArgumentException>(() => new CustomToolInput("drill", null, 60, Size: "huge").ToCustomTool());
    }
}
