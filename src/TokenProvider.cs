using System.Text.Json;

namespace ToolsMcp;

/// <summary>
/// `tools-mcp login` signs in once through the browser (scope offline_access) and stores ONLY the
/// refresh token under %APPDATA%. Afterwards fresh access tokens are minted on demand.
/// TOOLS_TOKEN, when set, overrides everything: a CI/debug escape hatch. An access token lives
/// minutes, so it is no way to hand a session in.
/// </summary>
public class TokenProvider
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /** The HTTP client for Keycloak's token endpoint, replaced in tests. */
    internal HttpClient Client { get; init; } = Http;

    /** One refresh at a time: Keycloak rotates refresh tokens, so a second refresh with the same token fails. */
    private readonly SemaphoreSlim _refreshing = new(1, 1);

    private readonly string _tokenEndpoint =
        Environment.GetEnvironmentVariable("TOOLS_KEYCLOAK_TOKEN_ENDPOINT")
        ?? $"{Brand.KeycloakUrl}realms/{Brand.KeycloakRealm}/protocol/openid-connect/token";

    private readonly string _clientId =
        Environment.GetEnvironmentVariable("TOOLS_CLIENT_ID") ?? Brand.KeycloakClient;

    private string? _cachedAccess;
    private DateTimeOffset _cachedUntil = DateTimeOffset.MinValue;

    /** TOOLS_AUTH_FILE points the token file elsewhere; tests use it. */
    public static string AuthFilePath =>
        Environment.GetEnvironmentVariable("TOOLS_AUTH_FILE") is { Length: > 0 } file
            ? file
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Brand.AuthFolder, "auth.json");

    /// <summary>A fresh access token: the environment override, then the cache, then one minted from the stored
    /// refresh token. Null when the person never signed in or Keycloak refused the refresh; either way the
    /// remedy is `tools-mcp login`.</summary>
    public virtual async Task<string?> GetAccessToken()
    {
        var env = Environment.GetEnvironmentVariable("TOOLS_TOKEN");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        if (_cachedAccess != null && DateTimeOffset.UtcNow < _cachedUntil) return _cachedAccess;

        // An agent can call two tools at once. Both find the cache expired; the first refreshes and the
        // second waits, then takes what the first cached.
        await _refreshing.WaitAsync();
        try
        {
            if (_cachedAccess != null && DateTimeOffset.UtcNow < _cachedUntil) return _cachedAccess;

            var stored = ReadStored();
            if (stored.Refresh == null) return null;

            // Refresh with the client that ISSUED the token: a refresh token belongs to its client.
            var resp = await Client.PostAsync(_tokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = stored.ClientId ?? _clientId,
                ["refresh_token"] = stored.Refresh,
            }));
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            _cachedAccess = doc.RootElement.GetProperty("access_token").GetString();
            int expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 300;
            _cachedUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 60));
            // Keycloak rotates refresh tokens: keep the newest one.
            if (doc.RootElement.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { Length: > 0 } newRefresh)
                SaveRefreshToken(newRefresh, stored.ClientId ?? _clientId);
            return _cachedAccess;
        }
        finally { _refreshing.Release(); }
    }

    /**
     * Sign in through the ENCY sign-in page in a browser: this tool has no business seeing anyone's password, and
     * two-factor and SSO only work there. It is the only way: the site's client has Direct Access Grants off.
     */
    public async Task<int> LoginBrowser()
    {
        string? refresh = await BrowserLogin.SignIn(_tokenEndpoint, _clientId, OpenInBrowser, Console.Error.WriteLine, Http);
        if (refresh == null) return 1;
        SaveRefreshToken(refresh, _clientId);
        Console.WriteLine("Signed in to " + Brand.Product + ". Tokens are minted automatically from now on (stored: "
                          + AuthFilePath + ").");
        return 0;
    }

    private static Task OpenInBrowser(string url)
    {
        try
        {
            // UseShellExecute hands the URL to the default browser rather than trying to execute it.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            // Not fatal: the address was printed, and pasting it by hand works the same.
            Console.Error.WriteLine("Could not open the browser (" + e.Message + ") - open the address above.");
        }
        return Task.CompletedTask;
    }

    private record Stored(string? Refresh, string? ClientId);

    private static Stored ReadStored()
    {
        try
        {
            if (!File.Exists(AuthFilePath)) return new Stored(null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(AuthFilePath));
            var root = doc.RootElement;
            return new Stored(
                root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
                root.TryGetProperty("client_id", out var cid) ? cid.GetString() : null);
        }
        catch (Exception) { return new Stored(null, null); }
    }

    /**
     * The refresh token is an offline session: whoever reads the file acts as the account. On Unix the file is the
     * owner's alone (0600) and a folder created for it 0700; under the usual umask 022 they came out
     * world-readable in dmc-mcp.
     */
    private static void SaveRefreshToken(string refresh, string clientId)
    {
        var json = JsonSerializer.Serialize(new { refresh_token = refresh, client_id = clientId });
        var dir = Path.GetDirectoryName(AuthFilePath)!;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(AuthFilePath, json);
            return;
        }
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Directory.CreateDirectory(dir, ownerOnly | UnixFileMode.UserExecute);
        using (var f = new FileStream(AuthFilePath, new FileStreamOptions
               { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = ownerOnly }))
        using (var w = new StreamWriter(f))
            w.Write(json);
        File.SetUnixFileMode(AuthFilePath, ownerOnly); // UnixCreateMode applies only to a file it creates
    }
}
