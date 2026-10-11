using Dapper;
using Microsoft.Data.Sqlite;
using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Storage.Boot;

/// <summary>
/// What one rewrite puts into the boot store (docs/design/step-b-spec.md 4.3 rule 3), computed by the mirror from the open sealed
/// database. <see cref="Doors"/> is the complete current set; their <c>DoorId</c> and <c>Pending</c> are ignored.
/// <see cref="DekFingerprint"/> of a door is null when the sealed database does not say which DEK the door yields.
/// </summary>
/// <param name="CurrentDekFingerprint">The master DEK the session holds, or null when it holds none (locked, closing).</param>
/// <param name="CandidateDekFingerprints">Every DEK the session holds (current, retired in memory and persisted); empty when locked.</param>
/// <param name="WrappersToAdd">Wrappers the rewrite wants present; added when missing and when their DEK is kept.</param>
/// <param name="Identity">The public identity, or null when the sealed database has none yet (a vault that was never initialised).</param>
/// <param name="Whitelist">The sealed whitelist, one dictionary of column name to value per row.</param>
/// <param name="UnlockMaterialVersion">The sealed database's <c>unlock_material_version</c> the rewrite saw.</param>
public sealed record BootRewrite(
    IReadOnlyList<BootDoor> Doors,
    string? CurrentDekFingerprint,
    IReadOnlyCollection<string> CandidateDekFingerprints,
    IReadOnlyList<BootDbKeyRow> WrappersToAdd,
    BootIdentity? Identity,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Whitelist,
    string UnlockMaterialVersion);

/// <summary>What a rewrite did, for logs and tests (counts and fingerprints of wrappers only; no key material).</summary>
public sealed record BootRewriteResult(int Doors, IReadOnlyList<string> WrapperFingerprints, int WrappersPruned);

/// <summary>
/// Replaces the derived copies of the boot store (doors, wrappers, identity and whitelist mirrors, the mirrored version) in ONE boot
/// transaction. The rules of spec 3.4 are applied inside that transaction, against the rows it sees:
/// a door keeps the known <c>dek_fp</c> of an existing row with the same source and content hash; the wrappers kept are the current DEK's,
/// those of every door with a known fingerprint, and - when a door's fingerprint is unknown - those of every DEK the session holds;
/// every other wrapper is deleted. With a door of unknown fingerprint and no DEK in the session (a locked or closing node) nothing is
/// deleted: the rewrite cannot tell which wrapper the door needs, and a lock-out is worse than a leftover wrapper.
/// </summary>
public sealed class BootStoreWriter(BootStoreConnectionFactory factory)
{
    public async Task<BootRewriteResult> RewriteAsync(BootRewrite plan, CancellationToken ct = default)
    {
        DapperConfig.Configure();
        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var existing = await BootDoorStore.ListAsync(connection, transaction);
            var knownFingerprints = new Dictionary<(string Kind, long Source, string Hash), string>();
            foreach (var d in existing)
                if (d.DekFingerprint is not null) knownFingerprints[(d.Kind, d.SourceId, d.ContentSha256)] = d.DekFingerprint;

            await connection.ExecuteAsync("DELETE FROM tbl_boot_door", transaction: transaction);
            var now = DateTime.UtcNow.ToString("o");
            var doorFingerprints = new List<string?>();
            foreach (var door in plan.Doors)
            {
                var fingerprint = door.DekFingerprint;
                if (fingerprint is null && knownFingerprints.TryGetValue((door.Kind, door.SourceId, door.ContentSha256), out var known)) fingerprint = known;
                doorFingerprints.Add(fingerprint);
                await connection.ExecuteAsync(
                    @"INSERT OR IGNORE INTO tbl_boot_door
                        (kind, source_id, slot_type, encrypted_master_dek, iv, salt, argon_memory, argon_iterations, argon_parallelism, key_hash,
                         kdf_version, content_sha256, dek_fp, pending, written_at)
                      VALUES (@Kind, @SourceId, @SlotType, @EncryptedMasterDek, @Iv, @Salt, @ArgonMemory, @ArgonIterations, @ArgonParallelism, @KeyHash,
                              @KdfVersion, @ContentSha256, @fingerprint, 0, @now)",
                    new
                    {
                        door.Kind, door.SourceId, door.SlotType, door.EncryptedMasterDek, door.Iv, door.Salt, door.ArgonMemory, door.ArgonIterations,
                        door.ArgonParallelism, door.KeyHash, door.KdfVersion, door.ContentSha256, fingerprint, now,
                    }, transaction);
            }

            // Wrappers (spec 3.4).
            var keep = new HashSet<string>(StringComparer.Ordinal);
            if (plan.CurrentDekFingerprint is not null) keep.Add(plan.CurrentDekFingerprint);
            foreach (var fingerprint in doorFingerprints)
                if (fingerprint is not null) keep.Add(fingerprint);
            var anyUnknown = doorFingerprints.Any(f => f is null);
            if (anyUnknown) keep.UnionWith(plan.CandidateDekFingerprints);
            var mayPrune = !anyUnknown || plan.CandidateDekFingerprints.Count > 0;

            var pruned = 0;
            if (mayPrune)
                foreach (var row in await BootDbKeyStore.ListAsync(connection, transaction))
                    if (!keep.Contains(row.DekFingerprint))
                    {
                        await connection.ExecuteAsync("DELETE FROM tbl_boot_db_key WHERE dek_fp = @fp AND dbk_generation = @generation",
                            new { fp = row.DekFingerprint, generation = row.DbkGeneration }, transaction);
                        pruned++;
                    }
            foreach (var row in plan.WrappersToAdd)
                if (keep.Contains(row.DekFingerprint) || !mayPrune)
                    await BootDbKeyStore.AddIfMissingAsync(connection, transaction, row);

            // Identity and whitelist mirrors: replaced as a whole (a suspension ends here, spec 6.4).
            await connection.ExecuteAsync("DELETE FROM tbl_boot_identity", transaction: transaction);
            if (plan.Identity is { } identity)
                await connection.ExecuteAsync(
                    "INSERT INTO tbl_boot_identity (node_id, display_name, ed25519_public_key, updated_at) VALUES (@nodeId, @DisplayName, @key, @now)",
                    new { nodeId = identity.NodeId.ToString("D"), identity.DisplayName, key = identity.Ed25519PublicKey, now }, transaction);

            await ReplaceWhitelistAsync(connection, transaction, plan.Whitelist);
            await BootStore.SetMetaAsync(connection, transaction, BootMetaKeys.UnlockMaterialVersion, plan.UnlockMaterialVersion);

            var fingerprints = (await BootDbKeyStore.ListAsync(connection, transaction)).Select(r => r.DekFingerprint).ToList();
            transaction.Commit();
            return new BootRewriteResult(plan.Doors.Count, fingerprints, pruned);
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static async Task ReplaceWhitelistAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        await connection.ExecuteAsync("DELETE FROM tbl_whitelist", transaction: transaction);
        if (rows.Count == 0) return;
        var bootColumns = (await connection.QueryAsync<string>("SELECT name FROM pragma_table_info('tbl_whitelist')", transaction: transaction))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var columns = row.Keys.Where(bootColumns.Contains).ToList();
            var sql = $"INSERT OR REPLACE INTO tbl_whitelist ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select((_, i) => "@p" + i))})";
            var parameters = new DynamicParameters();
            for (var i = 0; i < columns.Count; i++) parameters.Add("p" + i, row[columns[i]]);
            await connection.ExecuteAsync(sql, parameters, transaction);
        }
    }
}
