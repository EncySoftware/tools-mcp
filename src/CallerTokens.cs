namespace ToolsMcp;

/// <summary>
/// The hosted server's TokenProvider: the token of the person whose request this is (<see cref="Caller.Current"/>),
/// never a stored sign-in - the server has none of its own. The tool classes take a TokenProvider as they do at home,
/// so they run unchanged here; outside a request there is no token, and a tool says so.
/// </summary>
public sealed class CallerTokens : TokenProvider
{
    public override Task<string?> GetAccessToken() => Task.FromResult(Caller.Current?.AccessToken);

    /** The person signed in through their agent, and only the agent can sign them in again. */
    public override string SignInFix => "the person must sign in to tools.encycam.com again in the agent";
}
