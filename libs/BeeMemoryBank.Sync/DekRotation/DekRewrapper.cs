using System;
using System.Linq;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Sync.DekRotation;

/// <summary>
/// The destructive half of a DEK rotation: re-wrap every key-bearing row under the new master DEK,
/// invalidate agents and stale key slots, bump the sentinel and epoch, commit, then swap the
/// in-memory DEK.
///
/// <para>
/// Lives in Sync, not the API project, because it is not server-only work. Every node in a cluster
/// has to perform this same rewrap when a peer rotates — including mobile and CLI nodes, which have
/// no API layer. While this code was API-private, those hosts fell back to a no-op applier: they
/// logged a warning, stayed on the retired DEK forever, and every article that arrived afterwards
/// was wrapped under a key they did not have. Two copies of a routine this dangerous is not an
/// option either, so the server calls exactly this one.
/// </para>
/// </summary>
public static class DekRewrapper
{
    private static void Report(Action<DekRotationFlowStep, int, string>? progress,
        DekRotationFlowStep step, int pct, string message) => progress?.Invoke(step, pct, message);

    public static async Task<(int agentsDeleted, int slotsDeleted)> RewrapAllAsync(
        DbConnectionFactory connFactory,
        SessionService sessionService,
        byte[] oldDek, byte[] newDek, int newEpoch, string commitEventId,
        bool isInitiator,
        int? initiatorSlotId = null,
        byte[]? newWrappedSlotDek = null,
        byte[]? newWrappedSlotIv = null,
        Action<DekRotationFlowStep, int, string>? progress = null)
    {
        int agentsDeleted = 0;
        int slotsDeleted = 0;

        Report(progress, DekRotationFlowStep.ReWrappingPerItem, 20,
            isInitiator ? "Re-wrapping article bodies..." : "Auto-accept: re-wrapping article bodies...");

        using var conn = connFactory.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            ReWrapTableAsync(conn, tx, "tbl_article_body", "article_id", "encrypted_dek", "dek_iv", oldDek, newDek);
            Report(progress, DekRotationFlowStep.ReWrappingPerItem, 35,
                isInitiator ? "Re-wrapping article versions..." : "Auto-accept: re-wrapping article versions...");

            // aadIdColumn = "article_id": version/conflict rows are keyed by their own GUID but
            // carry a copy of the ARTICLE's wrapped DEK, so the AAD is the article's.
            ReWrapTableAsync(conn, tx, "tbl_article_version", "id", "encrypted_dek", "dek_iv", oldDek, newDek,
                aadIdColumn: "article_id");
            Report(progress, DekRotationFlowStep.ReWrappingPerItem, 50,
                isInitiator ? "Re-wrapping conflict versions..." : "Auto-accept: re-wrapping conflict versions...");

            ReWrapTableAsync(conn, tx, "tbl_conflict_version", "id", "encrypted_dek", "dek_iv", oldDek, newDek,
                aadIdColumn: "article_id");
            Report(progress, DekRotationFlowStep.ReWrappingPerItem, 65,
                isInitiator ? "Re-wrapping media..." : "Auto-accept: re-wrapping media...");

            ReWrapTableAsync(conn, tx, "tbl_media", "id", "encrypted_dek", "dek_iv", oldDek, newDek);

            Report(progress, DekRotationFlowStep.ReWrappingPerItem, 70,
                isInitiator ? "Re-wrapping projection matrix..." : "Auto-accept: re-wrapping projection matrix...");

            ReWrapProjectionMatrix(conn, tx, oldDek, newDek);

            Report(progress, DekRotationFlowStep.InvalidatingAgents, 75,
                isInitiator ? "Invalidating agents..." : "Auto-accept: invalidating agents...");

            // --- tbl_agent: agents hold API keys encrypted with the old DEK; server
            // cannot re-wrap them (no access to plaintext keys). Delete all agents.
            agentsDeleted = await conn.ExecuteAsync("DELETE FROM tbl_agent", transaction: tx);

            if (isInitiator)
            {
                Report(progress, DekRotationFlowStep.InvalidatingSlots, 80,
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
                Report(progress, DekRotationFlowStep.InvalidatingAgents, 80,
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

            Report(progress, DekRotationFlowStep.Finalizing, 85,
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

        sessionService.SwapMasterDek(newDek);
        // Do NOT clear newDek — ownership transferred to SessionService.
        Array.Clear(oldDek, 0, oldDek.Length);

        var completedMsg = isInitiator
            ? $"DEK rotation completed. Epoch {newEpoch - 1}\u2192{newEpoch}. Agents invalidated: {agentsDeleted}."
            : $"DEK rotation auto-accept completed. Epoch {newEpoch - 1}\u2192{newEpoch}. Agents invalidated: {agentsDeleted}. Recovery slots removed: {slotsDeleted}.";
        Report(progress, DekRotationFlowStep.Completed, 100, completedMsg);
        

        return (agentsDeleted, slotsDeleted);
    }

    /// <summary>
    /// Re-wraps the semantic-search projection matrix, which is sealed directly under the master
    /// DEK (ProjectionMatrix.Wrap, no AAD) rather than under a per-row DEK — so it is invisible to
    /// ReWrapTableAsync's encrypted_dek/dek_iv shape and needs its own pass.
    /// <para>
    /// Omitting it used to leave the matrix sealed under the RETIRED DEK after a successful
    /// rotation: EmbeddingProjectionService.LoadMatrixAsync unwraps with the current master DEK,
    /// so every semantic query and every background re-embed threw CryptographicException from
    /// then on, permanently and with no recovery path.
    /// </para>
    /// Runs inside the rotation transaction, so a failure here rolls the whole rotation back
    /// rather than leaving a half-rotated vault.
    /// </summary>
    internal static void ReWrapProjectionMatrix(
        System.Data.IDbConnection conn, System.Data.IDbTransaction tx, byte[] oldDek, byte[] newDek)
    {
        // Nodes that have never run semantic search have no row at all — nothing to do.
        var rows = conn.Query<dynamic>(
            "SELECT id AS id, encrypted_matrix AS enc, iv AS iv FROM tbl_projection_matrix", transaction: tx).ToList();

        foreach (var row in rows)
        {
            var enc = (byte[])row.enc;
            var iv = (byte[])row.iv;
            var id = (long)row.id;

            // UnwrapVersioned, not UnwrapDek: the payload is the serialized matrix (hundreds of
            // KB), not a 32-byte DEK, so UnwrapDek's exact-length dispatch would reject it.
            var plainMatrix = DekManager.UnwrapVersioned(enc, iv, oldDek);
            try
            {
                var (newEnc, newIv) = DekManager.WrapDek(plainMatrix, newDek);
                conn.Execute(
                    "UPDATE tbl_projection_matrix SET encrypted_matrix = @enc, iv = @iv WHERE id = @id",
                    new { enc = newEnc, iv = newIv, id }, tx);
            }
            finally
            {
                Array.Clear(plainMatrix, 0, plainMatrix.Length);
            }
        }
    }

    /// <summary>
    /// Builds AAD for a per-row DEK wrap. Format must match the encrypt-side AAD used
    /// when the row was created. For Wave 1 v=1 rows, AAD includes a table-specific
    /// prefix and the OWNING ENTITY's id bytes — the article_id for every article-scoped
    /// DEK, the media_id for media. For v=0 rows (legacy plaintext wrap) returns null —
    /// DekManager.UnwrapDek handles that path.
    /// </summary>
    /// <param name="aadId">
    /// The owning entity's id, NOT necessarily the row's primary key. For tbl_article_body
    /// and tbl_media the two coincide; for tbl_article_version / tbl_conflict_version the PK
    /// is a per-row GUID while the AAD is built from the parent article_id — those rows carry
    /// a byte-for-byte copy of the article body's wrapped DEK (ArticleService.UpdateAsync,
    /// EventApplier.Article's conflict paths), so they must be unwrapped with the article's
    /// AAD, exactly as every reader does (BeeReadTools.GetArticleVersion, VersionEndpoints).
    /// Deriving it from the row PK instead made rotation throw AuthenticationTagMismatch on
    /// any vault whose articles had ever been edited.
    /// </param>
    internal static byte[]? BuildPerRowAadForTable(string tableName, string aadId, byte[] wrapped)
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
        // aadId is the article_id / media_id GUID (string form). Convert back to bytes via Guid.
        if (!Guid.TryParse(aadId, out var pkGuid))
        {
            // Not a GUID at all — no AAD scheme applies. UnwrapDek falls through to the legacy
            // path on length 48; a length-49 v=1 row would throw, which is the correct signal
            // that the row's format is not what this table's scheme expects.
            return null;
        }
        var pkBytes = pkGuid.ToByteArray();
        var aad = new byte[prefix.Length + pkBytes.Length];
        prefix.CopyTo(aad, 0);
        pkBytes.CopyTo(aad, prefix.Length);
        return aad;
    }

    /// <param name="aadIdColumn">
    /// Column holding the id the per-row AAD is built from. Defaults to <paramref name="pkColumn"/>,
    /// which is correct only where the row IS the entity (tbl_article_body, tbl_media). Version and
    /// conflict rows must pass "article_id" — see <see cref="BuildPerRowAadForTable"/>.
    /// </param>
    internal static void ReWrapTableAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        string tableName,
        string pkColumn,
        string dekColumn,
        string dekIvColumn,
        byte[] oldDek,
        byte[] newDek,
        string? aadIdColumn = null)
    {
        aadIdColumn ??= pkColumn;
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
                ? $"SELECT [{pkColumn}] AS pk, [{aadIdColumn}] AS aad_id, [{dekColumn}] AS enc_dek, [{dekIvColumn}] AS dek_iv FROM [{tableName}] ORDER BY [{pkColumn}] LIMIT @limit"
                : $"SELECT [{pkColumn}] AS pk, [{aadIdColumn}] AS aad_id, [{dekColumn}] AS enc_dek, [{dekIvColumn}] AS dek_iv FROM [{tableName}] WHERE [{pkColumn}] > @lastPk ORDER BY [{pkColumn}] LIMIT @limit";

            var rows = conn.Query<dynamic>(sql, new { limit = batchSize, lastPk }, tx).ToList();
            if (rows.Count == 0) break;

            foreach (var row in rows)
            {
                var encDek = (byte[])row.enc_dek;
                var dekIv = (byte[])row.dek_iv;
                var pk = (string)row.pk;
                var aadId = (string)row.aad_id;

                // Hold plainDek in try/finally so an exception from Wrap or Execute can't leak
                // the per-item DEK on the heap. Use DekManager (per-row AAD) — these are
                // article/media DEKs, not master DEKs. AAD format depends on the table.
                // Framing must survive the rewrap. Every reader decides "is this row v1?" from the
                // DEK blob itself (length > 48 && blob[0] == 0x01) and then applies the v1 AAD to
                // BOTH the DEK unwrap and the BODY decrypt. WrapDek always emits v1, so re-wrapping
                // a legacy v0 row with it silently relabels the row: readers switch to v1 AAD while
                // the body ciphertext is still v0 and was sealed with none, and the row is lost for
                // good. Rotation is the one place that must preserve v0.
                var isLegacyV0 = encDek.Length == 48;
                var aad = BuildPerRowAadForTable(tableName, aadId, encDek);
                var plainDek = DekManager.UnwrapDek(encDek, dekIv, oldDek, aad);
                try
                {
                    var (newEnc, newIv) = isLegacyV0
                        ? DekManager.WrapDekLegacyV0(plainDek, newDek)
                        : DekManager.WrapDek(plainDek, newDek, aad);
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
