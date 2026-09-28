using BeeMemoryBank.Core.Interfaces;
using Dapper;

namespace BeeMemoryBank.Sync.Recovery;

/// <summary>
/// A recovery box that is no longer active keeps no key material on this node (F7). A password change
/// does not change the DEK, so a superseded or retired box still opens to the current key under the
/// password it was made with. Kept, its bytes would let the OLD password recover the vault from this
/// database or any copy of it made later (a blind node's backups), which is exactly what the change
/// was meant to end.
///
/// <para>Two places hold the bytes: the row in <c>tbl_recovery_box</c>, and the recovery_box_set event
/// in <c>tbl_event</c>, which this node also serves to every peer that pulls. The row stays, without
/// its salt, wrap and IV: its id and version are what keep a late or resent event of the same box from
/// coming back as active (see the applier), and its status and <c>retired_by_box_id</c> stay for the
/// record. The event goes: nothing reads a logged box event again, and a peer that never got it loses
/// nothing, because the box it describes can never become active again anywhere.</para>
///
/// <para>Both writes run with <c>secure_delete</c>, and the file scrub of <see cref="StoredEventRepair"/>
/// is marked as owed in the same transaction: the sync scheduler finishes it (WAL truncate, VACUUM), so
/// neither a free page nor an old WAL frame keeps the bytes. Copies made while a box was still active
/// are out of reach, as the plan accepts (6.9).</para>
///
/// <para>Idempotent and cheap when there is nothing to do. It runs at startup, for what an older build left: the
/// applier itself clears a box and its event in the same transaction that makes the box inactive, and never logs an
/// inactive box's event (see EventApplier.Recovery).</para>
/// </summary>
public static class RecoveryBoxMaterialPurge
{
    /// <returns>The rows cleared plus the events removed.</returns>
    public static async Task<int> RunAsync(IDbConnectionFactory db)
    {
        using var conn = db.CreateConnection();
        await conn.ExecuteAsync("PRAGMA secure_delete = ON");
        using var tx = conn.BeginTransaction();

        var rows = await conn.ExecuteAsync(
            @"UPDATE tbl_recovery_box SET salt = X'', wrapped = X'', iv = X''
              WHERE status <> 'A' AND (length(salt) > 0 OR length(wrapped) > 0 OR length(iv) > 0)",
            transaction: tx);

        // The events of a box that is here and inactive; and the events whose row an older build trimmed past the
        // ten-row limit (release-a2 review, spec #2), recognised by content: no row with that id, and an active box of
        // the same author and kind with a later timestamp, so the event's box can never be active again. An event
        // whose row is missing for any other reason (one a restore logged before applying it) is kept.
        var events = await conn.ExecuteAsync(
            @"DELETE FROM tbl_event
              WHERE event_type = @Type
                AND (EXISTS (SELECT 1 FROM tbl_recovery_box b
                             WHERE b.status <> 'A' AND b.box_id = (CASE WHEN json_valid(tbl_event.payload)
                                 THEN json_extract(tbl_event.payload, '$.box_id') END) COLLATE NOCASE)
                  OR (NOT EXISTS (SELECT 1 FROM tbl_recovery_box b
                                  WHERE b.box_id = (CASE WHEN json_valid(tbl_event.payload)
                                      THEN json_extract(tbl_event.payload, '$.box_id') END) COLLATE NOCASE)
                      AND EXISTS (SELECT 1 FROM tbl_recovery_box a
                                  WHERE a.status = 'A' AND a.lamport_ts > tbl_event.lamport_ts
                                    AND a.author_node_id = (CASE WHEN json_valid(tbl_event.payload)
                                        THEN json_extract(tbl_event.payload, '$.author_node_id') END) COLLATE NOCASE
                                    AND a.kind = (CASE WHEN json_valid(tbl_event.payload)
                                        THEN json_extract(tbl_event.payload, '$.kind') END))))",
            new { Type = EventTypes.RecoveryBoxSet }, tx);

        if (rows + events > 0)
        {
            var now = DateTime.UtcNow.ToString("O");
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_blind_state (key, value, updated_at) VALUES (@key, @now, @now)
                  ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at",
                new { key = StoredEventRepair.CleanupPendingKey, now }, tx);
        }

        tx.Commit();
        return rows + events;
    }
}
