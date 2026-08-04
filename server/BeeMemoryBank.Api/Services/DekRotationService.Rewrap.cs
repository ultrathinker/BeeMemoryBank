using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services;

public partial class DekRotationService
{
    /// <summary>
    /// Shared destructive core for both initiator Accept and peer AutoAccept paths.
    /// Opens a single atomic transaction, re-wraps all encrypted DEKs in 4 tables,
    /// deletes agents, handles slot cleanup (initiator: re-wrap own slot + delete others;
    /// auto-accept: delete recovery slots only), updates sentinel + epoch + state, commits,
    /// then swaps the in-memory master DEK and marks progress Completed.
    /// Returns (agentsDeleted, slotsDeleted) for caller-side logging.
    /// </summary>
    private async Task<(int agentsDeleted, int slotsDeleted)> RewrapDestructiveCoreAsync(
        byte[] oldDek, byte[] newDek, int newEpoch, string commitEventId,
        bool isInitiator,
        int? initiatorSlotId = null,
        byte[]? newWrappedSlotDek = null,
        byte[]? newWrappedSlotIv = null)
    {
        int agentsDeleted = 0;
        int slotsDeleted = 0;

        _progress.Update(DekRotationFlowStep.ReWrappingPerItem, 20,
            isInitiator ? "Re-wrapping article bodies..." : "Auto-accept: re-wrapping article bodies...");

        using var conn = _connFactory.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            ReWrapTableAsync(conn, tx, "tbl_article_body", "article_id", "encrypted_dek", "dek_iv", oldDek, newDek);
            _progress.Update(DekRotationFlowStep.ReWrappingPerItem, 35,
                isInitiator ? "Re-wrapping article versions..." : "Auto-accept: re-wrapping article versions...");

            ReWrapTableAsync(conn, tx, "tbl_article_version", "id", "encrypted_dek", "dek_iv", oldDek, newDek);
            _progress.Update(DekRotationFlowStep.ReWrappingPerItem, 50,
                isInitiator ? "Re-wrapping conflict versions..." : "Auto-accept: re-wrapping conflict versions...");

            ReWrapTableAsync(conn, tx, "tbl_conflict_version", "id", "encrypted_dek", "dek_iv", oldDek, newDek);
            _progress.Update(DekRotationFlowStep.ReWrappingPerItem, 65,
                isInitiator ? "Re-wrapping media..." : "Auto-accept: re-wrapping media...");

            ReWrapTableAsync(conn, tx, "tbl_media", "id", "encrypted_dek", "dek_iv", oldDek, newDek);

            _progress.Update(DekRotationFlowStep.InvalidatingAgents, 75,
                isInitiator ? "Invalidating agents..." : "Auto-accept: invalidating agents...");

            // --- tbl_agent: agents hold API keys encrypted with the old DEK; server
            // cannot re-wrap them (no access to plaintext keys). Delete all agents.
            agentsDeleted = await conn.ExecuteAsync("DELETE FROM tbl_agent", transaction: tx);

            if (isInitiator)
            {
                _progress.Update(DekRotationFlowStep.InvalidatingSlots, 80,
                    "Re-wrapping initiator key slot, removing others...");

                await conn.ExecuteAsync(
                    "UPDATE tbl_key_slot SET encrypted_master_dek = @encDek, iv = @iv WHERE slot_id = @slotId",
                    new { encDek = newWrappedSlotDek, iv = newWrappedSlotIv, slotId = initiatorSlotId!.Value }, tx);

                slotsDeleted = await conn.ExecuteAsync(
                    "DELETE FROM tbl_key_slot WHERE slot_id <> @slotId",
                    new { slotId = initiatorSlotId!.Value }, tx);

                await conn.ExecuteAsync(
                    "UPDATE tbl_user SET key_slot_id = NULL WHERE key_slot_id IS NOT NULL AND key_slot_id <> @slotId",
                    new { slotId = initiatorSlotId!.Value }, tx);
            }
            else
            {
                _progress.Update(DekRotationFlowStep.InvalidatingAgents, 80,
                    "Auto-accept: removing recovery key slots...");

                // os_auto_unlock must be invalidated here too, not just recovery: this node's
                // DPAPI secret file is unchanged, but its slot still wraps the OLD (now invalid)
                // DEK, and the server has no access to the plaintext secret to re-wrap it here.
                // Leaving it would make IsEnabledAsync() report "enabled" while auto-unlock
                // silently fails sentinel verification and returns false forever afterward — a
                // Codex-reviewed finding (leaving the feature enabled-looking but non-functional
                // is worse than requiring the admin to notice and re-enable it).
                slotsDeleted = await conn.ExecuteAsync(
                    "DELETE FROM tbl_key_slot WHERE slot_type IN ('recovery', 'os_auto_unlock')", transaction: tx);
            }

            _progress.Update(DekRotationFlowStep.Finalizing, 85,
                isInitiator ? "Updating sentinel and epoch..." : "Auto-accept: updating sentinel and epoch...");

            var newSentinel = MasterKeyManager.ComputeSentinel(newDek);
            await conn.ExecuteAsync(
                "UPDATE tbl_node_identity SET sentinel_value = @sentinel, dek_epoch = @epoch",
                new { sentinel = newSentinel, epoch = newEpoch }, tx);

            // Mark Applied INSIDE the rotation tx. If the process crashes between
            // tx.Commit() and the swap, the DB+state agree (Applied + new sentinel);
            // the startup sweep won't mark this as Failed.
            await conn.ExecuteAsync(
                @"UPDATE tbl_dek_rotation_state SET state = @state, applied_at = @now, updated_at = @now
                  WHERE event_id = @eventId",
                new { state = DekRotationState.Applied.ToString().ToUpperInvariant(), now = DateTime.UtcNow.ToString("O"), eventId = commitEventId },
                tx);

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        _sessionService.SwapMasterDek(newDek);
        // Do NOT clear newDek — ownership transferred to SessionService.
        Array.Clear(oldDek, 0, oldDek.Length);

        var completedMsg = isInitiator
            ? $"DEK rotation completed. Epoch {newEpoch - 1}\u2192{newEpoch}. Agents invalidated: {agentsDeleted}."
            : $"DEK rotation auto-accept completed. Epoch {newEpoch - 1}\u2192{newEpoch}. Agents invalidated: {agentsDeleted}. Recovery slots removed: {slotsDeleted}.";
        _progress.Update(DekRotationFlowStep.Completed, 100, completedMsg);
        _progress.ClearError();

        return (agentsDeleted, slotsDeleted);
    }

