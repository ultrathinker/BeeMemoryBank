using BeeMemoryBank.Api.Services.BlindBackup;
using BeeMemoryBank.Api.Services.BlindStatus;

namespace BeeMemoryBank.Api.Services.BlindLimits;

/// <summary>
/// The <c>limits</c> section of <c>GET /api/blind/status</c> (release 2.7): the two caps and how
/// the node stands against them — what the console's Limits card and <c>bmb blind status</c> show.
/// The numbers are the node's own (a local-admin surface; the endpoint gates its callers). A node
/// with no limits still reports its traffic of today and its stored bytes, which the console shows
/// next to the empty limit boxes.
/// </summary>
public sealed class BlindLimitsStatusContributor(
    BlindBackupSettingsStore settings,
    BlindUsageGuard guard) : IBlindStatusContributor
{
    public Task ContributeAsync(BlindStatusBuilder b, CancellationToken ct)
    {
        var limits = settings.Load();
        var reading = guard.StoredNow();
        var pause = guard.PauseNow();
        var counts = guard.TrafficUnavailableReason();
        b.Set("limits", new Dictionary<string, object?>
        {
            ["max_stored_bytes"] = limits.MaxStoredBytes,
            ["max_traffic_per_day"] = limits.MaxTrafficPerDay,
            ["stored_bytes"] = reading.Known ? reading.Bytes : null,
            ["traffic_today"] = guard.TrafficToday(),
            // When a reached daily traffic limit lifts (null while no traffic pause holds).
            ["traffic_resumes_at"] = pause is { Reason: BlindPause.Traffic } ? guard.NextLocalMidnight() : null,
            ["paused"] = pause?.Reason,
            ["traffic_unavailable"] = counts is not null,
        });
        return Task.CompletedTask;
    }
}
