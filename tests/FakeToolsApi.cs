using System.Collections.Concurrent;
using ToolsMcp;

/** tools.encycam.com in memory: results set per method, a failure per method, and every call with the token it carried. */
internal sealed class FakeToolsApi : IToolsApi
{
    public string Site => "https://tools.test";

    public ConcurrentQueue<(string Method, string Token)> Calls { get; } = new();
    /** A method's name → the refusal it answers with. */
    public Dictionary<string, ToolsHttpException> Fail { get; } = new();
    public bool Called(string method) => Calls.Any(c => c.Method == method);

    private void Saw(string method, string token)
    {
        Calls.Enqueue((method, token));
        if (Fail.TryGetValue(method, out var e)) throw e;
    }

    // ------------------------------------------------------------------ account and kits
    public Account Me { get; set; } = new("sub-alice", "alice", "alice@example.com");
    public List<KitSummary> Kits { get; } = new();
    public Dictionary<string, KitDetail> KitDetails { get; } = new();
    public Dictionary<string, long> Downloads { get; } = new();
    public Dictionary<string, DownloadedFile> KitFiles { get; } = new();

    /** When set, /me answers by the token - whose it is - after whatever wait the test puts in. */
    public Func<string, Task<Account>>? MeFor { get; set; }

    public async Task<Account> GetMe(string token)
    {
        Saw(nameof(GetMe), token);
        return MeFor != null ? await MeFor(token) : Me;
    }

    public Task<IReadOnlyList<KitSummary>> ListKits(string? query, string? discipline, int? encyVersion, string token)
    {
        Saw(nameof(ListKits), token);
        return Task.FromResult<IReadOnlyList<KitSummary>>(Kits);
    }

    public Task<KitDetail?> GetKit(string slug, string token)
    {
        Saw(nameof(GetKit), token);
        return Task.FromResult(KitDetails.GetValueOrDefault(slug));
    }

    public Task<IReadOnlyDictionary<string, long>> GetDownloads(string token)
    {
        Saw(nameof(GetDownloads), token);
        return Task.FromResult<IReadOnlyDictionary<string, long>>(Downloads);
    }

    public Task<DownloadedFile> DownloadKit(string slug, string token)
    {
        Saw(nameof(DownloadKit), token);
        return Task.FromResult(KitFiles[slug]);
    }

    // ------------------------------------------------------------------ search and components
    public ComponentsPage Page { get; set; } = new(0, 0, 20, []);
    public ComponentQuery? LastQuery { get; private set; }
    public Dictionary<string, CompatCounts> Counts { get; } = new();
    public FilterValues Filters { get; set; } = new([], [], [], [], [], new SizeRange(null, null), []);
    public Dictionary<string, ComponentDetail> Components { get; } = new();
    public Dictionary<string, Compat> Compats { get; } = new();
    public Dictionary<string, AssemblyChain> Assemblies { get; } = new();
    public Dictionary<string, IReadOnlyList<string>> Accepts { get; } = new();
    public Dictionary<(string Id, ComponentFileKind Kind), DownloadedFile> ComponentFiles { get; } = new();

    public Task<ComponentsPage> FindComponents(ComponentQuery query, string token)
    {
        Saw(nameof(FindComponents), token);
        LastQuery = query;
        return Task.FromResult(Page);
    }

    public Task<IReadOnlyDictionary<string, CompatCounts>> GetCompatCounts(IReadOnlyList<string> ids, string token)
    {
        Saw(nameof(GetCompatCounts), token);
        return Task.FromResult<IReadOnlyDictionary<string, CompatCounts>>(
            ids.Where(Counts.ContainsKey).ToDictionary(id => id, id => Counts[id]));
    }

    public Task<FilterValues> GetFilterValues(ComponentQuery query, string token)
    {
        Saw(nameof(GetFilterValues), token);
        LastQuery = query;
        return Task.FromResult(Filters);
    }

    public Task<ComponentDetail?> GetComponent(string id, string token)
    {
        Saw(nameof(GetComponent), token);
        return Task.FromResult(Components.GetValueOrDefault(id));
    }

    public Task<Compat?> GetCompat(string id, string token)
    {
        Saw(nameof(GetCompat), token);
        return Task.FromResult(Compats.GetValueOrDefault(id));
    }

    public string? LastSpindle { get; private set; }

    public Task<AssemblyChain?> GetAssembly(string id, string? spindle, string token)
    {
        Saw(nameof(GetAssembly), token);
        LastSpindle = spindle;
        return Task.FromResult(Assemblies.GetValueOrDefault(id));
    }

    public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetAcceptsAmong(IReadOnlyList<string> ids, string token)
    {
        Saw(nameof(GetAcceptsAmong), token);
        return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(
            ids.Where(Accepts.ContainsKey).ToDictionary(id => id, id => Accepts[id]));
    }

    public Task<DownloadedFile?> GetComponentFile(string id, ComponentFileKind kind, string token)
    {
        Saw(nameof(GetComponentFile), token);
        return Task.FromResult(ComponentFiles.GetValueOrDefault((id, kind)));
    }

    // ------------------------------------------------------------------ builds
    public (string Name, IReadOnlyList<string> Ids, IReadOnlyList<CustomTool> Custom, string? Sockets)? LastBuild { get; private set; }
    public CustomTool? LastCustomTool { get; private set; }

