using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Dapper;

namespace BeeMemoryBank.Sync.Metadata;

/// <summary>
/// Received-but-not-applied events of this node, read from tbl_event (docs/design/step-a-spec.md 6.6, 6.8, 6.9). The same
/// reading serves every host: a blind host stores sealed events relayed (state 2), never held, so it always reports none.
/// </summary>
public sealed class HeldEventStatus(IDbConnectionFactory connFactory) : IHeldEventStatus
{
    /// <summary>
    /// What an operation refused while events are held says (spec 6.8): the cause, and "unlock and wait". Compaction would
    /// cut rows not applied yet, a rotation or a restore would change the vault under them, an activation would switch with
    /// changes still unread.
    /// </summary>
    /// <summary>Spec 6.9: the status warns when more than this many events are held, or the oldest waits longer than <see cref="WarnAge"/>.</summary>
    public const int WarnCount = 100_000;
    public static readonly TimeSpan WarnAge = TimeSpan.FromDays(7);

    public static bool NeedsWarning(HeldEventSummary summary, DateTime utcNow) =>
        summary.Held > WarnCount || (summary.OldestHeldAt is { } oldest && utcNow - oldest > WarnAge);

    /// <summary>The status line for held events (Web, <c>bmb status</c>), or null when none is held.</summary>
    public static string? Describe(HeldEventSummary summary, DateTime utcNow)
    {
        if (summary.Held == 0) return null;
        var why = summary.HeldLocked > 0 ? " until this node is unlocked"
            : summary.HeldKey > 0 ? " until their metadata key arrives"
            : "";
        var line = $"{summary.Held} {(summary.Held == 1 ? "change waits" : "changes wait")} to be applied{why}.";
        return NeedsWarning(summary, utcNow)
            ? line + " They have waited long or are very many: unlock this node and let it sync."
            : line;
    }

    public static string RefusalMessage(string action) =>
        "This node holds changes it received but has not applied yet (it was locked, or a metadata key has not arrived). " +
        $"Unlock it and wait until they are applied, then {action}.";

    public async Task<bool> AnyHeldAsync(CancellationToken ct = default)
    {
        using var conn = connFactory.CreateConnection();
        return await conn.ExecuteScalarAsync<long>(
            $"SELECT EXISTS (SELECT 1 FROM tbl_event WHERE apply_state = {EventApplyStates.Held})") == 1;
    }

    public async Task<HeldEventSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        using var conn = connFactory.CreateConnection();
        var row = await conn.QuerySingleAsync<SummaryRow>(
            $@"SELECT
                 COALESCE(SUM(CASE WHEN apply_state = {EventApplyStates.Held} THEN 1 ELSE 0 END), 0) AS Held,
                 COALESCE(SUM(CASE WHEN apply_state = {EventApplyStates.Held} AND hold_reason = '{HoldReasons.Locked}' THEN 1 ELSE 0 END), 0) AS HeldLocked,
                 COALESCE(SUM(CASE WHEN apply_state = {EventApplyStates.Held} AND hold_reason = '{HoldReasons.Key}' THEN 1 ELSE 0 END), 0) AS HeldKey,
                 COALESCE(SUM(CASE WHEN apply_state = {EventApplyStates.Held} AND hold_reason = '{HoldReasons.Order}' THEN 1 ELSE 0 END), 0) AS HeldOrder,
                 MIN(CASE WHEN apply_state = {EventApplyStates.Held} THEN held_at END) AS OldestHeldAt,
                 MIN(CASE WHEN apply_state = {EventApplyStates.Held} THEN sequence_num END) AS FirstHeld,
                 COALESCE(MAX(sequence_num), 0) AS Head,
                 COALESCE(SUM(CASE WHEN apply_state = {EventApplyStates.QuarantinedAfterReceipt} THEN 1 ELSE 0 END), 0) AS Quarantined
               FROM tbl_event");
        DateTime? oldest = DateTime.TryParse(row.OldestHeldAt, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var at) ? at : null;
        // Applied position (spec 6.6): every row up to here is applied; the head when nothing waits.
        var appliedThrough = row.FirstHeld is { } first ? first - 1 : row.Head;
        return new HeldEventSummary((int)row.Held, (int)row.HeldLocked, (int)row.HeldKey, (int)row.HeldOrder, oldest,
            appliedThrough, row.Head, (int)row.Quarantined);
    }

    private sealed class SummaryRow
    {
        public long Held { get; set; }
        public long HeldLocked { get; set; }
        public long HeldKey { get; set; }
        public long HeldOrder { get; set; }
        public string? OldestHeldAt { get; set; }
        public long? FirstHeld { get; set; }
        public long Head { get; set; }
        public long Quarantined { get; set; }
    }
}
