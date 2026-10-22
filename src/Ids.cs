namespace ToolsMcp;

/// <summary>
/// The ids the server hands out are GUIDs, written lower-case. An agent passes a designation or a library's name where an
/// id goes, and the server answered that with a raw 400 body; and it keys its answers by the lower-case spelling, so an
/// upper-case id missed its own entry (both found in the final review, 2026-10-22).
/// </summary>
internal static class Ids
{
    /** The id as the server spells it, or null when it is no id at all. */
    public static string? Of(string? raw) => Guid.TryParse(raw?.Trim(), out var id) ? id.ToString("D") : null;

    /** Every id as the server spells it, or the first one that is no id. */
    public static (List<string> Ids, string? Bad) All(IEnumerable<string>? raw)
    {
        var ids = new List<string>();
        foreach (string r in raw ?? [])
        {
            if (Of(r) is { } id) ids.Add(id);
            else return (ids, r);
        }
        return (ids, null);
    }

    public static string NotAComponent(string raw) => $"ERROR: '{raw}' is not a component id - find_components gives them";

    public static string NotALibrary(string raw) => $"ERROR: '{raw}' is not a library id - list_libraries gives them";
}
