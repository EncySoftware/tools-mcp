using ToolsMcp;
using Xunit;

public class BrowserLoginTests
{
    /** PKCE S256, and offline_access - without it the refresh token dies with the browser session. */
    [Fact]
    public void TheAuthorizeUrlCarriesPkceAndOfflineAccess()
    {
        string url = BrowserLogin.AuthorizeUrl(
            "https://webservices.encycam.com/keycloak/realms/licsys/protocol/openid-connect/auth",
            Brand.KeycloakClient, "http://localhost:5000/callback", "state",
            BrowserLogin.ChallengeFor(BrowserLogin.NewVerifier()));

        Assert.Contains("client_id=tools-encycam", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("offline_access", url);
    }
}
