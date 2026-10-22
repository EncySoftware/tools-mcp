using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ToolsMcp;

/// <summary>
/// What `tools-mcp serve` runs with. PublicUrl is the site as clients see it (TOOLS_MCP_PUBLIC_URL): behind nginx the
/// request itself says http://127.0.0.1.
/// </summary>
public sealed record ServeSettings(string DataDir, TokenSettings Tokens, string PublicUrl)
{
    public string ResourceUrl => PublicUrl + "/mcp";
    public string MetadataUrl => PublicUrl + Door.McpMetadataPath;
}

/// <summary>
/// `tools-mcp serve`: the same tools over MCP Streamable HTTP for an agent that cannot start a local process - Hermes.
/// Ported from dmc-mcp's hosted mode (2026-10-22) without its key: every call carries the access token of the person it
/// is for, checked at the door (<see cref="Door"/>), and every call to tools.encycam.com is made with that token
/// (<see cref="CallerTokens"/>). Configuration: TOOLS_MCP_CLIENTS and/or TOOLS_MCP_AUDIENCE (required),
/// TOOLS_MCP_ISSUER, TOOLS_MCP_PUBLIC_URL, TOOLS_MCP_DATA (default /data), ASPNETCORE_HTTP_PORTS / --urls for the port.
/// </summary>
public static class ServeCommand
{
    internal const string Instructions =
        "Hosted ENCY tools server (tools.encycam.com). Every call runs as the person whose access token it carries: " +
        "their supplier catalogues, their libraries. Files come back as links that open for 1 hour without a token; " +
        "a folder cannot be given. Libraries are the way into ENCY: the Tools catalogue extension shows them.";

    public static async Task<int> Run(string[] args)
    {
        var (settings, error) = Read(Environment.GetEnvironmentVariable);
        if (settings == null)
        {
            Console.Error.WriteLine("ERROR: " + error);
            return 1;
        }
        var app = Build(args, settings);
        app.Lifetime.ApplicationStarted.Register(() => _ = WarmUp(app));
        await app.RunAsync();
        return 0;
    }

