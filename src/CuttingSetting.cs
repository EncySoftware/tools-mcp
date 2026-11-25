using System.Globalization;
using System.Text.RegularExpressions;

namespace ToolsMcp;

internal sealed record CuttingSetting(string Unit, double Value)
{
    public override string ToString() => $"{Value:G} {Unit}";

    internal static IReadOnlyList<CuttingSetting> FromArticle(IEnumerable<string[]> fields, bool upper)
    {
        var settings = new List<CuttingSetting>();
        foreach (var field in fields)
        {
            if (field.Length != 2) continue;
            string? unit = field[0] switch
            {
                "Rotational speed" => "rpm", "Cutting speed" => "m/min",
                "Feed per tooth" => "mm/tooth", "Feed per revolution" => "mm/rev",
                "Feed rate" => "mm/min", _ => null
            };
            if (unit == null) continue;
            string number = @"([0-9]+(?:\.[0-9]+)?)\s*" + Regex.Escape(unit);
            var range = Regex.Match(field[1].Trim(), @"^min\.\s*" + number + @"\s*↔\s*max\.\s*" + number + "$");
            var match = range.Success ? range : Regex.Match(field[1].Trim(), "^" + number + "$");
            if (!match.Success) continue;
            double low = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            double high = range.Success ? double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : low;
            if (low > 0 && double.IsFinite(high) && high >= low)
                settings.Add(new(unit, upper ? high : low));
        }
        return settings;
    }

    internal (string ModePath, int Mode, string ValuePath) Paths(bool milling) => (milling, Unit) switch
    {
        (true, "rpm") => ("ConditionsSection.Spindle.Speed.Mode", 0, "ConditionsSection.Spindle.Speed.RPM"),
        (true, "m/min") => ("ConditionsSection.Spindle.Speed.Mode", 1, "ConditionsSection.Spindle.Speed.CuttingSpeed"),
        (true, "mm/min") => MillFeed(0, "ValuePerMinut"),
        (true, "mm/rev") => MillFeed(1, "ValuePerRevolution"),
        (true, "mm/tooth") => MillFeed(2, "ValuePerTooth"),
        (false, "rpm") => ("SpindleSpeed.LatheSpindleSpeedType", 0, "SpindleSpeed.SpeedRPM"),
        (false, "m/min") => ("SpindleSpeed.LatheSpindleSpeedType", 1, "SpindleSpeed.LinearSpeed"),
        (false, "mm/min") => ("CutFeed.LatheFeedMeasurement", 0, "CutFeed.ValuePerMinut"),
        (false, "mm/rev") => ("CutFeed.LatheFeedMeasurement", 1, "CutFeed.ValuePerRevolution"),
        _ => throw new InvalidOperationException($"Unsupported feed or speed unit for this operation: {Unit}")
    };

    private static (string, int, string) MillFeed(int mode, string field) =>
        ("ConditionsSection.Feeds.WorkFeed.Measurement", mode, "ConditionsSection.Feeds.WorkFeed.Value." + field);

    internal static bool IsPair(IReadOnlyList<CuttingSetting> pair) => pair.Count == 2
        && pair.Count(s => s.Unit is "rpm" or "m/min") == 1 && pair.Count(s => s.Unit.StartsWith("mm/")) == 1;

    internal double OperationValue(bool imperial) => !imperial || Unit == "rpm" ? Value
        : Unit == "m/min" ? Value / 0.3048 : Value / 25.4;

    internal static void Apply(IReadOnlyList<CuttingSetting> pair, bool milling, bool imperial,
        Func<string,bool> exists, Func<string,int> getMode, Action<string,int> setMode,
        Func<string,double> getValue, Action<string,double> setValue, Action commit)
    {
        if (!IsPair(pair) || pair.Any(s => !double.IsFinite(s.Value) || s.Value <= 0))
            throw new ArgumentException("Select one positive feed and one positive speed.");
        var changes = pair.Select(s =>
        {
            var paths = s.Paths(milling);
            if (!exists(paths.ModePath) || !exists(paths.ValuePath))
                throw new InvalidOperationException($"Operation does not support {s.Unit}.");
            return (paths.ModePath, paths.Mode, paths.ValuePath, Value: s.OperationValue(imperial),
                oldMode: getMode(paths.ModePath), oldValue: getValue(paths.ValuePath));
        }).ToArray();
        try
        {
            foreach (var c in changes)
            {
                setMode(c.ModePath, c.Mode);
                setValue(c.ValuePath, c.Value);
            }
            commit();
            foreach (var c in changes)
                if (getMode(c.ModePath) != c.Mode || !(Math.Abs(getValue(c.ValuePath) - c.Value) <= c.Value * 1e-8))
                    throw new InvalidOperationException("ENCY did not retain the selected cutting conditions.");
        }
        catch (Exception failure)
        {
            try
            {
                foreach (var c in changes)
                {
                    setValue(c.ValuePath, c.oldValue);
                    setMode(c.ModePath, c.oldMode);
                }
                commit();
            }
            catch (Exception rollback)
            {
                throw new AggregateException("Check the operation: restoring its original values failed.", failure, rollback);
            }
            throw;
        }
    }
}