using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ToolsMcp;

/// <summary>A tool the catalogue lacks, by its numbers - or by the size or thread a setup sheet writes it by.</summary>
public record CustomToolInput(
    [property: Description("drill, endmill, ball, torus (an end mill with a corner radius), facemill, spot, center, countersink, chamfer, tap, reamer")] string Family,
    [property: Description("Cutting diameter, mm (0.1 to 100; a face mill to 400) - or give size or thread instead")]
    double? DiameterMm,
    [property: Description("Overall length, mm (1 to 400)")] double LengthMm,
    [property: Description("Shank diameter, mm (0.1 to 100); the cutting diameter when left out")] double? ShankDiameterMm = null,
    [property: Description("A name; by default the size and the family")] string? Name = null,
    [property: Description("Corner radius, mm: an endmill's, torus's or facemill's")] double? CornerRadiusMm = null,
    [property: Description("Included angle, degrees: a drill's point, a spot or center drill's, a countersink's or chamfer mill's")]
    double? AngleDeg = null,
    [property: Description("A tap's pitch, mm; a metric tap takes its coarse pitch when left out")] double? PitchMm = null,
    [property: Description("The diameter as a setup sheet writes it: 1/4in, 1/2\", #7, F, 6.8mm")] string? Size = null,
    [property: Description("A tap's thread, which gives its diameter and pitch: M10, M10x1.25, 1/4-20 UNC, #10-32")]
    string? Thread = null)
{
    /** The tool in millimetres: a size or a thread read into its diameter and pitch, the numbers given first. */
    public CustomTool ToCustomTool()
    {
        double? diameter = DiameterMm;
        double? pitch = PitchMm;
        if (!string.IsNullOrWhiteSpace(Thread))
        {
            var thread = ToolSizes.Thread(Thread)
                         ?? throw new ArgumentException($"'{Thread.Trim()}' is no thread: M10, M10x1.25, 1/4-20 UNC, #10-32");
            diameter ??= thread.DiameterMm;
            pitch ??= thread.PitchMm;
        }
        if (!string.IsNullOrWhiteSpace(Size))
            diameter ??= ToolSizes.MillimetresOf(Size)
                         ?? throw new ArgumentException($"'{Size.Trim()}' is no size: 1/4in, 1/2\", #7, F, 6.8mm");
        if (diameter is not { } d)
            throw new ArgumentException("give the tool's diameterMm, its size (1/4in, #7, F) or its thread (M10, 1/4-20 UNC)");
        return new CustomTool(Family, Math.Round(d, 3), LengthMm, ShankDiameterMm, Name, CornerRadiusMm, AngleDeg,
            pitch is { } p ? Math.Round(p, 4) : null);
    }
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

    internal const string GtcHint = "A system that reads GTC imports it; ENCY takes build_kit's .mKit.";

    [McpServerTool(Name = "build_gtc_package"), Description(
        "Writes the chosen components as a GTC package - ISO 13399 p21 data with 3D models and pictures, for a CAM or " +
        "tool management system that reads GTC rather than ENCY's .mKit - and saves it to the person's disk. Only " +
        "supplier catalogues' components with a GTC class go in; our own components are left out and counted. Parts " +
        "of several makers go in one package, each under its maker. 500 at most.")]
    public Task<string> BuildGtcPackage(
        [Description("The package's name")] string name,
        [Description("The components' ids, from find_components")] string[] componentIds,
        [Description("Folder to save into; by default Downloads\\ENCY tools")] string? folder = null) =>
        Replies.WithToken(tokens, async token =>
        {
            _files.Check(folder);
            var (ids, bad) = Ids.All(componentIds);
            if (bad != null) return Ids.NotAComponent(bad);
            var package = await api.BuildGtcPackage(name, ids, token);
            int picked = ids.Distinct().Count();
            string what = package.LeftOut > 0
                ? $"a GTC package of {picked - package.LeftOut} of the {picked} parts; {package.LeftOut} "
                  + $"{(package.LeftOut == 1 ? "has" : "have")} no place in GTC (our own components and catalogue rows "
                  + "with no GTC class are left out)"
                : $"a GTC package of {picked} parts";
            return $"{_files.Deliver(package.File, folder)} - {what}. " + GtcHint;
        });

    [McpServerTool(Name = "build_custom_tool"), Description(
        "A tool the catalogue does not carry, built from its numbers as an .mKit and saved to the person's disk: " +
        "drill, endmill, ball, torus (an end mill with a corner radius), facemill, spot, center, countersink, chamfer, tap, reamer. Diameter 0.1 to 100 mm (a face mill " +
        "to 400) - or a size as a setup sheet writes it (1/4in, #7, F) or a tap's thread (M10, 1/4-20 UNC); length 1 to " +
        "400 mm; shank 0.1 to 100 mm; a corner radius for an end mill, an angle for a spot drill or countersink, a pitch " +
        "for a tap.")]
    public Task<string> BuildCustomTool(
        [Description("drill, endmill, ball, torus (an end mill with a corner radius), facemill, spot, center, countersink, chamfer, tap, reamer")] string family,
        [Description("Cutting diameter, mm (0.1 to 100; a face mill to 400) - or give size or thread")] double? diameterMm = null,
        [Description("Overall length, mm (1 to 400)")] double? lengthMm = null,
        [Description("Shank diameter, mm (0.1 to 100); the cutting diameter when left out")] double? shankDiameterMm = null,
        [Description("A name; by default the size and the family")] string? name = null,
        [Description("Corner radius, mm: an endmill's, torus's or facemill's")] double? cornerRadiusMm = null,
        [Description("Included angle, degrees: a drill's point, a spot or center drill's, a countersink's")] double? angleDeg = null,
        [Description("A tap's pitch, mm; a metric tap takes its coarse pitch when left out")] double? pitchMm = null,
        [Description("The diameter as a setup sheet writes it: 1/4in, #7, F, 6.8mm")] string? size = null,
        [Description("A tap's thread: M10, M10x1.25, 1/4-20 UNC, #10-32")] string? thread = null,
        [Description("Folder to save into; by default Downloads\\ENCY tools")] string? folder = null) =>
        Replies.WithToken(tokens, async token =>
        {
            _files.Check(folder);
            if (lengthMm is not { } length) return "ERROR: give the tool's lengthMm, its overall length";
            var tool = new CustomToolInput(family, diameterMm, length, shankDiameterMm, name, cornerRadiusMm, angleDeg,
                pitchMm, size, thread).ToCustomTool();
            var file = await api.BuildCustomTool(tool, token);
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
