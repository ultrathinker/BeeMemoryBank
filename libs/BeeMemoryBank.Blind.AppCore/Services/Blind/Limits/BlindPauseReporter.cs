using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// Tells the source that the person has paused this copy, so that its page says "paused by the device's owner" and not "silent": the position report with the additive fields
/// <c>paused=&lt;reason&gt;&amp;pausedUntil=&lt;utc&gt;</c> (<see cref="IBlindPhonePullClient.ReportPausedAsync"/>). Sent at the moment a round or a long job stops for traffic, requests, storage or
/// "paused by you" (and the device's own limits), NOT for the network or the battery (nothing is wrong there and it is nobody's business), and then once every 24 hours while the
/// pause goes on. A round that ran ends the episode: the next pause is told at once. The report goes through a pipeline that counts but never blocks (about 3 KB); a source that fails to
/// answer is tried again after an hour, not at every pass. An older source ignores the two fields.
/// </summary>
public sealed class BlindPauseReporter(
    BlindPhoneState state, IBlindPhonePullClient client, BlindHttpClientProvider http, BlindPhoneLog log, TimeProvider time)
{
    /// <summary>While a lasting pause goes on, the source is told this often.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromHours(24);

    /// <summary>After a report that could not be delivered, the next try waits this long.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private DateTimeOffset? _failedAt;

    /// <summary>Tells the source about <paramref name="pause"/> if it is due. Never throws for a failed delivery (it is in the log); only for cancellation.</summary>
    public async Task ReportIfDueAsync(BlindCallCode target, BlindPause pause, CancellationToken ct)
    {
        if (!BlindPauseReasons.IsReportable(pause.Reason)) return;
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (state.PauseReportedReason == pause.Reason && state.PauseReportedAt is { } at && now - at < Every) return;
            if (_failedAt is { } failed && now - failed < RetryAfterFailure) return;
        }

        try
        {
            using var exempt = http.GetClient(target, exempt: true);
            await client.ReportPausedAsync(exempt, target, pause, ct).ConfigureAwait(false);
            lock (_gate)
            {
                state.PauseReportedAt = now;
                state.PauseReportedReason = pause.Reason;
                _failedAt = null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (_gate) _failedAt = now;
            BlindRunReport.Record(log, "paused", "The source could not be told about the pause: " + BlindRunReport.Reason(ex));
        }
    }

    /// <summary>A round ran: the pause is over, and the next one is a new episode that is told at once.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (state.PauseReportedAt is not null) state.PauseReportedAt = null;
            if (state.PauseReportedReason is not null) state.PauseReportedReason = null;
            _failedAt = null;
        }
    }
}
