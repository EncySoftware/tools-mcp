namespace ToolsMcp;

/// <summary>
/// The scratch copies simulated projects live in. A simulation killed at its time limit cannot remove its own copy,
/// so the next one sweeps copies older than any live simulation can be.
/// </summary>
internal static class SimulationScratch
{
    internal const string Prefix = "ency-mcp-simulation-";

    internal static int Sweep(string root, DateTime nowUtc, TimeSpan olderThan)
    {
        int removed = 0;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, Prefix + "*"))
            {
                try
                {
                    if (nowUtc - Directory.GetLastWriteTimeUtc(dir) < olderThan) continue;
                    Directory.Delete(dir, true);
                    removed++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* still open: the next sweep */ }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* no temporary folder to sweep */ }
        return removed;
    }
}
