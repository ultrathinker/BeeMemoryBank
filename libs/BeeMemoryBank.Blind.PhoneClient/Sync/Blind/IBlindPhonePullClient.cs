using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>Receives one authenticated, pull-only protocol-3 page for a blind phone.</summary>
public interface IBlindPhonePullClient
{
    Task SyncOnceAsync(HttpClient http, BlindCallCode target, CancellationToken ct);

    /// <summary>
    /// Tells the node this copy calls that the person has paused it (<paramref name="pause"/>): its usual position report with two additive fields,
    /// <c>paused=&lt;reason&gt;&amp;pausedUntil=&lt;utc&gt;</c>, so that the node shows "paused by the device's owner" and not "silent". A node that does not know the fields
    /// ignores them. The default does nothing (a client that never reports one).
    /// </summary>
    Task ReportPausedAsync(HttpClient http, BlindCallCode target, BlindPause pause, CancellationToken ct) => Task.CompletedTask;
}
