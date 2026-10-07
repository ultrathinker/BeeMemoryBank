using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting.WindowsServices;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Hosting;
using BeeMemoryBank.Infrastructure.Network;
using BeeMemoryBank.Infrastructure.Tls;

namespace BeeMemoryBank.Node;

public static class Program
{
    /// <summary>The node's shared hold on vault.lease, for the life of the process (a field: a local could be finalized).</summary>
    private static BeeMemoryBank.AppPaths.VaultLease? s_vaultLease;

    /// <summary>
    /// The vault gate for both start modes (review release-b R1-3): refused while a re-key runs, an interrupted swap
    /// finished or rolled back, and vault.lease held shared. Returns the data directory to use, or null after printing
    /// why the start is refused.
    /// </summary>
    private static string? EnterVault(string dataDirectory)
    {
        try
        {
            var (resolution, lease) = BeeMemoryBank.AppPaths.VaultStartup.Enter(Path.GetFullPath(dataDirectory));
            s_vaultLease = lease;
            return resolution.DataDir;
        }
        catch (BeeMemoryBank.AppPaths.VaultInUseException ex)
        {
            Console.Error.WriteLine($"[Error] {ex.Message}");
            return null;
        }
    }

    public static async Task<int> Main(string[] args)
    {
        BeeMemoryBank.Hosting.Utf8Console.EnableForRedirectedOutput();
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

        if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
        {
            ServiceBase.Run(new BmbdWindowsService(isAutoMode, dataDirectory, configPath));
            return 0;
        }

        var exitCode = await RunOrchestratorAsync(isAutoMode, dataDirectory, configPath, CancellationToken.None);

        // All graceful cleanup (lifeline/app/orchestrator disposal) has already run inside the
        // awaited call above - this is purely a defensive belt-and-suspenders exit. Observed
        // observed in practice: after a stdin-triggered graceful shutdown completes
        // (orchestrator logs "Stopped successfully"), the OS process itself sometimes never
        // actually terminates and `dotnet test`/callers hang waiting on it, even though nothing
        // further executes or logs. Root cause not fully isolated (thread-pool/native-handle
        // state at shutdown, not application logic) - Environment.Exit forces real process
        // termination instead of relying on the runtime's return-from-Main path, which this
        // process has been observed not to reliably take.
        Environment.Exit(exitCode);
        return exitCode; // unreachable; keeps the compiler happy about the return type.
    }

    public static async Task<int> RunOrchestratorAsync(
        bool isAutoMode,
        string? dataDirectory,
        string? configPath,
        CancellationToken stopToken)
    {
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

            if (!string.IsNullOrWhiteSpace(dataDirectory))
            {
                // --data was supplied explicitly; use it as-is.
            }
            else
            {
                var envDataPath = Environment.GetEnvironmentVariable("BMB_DATA_PATH");
                dataDirectory = !string.IsNullOrWhiteSpace(envDataPath)
                    ? envDataPath
                    : BeeMemoryBank.AppPaths.BmbPaths.DefaultVaultDir;
            }
            // The vault gate, before anything creates or opens D (the legacy rescue below included).
            if (EnterVault(dataDirectory!) is not { } entered) return 5;
            resolvedDataDirectory = entered;

            try
            {
                Directory.CreateDirectory(resolvedDataDirectory);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Error] Failed to create data directory '{resolvedDataDirectory}': {ex.Message}");
                return 1;
            }

            // §79-89: Rescue legacy data STRICTLY before DirectoryLock.Acquire (orchestrator.StartAsync).
            // Fix #4: Apply rescue whenever the resolved path EQUALS the canonical default vault dir,
            // regardless of how the path was supplied (no --data, explicit --data matching the default,
            // or BMB_DATA_PATH matching the default). Any OTHER explicit path represents a deliberate
            // operator choice (e.g. a portable/alternate installation) and must not trigger rescue.
            var canonicalDefaultDir = Path.GetFullPath(BeeMemoryBank.AppPaths.BmbPaths.DefaultVaultDir);
            bool isDefaultVaultDir = string.Equals(resolvedDataDirectory, canonicalDefaultDir,
                StringComparison.OrdinalIgnoreCase);

