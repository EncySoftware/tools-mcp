namespace ToolsMcp;

internal static class InstalledDrillPoint
{
    internal static bool IsStandardDrill(string type)
    {
        string name = type.Split('.').Last();
        return name.Equals("Drill", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("DrillMill", StringComparison.OrdinalIgnoreCase);
    }

    // The ENCY drill template stores Angle in degrees. Read only keys advertised by this component.
    internal static double? ReadAngle(IReadOnlyList<string> keys, Func<string, double> read)
    {
        foreach (string alias in new[] { "Angle", "SIG", "Point angle" })
        {
            string? key = keys.FirstOrDefault(k => k.Equals(alias, StringComparison.OrdinalIgnoreCase));
            if (key == null) continue;
            double degrees = read(key);
            if (ValidAngle(degrees)) return degrees;
        }
        return null;
    }

    internal static bool ValidAngle(double? degrees) => degrees is > 0 and <= 180;

    internal static double? RequiredDepth(double diameter, double depth, double breakthrough, double? degrees)
    {
        if (!ValidAngle(degrees)) return null;
        double point = degrees == 180 ? 0 : diameter / (2 * Math.Tan(Math.PI * degrees!.Value / 360));
        return depth + breakthrough + point;
    }
}