    public Task<DownloadedFile> BuildKit(string name, IReadOnlyList<string> ids, IReadOnlyList<CustomTool> custom,
                                        string? sockets, string token)
    {
        Saw(nameof(BuildKit), token);
        LastBuild = (name, ids, custom, sockets);
        return Task.FromResult(new DownloadedFile(name + ".mKit", [1, 2, 3]));
    }

    public Task<DownloadedFile> BuildCustomTool(CustomTool spec, string token)
    {
        Saw(nameof(BuildCustomTool), token);
        LastCustomTool = spec;
        return Task.FromResult(new DownloadedFile((spec.Name ?? "7.5mm Drill") + ".mKit", [4, 5]));
    }

    /** How many of the picks the next package leaves out, as the server counts them. */
    public int GtcLeftOut { get; set; }
    public (string Name, IReadOnlyList<string> Ids)? LastGtc { get; private set; }

    public Task<GtcPackage> BuildGtcPackage(string name, IReadOnlyList<string> ids, string token)
    {
        Saw(nameof(BuildGtcPackage), token);
        LastGtc = (name, ids);
        return Task.FromResult(new GtcPackage(new DownloadedFile(name + "-gtc.zip", [8, 9]), GtcLeftOut));
    }

    // ------------------------------------------------------------------ libraries, kept as the server keeps them
    public Dictionary<string, Library> Libraries { get; } = new();

    public Task<IReadOnlyList<LibrarySummary>> ListLibraries(string token)
    {
        Saw(nameof(ListLibraries), token);
        return Task.FromResult<IReadOnlyList<LibrarySummary>>(Libraries.Values
            .Select(l => new LibrarySummary(l.Id, l.Name, l.Items.Count, l.Items.Count(i => !i.Present), l.UpdatedAt))
            .ToList());
    }

    public Task<Library?> GetLibrary(string id, string token)
    {
        Saw(nameof(GetLibrary), token);
        return Task.FromResult(Libraries.GetValueOrDefault(id));
    }

    public Task<Library> CreateLibrary(string name, IReadOnlyList<LibraryEntry> items, string token)
    {
        Saw(nameof(CreateLibrary), token);
        var now = DateTimeOffset.Parse("2026-10-22T08:00:00Z");
        var library = new Library(Guid.NewGuid().ToString("D"), name, now, now, items.Select(ItemOf).ToList());
        Libraries[library.Id] = library;
        return Task.FromResult(library);
    }

    public Task<Library> AddToLibrary(string id, IReadOnlyList<LibraryEntry> items, string token)
    {
        Saw(nameof(AddToLibrary), token);
        var library = Libraries[id];
        var kept = library.Items.ToList();
        foreach (var item in items.Select(ItemOf))
            if (!kept.Any(k => k.Component == item.Component && k.Custom == item.Custom)) kept.Add(item);
        return Task.FromResult(Libraries[id] = library with { Items = kept });
    }

    public Task<Library> RemoveFromLibrary(string id, IReadOnlyList<LibraryEntry> items, string token)
    {
        Saw(nameof(RemoveFromLibrary), token);
        var library = Libraries[id];
        var kept = library.Items
            .Where(i => !items.Any(e => (e.Component != null && e.Component == i.Component)
                                        || (e.Custom != null && i.Custom != null && SameTool(e.Custom, i.Custom))))
            .ToList();
        return Task.FromResult(Libraries[id] = library with { Items = kept });
    }

    public Task<Library> RenameLibrary(string id, string name, string token)
    {
        Saw(nameof(RenameLibrary), token);
        return Task.FromResult(Libraries[id] = Libraries[id] with { Name = name });
    }

    public Task DeleteLibrary(string id, string token)
    {
        Saw(nameof(DeleteLibrary), token);
        Libraries.Remove(id);
        return Task.CompletedTask;
    }

    public Task<DownloadedFile> ExportLibrary(string id, string? sockets, string token)
    {
        Saw(nameof(ExportLibrary), token);
        LastBuild = (Libraries[id].Name, [], [], sockets);
        return Task.FromResult(new DownloadedFile(Libraries[id].Name + ".mKit", [9]));
    }

    /** As the server matches a custom tool to take out: by its numbers, and by name and shank only where given. */
    private static bool SameTool(CustomTool named, CustomTool stored) =>
        named.Family == stored.Family && named.DiameterMm == stored.DiameterMm && named.LengthMm == stored.LengthMm
        && (named.Name == null || named.Name == stored.Name)
        && (named.ShankDiameterMm == null || named.ShankDiameterMm == stored.ShankDiameterMm);

    /** As the server fills an item in: the component's own name and kit, or the custom tool as given. */
    private LibraryItem ItemOf(LibraryEntry entry)
    {
        if (entry.Custom != null)
            return new LibraryItem(null, null, null, true, entry.Custom.Family, "Custom tool",
                entry.Custom.DiameterMm, entry.Custom.LengthMm, entry.Custom);
        var c = Components.GetValueOrDefault(entry.Component!)?.Component;
        return new LibraryItem(entry.Component, c?.Name ?? entry.Component, c?.KitSlug, true, c?.Kind, c?.KitName,
            c?.DiameterMm, c?.LengthMm, null);
    }
}

/** A sign-in that is there (a fixed token) or is not (null). */
internal sealed class FakeTokens(string? token = "t1") : TokenProvider
{
    public override Task<string?> GetAccessToken() => Task.FromResult(token);
}
