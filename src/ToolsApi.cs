using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ToolsMcp;

/// <summary>A refusal from tools.encycam.com, with its status and the body that says why.</summary>
public class ToolsHttpException(int status, string body) : Exception($"{status} {body}")
{
    public int Status { get; } = status;
    public string Body { get; } = body;
}

/// <summary>Who /me says the token belongs to.</summary>
public record Account(string Sub, string? Username, string? Email);
public record SectionCount(string Section, long Count, string? Title, bool Icon, string? Hint);
public record KitSummary(string Slug, string Name, string? Category, string Discipline, int EncyVersion, string Format,
    string? Maker, int ItemsTotal, int? PullableTotal, IReadOnlyList<SectionCount>? Sections);
public record KitItem(string Section, string Name, string? Kind, double? DiameterMm, double? LengthMm, JsonObject? Params);
public record KitDetail(string Slug, string Name, string? Category, string Discipline, int EncyVersion, string Format,
    string? Description, int ItemsTotal, IReadOnlyList<SectionCount> Sections, IReadOnlyList<KitItem> Items);
public record Component(string Id, string Name, string? Kind, string Section, double? DiameterMm, double? LengthMm,
    string KitSlug, string KitName, int EncyVersion, bool Pullable, bool Picture, bool Drawing, string? Holds, bool Model,
    string? Maker);
public record ComponentsPage(long Total, int Page, int Size, IReadOnlyList<Component> Items);
public record ComponentDetail(Component Component, JsonObject? Params);
public record Compat(string? PlugKey, string? PlugLabel, IReadOnlyList<Component> PlugsInto, long PlugsIntoTotal,
    IReadOnlyList<string> SocketKeys, IReadOnlyList<Component> Accepts, long AcceptsTotal, JsonObject? Params);
public record AssemblyStep(Component Component, long Alternatives);
public record AssemblyChain(IReadOnlyList<AssemblyStep> Steps, bool ReachesSpindle, string? StoppedBecause);
public record CompatCounts(long PlugsInto, long Accepts);
public record SizeRange(double? Min, double? Max);
public record FilterValues(IReadOnlyList<SectionCount> Classes, IReadOnlyList<SectionCount> Families,
    IReadOnlyList<SectionCount> Makers, IReadOnlyList<SectionCount> Coatings, IReadOnlyList<SectionCount> Coolant,
    SizeRange Diameters, IReadOnlyList<SectionCount> Shanks);
public record CustomTool(string Family, double DiameterMm, double LengthMm, double? ShankDiameterMm = null, string? Name = null);
/// <summary>An item to put in or take out of a library: a catalogue component or a custom tool, never both.</summary>
public record LibraryEntry(string? Component = null, CustomTool? Custom = null);
public record LibraryItem(string? Component, string? Name, string? Kit, bool Present, string? Kind, string? KitName,
    double? DiameterMm, double? LengthMm, CustomTool? Custom);
public record Library(string Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<LibraryItem> Items);
public record LibrarySummary(string Id, string Name, int ItemCount, int MissingCount, DateTimeOffset UpdatedAt);
public record DownloadedFile(string FileName, byte[] Bytes);
public enum ComponentFileKind { Model, Drawing, Picture }

/// <summary>A search, in the agent's words; <see cref="ToolsApi"/> puts them into the API's.</summary>
public record ComponentQuery(string? Query = null, string? Kit = null, string? ClassCode = null, string? Family = null,
    string? MachineType = null, string? Maker = null, string? Coating = null, string? Coolant = null, string? Shank = null,
    double? MinDiameterMm = null, double? MaxDiameterMm = null, string? Fits = null, bool? Buildable = null,
    int? EncyVersion = null, string? Sort = null, bool Descending = false, int Page = 0, int Size = 20);

