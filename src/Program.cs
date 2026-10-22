using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ToolsMcp;

// The commands a person runs in a terminal print ✓ and ✗: in the console's own code page (866 on a Russian Windows)
// they came out as "?" (2026-10-22). The stdio server writes its protocol in UTF-8 itself and is not touched here.
if (args.Length > 0)
    Console.OutputEncoding = System.Text.Encoding.UTF8;

// `tools-mcp login`: a one-time sign-in to tools.encycam.com through the browser (MCP is not involved).
if (args.Length > 0 && args[0].Equals("login", StringComparison.OrdinalIgnoreCase))
    return await new TokenProvider().LoginBrowser();

// `tools-mcp setup [--no-login]`: register the server in the editors and sign in.
if (args.Length > 0 && args[0].Equals("setup", StringComparison.OrdinalIgnoreCase))
{
    if (await VersionCheck.Hint() is { } hint) Console.WriteLine(hint);
    var tokenProvider = new TokenProvider();
    return await SetupCommand.Run(SetupCommand.DefaultCursorConfigPath, CodexConfig.DefaultHome, new ProcessRunner(),
        () => File.Exists(TokenProvider.AuthFilePath), tokenProvider.LoginBrowser,
        args.Contains("--no-login", StringComparer.OrdinalIgnoreCase), Console.WriteLine);
}

// `tools-mcp doctor`: what is wrong - the sign-in, the site, the editors' registration. The answer to "it doesn't work".
if (args.Length > 0 && args[0].Equals("doctor", StringComparison.OrdinalIgnoreCase))
{
    if (await VersionCheck.Hint() is { } hint) Console.WriteLine(hint);
    return await Doctor.Run(new ToolsApi(), new TokenProvider(), new ProcessRunner(),
        SetupCommand.DefaultCursorConfigPath, CodexConfig.DefaultHome, Console.WriteLine);
}

// `tools-mcp serve`: the same tools over HTTP for an agent that cannot start a process (Hermes), each call as the
// person whose access token it carries.
if (args.Length > 0 && args[0].Equals("serve", StringComparison.OrdinalIgnoreCase))
    return await ServeCommand.Run(args[1..]);

var builder = Host.CreateApplicationBuilder(args);

// stdout is the MCP protocol; logs go to stderr only.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// The newer-version hint goes to stderr without waiting for nuget.org: the server starts at once.
_ = VersionCheck.Hint().ContinueWith(t => { if (t.Result is { } h) Console.Error.WriteLine(h); },
    TaskContinuationOptions.OnlyOnRanToCompletion);

builder.Services.AddSingleton<IToolsApi, ToolsApi>();
builder.Services.AddSingleton<TokenProvider>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<CatalogueTools>()
    .WithTools<SearchTools>()
    .WithTools<ComponentTools>()
    .WithTools<BuildTools>()
    .WithTools<LibraryTools>();

await builder.Build().RunAsync();
return 0;
