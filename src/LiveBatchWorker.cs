using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ToolsMcp;

// Stdin stays open until the operator answers. An orphaned helper cannot proceed from approval to a write.
internal static class LiveBatchWorker
{
    internal static readonly TimeSpan OperatorLimit = TimeSpan.FromMinutes(30);
    internal const string Waiting = "MCP_BATCH_WAITING_FOR_OPERATOR";
    internal const string Approved = "MCP_BATCH_OPERATOR_APPROVED";
    internal const string Proceed = "MCP_BATCH_PROCEED";

    internal static bool RequestConsent(TextReader supervisor, TextWriter progress, Func<Func<bool>,bool> show)
    {
        progress.WriteLine(Waiting);
        progress.Flush();
        // Console.In may be a synchronized TextReader whose ReadLineAsync blocks the calling thread.
        var permission = Task.Run(supervisor.ReadLine);
        if (!show(() => permission.IsCompleted)) return false;
        progress.WriteLine(Approved);
        progress.Flush();
        return permission.GetAwaiter().GetResult() == Proceed;
    }

    internal static async Task<string> Run(ProcessStartInfo start, string input, CancellationToken cancellation,
        TimeSpan? ipcTimeout = null, TimeSpan? operatorTimeout = null)
    {
        var request = JsonSerializer.Deserialize<ToolPlanRequest>(input, AuditOutput.Json)!;
        var limit = ipcTimeout ?? TimeSpan.FromMinutes(4);
        if (cancellation.IsCancellationRequested) return ProjectToolChangePlan.Interrupted(request, "");
        using var deadline = new CancellationTokenSource(limit);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token,cancellation);
        using var worker = Process.Start(start) ?? throw new InvalidOperationException("Could not start ENCY IPC.");
        var stdout = worker.StandardOutput.ReadToEndAsync();
        var diagnostics = new StringBuilder();
        int awaitingOperator = 0;
        bool released = false;
        var write = SendRequest();
        async Task ReadProgress()
        {
            bool waiting = false;
            while (await worker.StandardError.ReadLineAsync() is { } line)
            {
                if (line == Waiting && !waiting) {
                    waiting = true;
                    Volatile.Write(ref awaitingOperator, 1);
                    deadline.CancelAfter(operatorTimeout ?? OperatorLimit);
                } else if (line == Approved && waiting) {
                    waiting = false;
                    Volatile.Write(ref awaitingOperator, 0);
                    deadline.CancelAfter(limit);
                    if (!stop.IsCancellationRequested) {
                        try {
                            await write;
                            if (!stop.IsCancellationRequested) {
                                await worker.StandardInput.WriteLineAsync(Proceed);
                                released = true;
                            }
                        }
                        catch (IOException) { /* A closed pipe cannot authorize the write. */ }
                        catch (ObjectDisposedException) when (stop.IsCancellationRequested) { }
                    }
                } else diagnostics.AppendLine(line);
            }
        }
        async Task SendRequest()
        {
            try { await worker.StandardInput.WriteLineAsync(input); }
            catch (IOException) { /* The helper may exit during startup. */ }
        }
        var progress = ReadProgress();
        try { await worker.WaitForExitAsync(stop.Token); }
        catch (OperationCanceledException)
        {
            if (Volatile.Read(ref awaitingOperator) == 1)
            {
                worker.StandardInput.Close(); // EOF closes our own dialog at its next timer tick.
                await Task.WhenAny(worker.WaitForExitAsync(),Task.Delay(TimeSpan.FromSeconds(2)));
            }
            try { if (!worker.HasExited) worker.Kill(entireProcessTree:true); }
            catch (InvalidOperationException) when (worker.HasExited) { }
            await worker.WaitForExitAsync();
        }
        await Task.WhenAll(write,progress);
        string reply = (await stdout).Trim();
        if (worker.ExitCode == 0 && reply.Length > 0) return reply;
        return ProjectToolChangePlan.Interrupted(request, released ? diagnostics.ToString() : string.Empty);
    }
}
