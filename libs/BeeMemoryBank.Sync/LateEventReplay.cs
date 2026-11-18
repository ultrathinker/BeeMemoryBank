using System.Reflection;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Models;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Events a node stored while it ran a build that did not know their type, applied once it does.
/// <para>The applier stores an event of an unknown type and skips it (forward compatibility). From this build on it also records the event in
/// the node-local <c>tbl_migration_marker</c> (key <see cref="UnknownMarkerPrefix"/> + event id, value the type): no schema change, and the
/// table never leaves the node. At every start, after the migrations and the stored-event repairs, every recorded event whose type this build
/// knows is applied again (<see cref="EventApplier.ReplayStoredAsync"/>), in log order, and its record removed. A row that waits or was
/// quarantined (apply_state other than applied) keeps its verdict; a row compaction removed is simply gone; a failure that may heal (an
/// originator or rotation still on its way) keeps the record for the next start; any other failure removes it, with a warning.</para>
/// <para>A node updated from 2.10 or 2.11 recorded nothing. Once, at the first start of this build, every stored event of a type introduced in
/// 2.12 (<see cref="IntroducedIn212"/>) is recorded the same way, so the replay above takes it (marker <see cref="Upgrade212MarkerKey"/>).</para>
/// <para>those events never passed a Live check (the older build ran none), so they are recorded with
/// <see cref="LiveCheckSuffix"/> and replayed under the Live rule, the identity re-keys and their confirms first (still in log order among
/// themselves), so every other event is judged against the keys as they are after them. One signed by a retired key that its author's rotation does
/// not acknowledge is quarantined (hold reason <see cref="Metadata.HoldReasons.RetiredKey"/>) and raises the retired-key attention flag, so a
/// removal's S5 does not close over it. An event recorded as unknown by this build or a later one keeps the rule it was given at receipt.</para>
/// </summary>
public static class LateEventReplay
{
    public const string UnknownMarkerPrefix = "unknown-event:";
    public const string Upgrade212MarkerKey = "late-event-replay-2.12";

    /// <summary>The suffix of a record's value (after the type) for an event that never passed a Live check: replayed under the Live rule.</summary>
    public const string LiveCheckSuffix = "|live";

    /// <summary>Replayed first: the key events every other event of the one-time population is judged against.</summary>
    private static readonly IReadOnlySet<string> KeyTypes = new HashSet<string>(StringComparer.Ordinal) { EventTypes.NodeKeyRotated, EventTypes.NodeKeyConfirm };

    /// <summary>
    /// The event types of release 2.12 that a 2.10 or 2.11 node stored without applying. THE one list a later type of 2.12 extends. The removal of a
    /// superadmin's three are here too: a node that was 2.10 or 2.11 at a removal's S0 applies the tombstone, the process and the request for
    /// new identity keys at its first start. No build with this list but without them has shipped, so they ride the same one-time marker.
    /// </summary>
    public static readonly IReadOnlyList<string> IntroducedIn212 =
    [
        EventTypes.NodeState,
        EventTypes.NodeKeyRotated,
        EventTypes.NodeKeyConfirm,
        EventTypes.AccountsHomeSet,
        EventTypes.SuperadminDoorsSet,
        EventTypes.PersonRemoved,
        EventTypes.OffboardingState,
        EventTypes.NodeRekeyRequest,
    ];

    /// <summary>
    /// The types this build applies: every <see cref="EventTypes"/> constant but the outer type of a sealed event, which is always opened to its
    /// inner type before the applier dispatches (a guard test keeps every one of them a case of the dispatch).
    /// </summary>
    public static IReadOnlySet<string> KnownTypes { get; } = typeof(EventTypes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f is { IsLiteral: true, FieldType.Name: nameof(String) })
        .Select(f => (string)f.GetRawConstantValue()!)
        .Where(t => t != EventTypes.Sealed)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>Records an event the applier skipped because its type is unknown here (called from the dispatch's default branch).</summary>
    public static async Task RecordUnknownAsync(IDbConnectionFactory db, Guid eventId, string eventType)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync("INSERT OR IGNORE INTO tbl_migration_marker (key, value, set_at) VALUES (@k, @v, @now)",
            new { k = UnknownMarkerPrefix + eventId.ToString("D"), v = eventType, now = DateTime.UtcNow.ToString("O") });
    }

