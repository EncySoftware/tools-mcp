using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

    private async Task<HttpClient> Start(FakeToolsApi api)
    {
        var app = ServeCommand.Build(new[] { "--urls", "http://127.0.0.1:0" }, Settings(), s =>
        {
            s.AddSingleton<IToolsApi>(api);
            s.AddSingleton(new IssuerKeys(TestIssuer.Issuer, _realm));
            s.AddSingleton<ILoggerProvider>(_logs);
            s.Configure<LoggerFilterOptions>(o => o.MinLevel = LogLevel.Trace);
        });
        _apps.Add(app);
        await app.StartAsync();
        return new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

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
