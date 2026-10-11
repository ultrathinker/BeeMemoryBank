using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Sync.Blind.Format2;

/// <summary>What a host gives the format-2 writer: how it signs, where it writes, how it describes itself.</summary>
/// <param name="Sign">Signs a payload with the serving node's identity key (the session's on a full node, the external key
/// on a blind host). A full node that is locked cannot sign; the write then fails, as a format-1 package does.</param>
/// <param name="DefaultOutputDirectory">Where a package goes when the request names no directory.</param>
/// <param name="StagingDirectory">Owner-only scratch space for <c>control.db</c> and <c>deltas.jsonl</c>; emptied after each write.</param>
/// <param name="ServingNodeIsSuperadmin">How the serving node's own row reads in the package. Asked once per package; a blind
/// node never is one.</param>
/// <param name="ServingAddress">Where a receiver can dial the serving node (BMB_PUBLIC_ADDRESS), or null.</param>
/// <param name="ServingTlsSpki">The pin of that address, or null.</param>
public sealed record BlindPackageV2WriterOptions(
    Func<NodeIdentity, byte[], byte[]> Sign,
    string DefaultOutputDirectory,
    string StagingDirectory,
    Func<CancellationToken, Task<bool>>? ServingNodeIsSuperadmin = null,
    string? ServingAddress = null,
    string? ServingTlsSpki = null);

