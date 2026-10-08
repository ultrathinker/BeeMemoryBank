using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindIos.Services;

/// <summary>
/// The two background tasks the app asks iOS for, and what each does when iOS grants it. iOS decides when (and whether) they run: an app
/// refresh task gets about 30 seconds a few times a day at best, depending on how often the person uses the app, the battery and Low Power
/// Mode; a processing task gets minutes, typically at night on the charger. A blind copy listens on no port and keeps no socket, so these
/// rounds and the time the app is open are all it has.
/// </summary>
public static class IosBackgroundRounds
{
    /// <summary>BGAppRefreshTask: one sync round (or a piece of the first load, which resumes).</summary>
    public const string SyncTaskId = "com.beememorybank.blind.sync";

    /// <summary>BGProcessingTask with network: the first load or a due backup, then a sync round.</summary>
    public const string WorkTaskId = "com.beememorybank.blind.work";

    /// <summary>The earliest the next sync round is asked for: the Android worker's period. iOS treats it as "not before", never as "at".</summary>
    public static readonly TimeSpan SyncNotBefore = TimeSpan.FromMinutes(15);

    /// <summary>The earliest the next long-job window is asked for: the hourly long job of the other copies.</summary>
    public static readonly TimeSpan WorkNotBefore = TimeSpan.FromHours(1);

    /// <summary>
    /// The app refresh round. Opens the database and the identity first (iOS may have started the app for this task alone), then: nothing
    /// before pairing; the first load while it is not done (it resumes where the last piece stopped); otherwise one sync round. Returns the
    /// sentence for the log; throws only for the cancellation iOS asks for when the time is up.
    /// </summary>
    public static async Task<string> RunSyncAsync(IBlindAppController app, CancellationToken ct)
    {
        await app.InitializeAsync(ct);
        var status = app.GetStatus();
        if (!status.IsPaired) return "Not paired yet.";
        if (!status.InitialLoadDone) return await app.RunHeavyAsync(forceBackup: false, ct);
        return await app.RequestSyncAsync(ct);
    }

    /// <summary>The processing round: the first load or a due backup, then a sync round when the copy is loaded.</summary>
    public static async Task<string> RunWorkAsync(IBlindAppController app, CancellationToken ct)
    {
        await app.InitializeAsync(ct);
        if (!app.GetStatus().IsPaired) return "Not paired yet.";
        var heavy = await app.RunHeavyAsync(forceBackup: false, ct);
        if (!app.GetStatus().InitialLoadDone) return heavy;
        var sync = await app.RequestSyncAsync(ct);
        return $"{heavy} {sync}";
    }
}
