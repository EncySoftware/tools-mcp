using ToolsMcp;
using Xunit;

public class SimulationScratchTests
{
    [Fact]
    public void AKilledSimulationsCopyIsSweptOnceItIsStaleAndOthersAreLeftAlone()
    {
        var root = Directory.CreateTempSubdirectory("scratch-sweep-").FullName;
        try
        {
            var stale = Directory.CreateDirectory(Path.Combine(root, SimulationScratch.Prefix + "killed")).FullName;
            File.WriteAllText(Path.Combine(stale, "snapshot.stcp"), "copy");
            var running = Directory.CreateDirectory(Path.Combine(root, SimulationScratch.Prefix + "running")).FullName;
            var unrelated = Directory.CreateDirectory(Path.Combine(root, "someone-else")).FullName;
            var now = DateTime.UtcNow;
            Directory.SetLastWriteTimeUtc(stale, now.AddHours(-2));
            Directory.SetLastWriteTimeUtc(unrelated, now.AddHours(-2));

            Assert.Equal(1, SimulationScratch.Sweep(root, now, TimeSpan.FromHours(1)));
            Assert.False(Directory.Exists(stale));
            Assert.True(Directory.Exists(running));
            Assert.True(Directory.Exists(unrelated));
        }
        finally { Directory.Delete(root, true); }
    }
}
