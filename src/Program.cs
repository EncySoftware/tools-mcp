using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ToolsMcp;

// `tools-mcp login`: a one-time sign-in to tools.encycam.com through the browser (MCP is not involved).
if (args.Length > 0 && args[0].Equals("login", StringComparison.OrdinalIgnoreCase))
    return await new TokenProvider().LoginBrowser();

var builder = Host.CreateApplicationBuilder(args);

// stdout is the MCP protocol; logs go to stderr only.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<IToolsApi, ToolsApi>();
builder.Services.AddSingleton<TokenProvider>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<CatalogueTools>()
    .WithTools<SearchTools>()
    .WithTools<ComponentTools>()
    .WithTools<BuildTools>();

await builder.Build().RunAsync();
return 0;
