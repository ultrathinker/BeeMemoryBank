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

    public override Result DoWork()
    {
        try
        {
            Task.Run(RunAsync).GetAwaiter().GetResult();
            return Result.InvokeSuccess()!;
        }
        catch (Exception ex)
        {
            Services.SyncHeartbeat.RecordCycle(false, 0, "worker: " + ex.Message, "wm");
            return Result.InvokeRetry()!;
        }
    }

    private static async Task RunAsync()
    {
        // Backstop only: if the foreground service is alive in this process it's already syncing on its
        // own (5-min) cadence — defer to it so the two never run SyncClient concurrently. The heartbeat
        // still updates (tagged wm-skip) so we can confirm WorkManager itself is firing.
        if (SyncForegroundService.IsRunning)
        {
            Services.SyncHeartbeat.RecordCycle(true, 0, null, "wm-skip");
            return;
        }

        var services = IPlatformApplication.Current?.Services;
        if (services == null)
        {
            Services.SyncHeartbeat.RecordCycle(false, 0, "no services", "wm");
            return;
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
            return;
        }

        var whitelist = sp.GetRequiredService<IWhitelistRepository>();
        var syncClient = sp.GetRequiredService<SyncClient>();
        var httpFactory = services.GetRequiredService<IHttpClientFactory>();

        var nodes = (await whitelist.GetAllActiveAsync())
            .Where(n => !string.IsNullOrEmpty(n.ApiAddress)).ToList();
        if (nodes.Count == 0)
        {
            Services.SyncHeartbeat.RecordCycle(true, 0, null, "wm");
            return;
        }

        int applied = 0;
        string? err = null;
        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        foreach (var node in nodes)
        {
            try { applied += await syncClient.SyncWithAsync(http, node.ApiAddress!, CancellationToken.None); }
            catch (Exception ex) { err = ex.Message; }
        }
        Services.SyncHeartbeat.RecordCycle(err == null, applied, err, "wm");
    }
}
