using ToolsMcp;
using Xunit;

/**
 * `tools-mcp doctor`: the first thing people will say is "it doesn't work for me". It checks the sign-in, that
 * tools.encycam.com accepts the token and whose it is, and that the server is registered in the editors.
 */
public class DoctorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tools-doc-" + Guid.NewGuid().ToString("N")[..8]);
    private string CursorConfig => Path.Combine(_dir, "mcp.json");
    private string CodexHome => Path.Combine(_dir, "codex");

    public DoctorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private async Task<(int Code, string Out)> Run(FakeToolsApi api, string? token = "tok",
        FakeProcessRunner? proc = null, bool cursorConfigured = true, string? codexConfig = null)
    {
        if (cursorConfigured)
            File.WriteAllText(CursorConfig, """{"mcpServers":{"ency-tools":{"command":"tools-mcp"}}}""");
        if (codexConfig != null)
        {
            Directory.CreateDirectory(CodexHome);
            File.WriteAllText(Path.Combine(CodexHome, "config.toml"), codexConfig);
        }
        var lines = new List<string>();
        int code = await Doctor.Run(api, new FakeTokens(token),
            proc ?? new FakeProcessRunner().On("claude --version", stdout: "1.0")
                .On("claude mcp list", stdout: "ency-tools: tools-mcp - ✔ Connected"),
            CursorConfig, CodexHome, lines.Add);
        return (code, string.Join("\n", lines));
    }

    [Fact]
    public async Task DoctorNamesTheAccount()
    {
        var (code, output) = await Run(new FakeToolsApi());

        Assert.Equal(0, code);
        Assert.Contains("alice", output);
        Assert.DoesNotContain("✗", output);
    }

    [Fact]
    public async Task DoctorFailsWhenNotSignedIn()
    {
        var (code, output) = await Run(new FakeToolsApi(), token: null);

        Assert.Equal(1, code);
        Assert.Contains("tools-mcp login", output);
    }

    [Fact]
    public async Task ARejectedTokenIsNamed()
    {
        var api = new FakeToolsApi();
        api.Fail[nameof(IToolsApi.GetMe)] = new ToolsHttpException(401, "");

        var (code, output) = await Run(api);

        Assert.Equal(1, code);
        Assert.Contains("✗", output);
        Assert.Contains("login", output);
    }

    [Fact]
    public async Task CodexWithTheServerIsATick()
    {
        var (code, output) = await Run(new FakeToolsApi(), codexConfig: "[mcp_servers.ency-tools]\ncommand = \"tools-mcp\"\n");
        Assert.Equal(0, code);
        Assert.Contains("✓ Codex", output);
    }

    [Fact]
    public async Task CodexWithoutTheServerPointsAtSetup()
    {
        var (code, output) = await Run(new FakeToolsApi(), codexConfig: "model = \"gpt-5\"\n");
        Assert.Equal(1, code);
        Assert.Contains("✗ Codex", output);
        Assert.Contains("tools-mcp setup", output);
    }

    /** Codex may not be installed: that is information, not a failure, just as with Claude Code. */
    [Fact]
    public async Task CodexAbsentIsInformationNotFailure()
    {
        var (code, output) = await Run(new FakeToolsApi());
        Assert.Equal(0, code);
        Assert.Contains("— Codex", output);
    }

    [Fact]
    public async Task MissingCursorEntryPointsAtSetup()
    {
        var (code, output) = await Run(new FakeToolsApi(), cursorConfigured: false);
        Assert.Equal(1, code);
        Assert.Contains("setup", output);
    }

    /** Claude Code may not be installed: that is information, not a failure. */
    [Fact]
    public async Task ClaudeCodeAbsentIsInformationNotFailure()
    {
        var (code, output) = await Run(new FakeToolsApi(), proc: new FakeProcessRunner());
        Assert.Equal(0, code);
        Assert.Contains("Claude Code", output);
    }
}
