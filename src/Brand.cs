namespace ToolsMcp;

/// <summary>The only file with VALUES. The rest of the code reads them from here.</summary>
public static class Brand
{
    public const string Product = "ENCY tools";
    public const string Cli = "tools-mcp";

    /// <summary>"tools" alone is ambiguous in an agent's list of servers.</summary>
    public const string McpServerName = "ency-tools";

    public const string Api = "https://tools.encycam.com/api";
    public const string Site = "https://tools.encycam.com";

    /// <summary>
    /// The site's own client, checked 2026-10-22. It is public, PKCE S256, allows a loopback redirect
    /// (deploy/README-go-live.md in tools-encycam-com), and is the client the EncyToolsCatalog
    /// extension signs in through. The API already accepts its tokens. Borrowing another app's
    /// client, as dmc-mcp does, would make the API accept that app's tokens too, which commit
    /// 02388d5 was made to stop. Direct Access Grants are off on it, so there is no password
    /// sign-in.
    /// </summary>
    public const string KeycloakUrl = "https://webservices.encycam.com/keycloak/";
    public const string KeycloakRealm = "licsys";
    public const string KeycloakClient = "tools-encycam";

    /// <summary>The %APPDATA% folder for the refresh token. It is our own and not shared with the extension.</summary>
    public const string AuthFolder = "tools-mcp";

    /// <summary>Where downloads and builds go unless the person names a folder: under their Downloads.</summary>
    public const string DownloadsFolder = "ENCY tools";
}
