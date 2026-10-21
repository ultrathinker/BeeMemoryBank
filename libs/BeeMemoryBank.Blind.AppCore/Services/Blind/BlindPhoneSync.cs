using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>Runs the phone's pull-only protocol-3 sync through the call-code pinned client.</summary>
public sealed class BlindPhoneSync(
    IBlindPhonePullClient client, BlindHttpClientProvider http, BlindPhoneState state) : IBlindPhoneSync
{
    /// <summary>The limit guard: asked before a round starts (a pause is thrown as <see cref="BlindPausedException"/>, nothing goes out). Null for a composition that has none.</summary>
    public BlindLimitGuard? Guard { get; init; }

    /// <summary>Tells the source when a round stops for a pause the source is told about.</summary>
    public BlindPauseReporter? Reporter { get; init; }

    /// <summary>What the round counted is saved when it ends, however it ends.</summary>
    public BlindUsageLedger? Usage { get; init; }

    /// <summary>What the copy holds is measured again when a round ends (the counters that were kept live during it are re-synced with the folders).</summary>
    public BlindStoredBytes? Stored { get; init; }

    public async Task SyncOnceAsync(BlindCallCode target, CancellationToken ct)
    {
        try
        {
            await RunAsync(target, ct);
        }
        finally
        {
            Usage?.Flush();
            Stored?.Resync();
        }
    }

    private async Task RunAsync(BlindCallCode target, CancellationToken ct)
    {
        // A pause is not a failure: before the round starts the guard says so, and the source is told (once a day while it lasts); nothing else goes out.
        if (Guard?.Check() is { } held)
        {
            await TellAsync(target, held, ct);
            throw new BlindPausedException(held);
        }

        try
        {
            await client.SyncOnceAsync(http.GetClient(target), target, ct);
            // A round ran to its end: a pause that was told is over, the next one is told at once.
            Reporter?.Clear();
        }
        catch (BlindPausedException ex)
        {
            // A limit was reached DURING the round: the page in flight was not applied (the cursor is where the last applied event put it); the source is told.
            await TellAsync(target, ex.Pause, ct);
            throw;
        }
        catch (SnapshotRequiredException)
        {
            // The hourly heavy worker owns replica download. Marking
            // this stale position incomplete routes the next permitted heavy pass through it.
            state.InitialLoadDone = false;
            throw;
        }
    }

    private Task TellAsync(BlindCallCode target, BlindPause pause, CancellationToken ct) =>
        Reporter is { } reporter ? reporter.ReportIfDueAsync(target, pause, ct) : Task.CompletedTask;
}
