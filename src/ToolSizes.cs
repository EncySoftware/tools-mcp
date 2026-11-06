using System.Globalization;
using System.Text.RegularExpressions;

namespace ToolsMcp;

/// <summary>
/// The sizes a setup sheet writes a tool by, in millimetres: a fraction of an inch (1/4in, 1/4"), a number or letter
/// drill (#7, F), plain millimetres (6.8, 6.8mm) - and a thread, its diameter and pitch: M10, M10x1.5, 1/4-20 UNC,
/// #10-32. SA-11: a US sheet's T7 was a 1/4-20 tap and a #7 drill, and a custom tool took millimetres only.
/// </summary>
public static partial class ToolSizes
{
    private const double MmPerInch = 25.4;

    /// <summary>Number drills #1 to #80, inches (ASME B94.11M).</summary>
    private static readonly double[] NumberDrills =
    [
        0.2280, 0.2210, 0.2130, 0.2090, 0.2055, 0.2040, 0.2010, 0.1990, 0.1960, 0.1935,
        0.1910, 0.1890, 0.1850, 0.1820, 0.1800, 0.1770, 0.1730, 0.1695, 0.1660, 0.1610,
        0.1590, 0.1570, 0.1540, 0.1520, 0.1495, 0.1470, 0.1440, 0.1405, 0.1360, 0.1285,
        0.1200, 0.1160, 0.1130, 0.1110, 0.1100, 0.1065, 0.1040, 0.1015, 0.0995, 0.0980,
        0.0960, 0.0935, 0.0890, 0.0860, 0.0820, 0.0810, 0.0785, 0.0760, 0.0730, 0.0700,
        0.0670, 0.0635, 0.0595, 0.0550, 0.0520, 0.0465, 0.0430, 0.0420, 0.0410, 0.0400,
        0.0390, 0.0380, 0.0370, 0.0360, 0.0350, 0.0330, 0.0320, 0.0310, 0.0292, 0.0280,
        0.0260, 0.0250, 0.0240, 0.0225, 0.0210, 0.0200, 0.0180, 0.0160, 0.0145, 0.0135,
    ];

    /// <summary>Letter drills A to Z, inches.</summary>
    private static readonly double[] LetterDrills =
    [
        0.234, 0.238, 0.242, 0.246, 0.250, 0.257, 0.261, 0.266, 0.272, 0.277, 0.281, 0.290, 0.295,
        0.302, 0.316, 0.323, 0.332, 0.339, 0.348, 0.358, 0.368, 0.377, 0.386, 0.397, 0.404, 0.413,
    ];

    /// <summary>The diameter a size names, in millimetres; null for one it does not read.</summary>
    public static double? MillimetresOf(string size)
    {
        string s = size.Trim().ToUpperInvariant().Replace(" ", "");
        if (NumberDrill().Match(s) is { Success: true } n)
        {
            int k = int.Parse(n.Groups[1].Value, CultureInfo.InvariantCulture);
            return k is >= 1 and <= 80 ? NumberDrills[k - 1] * MmPerInch : null;
        }
        if (s.Length == 1 && s[0] is >= 'A' and <= 'Z') return LetterDrills[s[0] - 'A'] * MmPerInch;
        if (Inches().Match(s) is { Success: true } i) return InchesOf(i.Groups[1].Value) * MmPerInch;
        if (Millimetres().Match(s) is { Success: true } m)
            return double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        return null;
    }

    /// <summary>A thread's major diameter and pitch, mm; the pitch null for an M thread named without one.</summary>
    public static (double DiameterMm, double? PitchMm)? Thread(string designation)
    {
        string s = designation.Trim().ToUpperInvariant();
        if (Metric().Match(s) is { Success: true } m)
        {
            double d = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            double? p = m.Groups[2].Success ? double.Parse(m.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture) : null;
            return (d, p);
        }
        if (Unified().Match(s) is { Success: true } u)
        {
            string size = u.Groups[1].Value.Replace(" ", "-");
            double tpi = double.Parse(u.Groups[2].Value, CultureInfo.InvariantCulture);
            // a bare number is a number size where its pitch is fine enough to be one - 6-32 is #6-32 - else inches
            if (size.All(char.IsDigit) && int.Parse(size, CultureInfo.InvariantCulture) <= 12 && tpi >= 24) size = "#" + size;
            double inches = size.StartsWith('#')
                ? 0.060 + 0.013 * int.Parse(size[1..], CultureInfo.InvariantCulture)
                : InchesOf(size);
            return (inches * MmPerInch, MmPerInch / tpi);
        }
        return null;
    }

    /// <summary>"1/4", "1-1/4", "0.25" as inches.</summary>
    private static double InchesOf(string text)
    {
        var whole = WholeAndFraction().Match(text);
        if (whole.Success)
        {
            double w = whole.Groups[1].Success ? double.Parse(whole.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            return w + double.Parse(whole.Groups[2].Value, CultureInfo.InvariantCulture)
                / double.Parse(whole.Groups[3].Value, CultureInfo.InvariantCulture);
        }
        return double.Parse(text, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"^(?:NO\.?|#)(\d{1,2})$")]
    private static partial Regex NumberDrill();

    [GeneratedRegex(@"^((?:\d+-)?\d+/\d+|\d*\.\d+|\d+)(?:IN|INCH|"")$")]
    private static partial Regex Inches();

    [GeneratedRegex(@"^(\d+(?:[.,]\d+)?)(?:MM)?$")]
    private static partial Regex Millimetres();

    [GeneratedRegex(@"^(?:(\d+)-)?(\d+)/(\d+)$")]
    private static partial Regex WholeAndFraction();

    [GeneratedRegex(@"^MF?\s?(\d{1,3}(?:[.,]\d+)?)(?:\s?[X×]\s?(\d{1,2}(?:[.,]\d+)?))?(?:[\s-].*)?$")]
    private static partial Regex Metric();

    [GeneratedRegex(@"^(?:UN[A-Z]*\s?)?(#\d{1,2}|\d{1,2}[\s-]\d{1,2}/\d{1,2}|\d{1,2}/\d{1,2}|\d{1,2})\s?-\s?(\d{1,3})(?:\s?UN[A-Z]*)?(?:[\s-].*)?$")]
    private static partial Regex Unified();
}
