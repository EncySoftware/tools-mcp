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
            var library = await api.GetLibrary(id, token);
            return library == null ? NoLibrary(id) : Contents(library);
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
            if (!force)
            {
                var same = (await api.ListLibraries(token)).FirstOrDefault(l =>
                    string.Equals(l.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (same != null)
                    return $"ERROR: you already have a library named '{same.Name}' [id {same.Id}] - add_to_library adds to it; "
                           + "force=true makes a second one";
            }
            var library = await api.CreateLibrary(name, Entries(componentIds, customTools), token);
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
            var entries = Entries(componentIds, customTools);
            if (entries.Count == 0) return "ERROR: name at least one component id or custom tool to add";
            var before = await api.GetLibrary(id, token);
            if (before == null) return NoLibrary(id);
            var after = await api.AddToLibrary(id, entries, token);
            return $"'{after.Name}' [id {after.Id}] now holds {after.Items.Count} items (was {before.Items.Count}). "
                   + Replies.InEncy;
        });

    [McpServerTool(Name = "remove_from_library"), Description(
        "Takes the named items out of a library and keeps every other one as it is. Only when the person asked to " +
        "remove these items.")]
    public Task<string> RemoveFromLibrary(
        [Description("The library's id")] string id,
        [Description("Components to take out, by id")] string[]? componentIds = null,
        [Description("Custom tools to take out, by their numbers")] CustomToolInput[]? customTools = null) =>
        Replies.WithToken(tokens, async token =>
        {
            var entries = Entries(componentIds, customTools);
            if (entries.Count == 0) return "ERROR: name at least one component id or custom tool to take out";
            return Contents(await api.RemoveFromLibrary(id, entries, token)) + "\n" + Replies.InEncy;
        });

    [McpServerTool(Name = "rename_library"), Description("Renames a library; what it holds stays as it is.")]
    public Task<string> RenameLibrary(
        [Description("The library's id")] string id,
        [Description("The new name")] string name) =>
        Replies.WithToken(tokens, async token =>
        {
            var library = await api.RenameLibrary(id, name, token);
            return $"Renamed to '{library.Name}' [id {library.Id}]. " + Replies.InEncy;
        });

    [McpServerTool(Name = "delete_library"), Description(
        "Deletes the library for good. Only when the person explicitly asked to delete this library.")]
    public Task<string> DeleteLibrary([Description("The library's id")] string id) =>
        Replies.WithToken(tokens, async token =>
        {
            var library = await api.GetLibrary(id, token);
            if (library == null) return NoLibrary(id);
            await api.DeleteLibrary(id, token);
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
            if (BuildTools.SocketShapeError(socketShape) is { } refused) return refused;
            var file = await api.ExportLibrary(id, socketShape, token);
            return $"Saved: {SavedFiles.Save(file, folder)}. " + BuildTools.ImportHint;
        });

    private static List<LibraryEntry> Entries(string[]? componentIds, CustomToolInput[]? customTools) =>
        (componentIds ?? []).Select(c => new LibraryEntry(Component: c))
        .Concat((customTools ?? []).Select(t => new LibraryEntry(Custom: t.ToCustomTool())))
        .ToList();

    private static string NoLibrary(string id) => $"ERROR: no library {id}";

    private static string Contents(Library library)
    {
        var reply = new StringBuilder($"'{library.Name}' [id {library.Id}] holds {library.Items.Count} items:");
        foreach (var item in library.Items)
        {
            reply.AppendLine();
            if (item.Custom is { } t)
            {
                reply.Append($"  custom {t.Family} Ø{Replies.Mm(t.DiameterMm)} L {Replies.Mm(t.LengthMm)} mm");
                if (t.Name != null) reply.Append($" ({t.Name})");
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
