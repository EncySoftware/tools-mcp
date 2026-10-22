using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ToolsMcp;

/// <summary>
/// The hosted server's door for /mcp. One credential lets a request in: `Authorization: Bearer` with a person's access
/// token from the trusted realm (<see cref="UserTokens"/>), and the call then runs as that person
/// (<see cref="Caller.Enter"/>). There is no key, unlike dmc-mcp's door: behind a key there is one account, and everyone's
/// tools would land in its libraries. The resource metadata paths are public. Tokens never reach a log: a refusal is
/// logged by its reason.
/// </summary>
public sealed class Door(UserTokens tokens, string metadataUrl, ILogger log)
{
    public const string MetadataPath = "/.well-known/oauth-protected-resource";
    public const string McpMetadataPath = MetadataPath + "/mcp";

    public async Task Handle(HttpContext ctx, Func<Task> next)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (path is MetadataPath or McpMetadataPath)
        {
            await next(); // how a client learns where to sign in: no credentials needed
            return;
        }
        var bearer = Bearer(ctx.Request.Headers.Authorization.ToString());
        if (!UnderMcp(path) || bearer == null)
        {
            await Refuse(ctx, null, null);
            return;
        }

        var check = await tokens.Check(bearer, ctx.RequestAborted);
        if (check.Unavailable)
        {
            log.LogWarning("Tokens cannot be checked: the keys of {Issuer} could not be fetched ({Error}); answering 503",
                tokens.Settings.Issuer, tokens.KeysError);
            await Unavailable(ctx);
            return;
        }
        if (check.Caller == null)
        {
            log.LogInformation("Refused a token: {Reason}", check.Refusal);
            await Refuse(ctx, "invalid_token", check.Refusal);
            return;
        }
        using (Caller.Enter(check.Caller)) await next();
    }

    internal static bool UnderMcp(string path) => path == "/mcp" || path.StartsWith("/mcp/", StringComparison.Ordinal);

    /** The credential of an `Authorization: Bearer …` header; null for none or another scheme. */
    internal static string? Bearer(string? authorization) =>
        authorization != null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : null;

    /**
     * 401. The challenge names the resource metadata (MCP authorization, RFC 9728) and, for a bad token,
     * error="invalid_token" with a reason (RFC 6750) - fixed ASCII text, never the token.
     */
    private Task Refuse(HttpContext ctx, string? error, string? description)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        var parts = new List<string>();
        if (error != null) parts.Add($"error=\"{error}\"");
        if (description != null) parts.Add($"error_description=\"{description}\"");
        parts.Add($"resource_metadata=\"{metadataUrl}\"");
        ctx.Response.Headers.WWWAuthenticate = "Bearer " + string.Join(", ", parts);
        return ctx.Response.WriteAsJsonAsync(new Dictionary<string, string>
        {
            ["error"] = error ?? "unauthorized",
            ["error_description"] = description
                ?? "send Authorization: Bearer with the access token of the person the call is for",
        });
    }

    /** No keys to check a token with (Keycloak unreachable on first use): not the token's fault, so not a 401. */
    private static Task Unavailable(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        ctx.Response.Headers.RetryAfter = "60";
        return ctx.Response.WriteAsJsonAsync(new Dictionary<string, string>
        {
            ["error"] = "temporarily_unavailable",
            ["error_description"] = "the sign-in server's keys could not be fetched, so tokens cannot be checked; try again in a minute",
        });
    }
}
