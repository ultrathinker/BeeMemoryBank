using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Hosting;

namespace BeeMemoryBank.Node;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("=== BeeMemoryBank Node Orchestrator ===");

        bool isAutoMode = false;
        string? dataDirectory = null;
        string? configPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--help" || arg == "-h")
            {
                ShowUsage();
                return 0;
            }
            else if (arg == "--auto" || arg == "-a")
            {
                isAutoMode = true;
            }
            else if (arg == "--data" || arg == "-d")
            {
                if (i + 1 < args.Length)
                {
                    dataDirectory = args[++i];
                }
                else
                {
                    Console.Error.WriteLine("[Error] Missing value for --data / -d argument.");
                    ShowUsage();
                    return 1;
                }
            }
            else if (!arg.StartsWith("-"))
            {
                if (configPath != null)
                {
                    Console.Error.WriteLine($"[Error] Multiple configuration files specified: '{configPath}' and '{arg}'.");
                    ShowUsage();
                    return 1;
                }
                configPath = arg;
            }
            else
            {
                Console.Error.WriteLine($"[Error] Unknown option '{arg}'.");
                ShowUsage();
                return 1;
            }
        }

        // Determine if auto-discovery is triggered
        if (!isAutoMode)
        {
            if (configPath == null)
            {
                if (File.Exists("node.config.json"))
                {
                    configPath = "node.config.json";
                }
                else
                {
                    isAutoMode = true;
                }
            }
        }

        string resolvedDataDirectory;
        List<ChildProcessConfig> childConfigs;

        if (isAutoMode)
        {
            Console.WriteLine("[Node] Running in Auto-Discovery mode.");

            if (string.IsNullOrWhiteSpace(dataDirectory))
            {
                dataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
            }
            resolvedDataDirectory = Path.GetFullPath(dataDirectory);

            try
            {
                Directory.CreateDirectory(resolvedDataDirectory);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Error] Failed to create data directory '{resolvedDataDirectory}': {ex.Message}");
                return 1;
            }

            try
            {
                childConfigs = AutoDiscovery.Discover(AppContext.BaseDirectory, resolvedDataDirectory);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Error] Auto-discovery failed: {ex.Message}");
                return 1;
            }
        }
        else
        {
            if (configPath == null)
            {
                configPath = "node.config.json";
            }

            if (!File.Exists(configPath))
            {
                Console.Error.WriteLine($"[Error] Configuration file '{configPath}' not found.");
                ShowUsage();
                return 1;
            }

            NodeConfig config;
            try
            {
                var content = await File.ReadAllTextAsync(configPath);
                config = JsonSerializer.Deserialize<NodeConfig>(content, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                         ?? throw new InvalidOperationException("Failed to deserialize configuration.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Error] Failed to load configuration: {ex.Message}");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(config.DataDirectory))
            {
                Console.Error.WriteLine("[Error] 'dataDirectory' must be specified in the configuration.");
                return 1;
            }

            if (config.Children == null || config.Children.Count == 0)
            {
                Console.Error.WriteLine("[Error] No child processes configured under 'children'.");
                return 1;
            }

            resolvedDataDirectory = config.DataDirectory;
            childConfigs = config.Children.Select(c => new ChildProcessConfig(
                c.ApplicationName,
                c.ExecutablePath,
                c.WorkingDirectory,
                c.ReadyFilePath,
                c.Arguments,
                c.EnvironmentVariables
            )).ToList();
        }

        using var orchestrator = new NodeOrchestrator(resolvedDataDirectory, childConfigs);

        WebApplication? app = null;
        var tcs = new TaskCompletionSource<int>();

        orchestrator.OnAllReady += () =>
        {
            Console.WriteLine("[Node] Orchestrator successfully started all child processes and verified readiness.");
        };

        orchestrator.OnCriticalFailure += (reason) =>
        {
            Console.Error.WriteLine($"[Node] CRITICAL FAILURE: {reason}");
            if (app != null)
            {
                try
                {
                    Console.WriteLine("[Node] Stopping front app due to critical failure...");
                    app.StopAsync().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Node] Error stopping front: {ex.Message}");
                }
            }
            tcs.TrySetResult(2);
        };

        Console.CancelKeyPress += async (sender, e) =>
        {
            Console.WriteLine("[Node] Cancel key pressed. Stopping orchestrator...");
            e.Cancel = true; // Prevent process from immediately terminating
            if (app != null)
            {
                try
                {
                    Console.WriteLine("[Node] Stopping front app first...");
                    await app.StopAsync();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Node] Error stopping front: {ex.Message}");
                }
            }
            await orchestrator.StopAsync();
            tcs.TrySetResult(0);
        };

        try
        {
            Console.WriteLine($"[Node] Launching children with lock on data dir: '{resolvedDataDirectory}'...");
            await orchestrator.StartAsync(CancellationToken.None);

            Console.WriteLine("[Node] Orchestrator started. Building and starting front...");
            // Default to the plan's designated port (127.0.0.1:5310, distinct from
            // standalone/Docker's 5300/5301) instead of ASP.NET Core's own default
            // (5000), which commonly collides with other local dev tools. If it's
            // taken, fall back to an OS-assigned free port - the real bound port
            // always ends up in .runtime.json/node.status.json regardless.
            const int preferredFrontPort = 5310;

            // Opt-in HTTPS front on :5311. Gated behind BMB_HTTPS_ENABLED=1 (absent/false = OFF),
            // matching "по кнопке" in the superplan — a later task wires an actual UI toggle. When
            // disabled (the default) the front is byte-for-byte identical to before: only the
            // plain-HTTP listener runs.
            var httpsEnabled = Environment.GetEnvironmentVariable("BMB_HTTPS_ENABLED") == "1";
            if (httpsEnabled)
            {
                Console.WriteLine("[Node] BMB_HTTPS_ENABLED=1: additive HTTPS listener will be started on :5311.");
            }
            try
            {
                app = BuildFront(
                    new[] { "--urls", $"http://127.0.0.1:{preferredFrontPort}" },
                    orchestrator.ReadyChildren,
                    httpsEnabled,
                    resolvedDataDirectory);
                await app.StartAsync();
            }
            catch (IOException)
            {
                Console.WriteLine($"[Node] Port {preferredFrontPort} is unavailable, falling back to an OS-assigned port...");
                if (app != null)
                {
                    try { await app.DisposeAsync(); } catch { }
                }
                app = BuildFront(
                    new[] { "--urls", "http://127.0.0.1:0" },
                    orchestrator.ReadyChildren,
                    httpsEnabled,
                    resolvedDataDirectory);
                await app.StartAsync();
            }

            // Best-effort inbound firewall rule for the HTTPS port. This genuinely requires
            // administrator privileges (inbound rules have no CurrentUser escape hatch); a caught,
            // logged failure here leaves the HTTPS listener itself still running, just without an
            // automatic rule — the documented acceptable degraded outcome.
            if (httpsEnabled && OperatingSystem.IsWindows())
            {
                try
                {
                    var firewall = new FirewallService();
                    var ok = firewall.EnsureInboundTcpRule(NodeFront.HttpsPort, "BeeMemoryBank Node");
                    Console.WriteLine(ok
                        ? $"[Node] Inbound firewall rule ensured for TCP {NodeFront.HttpsPort}."
                        : $"[Node] WARNING: could not add inbound firewall rule for TCP {NodeFront.HttpsPort} " +
                          "(administrator elevation is required for inbound firewall rules). The HTTPS listener " +
                          "is running, but may be unreachable from other devices until the rule is added manually.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Node] WARNING: firewall rule setup failed: {ex.Message}");
                }
            }

            var frontUrl = app.Urls.FirstOrDefault();
            if (!string.IsNullOrEmpty(frontUrl))
            {
                Console.WriteLine($"[Node] Front is listening at: {frontUrl}");
                if (httpsEnabled)
                {
                    Console.WriteLine($"[Node] Front HTTPS listener on: https://<this-host>:{NodeFront.HttpsPort}");
                }
                orchestrator.UpdateFrontUrl(frontUrl);
            }

            Console.WriteLine("[Node] Node is running. Press Ctrl+C to shut down.");

            // Wait for shutdown or failure
            int exitCode = await tcs.Task;
            return exitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Node] Orchestrator failed to start: {ex.Message}");
            return 3;
        }
        finally
        {
            if (app != null)
            {
                try
                {
                    await app.StopAsync();
                }
                catch { }
                await app.DisposeAsync();
            }
        }
    }

    public static WebApplication BuildFront(
        string[] webArgs,
        IReadOnlyDictionary<string, ReadyFileInfo> readyChildren,
        bool enableHttps = false,
        string? dataPath = null)
    {
        var builder = WebApplication.CreateBuilder(webArgs);
        var front = NodeFrontBuilder.Build(builder, readyChildren, enableHttps, dataPath);
        var app = builder.Build();
        front.MapEndpoints(app);
        return app;
    }

    private static void ShowUsage()
    {
        Console.WriteLine("\nUsage:");
        Console.WriteLine("  BeeMemoryBank.Node.exe [path-to-node.config.json]");
        Console.WriteLine("  BeeMemoryBank.Node.exe --auto [-d/--data <data-directory-path>]");
        Console.WriteLine("\nOptions:");
        Console.WriteLine("  -a, --auto              Run in auto-discovery mode. Looks for sibling 'api' and 'web' directories.");
        Console.WriteLine("  -d, --data <path>       Specify custom directory for data, status, and ready files (used with --auto).");
        Console.WriteLine("  -h, --help              Show this help message.");
        Console.WriteLine("\nExample node.config.json:");
        var example = new NodeConfig(
            DataDirectory: @"C:\Users\evgeny\AppData\Local\Temp\bmb-node-data",
            Children: new List<ChildConfig>
            {
                new ChildConfig(
                    ApplicationName: "BeeMemoryBank.Api",
                    ExecutablePath: "dotnet",
                    WorkingDirectory: @"C:\VS_PROJECTS\_NonWork\BeeMemoryBank-wt-flash-c",
                    ReadyFilePath: @"C:\Users\evgeny\AppData\Local\Temp\bmb-node-data\api.ready",
                    Arguments: "run --project server/BeeMemoryBank.Api",
                    EnvironmentVariables: new Dictionary<string, string>
                    {
                        { "ASPNETCORE_URLS", "http://127.0.0.1:0" },
                        { "BMB_READY_FILE", @"C:\Users\evgeny\AppData\Local\Temp\bmb-node-data\api.ready" }
                    }
                )
            }
        );
        Console.WriteLine(JsonSerializer.Serialize(example, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public record NodeConfig(
    string DataDirectory,
    List<ChildConfig> Children
);

public record ChildConfig(
    string ApplicationName,
    string ExecutablePath,
    string WorkingDirectory,
    string ReadyFilePath,
    string? Arguments = null,
    Dictionary<string, string>? EnvironmentVariables = null
);

public static class AutoDiscovery
{
    public static List<ChildProcessConfig> Discover(string baseDirectory, string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentNullException(nameof(baseDirectory));
        }
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentNullException(nameof(dataDirectory));
        }

        var absBaseDir = Path.GetFullPath(baseDirectory);
        var absDataDir = Path.GetFullPath(dataDirectory);

        var apiReadyFilePath = Path.Combine(absDataDir, "api.ready");
        var webReadyFilePath = Path.Combine(absDataDir, "web.ready");

        var apiInfo = ResolveApplicationStartInfo(absBaseDir, "api", "BeeMemoryBank.Api");
        var webInfo = ResolveApplicationStartInfo(absBaseDir, "web", "BeeMemoryBank.Web");

        // Base environment variables
        var apiEnv = new Dictionary<string, string>
        {
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
            ["BMB_READY_FILE"] = apiReadyFilePath,
            ["BMB_STDIN_LIFELINE"] = "1",
            ["BMB_BEHIND_LOOPBACK_PROXY"] = "1",
            ["BMB_DATA_PATH"] = absDataDir
        };

        // NOTE: BMB_API_URL is deliberately NOT set here. Api binds to a random port
        // (ASPNETCORE_URLS=http://127.0.0.1:0) that isn't known until Api's own ready-file
        // is written - but NodeOrchestrator starts every child concurrently, with no
        // "wait for Api, then start Web with its resolved port" staging. Web already falls
        // back to http://localhost:5300 when BMB_API_URL is unset (see its Program.cs), so
        // it still starts and becomes ready correctly; its own Api-proxy calls just won't
        // reach the real Api process in auto-discovered mode until NodeOrchestrator gains
        // genuine staged/dependency-ordered startup - tracked as follow-up work, not
        // attempted here to avoid a fragile workaround.
        var webEnv = new Dictionary<string, string>
        {
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
            ["BMB_READY_FILE"] = webReadyFilePath,
            ["BMB_STDIN_LIFELINE"] = "1",
            ["BMB_BEHIND_LOOPBACK_PROXY"] = "1",
            ["BMB_DATA_PATH"] = absDataDir
        };

        var apiConfig = new ChildProcessConfig(
            ApplicationName: "BeeMemoryBank.Api",
            ExecutablePath: apiInfo.ExecutablePath,
            WorkingDirectory: apiInfo.WorkingDirectory,
            ReadyFilePath: apiReadyFilePath,
            Arguments: apiInfo.Arguments,
            EnvironmentVariables: apiEnv
        );

        var webConfig = new ChildProcessConfig(
            ApplicationName: "BeeMemoryBank.Web",
            ExecutablePath: webInfo.ExecutablePath,
            WorkingDirectory: webInfo.WorkingDirectory,
            ReadyFilePath: webReadyFilePath,
            Arguments: webInfo.Arguments,
            EnvironmentVariables: webEnv
        );

        return new List<ChildProcessConfig> { apiConfig, webConfig };
    }

    private static ResolvedApp ResolveApplicationStartInfo(string baseDir, string folderName, string appName)
    {
        var folderPath = Path.GetFullPath(Path.Combine(baseDir, "..", folderName));
        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"Sibling directory '{folderName}' not found relative to '{baseDir}' (expected at '{folderPath}').");
        }

        var exeNameWindows = $"{appName}.exe";
        var exeNameUnix = appName;

        var exePathWindows = Path.Combine(folderPath, exeNameWindows);
        var exePathUnix = Path.Combine(folderPath, exeNameUnix);

        if (File.Exists(exePathWindows))
        {
            return new ResolvedApp(exePathWindows, folderPath, null);
        }
        if (File.Exists(exePathUnix))
        {
            return new ResolvedApp(exePathUnix, folderPath, null);
        }

        var dllPath = Path.Combine(folderPath, $"{appName}.dll");
        if (File.Exists(dllPath))
        {
            return new ResolvedApp("dotnet", folderPath, $"\"{dllPath}\"");
        }

        throw new FileNotFoundException($"Could not find executable or DLL for '{appName}' in directory '{folderPath}'.");
    }

    private record ResolvedApp(string ExecutablePath, string WorkingDirectory, string? Arguments);
}
