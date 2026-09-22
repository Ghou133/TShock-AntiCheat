using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AntiCheat.DevMcp;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        bool stdioRequested = args.FirstOrDefault() == "stdio";
        try
        {
            var arguments = ParseArguments(args);
            if (arguments.Mode == "stdio")
            {
                var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
                {
                    Args = [],
                    ContentRootPath = arguments.ProjectRoot
                });
                builder.Logging.ClearProviders();
                builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
                builder.Services.AddSingleton(_ => new DevOperations(arguments.ProjectRoot));
                builder.Services.AddMcpServer().WithStdioServerTransport()
                    .WithToolsFromAssembly(serializerOptions: Cli.JsonOptions);
                // The hosting/stdio SDK owns protocol framing and async service disposal.
                await builder.Build().RunAsync();
                return 0;
            }

            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, e) =>
            {
                e.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            try
            {
                await using var operations = new DevOperations(arguments.ProjectRoot);
                return arguments.Mode == "run"
                    ? await Cli.RunAsync(operations, cancellation.Token)
                    : await Cli.InvokeAsync(operations, arguments.Tool!, cancellation.Token);
            }
            finally { Console.CancelKeyPress -= cancelHandler; }
        }
        catch (OperationCanceledException)
        {
            if (!stdioRequested) await Cli.WriteAsync(Cli.Error("canceled", "The CLI request was canceled."));
            return 130;
        }
        catch (CliInputException error)
        {
            if (stdioRequested) await Console.Error.WriteLineAsync(error.Message);
            else await Cli.WriteAsync(Cli.Error("invalid_request", error.Message));
            return 2;
        }
        catch (Exception error)
        {
            // Do not return internal paths or stack traces as tool/CLI payloads.
            await Console.Error.WriteLineAsync($"terraria-dev failed ({error.GetType().Name}).");
            if (!stdioRequested) await Cli.WriteAsync(Cli.Error("internal_error", "The operation failed; inspect owned run evidence."));
            return 1;
        }
    }

    private static FrontendArguments ParseArguments(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("stdio" or "cli" or "run"))
            throw new CliInputException("Use stdio --project-root ABS, cli --project-root ABS TOOL, or run --project-root ABS.");

        string? projectRoot = null, tool = null;
        for (int index = 1; index < args.Length; index++)
        {
            if (args[index] == "--project-root")
            {
                if (projectRoot is not null || ++index >= args.Length)
                    throw new CliInputException("Supply --project-root exactly once with an absolute directory.");
                projectRoot = args[index];
            }
            else if (args[0] == "cli" && tool is null && !args[index].StartsWith('-')) tool = args[index];
            else throw new CliInputException("Unknown or repeated command argument.");
        }
        if (projectRoot is null || !Path.IsPathFullyQualified(projectRoot) || !Directory.Exists(projectRoot))
            throw new CliInputException("--project-root must name an existing absolute project directory.");
        if (args[0] == "cli" && tool is null) throw new CliInputException("The cli mode requires a tool name.");
        return new(args[0], Path.GetFullPath(projectRoot), tool);
    }

    private sealed record FrontendArguments(string Mode, string ProjectRoot, string? Tool);
}
