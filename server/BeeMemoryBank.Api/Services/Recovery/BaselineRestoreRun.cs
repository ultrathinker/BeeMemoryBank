using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Sync.Recovery;
using BeeMemoryBank.Vault.Metadata;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>What the replay of the deltas did (docs/design/step-a-spec.md 12 step 6).</summary>
/// <param name="Total">Events in the deltas.</param>
/// <param name="Applied">Applied or already there (a replay run again finds its own work done).</param>
/// <param name="Dropped">Accepted and not stored: refused by a gate (hard-deleted, malformed path, a restore shield).</param>
/// <param name="Deferred">Could not be applied yet (a missing blob, an originator not known); the first sync brings them again.</param>
/// <param name="Skipped">Refused for good (a signature that does not verify, a malformed event); never applied.</param>
/// <param name="Resumed">The replay continued after a cut one.</param>
/// <param name="Incomplete">The deltas are more than one restore takes (<see cref="BaselineRestoreRun.MaxDeltas"/>): the rest comes with the
/// first sync, from the position of <paramref name="LastSequence"/> in the serving node's log.</param>
/// <param name="LastSequence">The serving node's sequence number of the last delta this restore replayed.</param>
public sealed record BaselineReplayResult(int Total, int Applied, int Dropped, int Deferred, int Skipped, bool Resumed,
    bool Incomplete = false, long LastSequence = 0);

/// <summary>
/// One restore from a baseline (spec 12): opens the metadata keys with the keys the password recovered, decrypts the baseline, imports
/// it with the control tables and the blobs, and replays the deltas through the ordinary applier with the package's whitelist and the
/// opened keys instead of this node's own. Holds open keys in memory until disposed.
/// </summary>
internal sealed class BaselineRestoreRun(IRestoreSourceV2 source, string dataPath, ILogger logger) : IDisposable
{
    /// <summary>How many deltas pass between two writes of the progress marker (a test lowers it).</summary>
    internal static int MarkerEvery { get; set; } = 200;

    /// <summary>
    /// The most deltas one restore replays: the limit <c>/api/blind/restore/events</c> has always had (200 000). More are left for the
    /// first sync with the blind node, and the restore says so (spec 12) instead of stopping without a word.
    /// </summary>
    internal static int MaxDeltas { get; set; } = 200_000;

    /// <summary>Set when the replay stopped at <see cref="MaxDeltas"/>: the serving node's position the restored node has really reached.</summary>
    public long? IncompleteAt { get; private set; }

    private const string ImportedMarker = "restore_baseline_imported";
    private const string ReplayMarker = "restore_replay_seq";

    private readonly Dictionary<string, byte[]> _meks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _eventKeys = new(StringComparer.Ordinal);

    /// <summary>The decrypted baseline database, set by <see cref="OpenDatabaseAsync"/>.</summary>
    public string DatabasePath { get; private set; } = "";

    /// <summary>Test seam: called with the decrypted baseline database once the restore has used it and before it is removed; a test plants or holds a sidecar here.</summary>
    internal static Action<string>? AfterStagingWork { get; set; }