            if (isDefaultVaultDir)
            {
                var legacyDataDir = Path.Combine(AppContext.BaseDirectory, "data");
                Console.WriteLine($"[Node] Checking for legacy data to rescue from '{legacyDataDir}'...");
                var rescueResult = BeeMemoryBank.AppPaths.LegacyDataRescue.TryRescue(legacyDataDir, resolvedDataDirectory);
                Console.WriteLine($"[Node] Rescue outcome: {rescueResult.Outcome}" +
                    (rescueResult.Message != null ? $" — {rescueResult.Message}" : string.Empty));

                if (rescueResult.Outcome == BeeMemoryBank.AppPaths.RescueOutcome.LegacyFoundButRescueFailed)
                {
                    Console.Error.WriteLine(
                        $"[Error] Legacy data rescue failed — refusing to start with empty storage.\n" +
                        $"  Source : {legacyDataDir}\n" +
                        $"  Reason : {rescueResult.Message}\n" +
                        "  Action : free the data directory (stop any running bmbd node) and retry.");
                    return 4; // non-zero; distinct from other bmbd exit codes
                }
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

            // The same gate as the auto mode: without it a configured node could start, or take node.lock, in the
            // middle of a re-key, and would never finish an interrupted swap.
            if (EnterVault(config.DataDirectory) is not { } enteredConfigured) return 5;
            resolvedDataDirectory = enteredConfigured;
            childConfigs = config.Children.Select(c => new ChildProcessConfig(
                c.ApplicationName,
                c.ExecutablePath,
                c.WorkingDirectory,
                c.ReadyFilePath,
                c.Arguments,
                c.EnvironmentVariables
            )).ToList();
        }

        var apiConfig = childConfigs.FirstOrDefault(c => c.ApplicationName == "BeeMemoryBank.Api");
        var webConfig = childConfigs.FirstOrDefault(c => c.ApplicationName == "BeeMemoryBank.Web");

        using var orchestrator = (isAutoMode && apiConfig != null && webConfig != null)
            ? new NodeOrchestrator(resolvedDataDirectory, new List<ChildProcessConfig> { apiConfig })
            : new NodeOrchestrator(resolvedDataDirectory, childConfigs);

