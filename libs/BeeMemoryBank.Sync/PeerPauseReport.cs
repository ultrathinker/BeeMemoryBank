using System.Globalization;

namespace BeeMemoryBank.Sync;

/// <summary>
/// What a blind copy says when its owner's limits or switch stop it (release 2.7): <c>paused=&lt;reason&gt;&amp;pausedUntil=&lt;time&gt;</c> on
/// <c>POST /api/sync/report-position</c>. A stopped copy goes quiet, and quiet looks exactly like "silent" (a lost phone, a dead battery); this tells the
/// owner of the network that the copy is fine and has been told to wait. Additive: a node that does not know the fields ignores them, a copy that does not send
/// them is not paused.
///
/// <para>Nothing a peer writes reaches a screen as it came. The reason is read through <see cref="Reason"/> (a closed set, anything else becomes
/// <see cref="Other"/>), the time through <see cref="Until"/> (a bounded instant, or nothing), and the sentence is made here (<see cref="Sentence"/>).</para>
/// </summary>
public static class PeerPauseReport
{
    /// <summary>A reason this build does not know: still a pause, said in general words.</summary>
    public const string Other = "other";

    /// <summary>The storage-limit token: the apps' report field and the paused answer's <c>X-BMB-Paused</c> header share it.</summary>
    public const string Disk = "disk";

    /// <summary>The daily-traffic-limit token, shared the same way.</summary>
    public const string Traffic = "traffic";

    /// <summary>
    /// A server blind node that is not at a limit but whose room is held by other transfers in flight (the paused answer's <c>X-BMB-Paused: busy</c>, with a <c>Retry-After</c> of about a
    /// minute): a short pause, asked again soon.
    /// </summary>
    public const string Busy = "busy";

    /// <summary>A pause report counts for this long. Past it the copy is judged on its contact again (<c>silent</c>): a pause that nobody renews is not an answer.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromDays(7);

    /// <summary>The reasons a copy states and the words for each. <c>metered-network</c> and <c>power-saving</c> are never sent: the person said not to use that network or battery.</summary>
    private static readonly Dictionary<string, string> Words = new(StringComparer.Ordinal)
    {
        [Traffic] = "daily traffic limit",
        ["requests"] = "daily request limit",
        [Disk] = "storage limit",
        [Busy] = "busy with other transfers",
        ["device-traffic"] = "daily traffic limit of the whole device",
        ["device-disk"] = "storage limit of the whole device",
        ["paused-by-you"] = "paused by hand",
        [Other] = "a limit set on the device",
    };

    /// <summary>The closed token for the reason a peer sent: null when it sent none, <see cref="Other"/> when it sent one this build does not know.</summary>
    public static string? Reason(string? declared)
    {
        var text = declared?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length <= 24 && Words.ContainsKey(text) && text != Other ? text : Other;
    }

    /// <summary>
    /// The time the pause ends, as an instant, and the device's offset from UTC in minutes when it said its own local time (<c>2026-10-22T00:00:00+03:00</c>):
    /// then the hub can say "00:00 device time". A time that is not an instant, or is more than a day in the past or a week ahead, is left out (the pause stays).
    /// </summary>
    public static (DateTime? UntilUtc, int? OffsetMinutes) Until(string? declared, DateTime nowUtc)
    {
        var text = declared?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 40
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            return (null, null);
        var utc = at.UtcDateTime;
        if (utc < nowUtc.AddDays(-1) || utc > nowUtc.Add(Fresh)) return (null, null);
        return (utc, at.Offset == TimeSpan.Zero ? null : (int)at.Offset.TotalMinutes);
    }

    /// <summary>
    /// "Paused by the device's owner: daily traffic limit (until 00:00 device time)". The end is told in the device's own clock when it said its offset, else
    /// in UTC; within a day it is the time alone, later the date too. A pause whose end has passed is told without one. With <paramref name="byNode"/> it is a
    /// blind NODE that said no to this node's own call (a server node's storage or daily traffic limit, <see cref="PeerPausedException"/>): "Paused by the blind
    /// node's owner: storage limit". <see cref="Busy"/> is not a pause anybody set - the node's room is held by other transfers for a moment - so it is never "paused by
    /// ... owner": "Busy with other transfers right now; asked again in about a minute" (a copy's own report of it, which no app sends, has no "asked again": that is this node's).
    /// </summary>
    public static string Sentence(string reason, DateTime? untilUtc, int? offsetMinutes, DateTime nowUtc, bool byNode = false)
    {
        if (reason == Busy) return byNode ? "Busy with other transfers right now; asked again in about a minute" : "Busy with other transfers right now";
        var words = byNode
            ? reason switch { Disk => "storage limit", Traffic => "daily traffic limit", _ => "a limit set on the node" }
            : Words.TryGetValue(reason, out var w) ? w : Words[Other];
        var text = (byNode ? "Paused by the blind node's owner: " : "Paused by the device's owner: ") + words;
        if (untilUtc is not { } until || until <= nowUtc) return text;

        var shift = TimeSpan.FromMinutes(offsetMinutes ?? 0);
        var shown = until + shift;
        var format = until - nowUtc <= TimeSpan.FromHours(24) ? "HH:mm" : "yyyy-MM-dd HH:mm";
        return text + " (until " + shown.ToString(format, CultureInfo.InvariantCulture) + (offsetMinutes is null ? " UTC" : " device time") + ")";
    }
}
