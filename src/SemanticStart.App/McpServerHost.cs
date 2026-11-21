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

        // Everything to stderr: stdout is the MCP transport, and a log line written there would be
        // parsed as a protocol message.
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        // And to the app's own log, so a server started by an MCP client - which captures stderr
        // somewhere the user cannot reach, or discards it - still leaves a record in the file the
        // README tells people to look at. Information and above: the hosting stack's own Trace
        // output would bury the app's lines without saying anything about this app.
        builder.Logging.AddProvider(FileLoggerProvider.Create(LogLevel.Information));

        builder.Services.AddSingleton<SemanticIndexRuntime>();
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync(cancellationToken);
    }
}
