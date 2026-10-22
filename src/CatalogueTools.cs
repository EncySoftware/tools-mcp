using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace ToolsMcp;

/// <summary>Who the agent works as, and the kits and supplier catalogues the account can see.</summary>
[McpServerToolType]
public class CatalogueTools(IToolsApi api, TokenProvider tokens)
{
    /**
     * An ENCY 2 library holds hundreds of tools with a dozen parameters each: listed whole, describe_kit came near the
     * limit of what an agent's tool reply may hold (final review, 2026-10-22). The rest is a search away.
     */
    internal const int ShownItems = 50;

    [McpServerTool(Name = "whoami"), Description(
        "Who the agent works as on tools.encycam.com: the account whose supplier catalogues and libraries it sees. " +
        "Call it first when in doubt.")]
    public Task<string> WhoAmI() => Replies.WithToken(tokens, async token =>
    {
        var me = await api.GetMe(token);
        return $"Signed in to tools.encycam.com as {me.Username ?? me.Sub}" + (me.Email != null ? $" ({me.Email})." : ".");
    });

    [McpServerTool(Name = "list_kits"), Description(
        "Our ready kits and the supplier catalogues on tools.encycam.com, with how many of each can go into a kit. " +
        "A supplier catalogue is not downloaded whole: search it with find_components.")]
    public Task<string> ListKits(
        [Description("Words to look for in kit names, e.g. collet")] string? query = null,
        [Description("MILLING, TURNING, TOOLING or MIXED")] string? discipline = null,
        [Description("The ENCY version a kit is for: 3 or 2")] int? encyVersion = null) =>
        Replies.WithToken(tokens, async token =>
        {
            var kits = await api.ListKits(query, discipline, encyVersion, token);
            if (kits.Count == 0) return "No kits match.";
            var downloads = await api.GetDownloads(token);
            var reply = new StringBuilder();
            foreach (var k in kits)
            {
                reply.Append($"{k.Name} (slug {k.Slug}) - {k.Maker ?? "Generic"}, {k.Format}, ENCY {k.EncyVersion}, "
                             + $"{k.ItemsTotal} items, {k.PullableTotal ?? 0} can go into a kit, "
                             + $"{downloads.GetValueOrDefault(k.Slug)} downloads");
                if (k.Format == "gtc") reply.Append(" - supplier catalogue - pick from it with find_components");
                reply.AppendLine();
            }
            reply.Append("describe_kit(slug) shows a kit's classes; download_kit(slug) saves one of our kits.");
            return reply.ToString();
        });

    [McpServerTool(Name = "describe_kit"), Description(
        "One kit or catalogue: what it is, its classes with their codes and counts, and for our own kits every item " +
        "with its parameters. A class code goes into find_components(classCode).")]
    public Task<string> DescribeKit([Description("The kit's slug, from list_kits")] string slug) =>
        Replies.WithToken(tokens, async token =>
        {
            var kit = await api.GetKit(slug, token);
            if (kit == null) return NoKit(slug);
            var reply = new StringBuilder();
            reply.AppendLine($"{kit.Name} (slug {kit.Slug}) - {kit.Category}, ENCY {kit.EncyVersion}, {kit.Format}, {kit.ItemsTotal} items");
            if (!string.IsNullOrWhiteSpace(kit.Description)) reply.AppendLine(kit.Description);
            if (kit.Sections.Count > 0)
            {
                reply.AppendLine("Classes:");
                foreach (var s in kit.Sections) reply.AppendLine($"  {s.Title ?? s.Section} ({s.Section}): {s.Count}");
            }
            if (kit.Format == "gtc")
            {
                reply.Append($"A supplier catalogue: find_components(kit: \"{kit.Slug}\") searches it.");
                return reply.ToString();
            }
            if (kit.Items.Count > 0)
            {
                reply.AppendLine("Items:");
                foreach (var item in kit.Items.Take(ShownItems))
                {
                    reply.Append($"  {item.Name} - {item.Kind ?? item.Section}");
                    if (item.DiameterMm is { } d) reply.Append($", Ø{Replies.Mm(d)} mm");
                    if (item.LengthMm is { } l) reply.Append($", L {Replies.Mm(l)} mm");
                    if (item.Params is { Count: > 0 } p)
                        reply.Append(" - " + string.Join("; ", p.Select(kv => $"{kv.Key}: {kv.Value}")));
                    reply.AppendLine();
                }
                if (kit.Items.Count > ShownItems)
                    reply.AppendLine($"  … and {kit.Items.Count - ShownItems} more: find_components(kit: \"{kit.Slug}\")");
            }
            reply.Append($"download_kit(\"{kit.Slug}\") saves its file.");
            return reply.ToString();
        });

    [McpServerTool(Name = "download_kit"), Description(
        "Saves one of OUR kits' files (.mKit for ENCY 3, .db for ENCY 2) to the person's disk and says where. " +
        "A supplier catalogue is refused: pick its components with find_components, then build_kit or add_to_library.")]
    public Task<string> DownloadKit(
        [Description("The kit's slug, from list_kits")] string slug,
        [Description("Folder to save into; by default Downloads\\ENCY tools")] string? folder = null) =>
        Replies.WithToken(tokens, async token =>
        {
            var kit = await api.GetKit(slug, token);
            if (kit == null) return NoKit(slug);
            if (kit.Format == "gtc")
                return $"ERROR: {kit.Name} is a supplier catalogue - its file is the catalogue's own data, which ENCY "
                       + "cannot open. Pick its components with find_components, then build_kit or add_to_library.";
            string path = SavedFiles.Save(await api.DownloadKit(slug, token), folder);
            return "Saved: " + path;
        });

    private static string NoKit(string slug) => $"ERROR: no kit '{slug}' - list_kits shows the slugs this account can see";
}
