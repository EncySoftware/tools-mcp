using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ToolsMcp;

/// <summary>
/// Sign in through the ENCY sign-in page in the system browser: OAuth authorization code +
/// PKCE, the reply on a loopback port. The extension never sees the password; SSO and
/// two-factor happen in the browser, where they work. Needs Standard Flow and a
/// http://127.0.0.1:*/callback redirect URI on the Keycloak client.
/// </summary>
public static class BrowserLogin
{
    /// <summary>Enough for a person to find the tab, sign in, and answer a second factor.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromMinutes(3);

    /// <summary>PKCE verifier: 32 random bytes, base64url.</summary>
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>S256 challenge for a verifier; plain is never used.</summary>
    public static string ChallengeFor(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>Keycloak's authorize endpoint sits beside its token endpoint.</summary>
    public static string AuthorizeEndpointFor(string tokenEndpoint) =>
        tokenEndpoint.EndsWith("/token", StringComparison.Ordinal)
            ? tokenEndpoint[..^"/token".Length] + "/auth"
            : tokenEndpoint;

    public static string AuthorizeUrl(string authorizeEndpoint, string clientId, string redirectUri,
                                      string state, string challenge)
    {
        var q = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            // offline_access is what lets the stored refresh token outlive the browser session
            ["scope"] = "openid offline_access",
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        return authorizeEndpoint + "?" + string.Join("&",
            q.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
    }

    /// <summary>A loopback port nobody is using: asking for 0 lets the OS pick.</summary>
    public static int FreeLoopbackPort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>What came back on the loopback redirect.</summary>
    public sealed record Callback(string? Code, string? State, string? Error);

    /// <summary>What the browser tab says once it has served its purpose.</summary>
    private const string DonePage =
        "<!doctype html><meta charset=utf-8><title>ENCY tools</title>"
        + "<body style=\"font:16px system-ui;padding:3rem;text-align:center\">"
        + "<p>Signed in to ENCY tools.</p>"
        + "<p style=\"color:#666\">You can close this tab and go back to your agent.</p>";

    /// <summary>
    /// Runs the whole flow and returns the refresh token, or null with the reason written to
    /// <paramref name="write"/>. One method on purpose: every step shares the verifier, the
    /// state and the port, and pulling them apart would only make room for them to disagree.
    /// </summary>
    public static async Task<string?> SignIn(string tokenEndpoint, string clientId,
                                            Func<string, Task> openBrowser, Action<string> write,
                                            HttpClient http)
    {
        string verifier = NewVerifier(), state = NewVerifier();
        int port = FreeLoopbackPort();

        // "localhost" first: on Windows HttpListener may bind it without elevation, while a
        // literal 127.0.0.1 prefix often needs a URL reservation. Whichever binds decides the
        // redirect_uri, which has to be the exact string sent to the authorize endpoint.
        using var listener = new HttpListener();
        string? redirectUri = null;
        foreach (var host in new[] { "localhost", "127.0.0.1" })
        {
            listener.Prefixes.Clear();
            listener.Prefixes.Add($"http://{host}:{port}/callback/");
            try
            {
                listener.Start();
                redirectUri = $"http://{host}:{port}/callback";
                break;
            }
            catch (HttpListenerException)
            {
                // try the other spelling
            }
        }
        if (redirectUri == null)
        {
            write($"Could not listen on port {port} for the browser's reply.");
            return null;
        }

        string url = AuthorizeUrl(AuthorizeEndpointFor(tokenEndpoint), clientId, redirectUri,
                                  state, ChallengeFor(verifier));
        write("Opening the ENCY sign-in page in your browser… If it does not open, paste this address: " + url);
        await openBrowser(url);

        var context = await WaitForCallback(listener, write);
        if (context == null)
        {
            return null;
        }

        var cb = Read(context.Request.Url);
        await Respond(context, DonePage);

        if (cb.Error != null)
        {
            write("The sign-in page said: " + cb.Error);
            return null;
        }
        if (cb.Code == null)
        {
            write("The sign-in page came back without a code.");
            return null;
        }
        if (cb.State != state)
        {
            write("The reply did not match this request — sign in again.");
            return null;
        }

        var resp = await http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["code"] = cb.Code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
            }));
        string json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            write("Could not exchange the code for a token: " + Trim(json));
            return null;
        }
        using var doc = JsonDocument.Parse(json);
        string? refresh = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        if (string.IsNullOrEmpty(refresh))
        {
            write("The server returned no refresh token — cannot stay signed in.");
            return null;
        }
        return refresh;
    }

    /// <summary>The browser may never come back: a refused redirect_uri looks exactly like silence.</summary>
    private static async Task<HttpListenerContext?> WaitForCallback(HttpListener listener, Action<string> write)
    {
        var incoming = listener.GetContextAsync();
        if (await Task.WhenAny(incoming, Task.Delay(Wait)) != incoming)
        {
            write("Gave up waiting for the browser. If the page refused the address as an invalid "
                  + "redirect URI, the Keycloak client does not allow http://127.0.0.1 yet.");
            return null;
        }
        return await incoming;
    }

    /// <summary>Extracted so the query parsing can be tested without a socket.</summary>
    public static Callback Read(Uri? requestUrl)
    {
        if (requestUrl == null)
        {
            return new Callback(null, null, "the browser sent no address");
        }
        var q = System.Web.HttpUtility.ParseQueryString(requestUrl.Query);
        return new Callback(q["code"], q["state"], q["error"]);
    }

    private static async Task Respond(HttpListenerContext context, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
}