    /// <summary>
    /// Builds AAD for a per-row DEK wrap. Format must match the encrypt-side AAD used
    /// when the row was created. For Wave 1 v=1 rows, AAD includes a table-specific
    /// prefix and the row's primary-key bytes (article_id / media_id). For v=0 rows
    /// (legacy plaintext wrap) returns null — DekManager.UnwrapDek handles that path.
    /// </summary>
    private static byte[]? BuildPerRowAadForTable(string tableName, string pk, byte[] wrapped)
    {
        // v=0 legacy: exactly 48 bytes, no version prefix → no AAD
        if (wrapped.Length == 48) return null;
        // Anything else: assume v=1 (49 bytes with 0x01 prefix). DekManager validates strictly.
        var prefix = tableName switch
        {
            "tbl_article_body" => "bmb-art-dek"u8.ToArray(),
            "tbl_article_version" => "bmb-art-dek"u8.ToArray(),
            "tbl_conflict_version" => "bmb-art-dek"u8.ToArray(),
            "tbl_media" => "bmb-media-dek"u8.ToArray(),
            _ => null
        };
        if (prefix == null) return null;
        // PK is the article_id / media_id GUID (string form). Convert back to bytes via Guid.
        if (!Guid.TryParse(pk, out var pkGuid))
        {
            // Some tables (tbl_article_version, tbl_conflict_version) use a separate row id
            // as PK, not articleId. Their AAD scheme would need the parent article_id.
            // For now skip AAD on those — UnwrapDek will fall through to legacy path on
            // length 48; for length-49 v=1 rows the unwrap will throw and the caller can decide.
            return null;
        }
        var pkBytes = pkGuid.ToByteArray();
        var aad = new byte[prefix.Length + pkBytes.Length];
        prefix.CopyTo(aad, 0);
        pkBytes.CopyTo(aad, prefix.Length);
        return aad;
    }

    private static void ReWrapTableAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        string tableName,
        string pkColumn,
        string dekColumn,
        string dekIvColumn,
        byte[] oldDek,
        byte[] newDek)
    {
        // Roadmap p7: keyset pagination instead of OFFSET. SQLite scans+discards rows on
        // OFFSET, making each batch progressively slower (O(n²) for the whole rewrap). Keyset
        // (WHERE pk > @lastPk ORDER BY pk LIMIT N) is O(n) total. PK columns here are TEXT
        // (article_id, id) — no special collation needed since rows we just UPDATE'd retain
        // their PK values, ORDER BY pk is stable across the batch.
        const int batchSize = 500;
        string? lastPk = null;
        while (true)
        {
            var sql = lastPk == null
                ? $"SELECT [{pkColumn}] AS pk, [{dekColumn}] AS enc_dek, [{dekIvColumn}] AS dek_iv FROM [{tableName}] ORDER BY [{pkColumn}] LIMIT @limit"
                : $"SELECT [{pkColumn}] AS pk, [{dekColumn}] AS enc_dek, [{dekIvColumn}] AS dek_iv FROM [{tableName}] WHERE [{pkColumn}] > @lastPk ORDER BY [{pkColumn}] LIMIT @limit";

            var rows = conn.Query<dynamic>(sql, new { limit = batchSize, lastPk }, tx).ToList();
            if (rows.Count == 0) break;

            foreach (var row in rows)
            {
                var encDek = (byte[])row.enc_dek;
                var dekIv = (byte[])row.dek_iv;
                var pk = (string)row.pk;

                // Hold plainDek in try/finally so an exception from Wrap or Execute can't leak
                // the per-item DEK on the heap. Use DekManager (per-row AAD) — these are
                // article/media DEKs, not master DEKs. AAD format depends on the table.
                var aad = BuildPerRowAadForTable(tableName, pk, encDek);
                var plainDek = DekManager.UnwrapDek(encDek, dekIv, oldDek, aad);
                try
                {
                    var (newEnc, newIv) = DekManager.WrapDek(plainDek, newDek, aad);
                    conn.Execute(
                        $"UPDATE [{tableName}] SET [{dekColumn}] = @enc, [{dekIvColumn}] = @iv WHERE [{pkColumn}] = @pk",
                        new { enc = newEnc, iv = newIv, pk },
                        tx);
                }
                finally
                {
                    Array.Clear(plainDek, 0, plainDek.Length);
                }

                lastPk = pk;
            }
        }
    }
}