    /// <summary>The recovery material of the serving node: boxes, links, anchors, sealed secrets (spec 8.4, 12 step 2).</summary>
    public async Task<RecoverySet> ReadRecoverySetAsync()
    {
        using var conn = new SqliteConnection($"Data Source={source.ControlDbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        return await RecoverySetBuilder.BuildAsync(conn);
    }

    /// <summary>
    /// Spec 12 steps 3 and 4: every metadata key of the control tables opened with the DEK of its fingerprint, the baseline's key among
    /// them (else nothing is written: this recovery material cannot open the baseline), then the baseline decrypted into the owner-only
    /// staging folder. Returns the path of its plaintext database.
    /// </summary>
    /// <param name="target">This node's database. When it is sealed the baseline's database is never written as a plain file: the container is
    /// decrypted through a pipe into <see cref="PlainImageImporter"/>, which leaves a staging copy under the vault's page key (docs/design/step-b-spec.md
    /// 8.4); the returned path is that encrypted copy.</param>
    public async Task<string> OpenDatabaseAsync(RecoveredKeys keys, CancellationToken ct, DbConnectionFactory? target = null)
    {
        var manifest = source.Baseline.Manifest;
        var candidates = new List<byte[]> { keys.Current };
        candidates.AddRange(keys.Retired.Values);
        foreach (var row in ReadKeyRows())
            if (MetadataKeyCrypto.TryUnwrap(row, candidates) is { } mek)
                _meks[row.KeyId] = mek;
        if (!_meks.ContainsKey(manifest.KeyId))
            throw new InvalidDataException(
                "The baseline is sealed under a metadata key that the recovered master keys do not open; it cannot be restored from this material.");

        var container = Path.Combine(source.Baseline.Directory, BaselineManifest.ContainerFileName);
        var info = new FileInfo(container);
        if (!info.Exists || info.Length != manifest.ContainerBytes)
            throw new InvalidDataException("The baseline container is not the size its manifest says.");
        await using (var stream = new FileStream(container, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            if (!string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)), manifest.ContainerSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The baseline container does not match the hash in its manifest.");

        var staging = SnapshotStaging.NewFile(dataPath);
        try
        {
            var label = ChunkedContainer.MekKeyLabel(manifest.KeyId);
            Func<ChunkedContainerHeader, ChunkKeyDeriver?> resolve = h => h.Key == label && _meks.TryGetValue(manifest.KeyId, out var mek)
                ? (info, salt) => MetadataKeyCrypto.Derive(mek, info, salt)
                : null;
            if (target is { IsSealed: true })
            {
                await SealedStaging.ImportAsync(target, staging, async (sink, token) =>
                {
                    await using var input = new FileStream(container, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                    await ChunkedContainer.DecryptWithAsync(input, sink, ChunkedContainer.PurposeBaseline, resolve, token);
                }, ct);
            }
            else
            {
                await using var input = new FileStream(container, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await using var output = new FileStream(staging, FileMode.Truncate, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await ChunkedContainer.DecryptWithAsync(input, output, ChunkedContainer.PurposeBaseline, resolve, ct);
            }
        }
        catch
        {
            // The failure is the one reported: a copy that stays is logged here, and the next start sweeps it.
            if (!SqliteStaging.TryCleanup(staging)) logger.LogError("A staging copy of a baseline could not be removed; node repair is required.");
            throw;
        }
        DatabasePath = staging;
        return staging;
    }

    /// <summary>
    /// Spec 12 step 4: the control tables, then the baseline's tables (the newer rows of the serving node first, so insert-or-ignore keeps
    /// them), then the blobs. A restore run again after a cut does not import the tables again: they hold the baseline and part of the
    /// replay, and importing the baseline over a replay that deleted something would bring it back.
    /// </summary>
    public async Task ImportAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<DbConnectionFactory>();
        var baselineId = source.Baseline.Manifest.BaselineId.ToString("N");
        if (ReadMarker(db, ImportedMarker) != baselineId)
        {
            ImportInto(db, source.ControlDbPath, SnapshotTables.Control, marker: null);
            ImportInto(db, DatabasePath, [.. SnapshotTables.Replicated, .. BlindPackageBuilder.ExtraTables], marker: (ImportedMarker, baselineId));
        }
        await ImportBlobsAsync(db, ct);
    }

    /// <summary>
    /// Spec 12 steps 6: the deltas, in order, through <see cref="EventApplier.ApplyAsync"/> with the package's whitelist as the originators
    /// and the opened metadata keys as the event keys. Progress is kept in <c>tbl_migration_marker</c> (<c>restore_replay_seq</c>) so a
    /// crash resumes after the last marked event; an event already in the log is applied again as a no-op either way.
    /// </summary>
    public async Task<BaselineReplayResult> ReplayAsync(IServiceProvider sp, Func<string, Task> step, CancellationToken ct)
    {
        var applier = sp.GetRequiredService<EventApplier>();
        var db = sp.GetRequiredService<DbConnectionFactory>();
        var manifest = source.Manifest;
        var originators = new Dictionary<Guid, OriginatorInfo>();
        foreach (var peer in manifest.Whitelist)
        {
            if (TryBase64(peer.PublicKeyB64) is { Length: 32 } key && !originators.ContainsKey(peer.NodeId))
                originators[peer.NodeId] = new OriginatorInfo(key, peer.IsSuperadmin);
        }

        var baselineId = source.Baseline.Manifest.BaselineId.ToString("N");
        var resumeAfter = ReadReplayMark(db, baselineId);
        int total = 0, applied = 0, dropped = 0, deferred = 0, skipped = 0, ordinal = 0;
        long lastSequence = 0;
        bool incomplete = false;
        foreach (var evt in source.ReadDeltas())
        {
            ct.ThrowIfCancellationRequested();
            if (ordinal >= MaxDeltas)
            {
                incomplete = true;
                IncompleteAt = lastSequence;
                break;
            }
            ordinal++;
            total++;
            lastSequence = evt.SequenceNum;
            if (ordinal <= resumeAfter)
            {
                applied++;
                continue;
            }

            if (evt.LamportTs is < 0 or >= RecoveryRestoreService.MaxRestoredLamport)
            {
                skipped++;
                logger.LogWarning("Delta {EventId} claims an impossible Lamport time and is skipped", evt.EventId);
                continue;
            }

            var context = new ApplyContext(
                ReceivedFrom: manifest.ProducerNodeId, ReceivedSeq: evt.SequenceNum,
                Originators: id => originators.GetValueOrDefault(id), EventKeys: EventKeyOf);
            try
            {
                var result = await applier.ApplyAsync(evt, context);
                if (result == EventApplyResult.SilentlyDropped) dropped++;
                else applied++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                switch (SyncFailureClassifier.Classify(ex))
                {
                    case SyncFailureKind.Deferred:
                        deferred++;
                        logger.LogWarning("Delta {EventId} ({Type}) cannot be applied yet ({Error}); the first sync brings it again",
                            evt.EventId, evt.EventType, ex.GetType().Name);
                        break;
                    default:
                        skipped++;
                        logger.LogWarning("Delta {EventId} ({Type}) is refused ({Error}) and skipped", evt.EventId, evt.EventType, ex.GetType().Name);
                        break;
                }
            }

            if (ordinal % MarkerEvery == 0) WriteMarker(db, ReplayMarker, $"{baselineId}:{ordinal}");
            await step($"replayed:{ordinal}");
        }

        WriteMarker(db, ReplayMarker, $"{baselineId}:{ordinal}");
        await step("replay-done");
        return new BaselineReplayResult(total, applied, dropped, deferred, skipped, Resumed: resumeAfter > 0, incomplete, lastSequence);
    }

    /// <summary>The deltas that can vouch for an anchor or a newer row: everything not sealed (anchors and the rest of the control events).</summary>
    public List<SyncEvent> ClearDeltas() => source.ReadDeltas().Where(e => e.EventType != EventClasses.Sealed).ToList();

    /// <summary>The restore marker comes down with the last write of the restore (spec 12 step 9).</summary>
    public void LowerMarkers(DbConnectionFactory db)
    {
        using var conn = db.CreateConnection();
        conn.Execute("DELETE FROM tbl_migration_marker WHERE key IN (@a, @b)", new { a = ImportedMarker, b = ReplayMarker });
    }

    private byte[]? EventKeyOf(string keyId)
    {
        lock (_eventKeys)
        {
            if (_eventKeys.TryGetValue(keyId, out var cached)) return cached;
            if (!_meks.TryGetValue(keyId, out var mek)) return null;
            return _eventKeys[keyId] = MetadataKeyCrypto.DeriveEventKey(mek);
        }
    }

    private List<MetadataKeyRow> ReadKeyRows()
    {
        using var conn = new SqliteConnection($"Data Source={source.ControlDbPath};Mode=ReadOnly;Pooling=False");
        conn.Open();
        return conn.Query<(string KeyId, long Generation, string Fp, byte[] Wrapped, byte[] Iv)>(
                "SELECT key_id, generation, dek_fingerprint, wrapped, iv FROM tbl_metadata_key")
            .Select(r => new MetadataKeyRow(r.KeyId, (int)r.Generation, r.Fp, r.Wrapped, r.Iv)).ToList();
    }

    private static void ImportInto(DbConnectionFactory db, string databasePath, IEnumerable<string> tables, (string Key, string Value)? marker)
    {
        using var conn = (SqliteConnection)db.CreateConnection();
        using (var off = conn.CreateCommand()) { off.CommandText = "PRAGMA foreign_keys = OFF"; off.ExecuteNonQuery(); }
        using (var attach = conn.CreateCommand())
        {
            attach.CommandText = db.AttachStatement(databasePath, "snap");
            attach.ExecuteNonQuery();
        }
        try
        {
            using var tx = conn.BeginTransaction();
            foreach (var table in tables)
            {
                if (!SnapshotTableImport.SnapshotHasTable(conn, tx, table)) continue;
                SnapshotTableImport.CopyTable(conn, tx, table, orIgnore: true);
            }
            SnapshotTableImport.AdoptLegacyInlineCiphertext(conn, tx);
            if (marker is { } m)
                conn.Execute("INSERT OR REPLACE INTO tbl_migration_marker (key, value, set_at) VALUES (@k, @v, @t)",
                    new { k = m.Key, v = m.Value, t = DateTime.UtcNow.ToString("O") }, tx);
            tx.Commit();
        }
        finally
        {
            using (var detach = conn.CreateCommand()) { detach.CommandText = "DETACH DATABASE snap"; detach.ExecuteNonQuery(); }
            using var on = conn.CreateCommand();
            on.CommandText = "PRAGMA foreign_keys = ON";
            on.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// The blobs of the baseline's list and the deltas', each hashed as it is stored (spec 8.6 step 4 again: a source is not trusted
    /// twice over for what it is cheap to check). Every hash of <c>blobs.txt</c> must be there; a delta's blob that is not stays for
    /// the first sync (its event is deferred).
    /// </summary>
    private async Task ImportBlobsAsync(DbConnectionFactory db, CancellationToken ct)
    {
        var listed = (await File.ReadAllLinesAsync(Path.Combine(source.Baseline.Directory, BaselineManifest.BlobListFileName), ct))
            .Where(l => l.Length > 0).ToList();
        var required = listed.ToHashSet(StringComparer.Ordinal);
        var wanted = new HashSet<string>(required, StringComparer.Ordinal);
        foreach (var evt in source.ReadDeltas())
        {
            if (evt.EventType != EventClasses.Sealed) continue;
            try
            {
                foreach (var hash in BeeMemoryBank.Sync.Metadata.SealedEnvelope.Parse(evt.Payload).BlobHashes) wanted.Add(hash);
            }
            catch (InvalidDataException)
            {
                // Malformed: the replay refuses it, with the rest of what it cannot apply.
            }
        }

        using var conn = db.CreateConnection();
        var now = DateTime.UtcNow.ToString("o");
        var missing = 0;
        foreach (var hash in wanted)
        {
            ct.ThrowIfCancellationRequested();
            await using var stream = source.OpenBlob(hash);
            if (stream is null)
            {
                if (required.Contains(hash)) missing++;
                continue;
            }
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, ct);
            var data = memory.ToArray();
            if (!BlobHash.Matches(hash, data))
                throw new InvalidDataException("A blob in the source does not hash to its name.");
            await conn.ExecuteAsync(
                "INSERT OR IGNORE INTO tbl_blob (hash, data, size, created_at) VALUES (@hash, @data, @size, @now)",
                new { hash, data, size = data.LongLength, now });
        }
        if (missing > 0)
            throw new InvalidDataException(
                $"The source holds no copy of {missing} stored content blob(s) the baseline lists; restore from a source that has them.");
    }

    private static string? ReadMarker(DbConnectionFactory db, string key)
    {
        using var conn = db.CreateConnection();
        return conn.ExecuteScalar<string?>("SELECT value FROM tbl_migration_marker WHERE key = @key", new { key });
    }

    private static void WriteMarker(DbConnectionFactory db, string key, string value)
    {
        using var conn = db.CreateConnection();
        conn.Execute("INSERT OR REPLACE INTO tbl_migration_marker (key, value, set_at) VALUES (@key, @value, @t)",
            new { key, value, t = DateTime.UtcNow.ToString("O") });
    }

    /// <summary>The event count a cut replay of this baseline had marked, or 0 (another baseline's mark does not count).</summary>
    private static int ReadReplayMark(DbConnectionFactory db, string baselineId)
    {
        var value = ReadMarker(db, ReplayMarker);
        var parts = value?.Split(':');
        return parts is [var id, var n] && id == baselineId && int.TryParse(n, out var count) && count > 0 ? count : 0;
    }

    private static byte[]? TryBase64(string? value)
    {
        try { return value is null ? null : Convert.FromBase64String(value); }
        catch (FormatException) { return null; }
    }

    /// <summary>
    /// Removes the decrypted baseline database with its SQLite sidecars, once the restore has used it. A copy that stays is repair-required: the
    /// restore that used it is not reported as done.
    /// </summary>
    public void RemoveDatabase()
    {
        if (DatabasePath.Length == 0) return;
        AfterStagingWork?.Invoke(DatabasePath);
        if (!SqliteStaging.TryCleanup(DatabasePath))
            throw new IOException("The decrypted baseline database could not be removed. The restore requires repair and was not reported as done.");
        DatabasePath = "";
    }

    public void Dispose()
    {
        foreach (var mek in _meks.Values) CryptographicOperations.ZeroMemory(mek);
        foreach (var key in _eventKeys.Values) CryptographicOperations.ZeroMemory(key);
        _meks.Clear();
        _eventKeys.Clear();
        if (DatabasePath.Length > 0 && !SqliteStaging.TryCleanup(DatabasePath))
            logger.LogError("The decrypted baseline database could not be removed; node repair is required.");
        DatabasePath = "";
    }
}