    /** The settings the environment gives, or why the server cannot start with them. */
    internal static (ServeSettings? Settings, string? Error) Read(Func<string, string?> env)
    {
        TokenSettings? tokens;
        try { tokens = TokenSettings.Parse(env("TOOLS_MCP_ISSUER"), env("TOOLS_MCP_CLIENTS"), env("TOOLS_MCP_AUDIENCE")); }
        catch (ArgumentException e) { return (null, e.Message); }
        // No key here: without a list of clients no token is taken, and nobody at all could get in.
        if (tokens == null)
            return (null, "set TOOLS_MCP_CLIENTS (Hermes's Keycloak client) or TOOLS_MCP_AUDIENCE: without them no token can be accepted");
        var data = env("TOOLS_MCP_DATA") is { Length: > 0 } d ? d : "/data";
        var publicUrl = (env("TOOLS_MCP_PUBLIC_URL") is { Length: > 0 } p ? p.Trim() : Brand.Site).TrimEnd('/');
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri) || !TokenSettings.IsSecure(uri) || uri.AbsolutePath != "/")
            return (null, $"TOOLS_MCP_PUBLIC_URL must be the site's https address, like {Brand.Site}, not \"{publicUrl}\".");
        return (new ServeSettings(data, tokens, publicUrl), null);
    }

    /** The issuer's keys fetched ahead of the first token, and a line saying whether Keycloak answered. */
    private static async Task WarmUp(WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ToolsMcp.Serve");
        var tokens = app.Services.GetRequiredService<UserTokens>();
        try
        {
            var keys = await tokens.WarmUp(app.Lifetime.ApplicationStopping);
            log.LogInformation("Keys of {Issuer}: {Count} loaded - tokens can be checked", tokens.Settings.Issuer, keys.Count);
        }
        catch (Exception e)
        {
            log.LogWarning("Keys of {Issuer} could not be fetched ({Error}); tokens are answered 503 until they can",
                tokens.Settings.Issuer, e.Message);
        }
    }

    /** The tools this server has, by name: what a call may name. The same five classes Build registers. */
    internal static readonly HashSet<string> ToolNames = new[]
        { typeof(CatalogueTools), typeof(SearchTools), typeof(ComponentTools), typeof(BuildTools), typeof(LibraryTools) }
        .SelectMany(t => t.GetMethods())
        .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
        .OfType<string>()
        .ToHashSet(StringComparer.Ordinal);

    /** What a call's line names of its arguments: where in the catalogues it looks, never what a person searches for. */
    private static readonly string[] LoggedArguments = ["kit", "classCode", "page", "pageSize"];

    /** Whose call, for its line: the first 12 hex of the person's hash (Caller.Owner), never a name or the token. */
    internal static string Who(Caller? caller) =>
        caller?.Owner is { } owner && owner.StartsWith("user:", StringComparison.Ordinal) ? owner["user:".Length..][..12] : "nobody";

    internal static string Where(IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
    {
        if (arguments == null) return "";
        var given = arguments.ToDictionary(a => a.Key, a => a.Value);
        return string.Join(", ", LoggedArguments.Where(given.ContainsKey).Select(k => k + "=" + Clean(given[k])));
    }

    /** An argument as plain text: no control characters, so it cannot start a log line of its own; 60 characters at most. */
    private static string Clean(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
        text = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        return text.Length > 60 ? text[..60] + "..." : text;
    }

    /** The startup line: who may come in. */
    internal static string Modes(ServeSettings settings) =>
        $"Ways in: {settings.Tokens.Describe()}; each call runs as the token's person; files as links for 1 hour; "
        + $"resource metadata at {settings.MetadataUrl}";

    internal static WebApplication Build(string[] args, ServeSettings settings, Action<IServiceCollection>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        // One line a call: through this server every call reaches the site from one address, and without the line
        // wholesale paging of a catalogue could not be told from ordinary use (design, 2026-10-22). Set once the app is
        // built; no call comes before that.
        ILogger? calls = null;
        // A token travels in a header: keep ASP.NET from logging requests.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        // The SDK writes a line for every call, with the tool's name as the agent sent it - text that can forge a line of
        // its own (final review, 2026-10-22). The call line below is the record of a call.
        builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Warning);

        // what the site sends is held in memory on its way to a link: never more than the store keeps
        builder.Services.AddSingleton<IToolsApi>(new ToolsApi { MaxFile = DownloadStore.MaxFile });
        builder.Services.AddSingleton<TokenProvider, CallerTokens>();
        builder.Services.AddSingleton(new DownloadStore(Path.Combine(settings.DataDir, "files")));
        builder.Services.AddSingleton<IFileSink>(sp => new LinkFiles(sp.GetRequiredService<DownloadStore>(), settings.PublicUrl));
        builder.Services.AddHostedService<DownloadJanitor>();
        builder.Services.AddSingleton(new IssuerKeys(settings.Tokens.Issuer));
        builder.Services.AddSingleton(sp => new UserTokens(settings.Tokens, sp.GetRequiredService<IssuerKeys>()));
        configure?.Invoke(builder.Services); // tests replace the site, the issuer's keys and the store's clock
        builder.Services
            .AddMcpServer(o => o.ServerInstructions = Instructions)
            // Stateless: nothing to lose on a restart or an update - a session would end with each, and a client that
            // does not re-initialize on 404 stays broken. Each request runs in its own execution context, which is what
            // carries the caller the door let in.
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<CatalogueTools>()
            .WithTools<SearchTools>()
            .WithTools<ComponentTools>()
            .WithTools<BuildTools>()
            .WithTools<LibraryTools>()
            .WithRequestFilters(f => f.AddCallToolFilter(next => async (request, ct) =>
            {
                // A name is the agent's text: one that names no tool is answered here, unwritten - the SDK's own
                // error line would quote it whole (final review, 2026-10-22).
                if (request.Params?.Name is not { } tool || !ToolNames.Contains(tool))
                    return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "ERROR: no such tool" }] };
                calls?.LogInformation("Call {Tool} by {Who} {Args}", tool, Who(Caller.Current), Where(request.Params.Arguments));
                return await next(request, ct);
            }));

        var app = builder.Build();
        var loggers = app.Services.GetRequiredService<ILoggerFactory>();
        calls = loggers.CreateLogger("ToolsMcp.Calls");
        var door = new Door(app.Services.GetRequiredService<UserTokens>(), settings.MetadataUrl, loggers.CreateLogger<Door>());
        app.Use((ctx, next) => door.Handle(ctx, next));
        app.UseRouting();

        // RFC 9728, as the MCP authorization spec asks: where to get a token for this server. The SDK's own
        // McpAuthenticationHandler is not used - it derives these addresses from the request (http behind nginx).
        var metadata = new ProtectedResourceMetadata
        {
            Resource = settings.ResourceUrl,
            AuthorizationServers = [settings.Tokens.Issuer],
            BearerMethodsSupported = ["header"],
            ScopesSupported = ["openid"],
        };
        var typeInfo = McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(ProtectedResourceMetadata));
        app.MapGet(Door.McpMetadataPath, () => Results.Json(metadata, typeInfo));
        app.MapGet(Door.MetadataPath, () => Results.Json(metadata, typeInfo));
        // A link opens without a token (the door lets /mcp/files/ through): its 128-bit id is the credential. Nothing
        // about an unknown or expired one is told.
        app.MapGet(Door.FilesPath + "{id}/{name}", (string id, DownloadStore store, HttpContext ctx) =>
        {
            if (store.Find(id) is not { } file) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.File(file.Path, "application/octet-stream", file.Name);
        });
        app.MapMcp("/mcp");

        var log = loggers.CreateLogger("ToolsMcp.Serve");
        app.Lifetime.ApplicationStarted.Register(() => log.LogInformation("{Modes}", Modes(settings)));
        return app;
    }
}
