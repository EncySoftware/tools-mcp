using System.Text.Json;

namespace ToolsMcp;

/// <summary>
/// `tools-mcp doctor`: one line per check - the sign-in, that tools.encycam.com accepts the token and whose it is,
/// that the server is registered in the editors. The first thing people will say is "it doesn't work for me";
/// this output is the answer to it.
/// </summary>
public static class Doctor
{
    public static async Task<int> Run(IToolsApi api, TokenProvider tokens, IProcessRunner proc,
        string cursorConfigPath, string codexHome, Action<string> write)
    {
        bool bad = false;
        void Ok(string s) => write("✓ " + s);
        void Fail(string s) { bad = true; write("✗ " + s); }
        void Info(string s) => write("— " + s);

        Ok($".NET {Environment.Version}, {Brand.Cli} {VersionCheck.Current}");

        string? token = null;
        try { token = await tokens.GetAccessToken(); }
        catch (Exception e) { Fail("the ENCY sign-in server did not answer: " + e.Message); }
        if (token == null) Fail($"not signed in, or the sign-in has expired - run `{Brand.Cli} login`");
        else Ok("signed in (" + TokenProvider.AuthFilePath + ")");

        if (token != null)
        {
            try
            {
                var me = await api.GetMe(token);
                Ok($"tools.encycam.com answers: you are {me.Username ?? me.Sub}" + (me.Email != null ? $" ({me.Email})" : ""));
            }
            catch (ToolsHttpException e) when (e.Status == 401)
            {
                Fail($"tools.encycam.com refused the token - run `{Brand.Cli} login` again");
            }
            catch (Exception e) { Fail("tools.encycam.com did not answer: " + e.Message); }
        }

        // ---- Cursor: the server in ~/.cursor/mcp.json
        bool cursor = false;
        try
        {
            if (File.Exists(cursorConfigPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cursorConfigPath));
                cursor = doc.RootElement.TryGetProperty("mcpServers", out var s)
                         && s.ValueKind == JsonValueKind.Object && s.TryGetProperty(Brand.McpServerName, out _);
            }
        }
        catch (JsonException) { /* a broken file counts as a missing entry */ }
        if (cursor) Ok($"Cursor: server {Brand.McpServerName} is configured ({cursorConfigPath})");
        else Fail($"Cursor: server {Brand.McpServerName} is missing from {cursorConfigPath} - run `{Brand.Cli} setup`");

        // ---- Claude Code: through its CLI; no CLI is information, not a failure
        var version = await proc.Run("claude", "--version");
        if (!version.Ok) Info("Claude Code is not installed (or not on PATH) - this is not an error");
        else
        {
            var list = await proc.Run("claude", "mcp list");
            if (list.Ok && list.StdOut.Contains(Brand.McpServerName)) Ok("Claude Code: the server is registered");
            else Fail($"Claude Code: the server is not registered for the user - `claude mcp add --scope user {Brand.McpServerName} -- {Brand.Cli}`");
        }

        // ---- Codex: ~/.codex/config.toml; no Codex on the machine is information, not a failure
        if (!Directory.Exists(codexHome) && !(await proc.Run("codex", "--version")).Ok)
            Info("Codex is not installed - this is not an error");
        else
        {
            string codexConfig = CodexConfig.ConfigPath(codexHome);
            bool codex = File.Exists(codexConfig)
                         && CodexConfig.HasServer(File.ReadAllText(codexConfig), Brand.McpServerName);
            if (codex) Ok($"Codex: server {Brand.McpServerName} is configured ({codexConfig})");
            else Fail($"Codex: server {Brand.McpServerName} is missing from {codexConfig} - run `{Brand.Cli} setup` "
                      + $"or `codex mcp add {Brand.McpServerName} -- {Brand.Cli}`");
        }

        write(bad ? "There are problems - see the lines with ✗." : "Everything is fine.");
        return bad ? 1 : 0;
    }
}
