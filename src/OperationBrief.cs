using System.Text.Json.Nodes;

namespace ToolsMcp;

public sealed record OperationBrief(string? Operation, double? Diameter, double? Width, double? Depth, string? Bottom,
    double? Breakthrough, double? InnerRadius, bool? NoInsideCorner, string? Material, string? Spindle, int Offset, int Size)
{
    internal static OperationBrief FromContext(JsonObject context, string? mode, double? width, double? depth,
        double breakthrough, double? radius, bool? noInsideCorner, string material, string? spindle, int offset, int size)
    {
        material = string.IsNullOrWhiteSpace(material) ? context["material"]?.GetValue<string>() ?? "" : material;
        if (context["holes"] is JsonArray holes)
        {
            var diameters = Numbers(holes, "diameterMm");
            var depths = Numbers(holes, "depthMm");
            var bottoms = holes.Select(h => h?["bottom"]?.GetValue<string>()).Distinct().ToArray();
            double? diameter = diameters is { Length: 1 } ? diameters[0] : null;
            return new("drilling", width ?? diameter, null, depth ?? depths?.Max(), mode ?? (bottoms.Length == 1 ? bottoms[0] : null),
                breakthrough, null, null, material, spindle, offset, size);
        }
        var shapes = context["features"] as JsonArray ?? [];
        var heights = Numbers(shapes, "Height");
        var corners = Numbers(shapes, "Corner");
        bool shoulder = shapes.Count > 0 && shapes.All(s => s?["Kind"]?.GetValue<string>() == "shoulder");
        return new(mode ?? (shoulder ? "shoulder" : "milling"), null, width ?? context["availableWidthMm"]?.GetValue<double>(),
            depth ?? heights?.Max(), null, null, radius ?? (noInsideCorner == true ? null : corners?.Min()), noInsideCorner,
            material, spindle, offset, size);
    }

    private static double[]? Numbers(JsonArray entries, string field)
    {
        var values = entries.Select(e => e?[field]?.GetValue<double>()).ToArray();
        return values.Length > 0 && values.All(v => v is > 0 && double.IsFinite(v.Value))
            ? values.Select(v => v!.Value).Distinct().ToArray() : null;
    }
}
