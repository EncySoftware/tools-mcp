using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ToolsMcp;
using Xunit;

public class LiveBatchWorkerTests
{
    private static ProcessStartInfo Helper(string powershell, string sh)
    {
        var start = new ProcessStartInfo { UseShellExecute=false, CreateNoWindow=true, RedirectStandardInput=true,
            RedirectStandardOutput=true, RedirectStandardError=true };
        if (OperatingSystem.IsWindows()) {
            start.FileName=Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe");
            foreach (var arg in new[] {"-NoProfile","-NonInteractive","-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(powershell))}) start.ArgumentList.Add(arg);
        } else {
            start.FileName="/bin/sh"; start.ArgumentList.Add("-c"); start.ArgumentList.Add(sh);
        }
        return start;
    }

    private static readonly string Input = JsonSerializer.Serialize(new ToolPlanRequest("part.stcp", "metric",
        [new(Guid.NewGuid().ToString(),"42","id","through",10,40)], "preview", true),AuditOutput.Json);

    [Fact] public async Task HumanWaitCanExceedTheIpcTimeBudget()
    {
        var p = Helper(
            $"$null=[Console]::ReadLine(); [Console]::Error.WriteLine('{LiveBatchWorker.Waiting}'); Start-Sleep -Seconds 6; " +
            $"[Console]::Error.WriteLine('{LiveBatchWorker.Approved}'); [Console]::WriteLine([Console]::ReadLine())",
            $"read -r req; echo '{LiveBatchWorker.Waiting}' >&2; sleep 6; echo '{LiveBatchWorker.Approved}' >&2; read -r ack; echo \"$ack\"");
        Assert.Equal(LiveBatchWorker.Proceed, await LiveBatchWorker.Run(p, Input, CancellationToken.None,
            TimeSpan.FromSeconds(5),TimeSpan.FromSeconds(20)));
    }

    [Fact] public async Task UnansweredApprovalExpiresBeforeAnyWrite()
    {
        var p = Helper(
            $"$null=[Console]::ReadLine(); [Console]::Error.WriteLine('{LiveBatchWorker.Waiting}'); $null=[Console]::ReadLine(); exit 2",
            $"read -r req; echo '{LiveBatchWorker.Waiting}' >&2; read -r ack; exit 2");
        var report = JsonNode.Parse(await LiveBatchWorker.Run(p,Input,CancellationToken.None,
            TimeSpan.FromSeconds(10),TimeSpan.FromMilliseconds(150)))!;
        Assert.Equal("notApplied",(string?)report["status"]);
        Assert.Equal("notApplied",(string?)report["outcomes"]![0]!["status"]);
    }

    [Theory]
    [InlineData(false,"MCP_BATCH_PROCEED",false)]
    [InlineData(true,"",false)]
    [InlineData(true,"MCP_BATCH_PROCEED",true)]
    public void BothDesktopAndSupervisorMustApprove(bool operatorAccepted,string acknowledgement,bool expected)
    {
        using var input=new StringReader(acknowledgement);
        using var progress=new StringWriter();
        Assert.Equal(expected,LiveBatchWorker.RequestConsent(input,progress,_=>operatorAccepted));
        Assert.Contains(LiveBatchWorker.Waiting,progress.ToString());
        Assert.Equal(operatorAccepted,progress.ToString().Contains(LiveBatchWorker.Approved));
    }

    [Fact] public async Task CancelledCallDoesNotStartItsWorker()
    {
        var report=JsonNode.Parse(await LiveBatchWorker.Run(new ProcessStartInfo("no-such-program"),
            Input,new CancellationToken(canceled:true)))!;
        Assert.Equal("notApplied",(string?)report["status"]);
        Assert.Equal("notApplied",(string?)report["outcomes"]![0]!["status"]);
    }
}
