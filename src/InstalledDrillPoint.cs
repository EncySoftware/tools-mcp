namespace ToolsMcp;

internal static class InstalledDrillPoint
{
    internal static bool IsStandardDrill(string type)
    {
        string name = type.Split('.').Last();
        return name.Equals("Drill", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("DrillMill", StringComparison.OrdinalIgnoreCase);
    }

    // The ENCY drill template stores Angle in degrees, 120° when a kit set none: the catalogue's SIG or Point angle
    // comes first. Read only keys advertised by this component.
    internal static double? ReadAngle(IReadOnlyList<string> keys, Func<string, double> read)
    {
        foreach (string alias in new[] { "SIG", "Point angle", "Angle" })
        {
            string? key = keys.FirstOrDefault(k => k.Equals(alias, StringComparison.OrdinalIgnoreCase));
            if (key == null) continue;
            double degrees = read(key);
            if (ValidAngle(degrees)) return degrees;
        }
        return null;
    }

    /// <summary>ENCY's drill template answers half the overall length as the working length when a kit gave none.</summary>
    internal static bool TemplateReach(double workingLength, double length) =>
        double.IsFinite(length) && length > 0 && Math.Abs(workingLength - length / 2) <= 1e-6;

    internal static bool ValidAngle(double? degrees) => degrees is > 0 and <= 180;

    internal static double? RequiredDepth(double diameter, double depth, double breakthrough, double? degrees)
    {
        if (!ValidAngle(degrees)) return null;
        double point = degrees == 180 ? 0 : diameter / (2 * Math.Tan(Math.PI * degrees!.Value / 360));
        return depth + breakthrough + point;
    }
}
