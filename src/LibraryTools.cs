using System.ComponentModel;
using System.Globalization;
using System.Text;
using ModelContextProtocol.Server;

namespace ToolsMcp;

/// <summary>A tool kept built, as an agent names it: its parts in any order, put in the order they fit.</summary>
public record AssemblyInput(
    [property: Description("Its parts by component id, in any order: they are put in the order they fit, the machine end first")]
    string[] ComponentIds,
    [property: Description("A custom drill or end mill as its cutter, at the cutting end")] CustomToolInput? CustomTool = null,
    [property: Description("Its T number, e.g. T12")] string? Number = null,
    [property: Description("Where it stands: machine, magazine pocket, turret station")] string? Place = null,
    [property: Description("MAIN or SUB, the spindle it works on")] string? Spindle = null,
    [property: Description("Measured gauge length, mm: from where the holder mounts in the spindle to the tip")]
    double? GaugeLengthMm = null,
    [property: Description("A name; the cutter's when left out")] string? Name = null,
    [property: Description("Measured setup and intended uses, with measurement sources")] ShelfSetup? Setup = null);

/// <summary>
/// The person's libraries on tools.encycam.com: shelves of tools in their account, which ENCY opens through the
/// Tools catalogue extension. This is where what an agent finds is meant to end up.
/// </summary>
[McpServerToolType]
public class LibraryTools(IToolsApi api, TokenProvider tokens, IFileSink? files = null)
{
    /** Where a saved file goes: the person's disk at home, a link on the hosted server. */
    private readonly IFileSink _files = files ?? LocalFiles.Instance;