/// <summary>
/// Assembles and signs a format-2 package (docs/design/step-a-spec.md 8): a baseline this node can serve, and for a
/// <c>full</c> package the clear control tables (<c>control.db</c>, 8.4), the deltas of this node's own log after the
/// baseline's cut (<c>deltas.jsonl</c>, 8.5) and, on request, the blobs both need. It never opens anything: the baseline is
/// copied as it is, the deltas are the stored signed bytes, the control rows are the clear recovery and key material every
/// node holds. No protocol-1..3 content event is ever written into a package (5.7), so no title or path travels in one.
/// </summary>
public sealed class BlindPackageV2Writer(
    IDbConnectionFactory db,
    INodeIdentityRepository nodeRepo,
    IWhitelistRepository whitelist,
    ISyncPositionRepository positions,
    IEventLogRepository events,
    BlindPackageV2WriterOptions options) : IBlindPackageV2Writer
{
    private const int DeltaPage = 1000;

    public async Task<BlindPackageV2> WriteAsync(BlindPackageV2Request request, CancellationToken ct = default)
    {
        if (request.Kind is not (BlindPackageKinds.Full or BlindPackageKinds.Baseline))
            throw new ArgumentException($"Unknown package kind '{request.Kind}'.", nameof(request));
        var full = request.Kind == BlindPackageKinds.Full;
        if (request.IncludeBlobs && !full)
            throw new ArgumentException("Only a full package carries blobs.", nameof(request));

        var self = await nodeRepo.GetAsync() ?? throw new InvalidOperationException("Node is not initialized.");
        var baseline = request.Baseline;
        BaselineContainerFraming.Validate(Path.Combine(baseline.Files.Directory, BaselineManifest.ContainerFileName), baseline.Files.Manifest.KeyId);
        var cp = await events.GetMaxSequenceAsync();
        var outputDir = request.OutputDirectory ?? options.DefaultOutputDirectory;
        Directory.CreateDirectory(outputDir);
        var staging = Path.Combine(options.StagingDirectory, "v2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            // The parts the package carries beside the baseline, staged as files so each is hashed before it is written.
            string? controlDb = null, deltas = null;
            var blobHashes = new SortedSet<string>(StringComparer.Ordinal);
            var deltaCount = 0;
            if (full)
            {
                controlDb = Path.Combine(staging, BlindManifestV2.ControlDbFileName);
                await WriteControlDbAsync(controlDb, ct);
                deltas = Path.Combine(staging, BlindManifestV2.DeltasFileName);
                deltaCount = await WriteDeltasAsync(deltas, baseline.CutInLocalLog, cp, request.IncludeBlobs ? blobHashes : null, ct);
                if (request.IncludeBlobs)
                    foreach (var hash in Format2Package.ParseBlobList(
                                 await File.ReadAllBytesAsync(Path.Combine(baseline.Files.Directory, BaselineManifest.BlobListFileName), ct)))
                        blobHashes.Add(hash);
            }

            var manifest = await BuildManifestAsync(request, self, cp, deltaCount, full && request.IncludeBlobs, ct);
            var manifestBytes = manifest.ToBytes();

            var files = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                [BlindManifestV2.FileName] = Convert.ToHexStringLower(SHA256.HashData(manifestBytes)),
                [Format2Package.BaselineManifestEntry] = Convert.ToHexStringLower(SHA256.HashData(baseline.Files.ManifestBytes)),
                [Format2Package.BaselineSignatureEntry] = Convert.ToHexStringLower(SHA256.HashData(baseline.Files.Signature)),
                [Format2Package.BaselineBlobListEntry] = await Format2Package.HashFileAsync(
                    Path.Combine(baseline.Files.Directory, BaselineManifest.BlobListFileName), ct),
                [Format2Package.BaselineContainerEntry] = await Format2Package.HashFileAsync(
                    Path.Combine(baseline.Files.Directory, BaselineManifest.ContainerFileName), ct),
            };
            // What is copied must be what the producer signed: a file that changed on this node's disk is never packed.
            if (!string.Equals(files[Format2Package.BaselineContainerEntry], baseline.Files.Manifest.ContainerSha256, StringComparison.Ordinal))
                throw new InvalidDataException("The baseline container does not match its signed hash; it is not served.");
            if (!string.Equals(files[Format2Package.BaselineBlobListEntry], baseline.Files.Manifest.BlobListSha256, StringComparison.Ordinal))
                throw new InvalidDataException("The baseline's blob list does not match its signed hash; it is not served.");
            if (controlDb is not null) files[BlindManifestV2.ControlDbFileName] = await Format2Package.HashFileAsync(controlDb, ct);
            if (deltas is not null) files[BlindManifestV2.DeltasFileName] = await Format2Package.HashFileAsync(deltas, ct);
            // A blob's name is its SHA-256; it is checked against its bytes when it is written below.
            foreach (var hash in blobHashes) files[Format2Package.BlobEntry(hash)] = hash;

            var outerBytes = OuterManifest(self.NodeId, cp, files, await SchemaVersionAsync());
            var embeddedSignature = options.Sign(self, Format2Package.EmbeddedSignaturePayload(outerBytes));

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var path = Path.Combine(outputDir, $"bmb-blind-v2-{request.Kind}-{timestamp}-{Guid.NewGuid():N}.tar.gz");
            await using (var file = File.Create(path))
            await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
            using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
            {
                await WriteBytesAsync(tar, BlindManifestV2.FileName, manifestBytes, ct);
                await WriteBytesAsync(tar, Format2Package.BaselineManifestEntry, baseline.Files.ManifestBytes, ct);
                await WriteBytesAsync(tar, Format2Package.BaselineSignatureEntry, baseline.Files.Signature, ct);
                await WriteFileAsync(tar, Format2Package.BaselineBlobListEntry,
                    Path.Combine(baseline.Files.Directory, BaselineManifest.BlobListFileName), ct);
                await WriteFileAsync(tar, Format2Package.BaselineContainerEntry,
                    Path.Combine(baseline.Files.Directory, BaselineManifest.ContainerFileName), ct);
                if (controlDb is not null) await WriteFileAsync(tar, BlindManifestV2.ControlDbFileName, controlDb, ct);
                if (deltas is not null) await WriteFileAsync(tar, BlindManifestV2.DeltasFileName, deltas, ct);
                await WriteBlobsAsync(tar, blobHashes, ct);
                await WriteBytesAsync(tar, Format2Package.ManifestFileName, outerBytes, ct);
                await WriteBytesAsync(tar, Format2Package.ManifestSignatureFileName, embeddedSignature, ct);
            }

            var detached = options.Sign(self, await Format2Package.DetachedSignaturePayloadAsync(outerBytes, path, ct));
            await File.WriteAllBytesAsync(path + ".sig", detached, ct);
            return new BlindPackageV2(path, await Format2Package.HashFileAsync(path, ct), new FileInfo(path).Length, detached, manifest);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (IOException) { /* swept with the staging folder at the next start */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<BlindManifestV2> BuildManifestAsync(
        BlindPackageV2Request request, NodeIdentity self, long cp, int deltaCount, bool containsBlobs, CancellationToken ct)
    {
        var servingIsSuperadmin = options.ServingNodeIsSuperadmin is { } ask && await ask(ct);
        var spki = string.IsNullOrEmpty(options.ServingAddress) ? null : options.ServingTlsSpki;
        var peers = (await whitelist.GetAllActiveAsync())
            .Where(r => r.NodeId != self.NodeId)
            .Select(r => new BlindManifestPeer(r.NodeId, r.DisplayName, Convert.ToBase64String(r.Ed25519PublicKey),
                r.ApiAddress, r.IsSuperadmin, r.EffectivePin, r.LamportTs, r.SourceNodeId, r.EffectiveTlsTrust))
            .Prepend(new BlindManifestPeer(self.NodeId, self.DisplayName, Convert.ToBase64String(self.Ed25519PublicKey),
                string.IsNullOrEmpty(options.ServingAddress) ? null : options.ServingAddress, servingIsSuperadmin, spki,
                LamportTs: 0, SourceNodeId: null, TlsTrust: spki is null ? null : BlindTrust.Pin))
            .ToList();
        var pulled = (await positions.GetAllAsync())
            .Select(p => new BlindManifestPosition(p.RemoteNodeId, p.LastSequenceNum))
            .ToList();
        return new BlindManifestV2(BlindManifestV2.CurrentFormat, request.Kind, request.SeedId, self.NodeId, cp,
            request.IncludesUpTo, DateTime.UtcNow, peers, pulled, request.Baseline.Files.Manifest.BaselineId,
            request.Baseline.CutInLocalLog, deltaCount, containsBlobs, await ActivationAsync(),
            await StandingEventsAsync(self.NodeId));
    }

    /// <summary>
    /// <c>control.db</c> (spec 8.4): a plain SQLite file holding exactly the tables of <see cref="SnapshotTables.Control"/>,
    /// row for row, same column names. Copied through an ATTACH on one connection, detached again before it goes back.
    /// </summary>
    private async Task WriteControlDbAsync(string path, CancellationToken ct)
    {
        using (var created = new SqliteConnection($"Data Source={path};Pooling=False"))
            created.Open();
        using var conn = db.CreateConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        await conn.ExecuteAsync($"ATTACH DATABASE {Quote(path)} AS ctl");
        try
        {
            foreach (var table in SnapshotTables.Control)
            {
                ct.ThrowIfCancellationRequested();
                await conn.ExecuteAsync($"CREATE TABLE ctl.\"{table}\" AS SELECT * FROM main.\"{table}\"");
            }
        }
        finally
        {
            await conn.ExecuteAsync("DETACH DATABASE ctl");
        }
        using var cleared = new SqliteConnection($"Data Source={path};Pooling=False");
        SqliteConnection.ClearPool(cleared);
    }

    /// <summary>
    /// <c>deltas.jsonl</c> (spec 8.5): every row of this node's log after <paramref name="cut"/> up to <paramref name="head"/>,
    /// in order, in wire form; protocol-1..3 content rows are left out (5.7). Collects the blobs they reference.
    /// </summary>
    private async Task<int> WriteDeltasAsync(string path, long cut, long head, ISet<string>? blobHashes, CancellationToken ct)
    {
        var count = 0;
        await using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false)) { NewLine = "\n" };
        var after = cut;
        while (after < head)
        {
            var page = await events.GetAllAfterSequenceAsync(after, DeltaPage);
            if (page.Count == 0) break;
            foreach (var evt in page)
            {
                ct.ThrowIfCancellationRequested();
                if (evt.SequenceNum > head) break;
                after = evt.SequenceNum;
                if (EventClasses.IsClearContent(evt.ProtocolVersion, evt.EventType)) continue;
                if (evt.LamportTs < 0 || evt.LamportTs > BlindManifestV2.MaximumLamport)
                    throw new InvalidDataException($"Event {evt.EventId} has a Lamport time outside the format-2 limit.");
                await writer.WriteLineAsync(Format2Package.ToDeltaLine(evt));
                count++;
                if (blobHashes is not null)
                    foreach (var hash in Format2Package.BlobsOf(evt)) blobHashes.Add(hash);
            }
            if (page[^1].SequenceNum > head) break;
        }
        return count;
    }

    private async Task WriteBlobsAsync(TarWriter tar, IEnumerable<string> hashes, CancellationToken ct)
    {
        using var conn = db.CreateConnection();
        foreach (var hash in hashes)
        {
            ct.ThrowIfCancellationRequested();
            var data = await conn.QuerySingleOrDefaultAsync<byte[]>("SELECT data FROM tbl_blob WHERE hash = @hash", new { hash })
                ?? throw new InvalidOperationException($"Blob {hash} is not stored here; a full package needs every blob it names.");
            if (!Core.Services.BlobHash.Matches(hash, data))
                throw new InvalidDataException($"Stored blob {hash} does not match its hash.");
            await WriteBytesAsync(tar, Format2Package.BlobEntry(hash), data, ct);
        }
    }

    /// <summary>The activation this node holds (spec 8.3), or null before it.</summary>
    private async Task<BlindActivationRef?> ActivationAsync()
    {
        using var conn = db.CreateConnection();
        var row = await conn.QueryFirstOrDefaultAsync<(string ActivationId, string KeyId)?>(
            "SELECT activation_id, key_id FROM tbl_metadata_activation ORDER BY lamport_ts, activation_id LIMIT 1");
        return row is { } r && Guid.TryParse(r.ActivationId, out var id) ? new BlindActivationRef(id, r.KeyId) : null;
    }

    /// <summary>The signed whitelist events about this node still in its log, as a format-1 package carries them.</summary>
    private async Task<IReadOnlyList<SyncEvent>> StandingEventsAsync(Guid self)
    {
        using var conn = db.CreateConnection();
        return (await conn.QueryAsync<SyncEvent>(
            @"SELECT event_id AS EventId, node_id AS NodeId, lamport_ts AS LamportTs, event_type AS EventType,
                     article_id AS ArticleId, payload AS Payload, signature AS Signature,
                     protocol_version AS ProtocolVersion, created_at AS CreatedAt
              FROM tbl_event
              WHERE event_type IN (@Add, @Update, @Revoke) AND lower(json_extract(payload, '$.node_id')) = @Self
              ORDER BY lamport_ts, sequence_num",
            new { Add = EventTypes.WhitelistAdd, Update = EventTypes.WhitelistUpdate, Revoke = EventTypes.WhitelistRevoke, Self = self.ToString("D") })).ToList();
    }

    private async Task<int> SchemaVersionAsync()
    {
        using var conn = db.CreateConnection();
        return await conn.ExecuteScalarAsync<int?>("SELECT MAX(version) FROM tbl_migration") ?? 0;
    }

    /// <summary>The outer manifest: today's snapshot manifest shape with the format-2 markers (spec 8.2).</summary>
    private static byte[] OuterManifest(Guid servingNode, long cp, IReadOnlyDictionary<string, string> files, int schema)
    {
        var manifest = new Dictionary<string, object?>
        {
            ["version"] = 3,
            ["createdAt"] = DateTime.UtcNow.ToString("o"),
            ["files"] = files,
            ["snapshotFormatVersion"] = Format2Package.SnapshotFormatVersion,
            ["packageFormat"] = Format2Package.PackageFormat,
            ["dbEncrypted"] = false,
            ["producerNodeId"] = servingNode.ToString(),
            ["cpSequenceNum"] = cp,
            ["migrationVersion"] = schema,
        };
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task WriteBytesAsync(TarWriter tar, string name, byte[] bytes, CancellationToken ct) =>
        await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(bytes) }, ct);

    private static async Task WriteFileAsync(TarWriter tar, string name, string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = stream }, ct);
    }

    private static string Quote(string path) => "'" + path.Replace("'", "''") + "'";
}
