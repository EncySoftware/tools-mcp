using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ToolsMcp;

/// <summary>A drill or end mill the catalogue lacks, by its numbers: family drill or endmill.</summary>
public record CustomToolInput(
    [property: Description("drill or endmill")] string Family,
    [property: Description("Cutting diameter, mm (0.1 to 100)")] double DiameterMm,
    [property: Description("Overall length, mm (1 to 400)")] double LengthMm,
    [property: Description("Shank diameter, mm (0.1 to 100); the cutting diameter when left out")] double? ShankDiameterMm = null,
    [property: Description("A name; by default the size and the family")] string? Name = null)
{
    public CustomTool ToCustomTool() => new(Family, DiameterMm, LengthMm, ShankDiameterMm, Name);
}

/// <summary>Kits built on tools.encycam.com and saved to the person's disk as .mKit files.</summary>
[McpServerToolType]
public class BuildTools(IToolsApi api, TokenProvider tokens, IFileSink? files = null)
{
    internal const string ImportHint = "Import it into ENCY's tool library.";

    /** Where a saved file goes: the person's disk at home, a link on the hosted server. */
    private readonly IFileSink _files = files ?? LocalFiles.Instance;

    [McpServerTool(Name = "build_kit"), Description(
        "Builds the chosen components, plus any custom drills and end mills, into one .mKit for ENCY 3 and saves it to " +
        "the person's disk. Only components that can go into a kit (find_components buildableOnly=true); 500 at " +
        "most. To keep tools in the person's account instead, use add_to_library.")]
    public Task<string> BuildKit(
        [Description("The kit's name")] string name,
        [Description("The components' ids, from find_components")] string[] componentIds,
        [Description("Custom drills and end mills to add")] CustomToolInput[]? customTools = null,
        [Description("Leave empty for released ENCY; \"list\" for ENCY builds after 1 September 2026")] string? socketShape = null,
        [Description("Folder to save into; by default Downloads\\ENCY tools")] string? folder = null) =>
        Replies.WithToken(tokens, async token =>
        {
            _files.Check(folder);
            string? shape = SocketShape(socketShape);
            if (SocketShapeError(shape) is { } refused) return refused;
            var (ids, bad) = Ids.All(componentIds);
            if (bad != null) return Ids.NotAComponent(bad);
            var custom = (customTools ?? []).Select(t => t.ToCustomTool()).ToList();
            var file = await api.BuildKit(name, ids, custom, shape, token);
            return $"{_files.Deliver(file, folder)} - {ids.Count} components and {custom.Count} custom tools. "
                   + ImportHint;
        });

    [McpServerTool(Name = "build_custom_tool"), Description(
        "A drill or end mill the catalogue does not carry, built from its numbers as an .mKit and saved to the " +
        "person's disk. family: drill or endmill; diameter 0.1 to 100 mm; length 1 to 400 mm; shank 0.1 to 100 mm.")]
    public Task<string> BuildCustomTool(
        [Description("drill or endmill")] string family,
        [Description("Cutting diameter, mm (0.1 to 100)")] double diameterMm,
        [Description("Overall length, mm (1 to 400)")] double lengthMm,
        [Description("Shank diameter, mm (0.1 to 100); the cutting diameter when left out")] double? shankDiameterMm = null,
        [Description("A name; by default the size and the family")] string? name = null,
        [Description("Folder to save into; by default Downloads\\ENCY tools")] string? folder = null) =>
        Replies.WithToken(tokens, async token =>
        {
            _files.Check(folder);
            var file = await api.BuildCustomTool(new CustomTool(family, diameterMm, lengthMm, shankDiameterMm, name), token);
            return $"{_files.Deliver(file, folder)}. " + ImportHint;
        });

    /**
     * ENCY changed on 1 September 2026 how a socket names its connections - a list rather than one - and each build
     * refuses the other's shape. Empty - null, or "" as an agent passes it - keeps what every released ENCY reads.
     */
    internal static string? SocketShape(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

    internal static string? SocketShapeError(string? socketShape) =>
        socketShape is null or "list"
            ? null
            : "ERROR: socketShape is empty (released ENCY) or \"list\" (ENCY builds after 1 September 2026)";
}