    [McpServerTool(Name = "list_libraries"), Description(
        "The person's libraries: each with its id, how many items it holds and how many of them are gone from the catalogue.")]
    public Task<string> ListLibraries() => Replies.WithToken(tokens, async token =>
    {
        var libraries = await api.ListLibraries(token);
        if (libraries.Count == 0) return "No libraries yet - create_library makes one.";
        return string.Join("\n", libraries.Select(l =>
            $"{l.Name} [id {l.Id}]{(l.Shared ? $" (shared, {l.Role})" : "")} - {l.ItemCount} items ({l.MissingCount} gone from the catalogue), updated "
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
        "Adds components (by id), custom tools and assemblies - tools kept built, with their T number, place, spindle and " +
        "gauge length - to a library, keeping what it holds; a component already there is not added twice. Only " +
        "components that can go into a kit (find_components buildableOnly=true).")]
    public Task<string> AddToLibrary(
        [Description("The library's id")] string id,
        [Description("Components to add, by id")] string[]? componentIds = null,
        [Description("Custom drills and end mills to add")] CustomToolInput[]? customTools = null,
        [Description("Assemblies to add: each a tool as it stands built at the machine")] AssemblyInput[]? assemblies = null) =>
        Replies.WithToken(tokens, async token =>
        {
            if (Ids.Of(id) is not { } lid) return Ids.NotALibrary(id);
            var (entries, refused) = Entries(componentIds, customTools);
            if (refused != null) return refused;
            var (built, notes, wrong) = await Assemblies(assemblies, token);
            if (wrong != null) return wrong;
            entries.AddRange(built);
            if (entries.Count == 0) return "ERROR: name at least one component id, custom tool or assembly to add";
            var before = await api.GetLibrary(lid, token);
            if (before == null) return NoLibrary(lid);
            var after = await api.AddToLibrary(lid, entries, token);
            var reply = new StringBuilder($"'{after.Name}' [id {after.Id}] now holds {after.Items.Count} items (was {before.Items.Count}).");
            foreach (var item in after.Items.Where(i => i.Assembly != null).TakeLast(built.Count))
                reply.AppendLine().Append("  " + Assembly(item));
            foreach (var note in notes)
                reply.AppendLine().Append("  NOTE: " + note);
            return reply.AppendLine().Append(Replies.InEncy).ToString();
        });

    [McpServerTool(Name = "create_assembly"), Description(
        "Keeps a tool as it stands built at the machine - cutter in collet in chuck - on a library as one item: its parts " +
        "in the order they fit (worked out, the machine end first), its T number, where it stands, the spindle it works " +
        "on (MAIN or SUB) and its measured gauge length. ENCY then takes the library's assemblies as built, numbered " +
        "tools: export_tool_list, or the Tools button of the Tools catalogue extension.")]
    public Task<string> CreateAssembly(
        [Description("The library's id")] string id,
        [Description("Its parts by component id, in any order")] string[] componentIds,
        [Description("A custom drill or end mill as its cutter, at the cutting end")] CustomToolInput? customTool = null,
        [Description("Its T number, e.g. T12")] string? number = null,
        [Description("Where it stands: machine, magazine pocket, turret station")] string? place = null,
        [Description("MAIN or SUB, the spindle it works on")] string? spindle = null,
        [Description("Measured gauge length, mm: from where the holder mounts in the spindle to the tip")] double? gaugeLengthMm = null,
        [Description("A name; the cutter's when left out")] string? name = null,
        [Description("Measured setup and intended uses, including sources")] ShelfSetup? setup = null) =>
        AddToLibrary(id, assemblies: [new AssemblyInput(componentIds, customTool, number, place, spindle, gaugeLengthMm, name, setup)]);

    [McpServerTool(Name = "export_tool_list"), Description(
        "Saves a library's assemblies as ENCY's tool list (.tom): each assembly one tool, built and numbered, reaching as " +
        "far as measured. In ENCY: Tools, Load existing tool list.")]
    public Task<string> ExportToolList(
        [Description("The library's id")] string id,
        [Description("Folder to save into; by default Downloads\\ENCY tools")] string? folder = null) =>
        Replies.WithToken(tokens, async token =>
        {
            _files.Check(folder);
            if (Ids.Of(id) is not { } lid) return Ids.NotALibrary(id);
            var export = await api.ExportToolList(lid, token);
            string counts = (export.Exported, export.LeftOut) switch
            {
                (int saved, int omitted) => $"Exported {saved} of {saved + omitted} assemblies; {omitted} omitted.",
                (_, int omitted) => $"{omitted} assemblies omitted.",
                _ => "The server did not report how many assemblies were exported."
            };
            return $"{_files.Deliver(export.File, folder)}. {counts} In ENCY: Tools, Load existing tool list.";
        });

    [McpServerTool(Name = "update_assembly"), Description(
        "Edits a saved assembly in place. Give only the fields to change. The assembly id is in show_library.")]
    public Task<string> UpdateAssembly(string libraryId, string assemblyId, string? number = null,
        string? name = null, string? place = null, [Description("MAIN or SUB; empty to clear")] string? spindle = null,
        [Description("Measured gauge length in mm; zero to clear")] double? gaugeLengthMm = null,
        [Description("Replace the entire measured setup and intended uses. Omit to preserve it. Measurements require a value in mm and a source.")] ShelfSetup? setup = null) =>
        Replies.WithToken(tokens, async token =>
        {
            if (Ids.Of(libraryId) is not { } lib) return Ids.NotALibrary(libraryId);
            if (Ids.Of(assemblyId) is not { } aid) return $"ERROR: '{assemblyId}' is not an assembly id - see show_library";
            if (number == null && name == null && place == null && spindle == null && gaugeLengthMm == null && setup == null)
                return "ERROR: specify at least one assembly field to change";
            var updated = await api.UpdateAssembly(lib, aid, new AssemblyPatch(name, number, place, spindle, gaugeLengthMm, setup), token);
            return "Updated " + Assembly(updated.Items.Single(item => item.Assembly?.Id == aid));
        });

    [McpServerTool(Name="find_shelf_replacements", ReadOnly=true),Description("Find alternatives for a saved component, including withdrawn articles with dimensional snapshots. Results report differences, unverified fields and catalogue pagination. The default diameter tolerance is zero mm.")]
    public Task<string> FindShelfReplacements(string libraryId,string componentId,double diameterToleranceMm=0,int page=0) =>
        Replies.WithToken(tokens,async token => Ids.Of(libraryId) is not {} shelf ? Ids.NotALibrary(libraryId) :
            Ids.Of(componentId) is not {} id ? Ids.NotAComponent(componentId) :
            (await api.ShelfReplacements(shelf,id,diameterToleranceMm,page,token)).ToJsonString());

    [McpServerTool(Name="check_shelf_assembly_gap", ReadOnly=true),Description("Check the joint after a zero-based part index in a saved assembly, from machine to cutter. Returns recorded connection codes, suggested adapters and where they are on the named shelf.")]
    public Task<string> CheckShelfAssemblyGap(string libraryId,string assemblyId,int after) =>
        Replies.WithToken(tokens,async token=> Ids.Of(libraryId) is not {} shelf ? Ids.NotALibrary(libraryId) :
            Ids.Of(assemblyId) is not {} id ? "ERROR: unknown assembly id." : (await api.AssemblyGap(shelf,id,after,token)).ToJsonString());

    [McpServerTool(Name = "export_assembly"), Description(
        "Saves one assembly as an ENCY .sc12tool file. Get the library id and assembly id from list_libraries and show_library.")]
    public Task<string> ExportAssembly(string libraryId, string assemblyId,
        [Description("Local download folder; omit on the hosted server")] string? folder = null) =>
        Replies.WithToken(tokens, async token =>
        {
            if (Ids.Of(libraryId) is not { } lib) return Ids.NotALibrary(libraryId);
            if (Ids.Of(assemblyId) is not { } aid) return $"ERROR: '{assemblyId}' is not an assembly id - see show_library";
            _files.Check(folder);
            return _files.Deliver(await api.ExportAssembly(lib, aid, token), folder);
        });

    [McpServerTool(Name = "remove_from_library"), Description(
        "Takes the named items out of a library and keeps every other one as it is: a component by its id, a custom tool " +
        "by its numbers, an assembly by its assembly id (show_library). Only when the person asked to remove these items.")]
    public Task<string> RemoveFromLibrary(
        [Description("The library's id")] string id,
        [Description("Components to take out, by id")] string[]? componentIds = null,
        [Description("Custom tools to take out, by their numbers")] CustomToolInput[]? customTools = null,
        [Description("Assemblies to take out, by their assembly id")] string[]? assemblyIds = null) =>
        Replies.WithToken(tokens, async token =>
        {
            if (Ids.Of(id) is not { } lid) return Ids.NotALibrary(id);
            var (entries, refused) = Entries(componentIds, customTools);
            if (refused != null) return refused;
            foreach (var raw in assemblyIds ?? [])
            {
                if (Ids.Of(raw) is not { } aid) return $"ERROR: '{raw}' is not an assembly id - show_library lists them";
                entries.Add(new LibraryEntry(Assembly: new AssemblyEntry([], Id: aid)));
            }
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

    [McpServerTool(Name = "export_shelf_gtc"), Description(
        "Builds one GTC zip from the catalogue parts in a shelf's saved assemblies, like Tools catalogue → Add to project. Gives a download or local file; use import_project_gtc locally or Equipment → Load GTC in ENCY.")]
    public Task<string> ExportShelfGtc(string id, string? folder = null) =>
        Replies.WithToken(tokens, async token =>
        {
            if (Ids.Of(id) is not { } lid) return Ids.NotALibrary(id);
            _files.Check(folder);
            var shelf = await BuildShelfGtc(api, lid, token);
            return $"{_files.Deliver(shelf.Package.File, folder)}. {shelf.Summary} " + BuildTools.GtcHint;
        });

    internal static async Task<(GtcPackage Package, string Summary)> BuildShelfGtc(IToolsApi api, string id, string token)
    {
        var library = await api.GetLibrary(id, token) ?? throw new ArgumentException($"No library {id}.");
        var parts = library.Items.Where(i => i.Assembly != null).SelectMany(i => i.Assembly!.Parts).ToList();
        var ids = parts.Where(p => p.Present && p.Component != null).Select(p => p.Component!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0) throw new ArgumentException("The saved assemblies have no available catalogue components.");
        if (ids.Count > 500) throw new ArgumentException("A GTC package holds at most 500 distinct components.");
        var gtc = await api.BuildGtcPackage(library.Name, ids, token);
        var unavailable = parts.Count(p => !p.Present || p.Component == null);
        return (gtc, $"Packaged {ids.Count - gtc.LeftOut}/{ids.Count} catalogue components. Missing or custom part entries: {unavailable}.");
    }

    [McpServerTool(Name = "export_library"), Description(
        "Saves a library as one .mKit on the person's disk - for when they want a file rather than opening the " +
        "library in ENCY through the extension.")]
    public Task<string> ExportLibrary(
        [Description("The library's id")] string id,
        [Description("Folder to save into; by default Downloads\\ENCY tools")] string? folder = null) =>
        Replies.WithToken(tokens, async token =>
        {
            _files.Check(folder);
            if (Ids.Of(id) is not { } lid) return Ids.NotALibrary(id);
            var file = await api.ExportLibrary(lid, token);
            return $"{_files.Deliver(file, folder)}. " + BuildTools.ImportHint;
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

    /**
     * The assemblies as they are saved: each one's parts put in the order they fit - a part nothing else here takes
     * starts the chain, then each takes the next, as the site's «Your assembly» draws them - and a custom cutter at the
     * cutting end; with a note wherever two parts next to each other do not fit. Or why one cannot be saved.
     */
    private async Task<(List<LibraryEntry> Entries, List<string> Notes, string? Refused)> Assemblies(
        AssemblyInput[]? assemblies, string token)
    {
        var entries = new List<LibraryEntry>();
        var notes = new List<string>();
        foreach (var a in assemblies ?? [])
        {
            var (ids, bad) = Ids.All(a.ComponentIds);
            if (bad != null) return ([], [], Ids.NotAComponent(bad));
            ids = ids.Distinct().ToList();
            if (ids.Count == 0 && a.CustomTool == null) return ([], [], "ERROR: an assembly needs at least one part");
            if (ids.Count + (a.CustomTool == null ? 0 : 1) > 20)
                return ([], [], "ERROR: an assembly is 1 to 20 parts");
            string? spindle = string.IsNullOrWhiteSpace(a.Spindle) ? null : a.Spindle.Trim().ToUpperInvariant();
            if (spindle is not (null or "MAIN" or "SUB"))
                return ([], [], $"ERROR: a spindle is MAIN or SUB, not '{a.Spindle}'");
            if (a.GaugeLengthMm is { } g && (g <= 0 || g > 1000))
                return ([], [], "ERROR: gaugeLengthMm is more than 0 and up to 1000 mm");
            var takes = ids.Count > 1
                ? await api.GetAcceptsAmong(ids, token)
                : new Dictionary<string, IReadOnlyList<string>>();
            var ordered = OrderChain(ids, takes);
            for (int i = 1; i < ordered.Count; i++)
                if (!Takes(takes, ordered[i - 1], ordered[i]))
                    notes.Add($"{a.Number ?? a.Name ?? "the assembly"}: {ordered[i - 1]} and {ordered[i]} do not fit each " +
                              "other - something is missing between them (what_fits shows what goes there)");
            var parts = ordered.Select(c => new LibraryEntry(Component: c)).ToList();
            if (a.CustomTool != null) parts.Add(new LibraryEntry(Custom: a.CustomTool.ToCustomTool()));
            entries.Add(new LibraryEntry(Assembly: new AssemblyEntry(parts, a.Name, a.Number, a.Place, spindle, a.GaugeLengthMm, Setup:a.Setup)));
        }
        return (entries, notes, null);
    }

    private static bool Takes(IReadOnlyDictionary<string, IReadOnlyList<string>> takes, string holder, string part) =>
        takes.TryGetValue(holder, out var taken) && taken.Contains(part);

    /** Parts in the order they assemble, the machine end first. */
    internal static List<string> OrderChain(IReadOnlyList<string> ids, IReadOnlyDictionary<string, IReadOnlyList<string>> takes)
    {
        var ordered = new List<string>();
        var rest = ids.Distinct().ToList();
        while (rest.Count > 0)
        {
            var head = rest.FirstOrDefault(c => !rest.Any(o => o != c && Takes(takes, o, c))) ?? rest[0];
            ordered.Add(head);
            rest.Remove(head);
            var current = head;
            while (rest.FirstOrDefault(c => Takes(takes, current, c)) is { } next)
            {
                ordered.Add(next);
                rest.Remove(next);
                current = next;
            }
        }
        // Keep the usual order for a simple chain, or the labelled gaps when no complete chain exists.
        if (ordered.Zip(ordered.Skip(1)).All(pair => Takes(takes, pair.First, pair.Second)))
            return ordered;
        return CompleteChain(ordered, takes) ?? ordered;
    }

    private static List<string>? CompleteChain(List<string> parts, IReadOnlyDictionary<string, IReadOnlyList<string>> takes)
    {
        // The API accepts at most 20 parts. Each mask describes the unused parts, each bit within
        // failed[mask] a last part from which those remaining parts cannot form a chain.
        int count = parts.Count;
        var next = new int[count];
        for (int i = 0; i < count; i++)
            for (int j = 0; j < count; j++)
                if (i != j && Takes(takes, parts[i], parts[j])) next[i] |= 1 << j;

        int all = (1 << count) - 1;
        int possibleHeads = all & ~next.Aggregate(0, (mask, edges) => mask | edges);
        if (System.Numerics.BitOperations.PopCount((uint)possibleHeads) > 1 || next.Count(edges => edges == 0) > 1)
            return null;
        var failed = new int[all + 1];
        var path = new int[count];
        bool Finish(int index, int last, int remaining)
        {
            path[index] = last;
            if (remaining == 0) return true;
            if ((failed[remaining] & (1 << last)) != 0) return false;
            for (int choices = remaining & next[last]; choices != 0; choices &= choices - 1)
            {
                int candidate = System.Numerics.BitOperations.TrailingZeroCount((uint)choices);
                if (Finish(index + 1, candidate, remaining ^ (1 << candidate))) return true;
            }
            failed[remaining] |= 1 << last;
            return false;
        }

        for (int head = 0; head < count; head++)
            if ((possibleHeads == 0 || (possibleHeads & (1 << head)) != 0) && Finish(0, head, all ^ (1 << head)))
                return path.Select(i => parts[i]).ToList();
        return null;
    }

    /** Whether a stored item is the one named: a component by its id; a custom tool as the server matches it - by its
     * numbers, and by name and shank only where they are given; an assembly by its id. */
    private static bool Holds(LibraryItem item, LibraryEntry named)
    {
        if (named.Assembly?.Id is { } aid) return item.Assembly?.Id == aid;
        if (named.Component != null) return item.Component == named.Component;
        if (named.Custom is not { } n || item.Custom is not { } s) return false;
        return n.Family == s.Family && n.DiameterMm == s.DiameterMm && n.LengthMm == s.LengthMm
               && (n.Name == null || n.Name == s.Name)
               && (n.ShankDiameterMm == null || n.ShankDiameterMm == s.ShankDiameterMm);
    }

    private static string Describe(LibraryEntry entry) =>
        entry.Assembly?.Id is { } aid ? "assembly " + aid : entry.Custom is { } t ? Custom(t) : entry.Component!;

    /** An assembly as one line: its number and name, its parts from the machine down, where it stands, its id. */
    private static string Assembly(LibraryItem item)
    {
        var a = item.Assembly!;
        var text = new StringBuilder();
        if (a.Number != null) text.Append(a.Number + " ");
        text.Append(item.Name + ": " + string.Join(" -> ", a.Parts.Select(p => p.Custom is { } c ? Custom(c) : PartName(p))));
        if (a.Place != null) text.Append(" · " + a.Place);
        if (a.Spindle != null) text.Append($" · {a.Spindle} spindle");
        if (a.GaugeLengthMm is { } g) text.Append($" · gauge {Replies.Mm(g)} mm");
        if (a.Setup is { } setup) {
            if (setup.Machine != null) text.Append($" · machine {setup.Machine}");
            if (setup.Stickout is { } s) text.Append($" · stickout {Replies.Mm(s.Mm)} mm ({s.Source})");
            if (setup.ClampingDepth is { } d) text.Append($" · clamping depth {Replies.Mm(d.Mm)} mm ({d.Source})");
            foreach(var use in setup.Applications ?? []) text.Append($" · {use.Operation} {use.Material}");
        }
        text.Append($" [assembly id {a.Id}]");
        if (!item.Present) text.Append(" - a part is GONE from the catalogue");
        return text.ToString();
    }

    private static string Custom(CustomTool t)
    {
        var text = new StringBuilder($"custom {t.Family} Ø{Replies.Mm(t.DiameterMm)} L {Replies.Mm(t.LengthMm)} mm");
        if (t.ShankDiameterMm is { } shank) text.Append($", shank Ø{Replies.Mm(shank)} mm");
        if (t.CornerRadiusMm is { } r) text.Append($", R{Replies.Mm(r)}");
        if (t.AngleDeg is { } a) text.Append($", {Replies.Mm(a)} deg");
        if (t.PitchMm is { } p) text.Append($", pitch {Replies.Mm(p)} mm");
        if (t.Name != null) text.Append($" ({t.Name})");
        return text.ToString();
    }

    private static string NoLibrary(string id) => $"ERROR: no library {id}";

    private static string PartName(LibraryItem item)
    {
        if (item.Reference is not { } r) return item.Name ?? "";
        var text = new StringBuilder($"{item.Name} [article {r.Article}]");
        if (r.OverallMm is double o) text.Append($", overall {Replies.Mm(o)} mm");
        if (r.CornerRadiusMm is double rad) text.Append($", corner R{Replies.Mm(rad)} mm");
        if (r.UsableMm is double u) text.Append($", usable {Replies.Mm(u)} mm");
        else if (r.OutsideShankMm is double l) text.Append($", overall minus shank {Replies.Mm(l)} mm (calculated)");
        return text.ToString();
    }

    private static string Contents(Library library)
    {
        var reply = new StringBuilder($"'{library.Name}' [id {library.Id}]{(library.Shared ? $" (shared, {library.Role})" : "")} holds {library.Items.Count} items:");
        foreach (var item in library.Items)
        {
            reply.AppendLine();
            if (item.Assembly != null)
            {
                reply.Append("  " + Assembly(item));
                continue;
            }
            if (item.Custom is { } t)
            {
                reply.Append("  " + Custom(t));
                continue;
            }
            reply.Append($"  {PartName(item)} - {item.KitName ?? item.Kit}");
            if (item.DiameterMm is { } d) reply.Append($", Ø{Replies.Mm(d)} mm");
            if (item.Reference?.OverallMm == null && item.LengthMm is { } l) reply.Append($", L {Replies.Mm(l)} mm");
            reply.Append($" [id {item.Component}]");
            if (!item.Present) reply.Append(" - GONE from the catalogue");
        }
        return reply.ToString();
    }
}
