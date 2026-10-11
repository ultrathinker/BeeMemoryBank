using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Vault.Metadata;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Api.Services;

public enum BaselineRefusal { NotActivated, Locked, NoMetadataKey, EventsHeld, NoDiskSpace, BlobMissing, SelfCheckFailed, NoIdentity }

/// <summary>A baseline was not produced, and why. The message names counts and causes, never content.</summary>
public sealed class BaselineRefusedException(BaselineRefusal reason, string message, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    public BaselineRefusal Reason { get; } = reason;
}

/// <summary>Where a full node keeps the baselines it produced (docs/design/step-a-spec.md 7.3: the directory is the record).</summary>
public static class ProducedBaselines
{
    public static string Root(string dataPath) => Path.Combine(dataPath, "baselines", "produced");

    public static string DirectoryOf(string dataPath, Guid baselineId) => Path.Combine(Root(dataPath), baselineId.ToString("N"));

    /// <summary>The state of a baseline directory: only one whose signature file exists is complete (the signature is written last).</summary>
    public static bool IsComplete(string directory) => File.Exists(Path.Combine(directory, BaselineManifest.SignatureFileName));
}

/// <summary>
/// Builds a baseline on a full node (docs/design/step-a-spec.md 7.2, 7.3): the filtered, derived-columns-nulled, blob-less copy of the
/// replicated tables, sealed in a BMBCH1 container under the current metadata key, checked by opening it again, described by a manifest
/// the node signs. Only an activated, unlocked node that holds no held events and every blob its rows reference produces one; every other
/// case is a <see cref="BaselineRefusedException"/>, never a weaker file.
/// </summary>
public sealed class BaselineProducer(
    SnapshotService snapshots,
    DbConnectionFactory db,
    INodeIdentityRepository nodeRepo,
    IMetadataKeyRing ring,
    ISyncProtocolPolicy policy,
    IHeldEventStatus held,
    SessionService session,
    ISyncPositionRepository positions,
    IEventLogRepository events,
    ILamportClock clock,
    IBlobRepository blobs,
    ILogger<BaselineProducer> logger,
    IMetadataKeyPublishGate? publishGate = null,
    TimeProvider? time = null) : IBaselineProducer
{
    /// <summary>The produced baselines kept (spec 7.3 step 5): the newest two.</summary>
    public const int Kept = 2;

    private const int InListChunk = 500;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Test seam: runs on the container file right after it is written and before it is checked (a corrupted container must fail the check).</summary>
    internal Action<string>? AfterContainerWritten { get; set; }

    /// <summary>Test seam: called with each staging copy once the baseline is made and before the copies are removed; a test plants or holds a sidecar here.</summary>
    internal Action<string>? AfterStagingWork { get; set; }

    /// <summary>Test seam: the chunk size of the container (the default is 4 MiB).</summary>
    internal int ChunkSize { get; set; } = ChunkedContainer.DefaultChunkSize;

    /// <summary>Produces one baseline. Scoped like the repositories it reads; <see cref="BaselineProducerRunner"/> makes the scope and runs one at a time.</summary>
    public Task<BaselineFiles> ProduceAsync(string reason, CancellationToken ct = default) => ProduceCoreAsync(reason, ct);

    private async Task<BaselineFiles> ProduceCoreAsync(string reason, CancellationToken ct)
    {
        // 1. Preconditions that need no work.
        if (!policy.SealsMetadata)
            throw new BaselineRefusedException(BaselineRefusal.NotActivated, "Metadata encryption is not activated on this node: no baseline is produced.");
        if (!session.IsUnlocked)
            throw new BaselineRefusedException(BaselineRefusal.Locked, "The vault is locked: unlock it to produce a baseline.");
        // A rotation's new metadata key exists here before its metadata_key_set event does (spec 4.4): sealing under it would name a key no copy holds a row for.
        // NoMetadataKey is the reason the scheduler keeps the request for and asks again at once; the publication itself asks for the early baseline.
        if (publishGate is { IsPending: true })
            throw new BaselineRefusedException(BaselineRefusal.NoMetadataKey, "The new metadata key of the last key rotation is not published yet: no baseline is produced until it is.");
        var key = ring.Current
            ?? throw new BaselineRefusedException(BaselineRefusal.NoMetadataKey, "This node cannot open a metadata key now: no baseline is produced.");
        if (await held.AnyHeldAsync(ct))
            throw new BaselineRefusedException(BaselineRefusal.EventsHeld, "Some received changes are not applied yet. Unlock and wait for them to be applied, then try again.");
        var identity = await nodeRepo.GetAsync()
            ?? throw new BaselineRefusedException(BaselineRefusal.NoIdentity, "This node has no identity yet.");

        var dataPath = snapshots.DataPath;
        var producedRoot = ProducedBaselines.Root(dataPath);
        var needed = snapshots.LiveDatabaseBytes() * 2;
        EnsureFreeSpace(SnapshotStaging.DirIn(dataPath), needed);
        EnsureFreeSpace(producedRoot, needed);

        var baselineId = Guid.NewGuid();
        var staging = SnapshotStaging.NewFile(dataPath);
        var selfCheck = SnapshotStaging.NewFile(dataPath);
        var directory = ProducedBaselines.DirectoryOf(dataPath, baselineId);
        var complete = false;
        try
        {
            // 2. The cut, and the copy, with no event applied in between. The values are read before the copy: whatever the copy
            // holds beyond them is replayed from the deltas and every handler tolerates that.
            long logHead, lamport;
            List<BlindManifestPosition> pulled;
            int schemaVersion;
            using (await EventWriteGate.Instance.QuiesceAsync(ct))
            {
                logHead = await events.GetMaxSequenceAsync();
                lamport = clock.Current;
                pulled = (await positions.GetAllAsync()).Select(p => new BlindManifestPosition(p.RemoteNodeId, p.LastSequenceNum)).ToList();
                using var conn = (SqliteConnection)db.CreateConnection();
                schemaVersion = Convert.ToInt32(await conn.ExecuteScalarAsync<long?>("SELECT MAX(version) FROM tbl_migration") ?? 0);
                await conn.ExecuteAsync($"VACUUM INTO '{staging.Replace("'", "''")}'");
            }

            // 3. Filter, null, compact; the blob list; the references this node must be able to serve.
            await Task.Run(() => snapshots.FilterForBaseline(staging), ct);
            var (copyCounts, references) = ReadCopy(staging);
            var (blobList, blobBytes) = await CheckBlobsAsync(references, ct);

            // 4. Seal.
            EnsureOwnerOnlyDirectory(Path.GetDirectoryName(producedRoot)!);
            EnsureOwnerOnlyDirectory(producedRoot);
            OwnerOnlyFile.CreateDirectory(directory);
            var containerPath = Path.Combine(directory, BaselineManifest.ContainerFileName);
            // On an encrypted node the copy is a sealed file and the container is streamed from its plain image, page by page (spec 8.3).
            await using (var plain = OpenPlainImage(staging))
            await using (var output = OwnerOnlyFile.CreateNew(containerPath))
            {
                await ChunkedContainer.EncryptWithAsync(plain, output, ChunkedContainer.PurposeBaseline,
                    ChunkedContainer.MekKeyLabel(key.KeyId), (info, salt) => DeriveOrRefuse(key.KeyId, info, salt), ChunkSize, ct);
                await output.FlushAsync(ct);
            }
            AfterContainerWritten?.Invoke(containerPath);

            // 5. Self-check (MUST): the container opens, the database in it is whole and has what the copy had.
            await SelfCheckAsync(containerPath, selfCheck, key.KeyId, copyCounts, ct);

            // 6. Manifest and signature; the signature file is written last, so a directory without it is not a baseline.
            var blobListBytes = Encoding.ASCII.GetBytes(blobList.Count == 0 ? "" : string.Join('\n', blobList) + "\n");
            OwnerOnlyFile.WriteNew(Path.Combine(directory, BaselineManifest.BlobListFileName), blobListBytes);
            var manifest = new BaselineManifest(
                BaselineManifest.CurrentFormat, baselineId, identity.NodeId, _time.GetUtcNow().UtcDateTime, logHead, lamport, pulled,
                key.KeyId, key.Generation, schemaVersion,
                await Sha256OfFileAsync(containerPath, ct), new FileInfo(containerPath).Length,
                Convert.ToHexStringLower(SHA256.HashData(blobListBytes)), blobList.Count, blobBytes);
            var manifestBytes = manifest.ToBytes();
            var signature = snapshots.SignWithNodeIdentity(identity, BaselineManifest.SignaturePayload(manifestBytes));
            OwnerOnlyFile.WriteNew(Path.Combine(directory, BaselineManifest.FileName), manifestBytes);
            OwnerOnlyFile.WriteNew(Path.Combine(directory, BaselineManifest.SignatureFileName), signature);
            complete = true;

            Prune(dataPath, baselineId);
            logger.LogInformation(
                "Baseline {BaselineId} produced ({Reason}): key generation {Generation}, log head {Head}, {Blobs} blob(s), {Bytes} byte(s)",
                baselineId, reason, key.Generation, logHead, blobList.Count, manifest.ContainerBytes);
            return new BaselineFiles(directory, manifest, manifestBytes, signature);
        }
        finally
        {
            AfterStagingWork?.Invoke(staging);
            AfterStagingWork?.Invoke(selfCheck);
            // The plaintext copy and the self-check copy go with their SQLite sidecars. One that stays is repair-required: the baseline is not reported.
            var copiesGone = SqliteStaging.TryCleanup(staging) & SqliteStaging.TryCleanup(selfCheck);
            if (!copiesGone) logger.LogError("A staging copy of baseline {BaselineId} could not be removed; node repair is required.", baselineId);
            // An incomplete directory is not a baseline and is no use to anyone: remove what this run made, and a baseline that cannot be reported.
            if (!complete || !copiesGone) TryDeleteDirectory(directory);
            if (complete && !copiesGone)
                throw new IOException("A staging copy of the baseline could not be removed. The baseline requires repair and was not reported as made.");
        }
    }

    /// <summary>The plain image of the working copy: the file itself on a plain node, the page codec's stream over the sealed copy on an encrypted one.</summary>
    private Stream OpenPlainImage(string stagedPath)
    {
        if (!db.IsSealed)
            return new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var pageKey = db.CopyPageKey()!;
        try { return PlainImageReader.Open(stagedPath, pageKey); }
        finally { CryptographicOperations.ZeroMemory(pageKey); }
    }

    private byte[] DeriveOrRefuse(string keyId, string info, ReadOnlySpan<byte> salt)
    {
        try
        {
            return ring.DeriveKey(keyId, info, salt);
        }
        catch (MetadataKeyUnavailableException ex)
        {
            throw new BaselineRefusedException(BaselineRefusal.Locked, "The metadata key closed while the baseline was being sealed.", ex);
        }
    }

    private static void EnsureFreeSpace(string path, long bytes)
    {
        try
        {
            var probe = Path.GetFullPath(path);
            while (!Directory.Exists(probe) && Path.GetDirectoryName(probe) is { } parent) probe = parent;
            var free = new DriveInfo(Path.GetPathRoot(probe)!).AvailableFreeSpace;
            if (free < bytes)
                throw new BaselineRefusedException(BaselineRefusal.NoDiskSpace,
                    $"Not enough free disk space for a baseline: about {bytes / (1024 * 1024)} MB needed, {free / (1024 * 1024)} MB free.");
        }
        catch (Exception ex) when (ex is ArgumentException or DriveNotFoundException or IOException)
        {
            // DriveInfo fails on unusual paths; the later writes fail loudly if the disk is really full.
        }
    }

    /// <summary>The row counts the self-check compares, and the blob hashes the copy's rows reference (spec 7.6).</summary>
    private (CopyCounts Counts, List<string> References) ReadCopy(string stagedDbPath)
    {
        using var conn = db.OpenFile(stagedDbPath, readOnly: true);
        var counts = new CopyCounts(
            conn.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_folder"),
            conn.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_article"),
            conn.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_media"));
        var hashes = conn.Query<string>(
            @"SELECT ciphertext_hash FROM tbl_article_body WHERE ciphertext_hash IS NOT NULL
              UNION SELECT ciphertext_hash FROM tbl_article_version WHERE ciphertext_hash IS NOT NULL
              UNION SELECT ciphertext_sha256 FROM tbl_media WHERE ciphertext_sha256 IS NOT NULL").ToList();
        var wellFormed = hashes.Where(BlobReferences.IsWellFormedHash).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (wellFormed.Count != hashes.Count)
            logger.LogWarning("{Count} blob reference(s) of the baseline copy are not well-formed hashes and are not listed", hashes.Count - wellFormed.Count);
        return (counts, wellFormed);
    }

    /// <summary>This node must hold every blob the rows reference (spec 7.3 step 1); returns the list and their total size.</summary>
    private async Task<(List<string> List, long Bytes)> CheckBlobsAsync(List<string> references, CancellationToken ct)
    {
        var present = await blobs.GetExistingAsync(references);
        var missing = references.Count(h => !present.Contains(h));
        if (missing > 0)
            throw new BaselineRefusedException(BaselineRefusal.BlobMissing,
                $"This node does not hold {missing} stored content blob(s) the notes refer to; a baseline needs them all. Sync with a node that has them first.");

        long bytes = 0;
        using var conn = db.CreateConnection();
        foreach (var chunk in references.Chunk(InListChunk))
            bytes += await conn.ExecuteScalarAsync<long>("SELECT COALESCE(SUM(size), 0) FROM tbl_blob WHERE hash IN @chunk", new { chunk });
        return (references, bytes);
    }

    private async Task SelfCheckAsync(string containerPath, string checkPath, string keyId, CopyCounts expected, CancellationToken ct)
    {
        try
        {
            Func<ChunkedContainerHeader, ChunkKeyDeriver?> resolve = h =>
                h.Key == ChunkedContainer.MekKeyLabel(keyId) ? (info, salt) => DeriveOrRefuse(keyId, info, salt) : null;
            if (db.IsSealed)
            {
                // The container opens into a second sealed copy through the importer (never a plain file); the checks run on that.
                await SealedStaging.ImportAsync(db, checkPath, async (sink, token) =>
                {
                    await using var input = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                    await ChunkedContainer.DecryptWithAsync(input, sink, ChunkedContainer.PurposeBaseline, resolve, token);
                }, ct);
            }
            else
            {
                await using var input = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await using var output = new FileStream(checkPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await ChunkedContainer.DecryptWithAsync(input, output, ChunkedContainer.PurposeBaseline, resolve, ct);
            }

            using var conn = db.OpenFile(checkPath, readOnly: true);
            var integrity = conn.ExecuteScalar<string>("PRAGMA integrity_check");
            if (integrity != "ok") throw new InvalidDataException("integrity_check failed");
            var actual = new CopyCounts(
                conn.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_folder"),
                conn.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_article"),
                conn.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_media"));
            if (actual != expected) throw new InvalidDataException("row counts differ");
        }
        catch (Exception ex) when (ex is InvalidDataException or SqliteException)
        {
            throw new BaselineRefusedException(BaselineRefusal.SelfCheckFailed, $"The baseline failed its own check and was discarded ({ex.Message}).", ex);
        }
    }

    private void Prune(string dataPath, Guid justProduced)
    {
        try
        {
            var keep = Directory.EnumerateDirectories(ProducedBaselines.Root(dataPath))
                .Where(ProducedBaselines.IsComplete)
                .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, BaselineManifest.SignatureFileName)))
                .Take(Kept)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            keep.Add(ProducedBaselines.DirectoryOf(dataPath, justProduced));
            foreach (var dir in Directory.EnumerateDirectories(ProducedBaselines.Root(dataPath)).Where(ProducedBaselines.IsComplete).ToList())
                if (!keep.Contains(dir)) TryDeleteDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Older produced baselines could not be removed: {Reason}", ex.Message);
        }
    }

    private static void EnsureOwnerOnlyDirectory(string path)
    {
        if (!Directory.Exists(path)) OwnerOnlyFile.CreateDirectory(path);
    }

    private static async Task<string> Sha256OfFileAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    private static void TryDeleteDirectory(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a product cache: the next run removes it */ }
    }

    private readonly record struct CopyCounts(long Folders, long Articles, long Media);
}

/// <summary>
/// The singleton face of <see cref="BaselineProducer"/> (whose repositories are scoped): one production at a time on this node, each in
/// its own scope. The scheduler and the Admin button go through it.
/// </summary>
public sealed class BaselineProducerRunner(IServiceScopeFactory scopes) : IBaselineProducer
{
    private readonly SemaphoreSlim _one = new(1, 1);

    public async Task<BaselineFiles> ProduceAsync(string reason, CancellationToken ct = default)
    {
        await _one.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<BaselineProducer>().ProduceAsync(reason, ct);
        }
        finally
        {
            _one.Release();
        }
    }
}
