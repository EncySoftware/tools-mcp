using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ToolsMcp;
using Xunit;

/**
 * `tools-mcp serve` end to end on 127.0.0.1: a person's access token at the door, the tools behind it, and every call to
 * tools.encycam.com made with the token of the person whose request it is - never anyone else's.
 */
public class ServeTests : IAsyncLifetime
{
    private const string PublicUrl = "https://tools.test";
    private const string MetadataUrl = PublicUrl + "/.well-known/oauth-protected-resource/mcp";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "tools-serve-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly TestIssuer _realm = new();
    private readonly CapturedLogs _logs = new();
    private readonly List<WebApplication> _apps = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var app in _apps)
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
        try { Directory.Delete(_data, true); } catch { }
    }

    /** Hermes's client and the site's own, as on the server. */
    private ServeSettings Settings() => new(Path.Combine(_data, _apps.Count.ToString()),
        new TokenSettings(TestIssuer.Issuer, new[] { "dealerspace-mcp", "tools-encycam" }, Array.Empty<string>()), PublicUrl);

    /** files: the download store, made for the server's files folder (a clock of the test's). */
    private async Task<HttpClient> Start(FakeToolsApi api, Func<string, DownloadStore>? files = null)
    {
        var settings = Settings();
        _lastFiles = Path.Combine(settings.DataDir, "files");
        var app = ServeCommand.Build(new[] { "--urls", "http://127.0.0.1:0" }, settings, s =>
        {
            s.AddSingleton<IToolsApi>(api);
            if (files != null) s.AddSingleton(files(_lastFiles));
            s.AddSingleton(new IssuerKeys(TestIssuer.Issuer, _realm));
            s.AddSingleton<ILoggerProvider>(_logs);
            s.Configure<LoggerFilterOptions>(o => o.MinLevel = LogLevel.Trace);
        });
        _apps.Add(app);
        await app.StartAsync();
        return new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    private string _lastFiles = "";

    /** What the last server started keeps in its download store. */
    private string[] Kept() =>
        Directory.Exists(_lastFiles) ? Directory.GetFiles(_lastFiles, "*", SearchOption.AllDirectories) : Array.Empty<string>();

    private static HttpRequestMessage Rpc(string body, string? bearer)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        if (bearer != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return req;
    }

    private static HttpRequestMessage Initialize(string? bearer) => Rpc(
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
        bearer);

    private static HttpRequestMessage Call(string tool, object arguments, string? bearer) => Rpc(
        JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new { name = tool, arguments } }),
        bearer);

    /** The JSON-RPC answer: the body itself, or the data of the last event when the server streams. */
    private static async Task<JsonElement> Answer(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        var json = body.TrimStart().StartsWith('{')
            ? body
            : body.Split('\n').Last(l => l.StartsWith("data:", StringComparison.Ordinal))["data:".Length..].Trim();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /** What a tool replied: the text of its one content block. */
    private static async Task<string> Reply(HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await Answer(resp)).GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    private static string Challenge(HttpResponseMessage resp) => string.Join(", ", resp.Headers.WwwAuthenticate.Select(h => h.ToString()));

    // ------------------------------------------------------------------ the door

    [Fact]
    public async Task WithoutATokenItIs401WithTheMetadata()
    {
        var http = await Start(new FakeToolsApi());
        var resp = await http.SendAsync(Initialize(null));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal($"Bearer resource_metadata=\"{MetadataUrl}\"", Challenge(resp));

        foreach (var path in new[] { "/.well-known/oauth-protected-resource/mcp", "/.well-known/oauth-protected-resource" })
        {
            var meta = await http.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, meta.StatusCode);
            using var doc = JsonDocument.Parse(await meta.Content.ReadAsStringAsync());
            Assert.Equal(PublicUrl + "/mcp", doc.RootElement.GetProperty("resource").GetString());
            Assert.Equal(new[] { TestIssuer.Issuer },
                doc.RootElement.GetProperty("authorization_servers").EnumerateArray().Select(e => e.GetString()));
        }
    }

    [Fact]
    public async Task NothingOutsideMcpIsServed()
    {
        var http = await Start(new FakeToolsApi());
        var anna = _realm.Token();
        foreach (var path in new[] { "/", "/api/kits", "/mcpx" })
        {
            var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", anna);
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(req)).StatusCode);
        }
    }

    [Fact]
    public async Task APersonsTokenReachesTheTools()
    {
        var http = await Start(new FakeToolsApi());
        var anna = _realm.Token();
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Initialize(anna))).StatusCode);
        var list = await Answer(await http.SendAsync(Rpc("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", anna)));
        Assert.Equal(21, list.GetProperty("result").GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public async Task ACallRunsAsTheTokensPerson()
    {
        var api = new FakeToolsApi();
        var http = await Start(api);
        var anna = _realm.Token(sub: "sub-anna", name: "anna@example.com");
        Assert.StartsWith("Signed in to tools.encycam.com as", await Reply(await http.SendAsync(Call("whoami", new { }, anna))));
        Assert.Equal(new[] { ("GetMe", anna) }, api.Calls);
    }

    /** DealerSpace's own site client, or any other app of the realm: anyone can mint those, so they open nothing here. */
    [Fact]
    public async Task ATokenFromAnotherClientIsRefused()
    {
        var api = new FakeToolsApi();
        var http = await Start(api);
        var resp = await http.SendAsync(Call("whoami", new { }, _realm.Token(edit: c => c["azp"] = "dealer-space")));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.StartsWith("Bearer error=\"invalid_token\", error_description=\"", Challenge(resp));
        Assert.EndsWith($"resource_metadata=\"{MetadataUrl}\"", Challenge(resp));
        Assert.Empty(api.Calls);
    }

    /** Keycloak unreachable before its keys were ever fetched: not the token's fault, so not a 401. */
    [Fact]
    public async Task WhenTheRealmsKeysCannotBeHadItIs503()
    {
        _realm.Down = true;
        var api = new FakeToolsApi();
        var http = await Start(api);
        var resp = await http.SendAsync(Call("whoami", new { }, _realm.Token()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Empty(api.Calls);
    }

    /**
     * Two people through Hermes at the same moment: each call reaches the site with its own person's token. The fake
     * site holds both calls until both have arrived, so they really overlap.
     */
    [Fact]
    public async Task TwoPeopleAtOnceEachRunAsThemselves()
    {
        var anna = _realm.Token(sub: "sub-anna", name: "anna@example.com");
        var boris = _realm.Token(sub: "sub-boris", name: "boris@example.com");
        var arrived = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new FakeToolsApi
        {
            MeFor = async token =>
            {
                if (Interlocked.Increment(ref arrived) == 2) both.SetResult();
                await both.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return token == anna ? new Account("sub-anna", "anna", null) : new Account("sub-boris", "boris", null);
            },
        };
        var http = await Start(api);

        var replies = await Task.WhenAll(
            http.SendAsync(Call("whoami", new { }, anna)).ContinueWith(t => Reply(t.Result)).Unwrap(),
            http.SendAsync(Call("whoami", new { }, boris)).ContinueWith(t => Reply(t.Result)).Unwrap());

        Assert.Equal("Signed in to tools.encycam.com as anna.", replies[0]);
        Assert.Equal("Signed in to tools.encycam.com as boris.", replies[1]);
        Assert.Equal(new[] { anna, boris }.OrderBy(t => t), api.Calls.Select(c => c.Token).OrderBy(t => t));
    }

    /** The site refusing the person's token: the fix is a new sign-in in the agent, not a command on this server. */
    [Fact]
    public async Task ARefusedSignInAsksForANewOneInTheAgent()
    {
        var api = new FakeToolsApi();
        api.Fail["GetMe"] = new ToolsHttpException(401, "");
        var http = await Start(api);
        Assert.Equal("ERROR: the sign-in has expired or was refused - the person must sign in to tools.encycam.com again in the agent",
            await Reply(await http.SendAsync(Call("whoami", new { }, _realm.Token()))));
    }

    [Fact]
    public async Task NoTokenReachesTheLog()
    {
        var http = await Start(new FakeToolsApi());
        var anna = _realm.Token();
        var refused = _realm.Token(sub: "sub-boris", edit: c => c["azp"] = "dealer-space");
        await Reply(await http.SendAsync(Call("whoami", new { }, anna)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Call("whoami", new { }, refused))).StatusCode);

        Assert.Contains(_logs.Lines, l => l.Contains("Refused a token"));
        foreach (var token in new[] { anna, refused })
            foreach (var line in _logs.Lines)
            {
                Assert.DoesNotContain(token, line);
                Assert.DoesNotContain(token.Split('.')[^1], line); // the signature alone
            }
    }

    /**
     * Every update and every restart would end a session: the next call got 404 "Session not found", and a client that
     * does not re-initialize stayed broken (dmc-mcp, 2026-10). The server hands out no session, and a call works on a
     * process that never saw this client's initialize.
     */
    [Fact]
    public async Task TheServerKeepsNoSessionToLose()
    {
        var api = new FakeToolsApi();
        var http = await Start(api);
        var anna = _realm.Token();
        var init = await http.SendAsync(Initialize(anna));
        Assert.Equal(HttpStatusCode.OK, init.StatusCode);
        Assert.False(init.Headers.Contains("Mcp-Session-Id"));

        var restarted = await Start(api);
        Assert.StartsWith("Signed in", await Reply(await restarted.SendAsync(Call("whoami", new { }, anna))));
    }

    // ------------------------------------------------------------------ files

    private static readonly object Mills = new { name = "Mills", componentIds = new[] { TestIds.G("c1") } };

    /** The path of the link a reply hands out. */
    private static string LinkPath(string reply)
    {
        var m = Regex.Match(reply, @"^Download \(1 hour\): https://tools\.test(/mcp/files/[0-9a-f]{32}/[^ ]+)( |$)");
        Assert.True(m.Success, reply);
        return m.Groups[1].Value;
    }

    [Fact]
    public async Task ABuiltKitComesBackAsALink()
    {
        var http = await Start(new FakeToolsApi());
        var reply = await Reply(await http.SendAsync(Call("build_kit", Mills, _realm.Token())));
        Assert.EndsWith("/Mills.mKit - 1 components and 0 custom tools. Import it into ENCY's tool library.", reply);
        Assert.StartsWith("/mcp/files/", LinkPath(reply));
    }

    /** A person's browser or messenger sends no token: the link alone opens the file, for an hour. */
    [Fact]
    public async Task ALinkOpensWithoutATokenForAnHour()
    {
        var http = await Start(new FakeToolsApi());
        var path = LinkPath(await Reply(await http.SendAsync(Call("build_kit", Mills, _realm.Token()))));
        var resp = await http.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, await resp.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", resp.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("Mills.mKit", resp.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.True(resp.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task AnUnknownOrExpiredLinkIs404()
    {
        var now = DateTimeOffset.UtcNow;
        var http = await Start(new FakeToolsApi(), dir => new DownloadStore(dir, () => now));
        var path = LinkPath(await Reply(await http.SendAsync(Call("build_kit", Mills, _realm.Token()))));
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(path)).StatusCode);

        foreach (var unknown in new[] { "/mcp/files/0123456789abcdef0123456789abcdef/Mills.mKit", "/mcp/files/not-an-id/Mills.mKit" })
        {
            var resp = await http.GetAsync(unknown);
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            Assert.Empty(await resp.Content.ReadAsByteArrayAsync());
        }

        now += TimeSpan.FromMinutes(61);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(path)).StatusCode);
    }

    /** The server never writes where an agent points - and says so before the site builds or counts anything. */
    [Fact]
    public async Task AFolderIsRefusedOnTheHostedServer()
    {
        var api = new FakeToolsApi();
        var http = await Start(api);
        var reply = await Reply(await http.SendAsync(Call("build_kit",
            new { name = "Mills", componentIds = new[] { TestIds.G("c1") }, folder = "/etc" }, _realm.Token())));
        Assert.Equal("ERROR: this server cannot save into a folder - leave folder out, and a link comes back", reply);
        Assert.False(api.Called(nameof(api.BuildKit)));
        Assert.Empty(Kept());
    }

    /** The site's file is held in memory on its way to a link: the hosted server caps it at what the store keeps. */
    [Fact]
    public void TheHostedServerCapsWhatItDownloads()
    {
        var app = ServeCommand.Build(new[] { "--urls", "http://127.0.0.1:0" }, Settings(), s => s.AddSingleton(new IssuerKeys(TestIssuer.Issuer, _realm)));
        _apps.Add(app);
        Assert.Equal(DownloadStore.MaxFile, Assert.IsType<ToolsApi>(app.Services.GetRequiredService<IToolsApi>()).MaxFile);
    }

    /** With their share already full, a person learns so before the site builds anything for them. */
    [Fact]
    public async Task APersonWithAFullShareIsRefusedBeforeTheSiteBuilds()
    {
        var api = new FakeToolsApi();
        var http = await Start(api, dir => new DownloadStore(dir, null, 10, 3, 100)); // the fake's kit is 3 bytes
        var anna = _realm.Token();
        LinkPath(await Reply(await http.SendAsync(Call("build_kit", Mills, anna))));

        Assert.Equal("ERROR: your links hold 3 bytes - wait for older ones to expire (1 hour)",
            await Reply(await http.SendAsync(Call("build_kit", Mills, anna))));
        Assert.Single(api.Calls, c => c.Method == nameof(api.BuildKit));
    }

    /** Every tool that hands out a file, with and without a folder: a link, or the refusal - never a write to a disk. */
    [Theory]
    [InlineData("download_kit")]
    [InlineData("build_kit")]
    [InlineData("build_custom_tool")]
    [InlineData("export_library")]
    [InlineData("save_component_file")]
    public async Task EveryFileToolHandsOutALinkAndRefusesAFolder(string tool)
    {
        var api = new FakeToolsApi();
        var library = TestIds.G("lib1");
        api.KitDetails["mills"] = new KitDetail("mills", "Mills", "Category", "MIXED", 3, "mkit", "About it.", 2, [], []);
        api.KitFiles["mills"] = new DownloadedFile("Mills.mKit", [1, 2]);
        api.Libraries[library] = new Library(library, "Kept", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, []);
        api.Components[TestIds.G("c1")] = new ComponentDetail(new Component(TestIds.G("c1"), "C6-DCLNL", "External turning tool",
            "TRNGEI", null, 150, "sandvik-gtc", "Sandvik Coromant catalogue", 3, true, true, false, null, true, "Sandvik Coromant"), null);
        api.ComponentFiles[(TestIds.G("c1"), ComponentFileKind.Model)] = new DownloadedFile("", [7]);
        var args = new Dictionary<string, object>
        {
            ["download_kit"] = new { slug = "mills" },
            ["build_kit"] = new { name = "Mills", componentIds = new[] { TestIds.G("c1") } },
            ["build_custom_tool"] = new { family = "drill", diameterMm = 7.5, lengthMm = 80.0 },
            ["export_library"] = new { id = library },
            ["save_component_file"] = new { id = TestIds.G("c1"), kind = "model" },
        };
        var http = await Start(api);

        var linked = await Reply(await http.SendAsync(Call(tool, args[tool], _realm.Token())));
        Assert.StartsWith("/mcp/files/", LinkPath(linked));

        var withFolder = JsonSerializer.SerializeToNode(args[tool])!.AsObject();
        withFolder["folder"] = "/tmp";
        Assert.Equal("ERROR: this server cannot save into a folder - leave folder out, and a link comes back",
            await Reply(await http.SendAsync(Call(tool, withFolder, _realm.Token()))));
        Assert.Single(Directory.GetDirectories(_lastFiles)); // the first call's link, nothing more
    }

    // ------------------------------------------------------------------ the call line

    /** The lines that name a call of the tool. */
    private string[] CallLines(string tool) => _logs.Lines.Where(l => l.Contains("Call " + tool + " by ")).ToArray();

    /**
     * Through the hosted server every call reaches the site from one address: without this line, wholesale paging of a
     * catalogue could not be told from ordinary use (design, 2026-10-22).
     */
    [Fact]
    public async Task EveryCallIsLoggedWithWhoAndWhere()
    {
        var http = await Start(new FakeToolsApi());
        await http.SendAsync(Call("find_components", new { kit = "sandvik", page = 2, query = "CNMG 120408" }, _realm.Token(sub: "sub-anna")));
        var who = Caller.Person("t", "n", TestIssuer.Issuer, "sub-anna").Owner["user:".Length..][..12];
        var line = Assert.Single(CallLines("find_components"));
        Assert.Contains($"Call find_components by {who} kit=sandvik, page=2", line);
        Assert.DoesNotContain("CNMG", line); // what a person searches for is theirs
    }

    [Fact]
    public async Task TheCallLineNamesNoToken()
    {
        var http = await Start(new FakeToolsApi());
        var anna = _realm.Token(sub: "sub-anna", name: "anna@example.com");
        await Reply(await http.SendAsync(Call("whoami", new { }, anna)));
        var line = Assert.Single(CallLines("whoami"));
        foreach (var secret in new[] { anna, anna.Split('.')[^1], "anna@example.com", "sub-anna" })
            Assert.DoesNotContain(secret, line);
    }

    /**
     * A tool's name is the agent's text too: one that names no tool is answered without being written down, and the
     * SDK's own error line, which would quote it, never comes (final review, 2026-10-22).
     */
    [Fact]
    public async Task AToolNameCannotForgeALogLine()
    {
        var api = new FakeToolsApi();
        var http = await Start(api);
        var resp = await http.SendAsync(Call("whoami\nInformation ToolsMcp.Calls: Call delete_library by 000000000000", new { }, _realm.Token()));

        var answer = await Answer(resp);
        Assert.True(answer.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal("ERROR: no such tool", answer.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.DoesNotContain(_logs.Lines, l => l.Contains("delete_library"));
        Assert.DoesNotContain(_logs.Lines, l => l.StartsWith("Error"));
        Assert.Empty(api.Calls);
    }

    /** An argument is the agent's text: it cannot start a line of its own in the log. */
    [Fact]
    public async Task AnArgumentCannotForgeALogLine()
    {
        var http = await Start(new FakeToolsApi());
        await http.SendAsync(Call("find_components", new { kit = "a\nInformation ToolsMcp.Calls: Call delete_library by 000000000000" }, _realm.Token()));
        var line = Assert.Single(CallLines("find_components"));
        Assert.DoesNotContain("\n", line);
        Assert.Contains("kit=a Information", line);
    }

    // ------------------------------------------------------------------ the settings

    [Fact]
    public void ItDoesNotStartWithoutClientsOrAudience()
    {
        var (settings, error) = ServeCommand.Read(_ => null);
        Assert.Null(settings);
        Assert.Equal("set TOOLS_MCP_CLIENTS (Hermes's Keycloak client) or TOOLS_MCP_AUDIENCE: without them no token can be accepted", error);
    }

    [Fact]
    public void TheSettingsComeFromTheEnvironment()
    {
        var env = new Dictionary<string, string> { ["TOOLS_MCP_CLIENTS"] = "dealerspace-mcp, tools-encycam" };
        var (s, error) = ServeCommand.Read(k => env.GetValueOrDefault(k));
        Assert.Null(error);
        Assert.Equal("/data", s!.DataDir);
        Assert.Equal("https://tools.encycam.com", s.PublicUrl);
        Assert.Equal(TokenSettings.DefaultIssuer, s.Tokens.Issuer);
        Assert.Equal(new[] { "dealerspace-mcp", "tools-encycam" }, s.Tokens.Clients);

        env["TOOLS_MCP_PUBLIC_URL"] = "http://tools.encycam.com/mcp";
        Assert.Contains("TOOLS_MCP_PUBLIC_URL", ServeCommand.Read(k => env.GetValueOrDefault(k)).Error);
    }

    /** Every log line, formatted, with its exception - what an operator's `docker logs` would show. */
    private sealed class CapturedLogs : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public ILogger CreateLogger(string category) => new Logger(category, Lines);
        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? e, Func<TState, Exception?, string> format) =>
                lines.Enqueue($"{level} {category}: {format(state, e)} {e}");
        }
    }
}
