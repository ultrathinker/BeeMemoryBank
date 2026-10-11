using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>
/// A blind host's baseline store (docs/design/step-a-spec.md 9): the one signed, encrypted baseline it serves and restores
/// from (<c>tbl_baseline</c> and its files under <c>&lt;data&gt;/baselines/current/</c>), adopted only forward, and what
/// adoption does to the rest of the node: the first one purges the legacy plaintext content (9.3), each one trims the log
/// to what the baseline covers (9.5) and lets the blob store drop what nothing references any more (9.6). It never opens a
/// baseline and holds no key: the checks are signatures, hashes and positions.
///
/// <para>Files and rows switch in an order a crash cannot break: the new files are staged complete in
/// <c>baselines/incoming-&lt;id&gt;/</c>, then the rows switch in one transaction, then the directories; the row is the
/// record, and <see cref="GetCurrentAsync"/> finishes a switch the process did not.</para>
/// </summary>
public sealed partial class BaselineStore(
    DbConnectionFactory factory,
    string dataPath,
    ISyncProtocolPolicy policy,
    ILogger<BaselineStore>? logger = null,
    TimeProvider? time = null) : IBaselineStore
{
    /// <summary><c>tbl_blind_state</c> key set once the first format-2 adoption has purged the legacy content (spec 9.3).</summary>
    public const string OpaqueStoreKey = "opaque_store";

    /// <summary>How long an unreferenced blob is kept on a blind host (spec 9.6): long enough for a sender's blobs to wait for their package.</summary>
    public static readonly TimeSpan BlobGracePeriod = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a successful check of the current baseline's container (SHA-256 and framing) stands while its size and
    /// modification time are unchanged. The check is repeated after this, and at once when either changes.
    /// </summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(1);

    private const string CurrentDirectoryName = "current";
    private const string IncomingPrefix = "incoming-";
    private const string RetiredPrefix = "retired-";

    private readonly ILogger _logger = logger ?? NullLogger<BaselineStore>.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ContainerCheck? _containerCheck;
    private string? _unservableLogged;

    /// <summary>The outcome of hashing and framing one container file, for the size and modification time it had then.</summary>
    private sealed record ContainerCheck(string BaselineId, long Length, DateTime Stamp, DateTimeOffset At, bool Matches);

    /// <summary><c>&lt;data&gt;/baselines</c>: the current baseline's files, and a staged one while it is being adopted.</summary>
    public string BaselinesDirectory => Path.Combine(dataPath, "baselines");

    /// <summary>Where the current baseline's four files are (spec 9.1). Backups add this folder (9.8).</summary>
    public string CurrentDirectory => Path.Combine(BaselinesDirectory, CurrentDirectoryName);

    public async Task<AdoptedBaseline?> GetCurrentAsync(CancellationToken ct = default)
    {
        var row = await CurrentRowAsync();
        if (row is null) return null;
        if (!FilesMatch(row))
        {
            await _gate.WaitAsync(ct);
            try
            {
                ReconcileFiles(row);
            }
            finally
            {
                _gate.Release();
            }
            if (!FilesMatch(row))
            {
                // Once per baseline: the status page polls this.
                if (_unservableLogged != row.BaselineId)
                {
                    _unservableLogged = row.BaselineId;
                    _logger.LogError("Baseline {BaselineId}: its files are missing or are not the signed ones; this node cannot serve it until the same baseline is sent again", row.BaselineId);
                }
                return null;
            }
        }
        _unservableLogged = null;
        return row.ToAdopted(CurrentDirectory);
    }

    public async Task<AdoptResult> AdoptAsync(BaselineFiles files, long localCut, bool producerAuthorized, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await AdoptLockedAsync(files, localCut, producerAuthorized, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AdoptResult> AdoptLockedAsync(BaselineFiles files, long localCut, bool producerAuthorized, CancellationToken ct)
    {
        // The signed bytes are what counts; the record given with them must say the same.
        BaselineManifest m;
        try
        {
            m = BaselineManifest.Parse(files.ManifestBytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
        {
            return Refused($"its manifest cannot be read ({ex.Message})");
        }
        if (!m.ToBytes().AsSpan().SequenceEqual(files.Manifest.ToBytes()))
            return Refused("the manifest bytes are not the manifest given");

        // 1. The producer: the caller's authority (pair code or whitelist), and never against this node's own record of it.
        if (!producerAuthorized)
            return Refused("the producer is not authorized to give this node a baseline");
        if (await ProducerRowRefusalAsync(files) is { } producerProblem)
            return Refused(producerProblem);
        IReadOnlyList<string> blobs;
        try
        {
            blobs = await BlindPackageV2Reader.VerifyBaselineFilesAsync(files.Directory, m, ct);
        }
        catch (InvalidDataException ex)
        {
            return Refused(ex.Message);
        }

        // 2. Monotonic: a producer cannot roll this node back, in time, in key, or in its own log.
        var current = await CurrentRowAsync();
        if (current is not null)
        {
            if (current.BaselineId == m.BaselineId.ToString())
            {
                if (!files.ManifestBytes.AsSpan().SequenceEqual(current.Manifest) || !files.Signature.AsSpan().SequenceEqual(current.Signature))
                    return Refused("a baseline with this id is current here, but its signed manifest is another one");
                ReconcileFiles(current);
                if (!FilesMatch(current))
                {
                    // The files on this disk are not the signed ones (a change, a bit flip, a half-finished switch); the same
                    // baseline, verified just now, puts them back.
                    _logger.LogWarning("Baseline {BaselineId}: its files on this node were not the signed ones; restoring them from the package", m.BaselineId);
                    var restage = Path.Combine(BaselinesDirectory, IncomingPrefix + m.BaselineId.ToString("N"));
                    StageFiles(files, restage);
                    SwitchDirectories(restage);
                }
                // A purge the first adoption did not finish (a failed VACUUM, a crash) is retried by the next one, this included.
                if (!await IsOpaqueAsync()) await PurgeLockedAsync(ct);
                return new AdoptResult(AdoptOutcome.AlreadyCurrent, null, 0, 0);
            }
            if (m.LamportAtCut < current.LamportAtCut)
                return Refused($"its cut (Lamport {m.LamportAtCut}) is older than the current baseline's ({current.LamportAtCut})");
            if (m.KeyGeneration < current.KeyGeneration)
                return Refused($"it is sealed under an older key generation ({m.KeyGeneration} < {current.KeyGeneration})");
            if (localCut < current.LocalCut)
                return Refused($"it covers less of this node's log ({localCut}) than the current baseline ({current.LocalCut})");
        }
        if (localCut < 0) return Refused("a negative local cut");

        // 3. Every blob it names is here (the sender ships missing ones first; a phone fetches them).
        var present = await new BlobRepository(factory).GetExistingAsync(blobs.ToList());
        var missing = blobs.Count(h => !present.Contains(h));
        if (missing > 0)
            return Refused($"{missing} of its {blobs.Count} blobs are not stored here");

        // 4. Files staged complete, rows switched in one transaction, then the directories.
        var incoming = Path.Combine(BaselinesDirectory, IncomingPrefix + m.BaselineId.ToString("N"));
        StageFiles(files, incoming);
        // "First" is decided by the flag the purge sets, not by the absence of a row: an adoption whose purge failed leaves
        // a row and no flag, and the legacy content must not outlive it.
        var firstAdoption = !await IsOpaqueAsync();
        await SwitchRowsAsync(files, localCut, blobs, current);
        SwitchDirectories(incoming);
        _logger.LogInformation("Baseline {BaselineId} from {Producer} adopted (generation {Generation}, local cut {Cut})",
            m.BaselineId, m.ProducerNodeId, m.KeyGeneration, localCut);

        // 5. The first adoption on this host purges the legacy plaintext content (spec 9.3).
        if (firstAdoption)
            await PurgeLockedAsync(ct);

        // 6. Trim the log to what the baseline covers, collect the blobs nothing references any more.
        var trimmed = await TrimLockedAsync();
        var collected = await CollectGarbageLockedAsync();
        return new AdoptResult(AdoptOutcome.Adopted, null, trimmed, collected);
    }

    public async Task<int> CollectGarbageAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await CollectGarbageLockedAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Spec 9.6, through the one statement full and blind hosts share (<see cref="BlobRepository.SweepUnreferencedAsync"/>):
    /// it keeps the blobs of the current baseline (<c>tbl_baseline_blob</c>), of stored sealed events (<c>$.b</c>) and of
    /// stored protocol-1..3 rows, and everything younger than <see cref="BlobGracePeriod"/>.
    /// </summary>
    private Task<int> CollectGarbageLockedAsync() =>
        new BlobRepository(factory).SweepUnreferencedAsync(_time.GetUtcNow().UtcDateTime - BlobGracePeriod);

    private AdoptResult Refused(string reason)
    {
        _logger.LogWarning("Baseline refused: {Reason}", reason);
        return new AdoptResult(AdoptOutcome.Refused, reason, 0, 0);
    }

    /// <summary>
    /// Defence in depth for spec 9.2 step 1: whatever the caller decided, a producer this node has a row for must be active
    /// and superadmin there, and its key must have signed the manifest. (A first seed's producer may have no row yet; the
    /// pair code vouched for it and the reader checked its signature.)
    /// </summary>
    private async Task<string?> ProducerRowRefusalAsync(BaselineFiles files)
    {
        var row = await new WhitelistRepository(factory).GetByNodeIdAsync(files.Manifest.ProducerNodeId, includeDeleted: true);
        if (row is null) return null;
        if (row.Status != WhitelistStatuses.Active || row.DeletedAt is not null)
            return "its producer is not an active node here";
        if (!row.IsSuperadmin)
            return "its producer is not a superadmin here";
        if (files.Signature.Length != 64
            || !Ed25519Signer.Verify(row.Ed25519PublicKey, BaselineManifest.SignaturePayload(files.ManifestBytes), files.Signature))
            return "its signature does not verify under its producer's key";
        return null;
    }

    private Task<bool> IsOpaqueAsync() => IsOpaqueAsync(factory);

    /// <summary>Whether the first adoption's purge has finished on this node (the flag is set after the file is rebuilt, not before).</summary>
    public static async Task<bool> IsOpaqueAsync(IDbConnectionFactory factory)
    {
        using var conn = factory.CreateConnection();
        return await conn.ExecuteScalarAsync<string?>("SELECT value FROM tbl_blind_state WHERE key = @OpaqueStoreKey", new { OpaqueStoreKey }) == "1";
    }

    /// <summary>
    /// Whether a baseline was ever adopted here (the current row exists), whatever the state of its files. The legacy content is
    /// purged only after that: until then it is the only copy of what this node holds (spec 9.3, 9.5).
    /// </summary>
    public static async Task<bool> HasAdoptedAsync(IDbConnectionFactory factory)
    {
        using var conn = factory.CreateConnection();
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_baseline WHERE is_current = 1") > 0;
    }

    private async Task SwitchRowsAsync(BaselineFiles files, long localCut, IReadOnlyList<string> blobs, BaselineRow? previous)
    {
        var m = files.Manifest;
        var id = m.BaselineId.ToString();
        using var conn = factory.CreateConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        using var tx = conn.BeginTransaction();
        if (previous is not null)
        {
            await conn.ExecuteAsync("DELETE FROM tbl_baseline_blob WHERE baseline_id = @BaselineId", new { previous.BaselineId }, tx);
            await conn.ExecuteAsync("DELETE FROM tbl_baseline WHERE baseline_id = @BaselineId", new { previous.BaselineId }, tx);
        }
        await conn.ExecuteAsync("DELETE FROM tbl_baseline_blob WHERE baseline_id = @id", new { id }, tx);
        await conn.ExecuteAsync("DELETE FROM tbl_baseline WHERE baseline_id = @id", new { id }, tx);
        await conn.ExecuteAsync(
            @"INSERT INTO tbl_baseline (baseline_id, producer_node_id, created_at, adopted_at, lamport_at_cut, local_cut, key_id,
                                        key_generation, container_sha256, container_bytes, blob_count, blob_bytes, manifest, signature, is_current)
              VALUES (@id, @producer, @createdAt, @adoptedAt, @LamportAtCut, @localCut, @KeyId, @KeyGeneration, @ContainerSha256,
                      @ContainerBytes, @BlobCount, @BlobBytes, @manifest, @signature, 1)",
            new
            {
                id, producer = m.ProducerNodeId.ToString(), createdAt = m.CreatedAt.ToUniversalTime().ToString("O"),
                adoptedAt = _time.GetUtcNow().UtcDateTime.ToString("O"), m.LamportAtCut, localCut, m.KeyId, m.KeyGeneration,
                m.ContainerSha256, m.ContainerBytes, m.BlobCount, m.BlobBytes, manifest = files.ManifestBytes, signature = files.Signature,
            }, tx);
        foreach (var hash in blobs)
            await conn.ExecuteAsync("INSERT INTO tbl_baseline_blob (baseline_id, hash) VALUES (@id, @hash)", new { id, hash }, tx);
        tx.Commit();
    }

    /// <summary>The four files into <paramref name="incoming"/>, complete and checked, before any row or directory switches.</summary>
    private static void StageFiles(BaselineFiles files, string incoming)
    {
        if (Directory.Exists(incoming)) Directory.Delete(incoming, recursive: true);
        Directory.CreateDirectory(incoming);
        OwnerOnly(incoming);
        File.WriteAllBytes(Path.Combine(incoming, BaselineManifest.FileName), files.ManifestBytes);
        File.WriteAllBytes(Path.Combine(incoming, BaselineManifest.SignatureFileName), files.Signature);
        foreach (var name in new[] { BaselineManifest.BlobListFileName, BaselineManifest.ContainerFileName })
        {
            var target = Path.Combine(incoming, name);
            File.Copy(Path.Combine(files.Directory, name), target);
            using var flush = new FileStream(target, FileMode.Open, FileAccess.ReadWrite);
            flush.Flush(flushToDisk: true);
        }
    }

    /// <summary><c>current</c> aside, <c>incoming</c> in its place, the old one deleted (product files of this store only).</summary>
    private void SwitchDirectories(string incoming)
    {
        string? retired = null;
        if (Directory.Exists(CurrentDirectory))
        {
            retired = Path.Combine(BaselinesDirectory, RetiredPrefix + Guid.NewGuid().ToString("N"));
            Directory.Move(CurrentDirectory, retired);
        }
        Directory.Move(incoming, CurrentDirectory);
        if (retired is not null) TryDeleteDirectory(retired);
    }

    /// <summary>
    /// Finishes a switch a crash interrupted: the row is the record. If the current directory does not hold the row's
    /// manifest, the staged directory of that baseline takes its place. Left-over staged and retired directories go.
    /// </summary>
    private void ReconcileFiles(BaselineRow row)
    {
        if (!Directory.Exists(BaselinesDirectory)) return;
        var incoming = Path.Combine(BaselinesDirectory, IncomingPrefix + Guid.Parse(row.BaselineId).ToString("N"));
        if (!FilesMatch(row) && Directory.Exists(incoming))
        {
            _logger.LogWarning("Baseline {BaselineId}: finishing a switch of its files that did not complete", row.BaselineId);
            SwitchDirectories(incoming);
        }
        foreach (var dir in Directory.EnumerateDirectories(BaselinesDirectory))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith(RetiredPrefix, StringComparison.Ordinal)
                || (name.StartsWith(IncomingPrefix, StringComparison.Ordinal) && dir != incoming))
                TryDeleteDirectory(dir);
        }
    }

    /// <summary>
    /// Whether the four files in <c>current/</c> are the baseline the row records: the manifest and its signature byte for
    /// byte, the blob list and the container as the signed manifest says (size, SHA-256, and the BMBCH1 framing). The
    /// container's hash is cached for <see cref="RecheckInterval"/> per size and modification time, so a status poll does not
    /// read a large file each time. Anything else than a clean match is "not servable".
    /// </summary>
    private bool FilesMatch(BaselineRow row)
    {
        try
        {
            var m = BaselineManifest.Parse(row.Manifest);
            var manifest = Path.Combine(CurrentDirectory, BaselineManifest.FileName);
            var signature = Path.Combine(CurrentDirectory, BaselineManifest.SignatureFileName);
            var blobList = Path.Combine(CurrentDirectory, BaselineManifest.BlobListFileName);
            var container = new FileInfo(Path.Combine(CurrentDirectory, BaselineManifest.ContainerFileName));
            if (!File.Exists(manifest) || !File.Exists(signature) || !File.Exists(blobList) || !container.Exists) return false;
            if (!File.ReadAllBytes(manifest).AsSpan().SequenceEqual(row.Manifest)) return false;
            if (!File.ReadAllBytes(signature).AsSpan().SequenceEqual(row.Signature)) return false;
            if (!string.Equals(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(blobList))),
                    m.BlobListSha256, StringComparison.Ordinal)) return false;
            if (container.Length != m.ContainerBytes) return false;

            var now = _time.GetUtcNow();
            var known = _containerCheck;
            if (known is not null && known.BaselineId == row.BaselineId && known.Length == container.Length
                && known.Stamp == container.LastWriteTimeUtc && now - known.At < RecheckInterval)
                return known.Matches;

            bool matches;
            try
            {
                using (var stream = container.OpenRead())
                    matches = string.Equals(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)),
                        m.ContainerSha256, StringComparison.Ordinal);
                if (matches) BaselineContainerFraming.Validate(container.FullName, m.KeyId);
            }
            catch (InvalidDataException)
            {
                matches = false;
            }
            _containerCheck = new ContainerCheck(row.BaselineId, container.Length, container.LastWriteTimeUtc, now, matches);
            return matches;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private async Task<BaselineRow?> CurrentRowAsync()
    {
        using var conn = factory.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<BaselineRow>(
            @"SELECT baseline_id AS BaselineId, lamport_at_cut AS LamportAtCut, local_cut AS LocalCut,
                     key_generation AS KeyGeneration, adopted_at AS AdoptedAt, manifest AS Manifest, signature AS Signature
              FROM tbl_baseline WHERE is_current = 1");
    }

    private void TryDeleteDirectory(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException ex) { _logger.LogWarning(ex, "Could not remove {Dir}; the next adoption retries", dir); }
        catch (UnauthorizedAccessException ex) { _logger.LogWarning(ex, "Could not remove {Dir}; the next adoption retries", dir); }
    }

    private static void OwnerOnly(string dir)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private sealed class BaselineRow
    {
        public string BaselineId { get; set; } = "";
        public long LamportAtCut { get; set; }
        public long LocalCut { get; set; }
        public int KeyGeneration { get; set; }
        public string AdoptedAt { get; set; } = "";
        public byte[] Manifest { get; set; } = [];
        public byte[] Signature { get; set; } = [];

        public AdoptedBaseline ToAdopted(string directory) =>
            new(new BaselineFiles(directory, BaselineManifest.Parse(Manifest), Manifest, Signature), LocalCut,
                DateTime.Parse(AdoptedAt, null, System.Globalization.DateTimeStyles.RoundtripKind));
    }
}