        WebApplication? app = null;
        LanJoinListener? lanListener = null;
        LanFrontListener? lanFront = null;
        LanNetworkSwitch? lanNetwork = null;
        LanListenerState? listenerState = null;
        NodeFront.CachedLeafCert? lanLeaf = null;
        var tcs = new TaskCompletionSource<int>();
        // The front is only ever stopped through this gate. A stop (EOF, SIGTERM, Ctrl+C, the service token, a
        // critical failure) that arrives while the front is still starting waits for that start to finish
        // instead of stopping the host under its own StartAsync - Kestrel's heartbeat thread then throws on a
        // foreign thread and the whole process aborts (exit 134). A start that begins after the stop was
        // requested does not run at all. See FrontStartStopGate.
        var frontGate = new FrontStartStopGate<WebApplication>(
            async front =>
            {
                try
                {
                    Console.WriteLine("[Node] Stopping front app...");
                    using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await front.StopAsync(stopCts.Token);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Node] Error stopping front: {ex.Message}");
                }
            },
            FrontStartStopGate<WebApplication>.DefaultStartWaitBound,
            Console.WriteLine);
        var stopCoordinator = new NodeStopCoordinator(
            () => frontGate.StopAsync(),
            () => orchestrator.StopAsync(),
            tcs);

        using var registration = stopToken.Register(() => _ = stopCoordinator.RequestStopAsync());

        StdinLifeline? lifeline = null;
        if (Environment.GetEnvironmentVariable("BMB_STDIN_LIFELINE") == "1")
        {
            Console.WriteLine("[Node] BMB_STDIN_LIFELINE=1: monitoring stdin for EOF...");
            lifeline = StdinLifeline.Start(() =>
            {
                Console.WriteLine("[Node] Stdin lifeline triggered EOF. Initiating graceful shutdown...");
                _ = stopCoordinator.RequestStopAsync();
            });
        }

        orchestrator.OnAllReady += () =>
        {
            Console.WriteLine("[Node] Orchestrator successfully started all child processes and verified readiness.");
        };

        orchestrator.OnCriticalFailure += (reason) =>
        {
            Console.Error.WriteLine($"[Node] CRITICAL FAILURE: {reason}");
            _ = stopCoordinator.RequestStopAsync(2);
        };

        if (!stopToken.CanBeCanceled)
        {
            Console.CancelKeyPress += (sender, e) =>
            {
                Console.WriteLine("[Node] Cancel key pressed. Stopping orchestrator...");
                e.Cancel = true; // Prevent process from immediately terminating
                _ = stopCoordinator.RequestStopAsync();
            };
        }

        // launchd/logout use SIGTERM rather than a console Ctrl+C. Cancel the default signal
        // termination so the same ordered graceful stop runs before this process exits.
        using var sigtermRegistration = !OperatingSystem.IsWindows()
            ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                Console.WriteLine("[Node] SIGTERM received. Stopping orchestrator...");
                context.Cancel = true;
                _ = stopCoordinator.RequestStopAsync();
            })
            : null;

        try
        {
            // "Not listening" is written before the Api child starts, so the announcer there never reads what a crashed earlier run left.
            // The listener (below) and the BMB_HTTPS_ENABLED front say "listening" only once their port is really bound.
            listenerState = new LanListenerState(resolvedDataDirectory);
            if (!listenerState.Set(false))
                Console.WriteLine("[Node] WARNING: could not write the network listener state; the node will not be announced on the network.");

            Console.WriteLine($"[Node] Launching children with lock on data dir: '{resolvedDataDirectory}'...");
            await orchestrator.StartAsync(stopToken);
            // Every child is up: the first start on a swapped-in vault succeeded, so its journal and re-key lock go
            // (the Api child does the same; whichever comes second finds nothing to do). Review release-b R1-4.
            BeeMemoryBank.AppPaths.RekeySwapResolver.CompleteFirstStart(resolvedDataDirectory);

            if (isAutoMode && apiConfig != null && webConfig != null)
            {
                if (!orchestrator.ReadyChildren.TryGetValue("BeeMemoryBank.Api", out var apiReadyInfo))
                {
                    throw new InvalidOperationException("Api ready info not found after orchestrator started.");
                }
                var apiUrl = apiReadyInfo.Urls.FirstOrDefault()
                    ?? throw new InvalidOperationException("Api child process has no registered URLs.");

                Console.WriteLine($"[Node] Api resolved URL: {apiUrl}. Injecting into Web environment...");

                var updatedWebEnv = new Dictionary<string, string>(webConfig.EnvironmentVariables ?? new Dictionary<string, string>());
                updatedWebEnv["BMB_API_URL"] = apiUrl;

                var updatedWebConfig = webConfig with { EnvironmentVariables = updatedWebEnv };

                orchestrator.StartAdditionalChild(updatedWebConfig);

                await orchestrator.WaitForAllReadyOrFailureAsync(stopToken);
            }

            Console.WriteLine("[Node] Orchestrator started. Building and starting front...");
            // Default to the plan's designated port (127.0.0.1:5310, distinct from
            // standalone/Docker's 5300/5301) instead of ASP.NET Core's own default
            // (5000), which commonly collides with other local dev tools. If it's
            // taken, fall back to an OS-assigned free port - the real bound port
            // always ends up in .runtime.json/node.status.json regardless.
            const int preferredFrontPort = 5310;

            // Opening the node to the network is the profile's setting "Devices on my network" (Admin > Nodes), off by
            // default; it is served by a second listener that can be opened and closed while the node runs (LanFrontListener).
            // BMB_HTTPS_ENABLED=1 is the older way, kept as an override: it makes the front itself add the HTTPS listener on
            // :5311 for this run, and the setting then has nothing to change. With neither, the front is byte-for-byte what it
            // was: only the plain-HTTP listener runs.
            var networkStore = new NodeNetworkSettingsStore(resolvedDataDirectory);
            var startupExposure = networkStore.Current();
            var httpsEnabled = startupExposure.Source == NetworkExposureSource.Environment;
            if (httpsEnabled)
            {
                Console.WriteLine("[Node] BMB_HTTPS_ENABLED=1: additive HTTPS listener will be started on :5311.");
            }
            if (frontGate.StopRequested)
            {
                return await tcs.Task;
            }

            // "Connect a device" (plan section 10): unless the LAN listener is permanently on, the
            // Connect page opens it on demand — see LanJoinListener. Needs the internal key (the
            // page authenticates with it) and the local CA (Windows only).
            // The node's own internal key: the one the Api child was started with (auto mode generates it, or takes the one
            // the Desktop shell put in this process's environment), else this process's environment.
            string? ResolveInternalKey() => apiConfig?.EnvironmentVariables?.GetValueOrDefault("BMB_INTERNAL_KEY")
                ?? Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY");

            // POST /node/lock: the shell asks the front to lock the vault when the computer sleeps; the front asks the Api, with
            // the internal key. On every OS (unlike the LAN control). Null (the route stays a 501 stub) when there is no key.
            NodeLockControl? BuildLock()
            {
                var internalKey = ResolveInternalKey();
                if (string.IsNullOrEmpty(internalKey)) return null;
                if (!orchestrator.ReadyChildren.TryGetValue("BeeMemoryBank.Api", out var api)
                    || api.Urls.FirstOrDefault() is not { } apiUrl)
                    return null;
                return new NodeLockControl(apiUrl, internalKey);
            }

            // Windows and macOS: the two systems with a place to keep the local CA's key (DPAPI, the Keychain), which the HTTPS
            // listeners need. The listeners themselves are plain Kestrel; only the firewall differs (see ILanFirewall).
            LanControl? BuildLan(bool permanent)
            {
                var internalKey = ResolveInternalKey();
                if (!(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) || string.IsNullOrEmpty(internalKey)) return null;

                var leaf = lanLeaf ??= new NodeFront.CachedLeafCert(new LocalCaService(resolvedDataDirectory));
                var platform = OperatingSystem.IsWindows() ? "windows" : "macos";
                // The Windows service has no desktop to show a UAC prompt on (the installer's firewall option opens the ports),
                // and a Mac asks the user itself when the first connection arrives: only the Windows app edits the firewall.
                ILanFirewall firewall = OperatingSystem.IsWindows() && !WindowsServiceHelpers.IsWindowsService()
                    ? new NetshLanFirewall()
                    : new NoLanFirewall();

                // The door forwards joins to the Api child, so without one there is no door; a node that is permanently open
                // by BMB_HTTPS_ENABLED=1 never needed it.
                var apiUrl = orchestrator.ReadyChildren.TryGetValue("BeeMemoryBank.Api", out var api) ? api.Urls.FirstOrDefault() : null;
                if (apiUrl == null && !permanent) return null;
                if (apiUrl != null)
                    lanListener ??= new LanJoinListener(apiUrl, leaf.Get,
                        new System.Net.IPEndPoint(System.Net.IPAddress.Any, NodeFront.HttpsPort), TimeProvider.System);
                lanFront ??= new LanFrontListener(
                    () => NodeFrontBuilder.BuildNetworkFront(orchestrator.ReadyChildren,
                        new System.Net.IPEndPoint(System.Net.IPAddress.Any, NodeFront.HttpsPort), leaf.Get),
                    leaf.Get,
                    listening => listenerState?.Set(listening));
                // Rebuilt for every front (it holds references, no state of its own): a front that had to give up the
                // BMB_HTTPS_ENABLED listener is no longer "permanent by environment" and the setting takes over again.
                lanNetwork = new LanNetworkSwitch(networkStore, lanFront, lanListener,
                    permanent ? startupExposure : NetworkExposure.Resolve(networkStore.Load(), null));

                return new LanControl(permanent ? null : lanListener, leaf.Get, firewall, internalKey, lanNetwork, platform);
            }

            // Every start goes through the gate: it builds the front and starts it only if no stop has been
            // requested, a stop that arrives meanwhile waits for the start to finish, and a front whose start
            // failed is disposed by the gate itself. It returns null when the stop came first - then the front
            // never starts and the run is over (the stop completes the run wait).
            Task<WebApplication?> StartFrontAsync(string url, bool https) => frontGate.StartAsync(
                () => BuildFront(
                    new[] { "--urls", url },
                    orchestrator.ReadyChildren,
                    https,
                    resolvedDataDirectory,
                    BuildLan(https),
                    BuildLock()),
                front => front.StartAsync());

            try
            {
                app = await StartFrontAsync($"http://127.0.0.1:{preferredFrontPort}", httpsEnabled);
                if (app == null) return await tcs.Task;
            }
            catch (IOException)
            {
                if (frontGate.StopRequested)
                {
                    return await tcs.Task;
                }

                Console.WriteLine($"[Node] Port {preferredFrontPort} is unavailable, falling back to an OS-assigned port...");
                try
                {
                    app = await StartFrontAsync("http://127.0.0.1:0", httpsEnabled);
                    if (app == null) return await tcs.Task;
                }
                catch (IOException) when (httpsEnabled)
                {
                    if (frontGate.StopRequested)
                    {
                        return await tcs.Task;
                    }

                    // The opt-in HTTPS listener binds the FIXED NodeFront.HttpsPort — if THAT is what's
                    // actually unavailable (not the HTTP port), retrying with a different HTTP port
                    // won't help and would otherwise take the whole front down. HTTPS is additive and
                    // must never be able to prevent the plain-HTTP path from starting — degrade it off
                    // and retry once more with HTTPS disabled for this session.
                    Console.WriteLine(
                        $"[Node] WARNING: could not bind the opt-in HTTPS listener on :{NodeFront.HttpsPort} " +
                        "(port unavailable) — starting with HTTPS disabled for this session.");
                    httpsEnabled = false;
                    app = await StartFrontAsync("http://127.0.0.1:0", httpsEnabled);
                    if (app == null) return await tcs.Task;
                }
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

            // BMB_HTTPS_ENABLED=1: the front itself holds :5311, and it is bound only if httpsEnabled survived the fallback above.
            if (httpsEnabled) listenerState?.Set(true);

            // The profile's "Devices on my network" setting: if it is on, the second listener opens now, with the loopback front
            // already serving (a failure only leaves the node answering this computer, and says so in the log).
            if (lanNetwork != null) await lanNetwork.ApplyAtStartAsync(Console.WriteLine);

            Console.WriteLine("[Node] Node is running. Press Ctrl+C to shut down.");

            // Wait for shutdown or failure
            int exitCode = await tcs.Task;
            return exitCode;
        }
        catch (Exception ex)
        {
            // A stdin-triggered (or Ctrl+C/stopToken) graceful shutdown racing with startup
            // itself now surfaces here as an OperationCanceledException from
            // WaitForAllReadyOrFailureAsync (see NodeOrchestrator's _isStopping check) rather
            // than hanging forever waiting for a readiness signal that can no longer arrive.
            // That is a normal shutdown, not a startup failure - defer to the shutdown path's
            // own exit code (awaiting it if it hasn't resolved yet - it will, shortly, since
            // the exception firing at all means shutdown was already underway) instead of
            // reporting 3 for what is not actually a startup error.
            if (ex is OperationCanceledException)
            {
                return await tcs.Task;
            }

            Console.Error.WriteLine($"[Node] Orchestrator failed to start: {ex.Message}");
            return 3;
        }
        finally
        {
            lifeline?.Dispose();

            if (lanListener != null)
            {
                try { await lanListener.DisposeAsync(); } catch { }
            }
            if (lanFront != null)
            {
                try { await lanFront.DisposeAsync(); } catch { }
            }
            listenerState?.Set(false); // the BMB_HTTPS_ENABLED front goes down with the node too

            await stopCoordinator.RequestStopAsync();
            if (app != null) await app.DisposeAsync();
        }
    }

    public static WebApplication BuildFront(
        string[] webArgs,
        IReadOnlyDictionary<string, ReadyFileInfo> readyChildren,
        bool enableHttps = false,
        string? dataPath = null,
        LanControl? lan = null,
        NodeLockControl? lockControl = null)
    {
        var builder = WebApplication.CreateBuilder(webArgs);
        var front = NodeFrontBuilder.Build(builder, readyChildren, enableHttps, dataPath, lan, lockControl);
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
            DataDirectory: @"C:\ProgramData\BeeMemoryBank\data",
            Children: new List<ChildConfig>
            {
                new ChildConfig(
                    ApplicationName: "BeeMemoryBank.Api",
                    ExecutablePath: "dotnet",
                    WorkingDirectory: @"C:\Program Files\BeeMemoryBank\api",
                    ReadyFilePath: @"C:\ProgramData\BeeMemoryBank\data\api.ready",
                    Arguments: "BeeMemoryBank.Api.dll",
                    EnvironmentVariables: new Dictionary<string, string>
                    {
                        { "ASPNETCORE_URLS", "http://127.0.0.1:0" },
                        { "BMB_READY_FILE", @"C:\ProgramData\BeeMemoryBank\data\api.ready" }
                    }
                )
            }
        );
        Console.WriteLine(JsonSerializer.Serialize(example, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// Serializes every graceful shutdown trigger. The first request owns the complete stop
/// sequence, so overlapping EOF, Ctrl+C, and SIGTERM notifications cannot stop the front or
/// orchestrator twice or complete the run wait prematurely.
/// </summary>
public sealed class NodeStopCoordinator
{
    private readonly Func<Task> _stopFrontAsync;
    private readonly Func<Task> _stopOrchestratorAsync;
    private readonly TaskCompletionSource<int> _completion;
    private readonly object _gate = new();
    private Task? _stopTask;

    public NodeStopCoordinator(
        Func<Task> stopFrontAsync,
        Func<Task> stopOrchestratorAsync,
        TaskCompletionSource<int> completion)
    {
        _stopFrontAsync = stopFrontAsync;
        _stopOrchestratorAsync = stopOrchestratorAsync;
        _completion = completion;
    }

    public Task RequestStopAsync(int exitCode = 0)
    {
        lock (_gate)
        {
            return _stopTask ??= StopAsync(exitCode);
        }
    }

    private async Task StopAsync(int exitCode)
    {
        try
        {
            await _stopFrontAsync();
            await _stopOrchestratorAsync();
            _completion.TrySetResult(exitCode);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Node] Error during graceful shutdown: {ex.Message}");
            _completion.TrySetResult(exitCode);
        }
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
        // The Node front's HTTPS port — mDNS should advertise the port peers can ACTUALLY reach (the
        // front's :5311), not Api's own random ASPNETCORE_URLS=:0 port or Api's unrelated
        // standalone/Docker port. It is a fixed port (NodeFront.HttpsPort), unlike the loopback front's
        // OS-assigned fallback, so what is announced is what is served.
        const int frontHttpsPort = NodeFront.HttpsPort;
        var httpsEnabled = Environment.GetEnvironmentVariable("BMB_HTTPS_ENABLED") == "1";

        // Api's Program.cs fail-fasts in Production if BMB_INTERNAL_KEY is absent (it otherwise
        // falls back to a dev-only shared file, which is not something we want in a packaged
        // node). Generate one shared secret per orchestrator run and hand it to both children —
        // it never touches disk and isn't inherited by anything outside this process tree.
        //
        // EXCEPT: when a parent process (the Desktop app, hosting this bmbd for a profile) has
        // ALREADY set BMB_INTERNAL_KEY on our own environment before spawning us, reuse that key
        // instead of generating a different one. The parent generates it precisely so it can
        // authenticate its own /node/update/* guard requests against whichever bmbd it hosts;
        // silently overwriting it here would make that key permanently unguessable to the parent.
        var internalKey = Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY");
        if (string.IsNullOrEmpty(internalKey))
        {
            internalKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        }

        var apiEnv = new Dictionary<string, string>
        {
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
            ["BMB_READY_FILE"] = apiReadyFilePath,
            ["BMB_STDIN_LIFELINE"] = "1",
            ["BMB_BEHIND_LOOPBACK_PROXY"] = "1",
            // BMB_BEHIND_LOOPBACK_PROXY (above) isn't actually read by anything — the real
            // opt-in flag ForwardedHeadersExtensions.AddLoopbackForwardedHeaders checks is this
            // one. Without it, ForwardedHeadersMiddleware never runs, RemoteIpAddress for every
            // front-proxied request stays 127.0.0.1 (YARP's own loopback hop), and
            // RateLimitMiddleware's localhost-skip silently exempts every real client from
            // brute-force protection on /api/session/unlock, /api/session/login, /api/join.
            ["BMB_TRUST_LOOPBACK_FORWARDED_HEADERS"] = "true",
            ["BMB_DATA_PATH"] = absDataDir,
            // The front's HTTP listener (:5310) is loopback-only; only the HTTPS listener (:5311) is reachable from the network,
            // so that is the one worth announcing, and only while it is open: the profile's "Devices on my network" setting
            // (or BMB_HTTPS_ENABLED=1) decides, read by the announcer on every cycle. While it is off Api announces nothing
            // and opens no multicast socket, the thing that makes Windows Firewall ask about BeeMemoryBank.Api on a fresh install.
            ["BMB_MDNS_PORT"] = frontHttpsPort.ToString(),
            ["BMB_MDNS_HTTPS"] = "true",
            ["BMB_MDNS_ENABLED"] = "true",
            ["BMB_MDNS_FOLLOWS_NETWORK_SETTING"] = "1",
            ["BMB_INTERNAL_KEY"] = internalKey
        };

        // NOTE: BMB_API_URL is NOT set here — Api binds to a random port
        // (ASPNETCORE_URLS=http://127.0.0.1:0) that isn't known until Api's own ready-file is
        // written. RunOrchestratorAsync handles this: in auto-discovery mode it starts Api
        // alone first, reads its real resolved URL once ready, and injects BMB_API_URL into
        // Web's environment before starting Web (see the two-phase startup there).
        var webEnv = new Dictionary<string, string>
        {
            ["ASPNETCORE_URLS"] = "http://127.0.0.1:0",
            ["BMB_READY_FILE"] = webReadyFilePath,
            ["BMB_STDIN_LIFELINE"] = "1",
            // Web DOES read this one (Profile page): behind the front, /mcp is on the same address as the
            // page, so the page prints the address the user browses from in the AI-assistant snippet.
            ["BMB_BEHIND_LOOPBACK_PROXY"] = "1",
            ["BMB_TRUST_LOOPBACK_FORWARDED_HEADERS"] = "true",
            ["BMB_DATA_PATH"] = absDataDir,
            // So Connect.cshtml.cs (Web) can tell whether the front's opt-in HTTPS listener is
            // actually running before showing an https:// QR code that would otherwise silently
            // point at a port nothing is listening on (e.g. the MSI service's default config).
            ["BMB_HTTPS_ENABLED"] = httpsEnabled ? "1" : "0",
            ["BMB_INTERNAL_KEY"] = internalKey
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
