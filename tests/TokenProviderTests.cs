using System.Net;
using ToolsMcp;
using Xunit;

/** These tests set process-wide environment variables, so they run one at a time. */
[Collection("environment")]
public class TokenProviderTests
{
    /** Our own token file: it is not shared with the extension, which keeps its own sign-in. */
    [Fact]
    public void AuthFileLivesInItsOwnFolder()
    {
        Assert.EndsWith(Path.Combine("tools-mcp", "auth.json"), TokenProvider.AuthFilePath);
    }

    [Fact]
    public async Task NoStoredSignInMeansNoToken()
    {
        using var auth = new AuthFile(null);
        var keycloak = new FakeKeycloak();

        Assert.Null(await new TokenProvider { Client = new HttpClient(keycloak) }.GetAccessToken());
        Assert.Equal(0, keycloak.Calls);
    }

    /** TOOLS_TOKEN is for CI and debugging: a ready token bypasses the stored sign-in and Keycloak. */
    [Fact]
    public async Task TheEnvironmentTokenWinsOverEverything()
    {
        using var auth = new AuthFile("""{"refresh_token":"r1","client_id":"tools-encycam"}""");
        Environment.SetEnvironmentVariable("TOOLS_TOKEN", " abc ");
        try
        {
            var keycloak = new FakeKeycloak();
            Assert.Equal("abc", await new TokenProvider { Client = new HttpClient(keycloak) }.GetAccessToken());
            Assert.Equal(0, keycloak.Calls);
        }
        finally { Environment.SetEnvironmentVariable("TOOLS_TOKEN", null); }
    }

    /** Keycloak rotates refresh tokens: the old one is spent, so the new one must be what is kept. */
    [Fact]
    public async Task ARefreshTokenIsExchangedAndTheRotatedOneIsSaved()
    {
        using var auth = new AuthFile("""{"refresh_token":"r1","client_id":"tools-encycam"}""");
        var keycloak = new FakeKeycloak();

        Assert.Equal("a1", await new TokenProvider { Client = new HttpClient(keycloak) }.GetAccessToken());
        Assert.Equal("refresh_token", keycloak.LastForm["grant_type"]);
        Assert.Equal("tools-encycam", keycloak.LastForm["client_id"]);
        Assert.Equal("r1", keycloak.LastForm["refresh_token"]);
        Assert.Contains("\"r2\"", File.ReadAllText(auth.Path));
    }

    /** An agent calls two tools at once: one refresh, not two - rotation would spend the token under the second. */
    [Fact]
    public async Task TwoCallsAtOnceRefreshOnce()
    {
        using var auth = new AuthFile("""{"refresh_token":"r1","client_id":"tools-encycam"}""");
        var keycloak = new FakeKeycloak { Delay = TimeSpan.FromMilliseconds(50) };
        var provider = new TokenProvider { Client = new HttpClient(keycloak) };

        var tokens = await Task.WhenAll(provider.GetAccessToken(), provider.GetAccessToken());

        Assert.All(tokens, t => Assert.Equal("a1", t));
        Assert.Equal(1, keycloak.Calls);
    }

    /** A revoked or expired sign-in is the same to the person as none: sign in again. */
    [Fact]
    public async Task ARefusedRefreshMeansNotSignedIn()
    {
        using var auth = new AuthFile("""{"refresh_token":"r1","client_id":"tools-encycam"}""");
        var keycloak = new FakeKeycloak { Status = HttpStatusCode.BadRequest, Body = """{"error":"invalid_grant"}""" };

        Assert.Null(await new TokenProvider { Client = new HttpClient(keycloak) }.GetAccessToken());
    }
}

/** A temporary auth.json named by TOOLS_AUTH_FILE; null content means no file at all. */
internal sealed class AuthFile : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tools-auth-" + Guid.NewGuid().ToString("N")[..8] + ".json");

    public AuthFile(string? content)
    {
        if (content != null) File.WriteAllText(Path, content);
        Environment.SetEnvironmentVariable("TOOLS_AUTH_FILE", Path);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TOOLS_AUTH_FILE", null);
        File.Delete(Path);
    }
}

/** Keycloak's token endpoint: counts the calls and keeps the last form it was sent. */
internal sealed class FakeKeycloak : HttpMessageHandler
{
    public int Calls;
    public Dictionary<string, string> LastForm { get; private set; } = new();
    public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
    public string Body { get; init; } = """{"access_token":"a1","expires_in":300,"refresh_token":"r2"}""";
    public TimeSpan Delay { get; init; } = TimeSpan.Zero;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        var form = await request.Content!.ReadAsStringAsync(ct);
        LastForm = form.Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        return new HttpResponseMessage(Status) { Content = new StringContent(Body) };
    }
}
