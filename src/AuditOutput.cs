using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ToolsMcp;

internal sealed record ShelfCandidate(AuditCutter ProjectTool, bool SameNumber, bool SameMagazine, bool SameLabel);
internal sealed record ShelfComparison(string? ShelfName, string? ComponentId, string? AssemblyId, string? Place,
    string? Number, double? MeasuredGaugeLengthMm, string? Article, string Status, ShelfCandidate[] Candidates);

internal static class AuditOutput
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static ShelfComparison[] Compare(Library library, IReadOnlyList<AuditCutter> projectTools)
    {
        static bool Equal(string? left, string? right) => !string.IsNullOrWhiteSpace(left) &&
            string.Equals(left.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
        return library.Items.Select(entry => {
            var assembly = entry.Assembly;
            var part = assembly?.Parts.LastOrDefault() ?? entry;
            string? raw = assembly?.Number?.Trim();
            if (raw?.StartsWith("T", StringComparison.OrdinalIgnoreCase) == true) raw = raw[1..].TrimStart('#').Trim();
            bool numbered = int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var number);
            var candidates = projectTools.Select(t => new ShelfCandidate(t, numbered && t.Position == number,
                    Equal(assembly?.Place, t.Magazine),
                    Equal(part.Reference?.Article, t.Name) || Equal(part.Name, t.Name) || Equal(entry.Name, t.Name)))
                .Where(c => c.SameNumber || c.SameLabel).ToArray();
            var slots = candidates.Where(c => c.SameNumber).ToArray();
            var scoped = slots.Where(c => c.SameMagazine).ToArray();
            bool knownMagazine = assembly?.Place != null && projectTools.Any(t =>
                t.Source == "machineMagazine" && Equal(assembly.Place, t.Magazine));
            var possible = knownMagazine ? scoped : slots;
            string status = knownMagazine && numbered && scoped.Length == 0 && slots.Length > 0 ? "numberInAnotherMagazine" :
                possible.Length > 1 ? "ambiguousPosition" :
                possible.Length == 1 ? possible[0].SameLabel ? "positionAndLabelAgree" : "positionWithDifferentLabel" :
                candidates.Length > 0 ? "labelOnly" : "noRecordedMatch";
            return new ShelfComparison(entry.Name, entry.Component, assembly?.Id, assembly?.Place, assembly?.Number,
                assembly?.GaugeLengthMm, part.Reference?.Article, status, candidates);
        }).ToArray();
    }

    internal static byte[] SetupCsv(AuditReport report)
    {
        var text = new StringBuilder();
        // a Russian Excel splits a sheet on ";" and reads numbers with a decimal comma
        text.AppendLine("Machine;Setup;Operation;Operation ID;Assigned ID;Tool;Tool source;Magazine;T number;Adapters (tool to magazine);Diameter mm;Working reach mm;Required working reach mm;Point angle deg;Point angle state;Result;Reasons;Missing fields");
        foreach (var row in report.Operations)
        {
            var tool = row.AssignedTool;
            var reasons = row.Review.Reasons.Select(r =>
                r.Code + (r.ActualMm is double actual ? $" ({Number(actual)} mm; required {Number(r.RequiredMm)} mm)" : ""));
            string?[] fields = [row.Machine,row.Setup,row.OperationName,row.OperationId,row.AssignedId,
                tool?.Name,tool?.Source,tool?.Magazine,tool?.Position.ToString(CultureInfo.InvariantCulture),
                tool?.Adapters == null ? null : string.Join(" > ",tool.Adapters),Number(tool?.DiameterMm),Number(tool?.ReachMm),
                Number(row.Review.RequiredReachMm),Number(tool?.PointAngleDegrees),tool?.PointAngleState,row.Review.Status,string.Join(" / ",reasons),string.Join(" / ",row.Review.Missing)];
            text.AppendLine(string.Join(";", fields.Select(Cell)));
        }
        return [..Encoding.UTF8.GetPreamble(), ..Encoding.UTF8.GetBytes(text.ToString())];
    }

    private static readonly NumberFormatInfo SheetNumbers = new() { NumberDecimalSeparator = "," };
    private static string? Number(double? v) => v?.ToString("G12",SheetNumbers);

    private static string Cell(string? value)
    {
        value ??= "";
        string trimmed = value.TrimStart();
        if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