/// <summary>Every route of tools.encycam.com, called as the person signed in.</summary>
public interface IToolsApi
{
    string Site { get; }
    Task<Account> GetMe(string token);
    Task<IReadOnlyList<KitSummary>> ListKits(string? query, string? discipline, int? encyVersion, string token);
    Task<KitDetail?> GetKit(string slug, string token);
    Task<IReadOnlyDictionary<string, long>> GetDownloads(string token);
    Task<DownloadedFile> DownloadKit(string slug, string token);
    Task<ComponentsPage> FindComponents(ComponentQuery query, string token);
    Task<IReadOnlyDictionary<string, CompatCounts>> GetCompatCounts(IReadOnlyList<string> ids, string token);
    Task<FilterValues> GetFilterValues(ComponentQuery query, string token);
    Task<ComponentDetail?> GetComponent(string id, string token);
    Task<Compat?> GetCompat(string id, string token);
    /** spindle: the machine's, as ENCY's connection library names it (HSK63, SK40, Capto C6); null for any. */
    Task<AssemblyChain?> GetAssembly(string id, string? spindle, string token);
    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetAcceptsAmong(IReadOnlyList<string> ids, string token);
    Task<DownloadedFile?> GetComponentFile(string id, ComponentFileKind kind, string token);
    Task<DownloadedFile> BuildKit(string name, IReadOnlyList<string> ids, IReadOnlyList<CustomTool> custom, string? sockets, string token);
    Task<DownloadedFile> BuildCustomTool(CustomTool spec, string token);
    Task<IReadOnlyList<LibrarySummary>> ListLibraries(string token);
    Task<Library?> GetLibrary(string id, string token);
    Task<Library> CreateLibrary(string name, IReadOnlyList<LibraryEntry> items, string token);
    Task<Library> AddToLibrary(string id, IReadOnlyList<LibraryEntry> items, string token);
    Task<Library> RemoveFromLibrary(string id, IReadOnlyList<LibraryEntry> items, string token);
    Task<Library> RenameLibrary(string id, string name, string token);
    Task DeleteLibrary(string id, string token);
    Task<DownloadedFile> ExportLibrary(string id, string? sockets, string token);
}

public class ToolsApi : IToolsApi
{
    /** camelCase both ways; a null is left out when writing, so a rename carries no "items" for the server to re-check. */
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _api;

    /**
     * The largest file a download may be; null for no limit (at home the disk decides). The hosted server sets
     * DownloadStore.MaxFile: there a file is held in memory on its way to a link, in a 512 MB container, and it used to
     * be held twice and only then measured (final review, 2026-10-22).
     */
    public long? MaxFile { get; init; }

    public ToolsApi() : this(new HttpClient { Timeout = TimeSpan.FromSeconds(60) },
        Environment.GetEnvironmentVariable("TOOLS_API") is { Length: > 0 } api ? api : Brand.Api)
    {
    }

    internal ToolsApi(HttpClient http, string apiBase)
    {
        _http = http;
        _api = apiBase.TrimEnd('/');
    }

    /** The site beside the API, so a TOOLS_API pointed at another server links to that server's pages. */
    public string Site => _api.EndsWith("/api", StringComparison.Ordinal) ? _api[..^"/api".Length] : Brand.Site;

    public Task<Account> GetMe(string token) => Get<Account>("/me", token);

    public Task<IReadOnlyList<KitSummary>> ListKits(string? query, string? discipline, int? encyVersion, string token) =>
        Get<IReadOnlyList<KitSummary>>("/kits" + Params(("q", query), ("discipline", discipline), ("ency", encyVersion)), token);

    public Task<KitDetail?> GetKit(string slug, string token) => GetOrNull<KitDetail>("/kits/" + Esc(slug), token);

    private record Downloads(long Total, Dictionary<string, long>? ByKit);

    public async Task<IReadOnlyDictionary<string, long>> GetDownloads(string token) =>
        (await Get<Downloads>("/downloads", token)).ByKit ?? new Dictionary<string, long>();

    public async Task<DownloadedFile> DownloadKit(string slug, string token) =>
        await FileOf(await SendForFile(HttpMethod.Get, "/kits/" + Esc(slug) + "/download", token), slug);

    public Task<ComponentsPage> FindComponents(ComponentQuery query, string token) =>
        Get<ComponentsPage>("/components" + Filters(query, paging: true), token);

    public async Task<IReadOnlyDictionary<string, CompatCounts>> GetCompatCounts(IReadOnlyList<string> ids, string token) =>
        await Get<Dictionary<string, CompatCounts>>("/components/compat-counts" + Ids(ids), token);

    private record Choices(IReadOnlyList<SectionCount>? Coatings, IReadOnlyList<SectionCount>? Coolant,
        IReadOnlyList<SectionCount>? Makers);

