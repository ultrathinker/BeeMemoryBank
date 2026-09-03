using Android.Content;
using AndroidX.Work;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Mobile.Platforms.Android;

/// <summary>
/// WorkManager backstop for background backup sync. The foreground service gives near-real-time sync
/// while the app is recently active, but aggressive OEMs (OnePlus/ColorOS, Samsung) kill it after a
/// few hours AND block its restart (START_STICKY and BootReceiver both neutered). WorkManager survives
/// process death and reboots and is honoured by those OEMs far better, so this guarantees a sync at
/// least every ~15 minutes. It runs even while the vault is locked: background sync authenticates via
/// the Keystore ingest key (INodeAuthSigner) and only stores encrypted events.
/// </summary>
public class SyncWorker : Worker
{
    public SyncWorker(Context context, WorkerParameters workerParams) : base(context, workerParams) { }

    // Retry a transient failure a few times (exponential backoff from 30s), then stop retrying so the
    // normal 15-minute period resumes. Without this cap, a persistently-offline node would push the
    // backoff to WorkManager's max (~5h), destroying the every-15-min cadence we need.
    private const int MaxRetries = 3;

    public override Result DoWork()
    {
        try
        {
            var hadError = Task.Run(RunAsync).GetAwaiter().GetResult();
            if (!hadError) return Result.InvokeSuccess()!;
            return RunAttemptCount < MaxRetries ? Result.InvokeRetry()! : Result.InvokeSuccess()!;
        }
        catch (Exception ex)
        {
            Services.SyncHeartbeat.RecordCycle(false, 0, "worker: " + ex.Message, "wm");
            return RunAttemptCount < MaxRetries ? Result.InvokeRetry()! : Result.InvokeSuccess()!;
        }
    }

    /// <summary>Runs a sync cycle. Returns true if a node sync failed (so DoWork can request a retry).</summary>
    private static async Task<bool> RunAsync()
    {
        // Backstop only: if the foreground service is alive in this process it's already syncing on its
        // own (5-min) cadence — defer to it so the two never run SyncClient concurrently. The heartbeat
        // still updates (tagged wm-skip) so we can confirm WorkManager itself is firing.
        if (SyncForegroundService.IsRunning)
        {
            Services.SyncHeartbeat.RecordCycle(true, 0, null, "wm-skip");
            return false;
        }

        var services = IPlatformApplication.Current?.Services;
        if (services == null)
        {
            Services.SyncHeartbeat.RecordCycle(false, 0, "no services", "wm");
            return false;
        }

        var scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        // Keep the Lamport clock consistent if WorkManager started a fresh process.
        var eventLogRepo = sp.GetRequiredService<IEventLogRepository>();
        var maxTs = await eventLogRepo.GetMaxLamportTimestampAsync();
        services.GetRequiredService<LamportClock>().Initialize(maxTs);

        var invisible = sp.GetService<BeeMemoryBank.Core.Services.InvisibleModeService>();
        if (invisible is { IsInvisible: true })
        {
            Services.SyncHeartbeat.RecordCycle(true, 0, null, "wm");
            return false;
        }

        var whitelist = sp.GetRequiredService<IWhitelistRepository>();
        var syncClient = sp.GetRequiredService<SyncClient>();
        var httpFactory = services.GetRequiredService<IHttpClientFactory>();

        var nodes = (await whitelist.GetAllActiveAsync())
            .Where(n => !string.IsNullOrEmpty(n.ApiAddress)).ToList();
        if (nodes.Count == 0)
        {
            Services.SyncHeartbeat.RecordCycle(true, 0, null, "wm");
            return false;
        }

        int applied = 0;
        string? err = null;
        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        foreach (var node in nodes)
        {
            try { applied += await syncClient.SyncWithAsync(http, node.ApiAddress!, node.NodeId, CancellationToken.None); }
            catch (Exception ex) { err = ex.Message; }
        }
        Services.SyncHeartbeat.RecordCycle(err == null, applied, err, "wm");
        return err != null;
    }
}
