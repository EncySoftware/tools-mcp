using System.Runtime.Versioning;
using ToolsMcp;
using Xunit;

/** The commands that write into the open project wait for the operator's switch, as the batch apply does. */
[Collection("environment")]
[SupportedOSPlatform("windows")]
public class LiveWriteSwitchTests
{
    [Fact]
    public async Task WritingCommandsRefuseUntilTheOperatorEnablesWrites()
    {
        var before = Environment.GetEnvironmentVariable(LiveProjectChangeTools.Setting);
        Environment.SetEnvironmentVariable(LiveProjectChangeTools.Setting, null);
        try
        {
            var tools = new LocalEncyTools(new FakeToolsApi(), new FakeTokens());
            const string op = "0f8fad5b-d9cb-469f-a165-70867728950e";
            var replies = new[]
            {
                await tools.ImportProjectGtc(1, "C:/p.stcp", "C:/kit.zip"),
                await tools.InstallShelfInProject(op, 1, "C:/p.stcp"),
                await tools.AssignInstalledOperationTool(1, "C:/p.stcp", op, "T1", "id", "metric", "tip", 8, 20),
                await tools.AssignOperationMill(1, "C:/p.stcp", op, op),
                await tools.AssignOperationDrill(1, "C:/p.stcp", op, op),
                await tools.ApplyOperationCutting(op, 1, "maximum", "m/min", "mm/rev", 1, "C:/p.stcp", op, "metric"),
            };
            Assert.All(replies, r => Assert.StartsWith("ERROR: writing to the open ENCY project is disabled", r));
            // a preview only reads, so the switch leaves it alone
            var preview = await tools.ApplyOperationCutting("not-an-id", 1, "maximum", "m/min", "mm/rev", 1, "C:/p.stcp", op, "metric", previewOnly: true);
            Assert.DoesNotContain("disabled", preview);
        }
        finally { Environment.SetEnvironmentVariable(LiveProjectChangeTools.Setting, before); }
    }
}