    public async Task<FilterValues> GetFilterValues(ComponentQuery query, string token)
    {
        string filters = Filters(query, paging: false);
        var classes = Get<List<SectionCount>>("/components/sections" + filters, token);
        var families = Get<List<SectionCount>>("/components/families" + filters, token);
        var choices = Get<Choices>("/components/choices" + filters, token);
        var sizes = Get<SizeRange>("/components/size-range" + filters, token);
        var shanks = Get<List<SectionCount>>("/components/shanks" + filters, token);
        await Task.WhenAll(classes, families, choices, sizes, shanks);
        var c = choices.Result;
        return new FilterValues(classes.Result, families.Result, c.Makers ?? [], c.Coatings ?? [], c.Coolant ?? [],
            sizes.Result, shanks.Result);
    }

    public Task<ComponentDetail?> GetComponent(string id, string token) =>
        GetOrNull<ComponentDetail>("/components/" + Esc(id), token);

    public Task<Compat?> GetCompat(string id, string token) =>
        GetOrNull<Compat>("/components/" + Esc(id) + "/compat", token);

    public Task<AssemblyChain?> GetAssembly(string id, string? spindle, string token) =>
        GetOrNull<AssemblyChain>("/components/" + Esc(id) + "/assembly" + Params(("spindle", spindle)), token);

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetAcceptsAmong(IReadOnlyList<string> ids, string token)
    {
        var found = await Get<Dictionary<string, List<string>>>("/components/accepts-among" + Ids(ids), token);
        return found.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
    }

