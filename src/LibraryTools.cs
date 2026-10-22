using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;

namespace ToolsMcp;

/// <summary>
/// The person's libraries on tools.encycam.com: shelves of tools in their account, which ENCY opens through the
/// Tools catalogue extension. This is where what an agent finds is meant to end up.
/// </summary>
[McpServerToolType]
public class LibraryTools(IToolsApi api, TokenProvider tokens)
{
    [McpServerTool(Name = "list_libraries"), Description(
        "The person's libraries: each with its id, how many items it holds and how many of them are gone from the catalogue.")]
    public Task<string> ListLibraries() => Replies.WithToken(tokens, async token =>
    {
        var libraries = await api.ListLibraries(token);
        if (libraries.Count == 0) return "No libraries yet - create_library makes one.";
        return string.Join("\n", libraries.Select(l =>
            $"{l.Name} [id {l.Id}] - {l.ItemCount} items ({l.MissingCount} gone from the catalogue), updated "
            + l.UpdatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    });

    [McpServerTool(Name = "show_library"), Description(
        "What a library holds, item by item, marking any the supplier has since withdrawn from the catalogue.")]
    public Task<string> ShowLibrary([Description("The library's id, from list_libraries")] string id) =>
        Replies.WithToken(tokens, async token =>
        {
            if (Ids.Of(id) is not { } lid) return Ids.NotALibrary(id);
            var library = await api.GetLibrary(lid, token);
            return library == null ? NoLibrary(lid) : Contents(library);
        });

    [McpServerTool(Name = "create_library"), Description(
        "Creates a library in the person's account, empty or with components and custom tools in it. A library of " +
        "the same name stops it unless force=true: add_to_library adds to the one there is.")]
    public Task<string> CreateLibrary(
        [Description("The library's name")] string name,
        [Description("Components to put in it, by id; only ones that can go into a kit")] string[]? componentIds = null,
        [Description("Custom drills and end mills to put in it")] CustomToolInput[]? customTools = null,
        [Description("Create it even beside a library of the same name")] bool force = false) =>
        Replies.WithToken(tokens, async token =>
        {
            var (entries, refused) = Entries(componentIds, customTools);
            if (refused != null) return refused;
            if (!force)
            {
                var same = (await api.ListLibraries(token)).FirstOrDefault(l =>
                    string.Equals(l.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (same != null)
                    return $"ERROR: you already have a library named '{same.Name}' [id {same.Id}] - add_to_library adds to it; "
                           + "force=true makes a second one";
            }
            var library = await api.CreateLibrary(name, entries, token);
            return $"Created '{library.Name}' [id {library.Id}] with {library.Items.Count} items. " + Replies.InEncy;
        });

    [McpServerTool(Name = "add_to_library"), Description(
        "Adds components (by id) and custom tools to a library, keeping what it holds; one already there is not added " +
        "twice. Only components that can go into a kit (find_components buildableOnly=true).")]
    public Task<string> AddToLibrary(
        [Description("The library's id")] string id,
        [Description("Components to add, by id")] string[]? componentIds = null,
        [Description("Custom drills and end mills to add")] CustomToolInput[]? customTools = null) =>
        Replies.WithToken(tokens, async token =>
        {
            if (Ids.Of(id) is not { } lid) return Ids.NotALibrary(id);
            var (entries, refused) = Entries(componentIds, customTools);
            if (refused != null) return refused;
            if (entries.Count == 0) return "ERROR: name at least one component id or custom tool to add";
            var before = await api.GetLibrary(lid, token);
            if (before == null) return NoLibrary(lid);
            var after = await api.AddToLibrary(lid, entries, token);
            return $"'{after.Name}' [id {after.Id}] now holds {after.Items.Count} items (was {before.Items.Count}). "
                   + Replies.InEncy;
        });

    [McpServerTool(Name = "remove_from_library"), Description(
        "Takes the named items out of a library and keeps every other one as it is: a component by its id, a custom tool " +
        "by its numbers. Only when the person asked to remove these items.")]
    public Task<string> RemoveFromLibrary(
        [Description("The library's id")] string id,
        [Description("Components to take out, by id")] string[]? componentIds = null,
        [Description("Custom tools to take out, by their numbers")] CustomToolInput[]? customTools = null) =>
        Replies.WithToken(tokens, async token =>
        {
            if (Ids.Of(id) is not { } lid) return Ids.NotALibrary(id);
            var (entries, refused) = Entries(componentIds, customTools);
            if (refused != null) return refused;
            if (entries.Count == 0) return "ERROR: name at least one component id or custom tool to take out";
            var before = await api.GetLibrary(lid, token);
            if (before == null) return NoLibrary(lid);
            var after = await api.RemoveFromLibrary(lid, entries, token);
            // what was named but not there: a remove that found nothing said nothing, and read as done (final review)
            var absent = entries.Where(e => !before.Items.Any(i => Holds(i, e))).Select(Describe).ToList();
            var reply = new StringBuilder($"Removed {before.Items.Count - after.Items.Count}.");
            if (absent.Count > 0) reply.Append(" Not in the library: " + string.Join(", ", absent) + ".");
            return reply.AppendLine().AppendLine(Contents(after)).Append(Replies.InEncy).ToString();
        });

    [McpServerTool(Name = "rename_library"), Description("Renames a library; what it holds stays as it is.")]
    public Task<string> RenameLibrary(
        [Description("The library's id")] string id,
        [Description("The new name")] string name) =>
        Replies.WithToken(tokens, async token =>
        {
            if (Ids.Of(id) is not { } lid) return Ids.NotALibrary(id);
            var library = await api.RenameLibrary(lid, name, token);
            return $"Renamed to '{library.Name}' [id {library.Id}]. " + Replies.InEncy;
        });

    [McpServerTool(Name = "delete_library"), Description(
        "Deletes the library for good. Only when the person explicitly asked to delete this library.")]
    public Task<string> DeleteLibrary([Description("The library's id")] string id) =>
        Replies.WithToken(tokens, async token =>
        {
            if (Ids.Of(id) is not { } lid) return Ids.NotALibrary(id);
            var library = await api.GetLibrary(lid, token);
            if (library == null) return NoLibrary(lid);
            await api.DeleteLibrary(lid, token);
            return $"Deleted '{library.Name}'.";
        });

    [McpServerTool(Name = "export_library"), Description(
        "Saves a library as one .mKit on the person's disk - for when they want a file rather than opening the " +
        "library in ENCY through the extension.")]
    public Task<string> ExportLibrary(
        [Description("The library's id")] string id,
        [Description("Leave empty for released ENCY; \"list\" for ENCY builds after 1 September 2026")] string? socketShape = null,
        [Description("Folder to save into; by default Downloads\\ENCY tools")] string? folder = null) =>
        Replies.WithToken(tokens, async token =>
        {
            if (Ids.Of(id) is not { } lid) return Ids.NotALibrary(id);
            string? shape = BuildTools.SocketShape(socketShape);
            if (BuildTools.SocketShapeError(shape) is { } refused) return refused;
            var file = await api.ExportLibrary(lid, shape, token);
            return $"Saved: {SavedFiles.Save(file, folder)}. " + BuildTools.ImportHint;
        });

    /** The items to put in or take out, with every component id as the server spells it - or why there are none. */
    private static (List<LibraryEntry> Entries, string? Refused) Entries(string[]? componentIds, CustomToolInput[]? customTools)
    {
        var (ids, bad) = Ids.All(componentIds);
        if (bad != null) return ([], Ids.NotAComponent(bad));
        var entries = ids.Select(c => new LibraryEntry(Component: c))
            .Concat((customTools ?? []).Select(t => new LibraryEntry(Custom: t.ToCustomTool())))
            .ToList();
        return (entries, null);
    }

    /** Whether a stored item is the one named: a component by its id; a custom tool as the server matches it - by its
     * numbers, and by name and shank only where they are given. */
    private static bool Holds(LibraryItem item, LibraryEntry named)
    {
        if (named.Component != null) return item.Component == named.Component;
        if (named.Custom is not { } n || item.Custom is not { } s) return false;
        return n.Family == s.Family && n.DiameterMm == s.DiameterMm && n.LengthMm == s.LengthMm
               && (n.Name == null || n.Name == s.Name)
               && (n.ShankDiameterMm == null || n.ShankDiameterMm == s.ShankDiameterMm);
    }

    private static string Describe(LibraryEntry entry) =>
        entry.Custom is { } t ? Custom(t) : entry.Component!;

    private static string Custom(CustomTool t)
    {
        var text = new StringBuilder($"custom {t.Family} Ø{Replies.Mm(t.DiameterMm)} L {Replies.Mm(t.LengthMm)} mm");
        if (t.ShankDiameterMm is { } shank) text.Append($", shank Ø{Replies.Mm(shank)} mm");
        if (t.Name != null) text.Append($" ({t.Name})");
        return text.ToString();
    }

    private static string NoLibrary(string id) => $"ERROR: no library {id}";

    private static string Contents(Library library)
    {
        var reply = new StringBuilder($"'{library.Name}' [id {library.Id}] holds {library.Items.Count} items:");
        foreach (var item in library.Items)
        {
            reply.AppendLine();
            if (item.Custom is { } t)
            {
                reply.Append("  " + Custom(t));
                continue;
            }
            reply.Append($"  {item.Name} - {item.KitName ?? item.Kit}");
            if (item.DiameterMm is { } d) reply.Append($", Ø{Replies.Mm(d)} mm");
            if (item.LengthMm is { } l) reply.Append($", L {Replies.Mm(l)} mm");
            reply.Append($" [id {item.Component}]");
            if (!item.Present) reply.Append(" - GONE from the catalogue");
        }
        return reply.ToString();
    }
}
