using System;
using System.Linq;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging;

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

    public static async Task<(int agentsDeleted, int slotsDeleted, RewrapTally tally)> RewrapAllAsync(
        DbConnectionFactory connFactory,
        SessionService sessionService,
        byte[] oldDek, byte[] newDek, int newEpoch, string commitEventId,
        bool isInitiator,
        int? initiatorSlotId = null,
        byte[]? newWrappedSlotDek = null,
        byte[]? newWrappedSlotIv = null,
        string? chainEncryptedNewDekB64 = null,
        string? chainIvB64 = null,
        Action<DekRotationFlowStep, int, string>? progress = null,
        Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        int agentsDeleted = 0;
        int slotsDeleted = 0;
        var tally = new RewrapTally();

        Report(progress, DekRotationFlowStep.ReWrappingPerItem, 20,
            isInitiator ? "Re-wrapping article bodies..." : "Auto-accept: re-wrapping article bodies...");

        using var conn = connFactory.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            tally.Add(ReWrapTableAsync(conn, tx, "tbl_article_body", "article_id", "encrypted_dek", "dek_iv", oldDek, newDek, logger: logger));
            Report(progress, DekRotationFlowStep.ReWrappingPerItem, 35,
                isInitiator ? "Re-wrapping article versions..." : "Auto-accept: re-wrapping article versions...");

            // aadIdColumn = "article_id": version/conflict rows are keyed by their own GUID but
            // carry a copy of the ARTICLE's wrapped DEK, so the AAD is the article's.
            tally.Add(ReWrapTableAsync(conn, tx, "tbl_article_version", "id", "encrypted_dek", "dek_iv", oldDek, newDek,
                aadIdColumn: "article_id", logger: logger));
            Report(progress, DekRotationFlowStep.ReWrappingPerItem, 50,
                isInitiator ? "Re-wrapping conflict versions..." : "Auto-accept: re-wrapping conflict versions...");

            tally.Add(ReWrapTableAsync(conn, tx, "tbl_conflict_version", "id", "encrypted_dek", "dek_iv", oldDek, newDek,
                aadIdColumn: "article_id", logger: logger));
            Report(progress, DekRotationFlowStep.ReWrappingPerItem, 65,
                isInitiator ? "Re-wrapping media..." : "Auto-accept: re-wrapping media...");

            tally.Add(ReWrapTableAsync(conn, tx, "tbl_media", "id", "encrypted_dek", "dek_iv", oldDek, newDek, logger: logger));

            Report(progress, DekRotationFlowStep.ReWrappingPerItem, 70,
                isInitiator ? "Re-wrapping projection matrix..." : "Auto-accept: re-wrapping projection matrix...");

            ReWrapProjectionMatrix(conn, tx, oldDek, newDek, tally, logger);

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

            // Re-wrap this node's OWN Ed25519 identity seed to the new DEK, in the same
            // transaction. Without this the seed stays sealed under the pre-rotation DEK forever:
            // the FIRST rotation still opens it (oldDek is the key it was sealed under), but every
            // subsequent rotation — and every event signature after this one, which decrypts the
            // seed under the now-current new DEK — then fails. That is the "confidential rotation
            // works once, then wedges every v1 node" bug. v=0 rows hold a plaintext seed that does
            // not depend on the DEK, so they are left untouched. Follows the same never-roll-back
            // philosophy as the per-row rewrap: a seed that opens under neither key is a node
            // already broken by a pre-fix rotation, and aborting only makes it unrecoverable.
            ReWrapNodeIdentitySeed(conn, tx, oldDek, newDek, tally, logger);

            // Mark Applied INSIDE the rotation tx. If the process crashes between
            // tx.Commit() and the swap, the DB+state agree (Applied + new sentinel);
            // the startup sweep won't mark this as Failed.
            //
            // The chain material rides along in the SAME statement, and that is the point of
            // writing it here rather than anywhere else: LazySlotRewrapService walks the Applied
            // rotations to re-wrap a user's slot at their next login, and a row that says Applied
            // without the material to walk past it is the state that locks that user out. One
            // statement, so the two facts cannot disagree.
            //
            // It used to read the same values back from the dek_rotation_commit event in
            // tbl_event. Compaction deletes those (the initiator compacts right after rotating),
            // and once the row is gone the walk cannot start and the user can never unlock this
            // node again. tbl_dek_rotation_state is local, never synced, and nothing compacts it.
            // See migration 020, including why copying this material here adds no exposure that
            // tbl_event did not already have.
            await conn.ExecuteAsync(
                @"UPDATE tbl_dek_rotation_state
                     SET state = @state, applied_at = @now, updated_at = @now,
                         chain_encrypted_new_dek = COALESCE(@chainDek, chain_encrypted_new_dek),
                         chain_iv                = COALESCE(@chainIv,  chain_iv)
                   WHERE event_id = @eventId",
                new
                {
                    state = DekRotationState.Applied.ToString().ToUpperInvariant(),
                    now = DateTime.UtcNow.ToString("O"),
                    eventId = commitEventId,
                    chainDek = chainEncryptedNewDekB64,
                    chainIv = chainIvB64
                },
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
        // Rows that survived the rotation without being rotated are said out loud, in the same
        // message the operator already reads, rather than left in a log they have no reason to
        // open. "AlreadyOnNewKey" is routine and healthy — it is the peer race, correctly handled.
        // "Unreadable" is not: each one is an article or media item that no longer opens.
        if (tally.AlreadyOnNewKey > 0)
            completedMsg += $" Rows already on the new key: {tally.AlreadyOnNewKey}.";
        if (tally.Unreadable > 0)
            completedMsg += $" UNREADABLE ROWS: {tally.Unreadable} ({string.Join(", ", tally.UnreadableExamples)})"
                          + " — these need manual recovery.";

        Report(progress, DekRotationFlowStep.Completed, 100, completedMsg);

        if (tally.Unreadable > 0)
        {
            logger?.LogError(
                "DEK rotation to epoch {Epoch} finished with {Count} unreadable row(s): {Examples}. "
                + "The rotation itself succeeded — these rows opened under neither the old nor the new master key.",
                newEpoch, tally.Unreadable, string.Join(", ", tally.UnreadableExamples));
        }

        return (agentsDeleted, slotsDeleted, tally);
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
        System.Data.IDbConnection conn, System.Data.IDbTransaction tx, byte[] oldDek, byte[] newDek,
        RewrapTally tally, Microsoft.Extensions.Logging.ILogger? logger = null)
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
            //
            // Same old-key/new-key question as the per-row DEKs above, and for the same reason:
            // tbl_projection_matrix is a replicated table, so a peer that rotated first can ship a
            // matrix already sealed under the new key. Unwrapping that with the old one throws, and
            // because this pass runs INSIDE the rotation transaction the throw takes the whole
            // rotation with it — permanently, on every retry. A matrix already where it needs to be
            // is simply left alone.
            byte[]? plainMatrix = TryUnwrapVersioned(enc, iv, oldDek);
            if (plainMatrix == null)
            {
                if (TryUnwrapVersioned(enc, iv, newDek) is { } already)
                {
                    Array.Clear(already, 0, already.Length);
                    continue;
                }

                // Readable under neither key. The comment used to say aborting was worse than
                // losing the matrix and then aborted anyway — a throw here propagates out of the
                // rotation transaction and rolls the whole thing back, on every retry, which is the
                // exact lockout the rest of this class was rewritten to remove.
                //
                // So: count it, say so, and carry on. Leaving the row alone is deliberate rather
                // than deleting it — EmbeddingProjectionService.EnsureProjectionMatrixAsync already
                // handles a matrix that will not open, and regenerating one means every article's
                // stored projection has to be recomputed against the new matrix. That re-flagging is
                // its job and it knows how; a rewrap pass quietly deleting the row would start the
                // same recovery without the half that makes it correct.
                tally.Unreadable++;
                tally.UnreadableExamples.Add($"tbl_projection_matrix:{id}");
                logger?.LogError(
                    "DEK rotation: the projection matrix (row {Id}) opened under neither the old nor the new master key. "
                    + "The rotation continues; semantic search will regenerate the matrix and re-embed on its next pass.",
                    id);
                continue;
            }
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
    /// Re-wraps the node's own Ed25519 identity seed (tbl_node_identity.ed25519_private_key) from
    /// the old master DEK to the new one, so the rotation is self-perpetuating: after it, the seed
    /// opens under the current DEK, which is what the next rotation's <c>ResolveNewDek</c> and every
    /// post-rotation event signature both require. Only v=1 (DEK-wrapped) rows are touched; v=0
    /// (legacy plaintext seed) rows do not depend on the DEK.
    /// <para>
    /// Runs inside the rotation transaction. A seed that opens under neither key is counted as
    /// unreadable and logged rather than thrown — a throw here would roll the whole rotation back on
    /// every retry, the exact lockout the rest of this class was rewritten to avoid.
    /// </para>
    /// </summary>
    internal static void ReWrapNodeIdentitySeed(
        System.Data.IDbConnection conn, System.Data.IDbTransaction tx,
        byte[] oldDek, byte[] newDek, RewrapTally tally, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        var row = conn.QuerySingleOrDefault(
            @"SELECT node_id AS NodeId, ed25519_private_key AS Pk,
                     ed25519_private_key_iv AS Iv, ed25519_private_key_v AS V
                FROM tbl_node_identity LIMIT 1", transaction: tx);
        if (row is null) return;

        int version = (int)(long)row.V;
        if (version == 0) return; // plaintext seed, DEK-independent — nothing to re-wrap.

        var nodeIdStr = (string)row.NodeId;
        var nodeId = Guid.Parse(nodeIdStr);
        var storedPk = (byte[])row.Pk;
        var storedIv = row.Iv as byte[];

        void MarkUnreadable(string reason)
        {
            tally.Unreadable++;
            tally.UnreadableExamples.Add("tbl_node_identity:ed25519_private_key");
            logger?.LogError(
                "DEK rotation: the node's own Ed25519 identity seed could not be re-wrapped ({Reason}). "
                + "The rotation continues, but this node cannot sign events or rotate again until its "
                + "identity seed is restored.", reason);
        }

        if (storedIv is null)
        {
            MarkUnreadable("v1 row missing ed25519_private_key_iv");
            return;
        }

        byte[]? seed = null;
        try
        {
            seed = NodeIdentityCrypto.GetDecryptedPrivateKey(storedPk, storedIv, 1, nodeId, oldDek);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Not under the old key. Either a racing path already re-wrapped it to the new key
            // (idempotent, fine), or it opens under neither (genuinely unrecoverable).
            try
            {
                Array.Clear(NodeIdentityCrypto.GetDecryptedPrivateKey(storedPk, storedIv, 1, nodeId, newDek));
                return; // already on the new key.
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                MarkUnreadable("opened under neither the old nor the new master key");
                return;
            }
        }

        try
        {
            var (rewrapped, iv) = NodeIdentityCrypto.EncryptPrivateKey(seed, newDek, nodeId);
            conn.Execute(
                @"UPDATE tbl_node_identity
                     SET ed25519_private_key = @pk, ed25519_private_key_iv = @iv, ed25519_private_key_v = 1
                   WHERE node_id = @nodeId",
                new { pk = rewrapped, iv, nodeId = nodeIdStr }, tx);
        }
        finally
        {
            Array.Clear(seed, 0, seed.Length);
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
    internal static RewrapTally ReWrapTableAsync(
        System.Data.IDbConnection conn,
        System.Data.IDbTransaction tx,
        string tableName,
        string pkColumn,
        string dekColumn,
        string dekIvColumn,
        byte[] oldDek,
        byte[] newDek,
        string? aadIdColumn = null,
        Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        var tally = new RewrapTally();
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

                // A row that does not unwrap under the OLD DEK used to throw straight out of this
                // loop, roll the whole rotation back, and do it again on every retry — leaving the
                // node permanently unable to finish the rotation, with wiping and re-joining as the
                // only way out. That is not a hypothetical: it is the expected outcome whenever a
                // peer ships an article written AFTER the rotation but BEFORE this node applied it.
                // The body arrives with its DEK already wrapped under the NEW master key, and this
                // loop then insists on opening it with the old one.
                //
                // So the failure of one row must not be able to destroy the node. Try the old key
                // (the normal case), then the new one (the row raced ahead of us and is already
                // where it needs to be — leave it alone), and only if NEITHER opens it treat the row
                // as unreadable: record it, and carry on. Rolling back forever protects nothing —
                // the rows that could be rotated stay unrotated too, and the operator gets a node
                // that cannot be recovered rather than one article that cannot be read.
                byte[]? plainDek = TryUnwrap(encDek, dekIv, oldDek, aad);
                if (plainDek == null)
                {
                    if (TryUnwrap(encDek, dekIv, newDek, aad) is { } already)
                    {
                        Array.Clear(already, 0, already.Length);
                        tally.AlreadyOnNewKey++;
                        lastPk = pk;
                        continue;
                    }

                    // Readable under neither key. Nothing this routine can do will help, and it is
                    // the operator who has to know: name the row so it can be found.
                    tally.Unreadable++;
                    tally.UnreadableExamples.Add($"{tableName}:{pk}");
                    logger?.LogError(
                        "DEK rotation: {Table} row {Pk} could not be unwrapped with either the old or the new master key. "
                        + "The rotation continues; this row stays unreadable and needs manual recovery.",
                        tableName, pk);
                    lastPk = pk;
                    continue;
                }

                try
                {
                    var (newEnc, newIv) = isLegacyV0
                        ? DekManager.WrapDekLegacyV0(plainDek, newDek)
                        : DekManager.WrapDek(plainDek, newDek, aad);
                    conn.Execute(
                        $"UPDATE [{tableName}] SET [{dekColumn}] = @enc, [{dekIvColumn}] = @iv WHERE [{pkColumn}] = @pk",
                        new { enc = newEnc, iv = newIv, pk },
                        tx);
                    tally.Rewrapped++;
                }
                finally
                {
                    Array.Clear(plainDek, 0, plainDek.Length);
                }

                lastPk = pk;
            }
        }

        return tally;
    }

    /// <summary>
    /// Unwraps with <paramref name="candidateDek"/>, or returns null if that key is not the one this
    /// row was sealed under.
    ///
    /// <para>
    /// A failed unwrap is an authentication-tag mismatch, which is AES-GCM working correctly, not an
    /// error condition — so it is caught rather than propagated. Only the cryptographic failures are
    /// swallowed: anything else (a malformed row, a disposed key) still throws, because those mean
    /// the caller's assumptions are broken rather than "wrong key".
    /// </para>
    /// </summary>
    /// <summary>
    /// <see cref="TryUnwrap"/> for the versioned (non-fixed-length) payloads — see its doc for why a
    /// failed unwrap is a normal answer here rather than an error.
    /// </summary>
    private static byte[]? TryUnwrapVersioned(byte[] enc, byte[] iv, byte[] candidateDek)
    {
        try
        {
            return DekManager.UnwrapVersioned(enc, iv, candidateDek);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static byte[]? TryUnwrap(byte[] encDek, byte[] dekIv, byte[] candidateDek, byte[]? aad)
    {
        try
        {
            return DekManager.UnwrapDek(encDek, dekIv, candidateDek, aad);
        }
        catch (System.Security.Cryptography.AuthenticationTagMismatchException)
        {
            return null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Some platforms surface a tag mismatch as the base type rather than the specific one.
            return null;
        }
    }
}

/// <summary>
/// What one table's rewrap actually did. Counted rather than inferred, because "the rotation
/// finished" and "every row came with it" are different statements and an operator needs the second
/// one: a row left behind is an article that no longer opens, and it must not be discoverable only
/// by a user hitting it months later.
/// </summary>
public sealed class RewrapTally
{
    /// <summary>Rows opened with the old key and re-sealed under the new one — the normal path.</summary>
    public int Rewrapped { get; set; }

    /// <summary>
    /// Rows already sealed under the new key. These arrived from a peer that had applied the
    /// rotation before this node did; they are already where they need to be and were left alone.
    /// </summary>
    public int AlreadyOnNewKey { get; set; }

    /// <summary>Rows that opened under neither key. Each one is an article or media item that stays unreadable.</summary>
    public int Unreadable { get; set; }

    /// <summary>Identifiers of unreadable rows, for the operator to chase. Capped by what is worth logging.</summary>
    public List<string> UnreadableExamples { get; } = [];

    public void Add(RewrapTally other)
    {
        Rewrapped += other.Rewrapped;
        AlreadyOnNewKey += other.AlreadyOnNewKey;
        Unreadable += other.Unreadable;
        foreach (var e in other.UnreadableExamples)
            if (UnreadableExamples.Count < 20) UnreadableExamples.Add(e);
    }
}