    /// <returns>The number of events applied again.</returns>
    public static async Task<int> RunAsync(IServiceScopeFactory scopes, ILogger logger, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        if (sp.GetService<EventApplier>() is not { } applier) return 0;
        return await RunAsync(sp.GetRequiredService<IDbConnectionFactory>(), sp.GetRequiredService<INodeIdentityRepository>(), applier,
            sp.GetRequiredService<IEventLogRepository>(), logger, ct);
    }

    /// <summary>The replay with its collaborators given (what a start resolves from its container).</summary>
    public static async Task<int> RunAsync(IDbConnectionFactory db, INodeIdentityRepository nodes, EventApplier applier, IEventLogRepository log,
        ILogger logger, CancellationToken ct = default)
    {
        if (await nodes.GetAsync() is not { } self) return 0;

        await RecordStoredEventsOf212Async(db, self.NodeId, logger);

        List<(string Key, string Value, long? Sequence, int? ApplyState, string? NodeId, string? HoldReason)> rows;
        using (var conn = db.CreateConnection())
            rows = (await conn.QueryAsync<(string, string, long?, int?, string?, string?)>(
                @"SELECT m.key, m.value, e.sequence_num, e.apply_state, e.node_id, e.hold_reason
                  FROM tbl_migration_marker m
                  LEFT JOIN tbl_event e ON e.event_id = substr(m.key, @from) COLLATE NOCASE
                  WHERE m.key LIKE @prefix
                  ORDER BY e.sequence_num",
                new { from = UnknownMarkerPrefix.Length + 1, prefix = UnknownMarkerPrefix + "%" })).ToList();
        // Log order, the key events first (a stable sort keeps the log order within each group).
        var recorded = rows
            .Select(r => (r.Key, Live: r.Value.EndsWith(LiveCheckSuffix, StringComparison.Ordinal), r.Sequence, r.ApplyState, Raw: r.Value, r.NodeId, r.HoldReason))
            .Select(r => (r.Key, Type: r.Live ? r.Raw[..^LiveCheckSuffix.Length] : r.Raw, r.Live, r.Sequence, r.ApplyState, r.NodeId, r.HoldReason))
            .OrderBy(r => KeyTypes.Contains(r.Type) ? 0 : 1)
            .ToList();

        var applied = 0;
        foreach (var (key, type, live, sequence, applyState, nodeId, holdReason) in recorded)
        {
            ct.ThrowIfCancellationRequested();
            if (!KnownTypes.Contains(type)) continue;                       // still unknown here: kept for a later build
            var id = key[UnknownMarkerPrefix.Length..];
            if (sequence is null || applyState != EventApplyStates.Applied)  // compacted away, or held / quarantined: its verdict stands
            {
                // A row this replay quarantined for a retired key while the record still stands: an older build of this replay crashed
                // between the quarantine and the flag. The flag is raised now, before the record goes (P11 O2).
                if (applyState == EventApplyStates.QuarantinedAfterReceipt && holdReason == Metadata.HoldReasons.RetiredKey
                    && !SignedAsThisNode.PrunedOnTheirOwn.Contains(type, StringComparer.Ordinal) && Guid.TryParse(nodeId, out var author))
                    await RetiredKeyAttention.RaiseAsync(db, author, 1, onlyIfAbsent: true);
                await ForgetAsync(db, key);
                continue;
            }
            var evt = await log.GetByIdAsync(id);
            if (evt is null)
            {
                await ForgetAsync(db, key);
                continue;
            }
            if (evt.NodeId == self.NodeId)                                  // this node's own: applied when it was written
            {
                await ForgetAsync(db, key);
                continue;
            }
            try
            {
                await applier.ReplayStoredAsync(evt, ct, liveCheck: live);
                await ForgetAsync(db, key);
                applied++;
            }
            catch (RetiredKeyUnacknowledgedException)
            {
                // Never applied: quarantined with a plain reason, and the flag raised (unless the scan at the rotation counted it already). A type its
                // author prunes on its own proves nothing by its absence from the rotation's list: not applied, but no flag (as the scan).
                // (The reason is kept in hold_reason, which the repository's setters keep for held rows only.) The flag goes first: a crash
                // after it leaves the row applied and its record, so the next start quarantines it again; a crash after the quarantine finds
                // the flag already up (P11 O2).
                if (!SignedAsThisNode.PrunedOnTheirOwn.Contains(type, StringComparer.Ordinal))
                    await RetiredKeyAttention.RaiseAsync(db, evt.NodeId, 1, onlyIfAbsent: true);
                using (var conn = db.CreateConnection())
                    await conn.ExecuteAsync("UPDATE tbl_event SET apply_state = @q, hold_reason = @reason, held_at = NULL WHERE sequence_num = @s",
                        new { q = EventApplyStates.QuarantinedAfterReceipt, reason = Metadata.HoldReasons.RetiredKey, s = sequence.Value });
                await ForgetAsync(db, key);
                logger.LogWarning("Stored event {EventId} ({Type}) of node {NodeId} is signed by a retired identity key its rotation does not acknowledge: quarantined, not applied",
                    id, type, evt.NodeId);
            }
            catch (Exception ex) when (SyncFailureClassifier.Classify(ex) == SyncFailureKind.Deferred || ex is SessionLockedException)
            {
                logger.LogInformation("Stored event {EventId} ({Type}) waits for its replay until the next start ({Reason})", id, type, ex.GetType().Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await ForgetAsync(db, key);
                logger.LogWarning("Stored event {EventId} ({Type}) was not applied on its replay ({Reason})", id, type, ex.GetType().Name);
            }
        }
        if (applied > 0)
            logger.LogInformation("Applied {Count} stored event(s) of a type an older build did not know", applied);
        return applied;
    }

    /// <summary>Once per node: records every stored event of a 2.12 type, in log order, for the replay (an older build recorded nothing).</summary>
    private static async Task RecordStoredEventsOf212Async(IDbConnectionFactory db, Guid self, ILogger logger)
    {
        using var conn = db.CreateConnection();
        if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_migration_marker WHERE key = @k", new { k = Upgrade212MarkerKey }) > 0)
            return;
        using var tx = conn.BeginTransaction();
        var rows = (await conn.QueryAsync<(string EventId, string EventType)>(
            @"SELECT event_id, event_type FROM tbl_event
              WHERE event_type IN @types AND apply_state = @applied AND node_id <> @self COLLATE NOCASE
              ORDER BY sequence_num",
            new { types = IntroducedIn212, applied = EventApplyStates.Applied, self = self.ToString("D") }, tx)).ToList();
        var now = DateTime.UtcNow.ToString("O");
        foreach (var (eventId, eventType) in rows)
            await conn.ExecuteAsync("INSERT OR IGNORE INTO tbl_migration_marker (key, value, set_at) VALUES (@k, @v, @now)",
                new { k = UnknownMarkerPrefix + (Guid.TryParse(eventId, out var g) ? g.ToString("D") : eventId.ToLowerInvariant()), v = eventType + LiveCheckSuffix, now }, tx);
        await conn.ExecuteAsync("INSERT INTO tbl_migration_marker (key, value, set_at) VALUES (@k, @v, @now)",
            new { k = Upgrade212MarkerKey, v = rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), now }, tx);
        tx.Commit();
        if (rows.Count > 0)
            logger.LogInformation("{Count} stored event(s) of release 2.12 types are applied again at this first start of the new build", rows.Count);
    }

    private static async Task ForgetAsync(IDbConnectionFactory db, string key)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync("DELETE FROM tbl_migration_marker WHERE key = @key", new { key });
    }
}