    /** The model, drawing or photo of one component. These carry no file name: the caller names the file. */
    public async Task<DownloadedFile?> GetComponentFile(string id, ComponentFileKind kind, string token)
    {
        string part = kind switch
        {
            ComponentFileKind.Model => "model",
            ComponentFileKind.Drawing => "drawing",
            _ => "picture",
        };
        var response = await SendForFile(HttpMethod.Get, "/components/" + Esc(id) + "/" + part, token);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }
        return await FileOf(response, "");
    }

    public async Task<DownloadedFile> BuildKit(string name, IReadOnlyList<string> ids, IReadOnlyList<CustomTool> custom,
                                              string? sockets, string token) =>
        await FileOf(await SendForFile(HttpMethod.Post, "/pull", token, new { name, ids, sockets, custom }), name + ".mKit");

    public async Task<DownloadedFile> BuildCustomTool(CustomTool spec, string token) =>
        await FileOf(await SendForFile(HttpMethod.Post, "/custom-tool", token, spec), (spec.Name ?? "Custom tool") + ".mKit");

    public Task<IReadOnlyList<LibrarySummary>> ListLibraries(string token) =>
        Get<IReadOnlyList<LibrarySummary>>("/me/libraries", token);

    public Task<Library?> GetLibrary(string id, string token) => GetOrNull<Library>("/me/libraries/" + Esc(id), token);

    public Task<Library> CreateLibrary(string name, IReadOnlyList<LibraryEntry> items, string token) =>
        Send<Library>(HttpMethod.Post, "/me/libraries", token, new { name, items });

    public Task<Library> AddToLibrary(string id, IReadOnlyList<LibraryEntry> items, string token) =>
        Send<Library>(HttpMethod.Post, "/me/libraries/" + Esc(id) + "/items", token, new { items });

    public Task<Library> RemoveFromLibrary(string id, IReadOnlyList<LibraryEntry> items, string token) =>
        Send<Library>(HttpMethod.Post, "/me/libraries/" + Esc(id) + "/items/remove", token, new { items });

    public Task<Library> RenameLibrary(string id, string name, string token) =>
        Send<Library>(HttpMethod.Put, "/me/libraries/" + Esc(id), token, new { name });

    public async Task DeleteLibrary(string id, string token) =>
        await EnsureOk(await Send(HttpMethod.Delete, "/me/libraries/" + Esc(id), token));

    public async Task<DownloadedFile> ExportLibrary(string id, string? sockets, string token) =>
        await FileOf(await SendForFile(HttpMethod.Post, "/me/libraries/" + Esc(id) + "/pull", token, new { sockets }), "Library.mKit");

    // ------------------------------------------------------------------ plumbing

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string token, object? body = null) =>
        Send(method, path, token, body, HttpCompletionOption.ResponseContentRead);

    /** A request whose answer is a file: its body is left unread, for FileOf to measure before it reads it. */
    private Task<HttpResponseMessage> SendForFile(HttpMethod method, string path, string token, object? body = null) =>
        Send(method, path, token, body, HttpCompletionOption.ResponseHeadersRead);

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string token, object? body,
                                                 HttpCompletionOption completion)
    {
        var request = new HttpRequestMessage(method, _api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        return await _http.SendAsync(request, completion);
    }

    private async Task<T> Send<T>(HttpMethod method, string path, string token, object body)
    {
        var response = await Send(method, path, token, body);
        await EnsureOk(response);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private async Task<T> Get<T>(string path, string token)
    {
        var response = await Send(HttpMethod.Get, path, token);
        await EnsureOk(response);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private async Task<T?> GetOrNull<T>(string path, string token) where T : class
    {
        var response = await Send(HttpMethod.Get, path, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureOk(response);
        return await response.Content.ReadFromJsonAsync<T>(Json);
    }

    private static async Task EnsureOk(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new ToolsHttpException((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /**
     * The file and the name the server gave it. filename* (RFC 5987, UTF-8) comes first: a library named in Cyrillic
     * arrives that way. A plain filename comes next, then the caller's fallback.
     */
    private async Task<DownloadedFile> FileOf(HttpResponseMessage response, string fallback)
    {
        using (response)
        {
            await EnsureOk(response);
            var disposition = response.Content.Headers.ContentDisposition;
            string? name = disposition?.FileNameStar ?? disposition?.FileName?.Trim('"');
            return new DownloadedFile(string.IsNullOrWhiteSpace(name) ? fallback : name, await BodyOf(response.Content));
        }
    }

    /**
     * The body in one array, sized by its Content-Length where the server gives one, and never past MaxFile: a file over
     * it is refused by its length before its body is read, and one that names no length (chunked) as soon as it passes.
     */
    private async Task<byte[]> BodyOf(HttpContent content)
    {
        long? length = content.Headers.ContentLength;
        if (MaxFile is { } max && length > max) throw DownloadStore.TooLarge(max);
        await using var stream = await content.ReadAsStreamAsync();
        if (length is { } exact)
        {
            var bytes = new byte[exact];
            await stream.ReadExactlyAsync(bytes);
            return bytes;
        }
        using var read = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(chunk)) > 0)
        {
            if (MaxFile is { } cap && read.Length + n > cap) throw DownloadStore.TooLarge(cap);
            read.Write(chunk, 0, n);
        }
        return read.ToArray();
    }

    private static string Esc(string segment) => Uri.EscapeDataString(segment);

    private static string Ids(IReadOnlyList<string> ids) => "?ids=" + string.Join(",", ids.Select(Esc));

    /** The search in the API's words. Only what was given is sent, numbers in the invariant culture (no 7,5). */
    private static string Filters(ComponentQuery q, bool paging)
    {
        var pairs = new List<(string, object?)>
        {
            ("q", q.Query), ("kit", q.Kit), ("section", q.ClassCode), ("family", q.Family), ("for", q.MachineType),
            ("maker", q.Maker), ("coating", q.Coating), ("coolant", q.Coolant), ("shank", q.Shank),
            ("minDiameter", q.MinDiameterMm), ("maxDiameter", q.MaxDiameterMm), ("fits", q.Fits),
            ("pullable", q.Buildable), ("ency", q.EncyVersion),
        };
        if (paging)
        {
            pairs.Add(("sort", q.Sort));
            pairs.Add(("dir", q.Descending ? "desc" : null));
            pairs.Add(("page", q.Page));
            pairs.Add(("size", q.Size));
        }
        return Params(pairs.ToArray());
    }

    /** Only what was given: an agent passes "" for a filter it does not mean, and kit= filtered for a kit with no name. */
    private static string Params(params (string Name, object? Value)[] pairs)
    {
        var sent = pairs.Where(p => p.Value != null && !(p.Value is string s && string.IsNullOrWhiteSpace(s)))
            .Select(p => p.Name + "=" + Esc(p.Value switch
            {
                bool b => b ? "true" : "false",
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                var v => v!.ToString()!,
            }))
            .ToList();
        return sent.Count == 0 ? "" : "?" + string.Join("&", sent);
    }
}
