using System.Text.Json;

namespace BeeMemoryBank.Node;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("=== BeeMemoryBank Node Orchestrator ===");

        string configPath = "node.config.json";
        if (args.Length > 0)
        {
            if (args[0] == "--help" || args[0] == "-h")
            {
                ShowUsage();
                return 0;
            }
            configPath = args[0];
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

        var childConfigs = config.Children.Select(c => new ChildProcessConfig(
            c.ApplicationName,
            c.ExecutablePath,
            c.WorkingDirectory,
            c.ReadyFilePath,
            c.Arguments,
            c.EnvironmentVariables
        )).ToList();

        using var orchestrator = new NodeOrchestrator(config.DataDirectory, childConfigs);

        var tcs = new TaskCompletionSource<int>();

        orchestrator.OnAllReady += () =>
        {
            Console.WriteLine("[Node] Orchestrator successfully started all child processes and verified readiness.");
        };

        orchestrator.OnCriticalFailure += (reason) =>
        {
            Console.Error.WriteLine($"[Node] CRITICAL FAILURE: {reason}");
            tcs.TrySetResult(2);
        };

        Console.CancelKeyPress += async (sender, e) =>
        {
            Console.WriteLine("[Node] Cancel key pressed. Stopping orchestrator...");
            e.Cancel = true; // Prevent process from immediately terminating
            await orchestrator.StopAsync();
            tcs.TrySetResult(0);
        };

        try
        {
            Console.WriteLine($"[Node] Launching children with lock on data dir: '{config.DataDirectory}'...");
            await orchestrator.StartAsync(CancellationToken.None);
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
    }

    private static void ShowUsage()
    {
        Console.WriteLine("\nUsage:");
        Console.WriteLine("  BeeMemoryBank.Node.exe [path-to-node.config.json]");
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
