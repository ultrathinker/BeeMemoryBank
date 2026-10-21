using System.Net.Http.Headers;

namespace BeeMemoryBank.Sync;

/// <summary>
/// A listening blind node answered 503 with <see cref="HeaderName"/>: its owner's limits stop it
/// (a server blind node's storage or daily traffic cap, release 2.7). This is a typed pause, never
/// a failure: the door is open, the node said "not today" and when to ask again, so the push loop
/// holds the peer in <see cref="PausedPeers"/> for exactly that long — no unreachable back-off, no
/// failure streak — and the Blind nodes page says it is paused by its owner, not silent. The one exception in the words is <see cref="PeerPauseReport.Busy"/>: a node whose
/// room is held by other transfers for a moment is not paused by anybody, and is said to be busy.
///
/// <para>What the peer wrote travels only as the closed tokens of <see cref="PeerPauseReport"/>:
/// the reason is read through <see cref="Reason"/> (disk, traffic, or "other" for a token this
/// build does not know) and the sentences are this node's. An older full node sees a plain 503 and
/// backs off as for any 503: acceptable, documented.</para>
/// </summary>
public sealed class PeerPausedException(string reason, TimeSpan? retryAfter) : InvalidOperationException(
    PeerPauseReport.Reason(reason) == PeerPauseReport.Busy
        ? "The blind node is busy with other transfers. It is asked again in about a minute."
        : "The blind node is paused by its owner. Nothing moves until it says otherwise.")
{
    /// <summary>The header a paused blind node names its reason in.</summary>
    public const string HeaderName = "X-BMB-Paused";

    /// <summary>When nothing was said: one hour, the hourly retry of a paused peer.</summary>
    public static readonly TimeSpan DefaultRetry = TimeSpan.FromHours(1);

    /// <summary>How long a caller may not ask again (from the peer's own Retry-After, bounded to a day).</summary>
    public TimeSpan RetryAfter { get; } = retryAfter is { } r && r > TimeSpan.Zero
        ? (r > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : r)
        : DefaultRetry;

    /// <summary>The closed reason token (<see cref="PeerPauseReport.Reason"/> of the peer's own word): disk, traffic, other.</summary>
    public string Reason { get; } = PeerPauseReport.Reason(reason) ?? PeerPauseReport.Other;

    /// <summary>What the contact status shows for the failure column, in this node's words (never the peer's).</summary>
    public string Answer => Reason switch
    {
        PeerPauseReport.Disk => "it is paused by its owner (storage limit)",
        PeerPauseReport.Traffic => "it is paused by its owner (daily traffic limit)",
        PeerPauseReport.Busy => "it is busy with other transfers right now",
        _ => "it is paused by its owner (a limit set on the node)",
    };

    /// <summary>
    /// Turns a paused answer into this exception: a 503 that names a reason in <see cref="HeaderName"/>.
    /// Everything else (a 503 without the header — an overloaded proxy, a node in maintenance — a 507
    /// of a full disk) is what the caller's own handling of HTTP errors is for.
    /// </summary>
    public static void ThrowIfPaused(HttpResponseMessage response)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable) return;
        var reason = ReadReason(response.Headers);
        if (reason is null) return;

        TimeSpan? retry = null;
        if (response.Headers.RetryAfter is { Date: { } date })
            retry = date.UtcDateTime - DateTime.UtcNow;
        else if (response.Headers.RetryAfter is { Delta: { } delta })
            retry = delta;
        throw new PeerPausedException(reason, retry);
    }

    private static string? ReadReason(HttpResponseHeaders headers) =>
        headers.TryGetValues(HeaderName, out var values)
            && values.FirstOrDefault() is { Length: > 0 } token
            ? token
            : null;
}
