using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SemanticStart.Core.Query;

namespace SemanticStart.App;

/// <summary>Runs the app executable as a local read-only MCP stdio server.</summary>
internal static class McpServerHost
{
    public static async Task RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var builder = Host.CreateApplicationBuilder(args.Where(
            argument => !argument.Equals("--mcp", StringComparison.OrdinalIgnoreCase)).ToArray());
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddSingleton<SemanticIndexRuntime>();
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync(cancellationToken);
    }
}
