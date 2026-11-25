using Xunit;
using ToolsMcp;

public class CuttingSettingTests
{
    [Fact]
    public void ArticleKeepsEachEndpointInItsUnit()
    {
        string[][] fields = [["Feed per revolution", "min. 0.12 mm/rev   ↔   max. 0.18 mm/rev"],
            ["Cutting speed", "90 m/min"], ["Feed per tooth", "2 mm/rev"]];
        var low = CuttingSetting.FromArticle(fields, false);
        var high = CuttingSetting.FromArticle(fields, true);
        Assert.True(CuttingSetting.IsPair(low));
        Assert.Equal(new CuttingSetting("mm/rev", 0.12), low[0]);
        Assert.Equal(new CuttingSetting("mm/rev", 0.18), high[0]);
        Assert.Equal(new CuttingSetting("m/min", 90), high[1]);
        Assert.Equal(90 / 0.3048, high[1].OperationValue(true), 9);
        Assert.Equal(0.18 / 25.4, high[0].OperationValue(true), 9);
    }

    [Theory]
    [InlineData(true, "rpm", "mm/tooth", 2100, 0.105, 0, 2)]
    [InlineData(false, "m/min", "mm/rev", 90, 0.12, 1, 1)]
    public void AppliesSelectedFeedAndSpeed(bool milling, string speedUnit, string feedUnit,
        double speed, double feed, int speedMode, int feedMode)
    {
        CuttingSetting[] pair = [new(speedUnit, speed), new(feedUnit, feed)];
        var modes = new Dictionary<string, int>();
        var values = new Dictionary<string, double>();
        int commits = 0;
        CuttingSetting.Apply(pair, milling, false, _ => true, k => modes.GetValueOrDefault(k), (k, v) => modes[k] = v,
            k => values.GetValueOrDefault(k), (k, v) => values[k] = v, () => commits++);
        Assert.Equal(1, commits);
        Assert.Equal(speed, values[pair[0].Paths(milling).ValuePath]);
        Assert.Equal(feed, values[pair[1].Paths(milling).ValuePath]);
        Assert.Equal(speedMode, modes[pair[0].Paths(milling).ModePath]);
        Assert.Equal(feedMode, modes[pair[1].Paths(milling).ModePath]);
    }

    [Fact]
    public void RestoreAfterSecondWriteFails()
    {
        CuttingSetting[] pair = [new("rpm", 2000), new("mm/tooth", 0.105)];
        var modes = pair.ToDictionary(s => s.Paths(true).ModePath, s => 1);
        var values = pair.ToDictionary(s => s.Paths(true).ValuePath, s => 5d);
        bool failed = false;
        Assert.Throws<InvalidOperationException>(() => CuttingSetting.Apply(pair, true, false, _ => true,
            k => modes[k], (k,v) => modes[k] = v, k => values[k], (k,v) =>
            {
                if (!failed && k.EndsWith("ValuePerTooth")) { failed = true; throw new InvalidOperationException(); }
                values[k] = v;
            }, () => {}));
        Assert.All(modes.Values, v => Assert.Equal(1,v));
        Assert.All(values.Values, v => Assert.Equal(5d,v));
        Assert.Throws<InvalidOperationException>(() => pair[1].Paths(false));
    }
}