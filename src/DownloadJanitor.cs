using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ToolsMcp;

/// <summary>
/// Sweeps the hosted server's expired links: at start - a container stopped for an update leaves its files on the
/// volume - and every ten minutes after. A failed sweep is logged and the next one tries again; it never stops the
/// server.
/// </summary>
public sealed class DownloadJanitor(DownloadStore store, ILogger<DownloadJanitor> log) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        do
        {
            try
            {
                var removed = store.Purge();
                if (removed > 0) log.LogInformation("Removed {Count} expired links", removed);
            }
            catch (Exception e)
            {
                log.LogWarning("Could not sweep the expired links: {Error}", e.Message);
            }
        }
        while (await Next(timer, stoppingToken));
    }

    private static async Task<bool> Next(PeriodicTimer timer, CancellationToken stop)
    {
        try { return await timer.WaitForNextTickAsync(stop); }
        catch (OperationCanceledException) { return false; }
    }
}
