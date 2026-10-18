using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace BeeMemoryBank.Sync.Blind.Format2;

/// <summary>
/// Extracts a format-2 package and runs the checks of docs/design/step-a-spec.md 8.6, steps 1-4, in that order, before the
/// caller touches anything: the serving node's signatures and every entry's hash, the blind manifest, the baseline's
/// producer (a superadmin the RECEIVER trusts) and signature with its container and blob list, and the blobs. Nothing in
/// the package is trusted for what it says about itself: the serving node's key and the producer's key and standing come
/// from the caller. Every failure is an <see cref="InvalidDataException"/>; the target directory is then removed.
///
/// <para>An empty detached signature means the transport has none to give (a seed upload authenticated by its pair-code
/// proof or token): the embedded signature then carries the whole archive, because every entry must be listed with its
/// hash in the signed manifest and no other entry is accepted.</para>
/// </summary>
public sealed class BlindPackageV2Reader : IBlindPackageV2Reader
{
    /// <summary>The outer manifest, the blind manifest and the baseline manifest are small; anything larger is refused.</summary>
    private const long MaximumManifestBytes = 4L * 1024 * 1024;

    private readonly long _maximumEntryBytes;
    private readonly long _maximumTotalBytes;

    /// <param name="maximumEntryBytes">Largest single entry (the baseline container); default 64 GiB.</param>
    /// <param name="maximumTotalBytes">Largest extracted total; default 256 GiB.</param>
    public BlindPackageV2Reader(long maximumEntryBytes = 64L << 30, long maximumTotalBytes = 256L << 30)
    {
        _maximumEntryBytes = maximumEntryBytes;
        _maximumTotalBytes = maximumTotalBytes;
    }

    public async Task<VerifiedPackageV2> ExtractVerifiedAsync(
        string archivePath, byte[] detachedSignature, byte[] servingNodePublicKey,
        Func<Guid, (byte[] PublicKey, bool IsSuperadmin)?> trustedProducer,
        string targetDirectory, CancellationToken ct = default)
    {
        try
        {
            return await VerifyAsync(archivePath, detachedSignature, servingNodePublicKey, trustedProducer, targetDirectory, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDelete(targetDirectory);
            if (ex is InvalidDataException) throw;
            // The type, never the text: an IOException or a parser says where it was reading (a path of this node).
            throw new InvalidDataException($"Not a valid format-2 package ({ex.GetType().Name}).", ex);
        }
    }

    private async Task<VerifiedPackageV2> VerifyAsync(
        string archivePath, byte[] detachedSignature, byte[] servingKey,
        Func<Guid, (byte[] PublicKey, bool IsSuperadmin)?> trustedProducer, string dir, CancellationToken ct)
    {
        // 1. Outer: the detached signature over manifest || archive, then every entry against the signed manifest.
        var outerBytes = await Format2Package.ReadEntryAsync(archivePath, Format2Package.ManifestFileName, MaximumManifestBytes, ct)
            ?? throw new InvalidDataException("The package has no manifest.json.");
        if (detachedSignature.Length > 0
            && !Ed25519Signer.Verify(servingKey, await Format2Package.DetachedSignaturePayloadAsync(outerBytes, archivePath, ct), detachedSignature))
            throw new InvalidDataException("The package's detached signature does not verify under the serving node's key.");

        Directory.CreateDirectory(dir);
        var entries = await ExtractAsync(archivePath, dir, ct);
        if (!entries.Contains(Format2Package.ManifestSignatureFileName))
            throw new InvalidDataException("The package carries no embedded signature.");
        var extractedOuter = await File.ReadAllBytesAsync(Path.Combine(dir, Format2Package.ManifestFileName), ct);
        if (!CryptographicOperations.FixedTimeEquals(extractedOuter, outerBytes))
            throw new InvalidDataException("The package holds two different manifests.");
        var embedded = await File.ReadAllBytesAsync(Path.Combine(dir, Format2Package.ManifestSignatureFileName), ct);
        if (!Ed25519Signer.Verify(servingKey, Format2Package.EmbeddedSignaturePayload(outerBytes), embedded))
            throw new InvalidDataException("The package's manifest signature does not verify under the serving node's key.");

        Guid servingNodeId;
        using (var outer = JsonDocument.Parse(outerBytes))
        {
            var root = outer.RootElement;
            if (!root.TryGetProperty("packageFormat", out var format) || format.GetString() != Format2Package.PackageFormat)
                throw new InvalidDataException("The package is not format 2.");
            if (!root.TryGetProperty("snapshotFormatVersion", out var version) || version.GetInt32() != Format2Package.SnapshotFormatVersion)
                throw new InvalidDataException("The package's snapshot format version is not 5.");
            if (!root.TryGetProperty("producerNodeId", out var producer) || !Guid.TryParse(producer.GetString(), out servingNodeId))
                throw new InvalidDataException("The package does not name the node that serves it.");
            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in root.GetProperty("files").EnumerateObject())
            {
                listed.Add(file.Name);
                if (!entries.Contains(file.Name))
                    throw new InvalidDataException("The package's manifest lists a file the package does not carry.");   // no name: a package file's name is the sender's text
                var actual = await Format2Package.HashFileAsync(Path.Combine(dir, file.Name), ct);
                if (!string.Equals(actual, file.Value.GetString(), StringComparison.Ordinal))
                    throw new InvalidDataException("A file of the package does not match its hash in the signed manifest.");
            }
            foreach (var entry in entries)
                if (entry is not (Format2Package.ManifestFileName or Format2Package.ManifestSignatureFileName) && !listed.Contains(entry))
                    throw new InvalidDataException("The package carries a file its manifest does not list.");
        }

        // 2. The blind manifest: format and kind, the serving node named with the signing key, unique whitelist rows.
        if (!entries.Contains(BlindManifestV2.FileName))
            throw new InvalidDataException("The package has no blind-manifest.json.");
        var manifest = BlindManifestV2.Parse(await File.ReadAllBytesAsync(Path.Combine(dir, BlindManifestV2.FileName), ct));
        if (manifest.ProducerNodeId != servingNodeId)
            throw new InvalidDataException("The blind manifest names another serving node than the package.");
        if (manifest.Whitelist.Select(p => p.NodeId).Distinct().Count() != manifest.Whitelist.Count)
            throw new InvalidDataException("The blind manifest lists a node twice.");
        var servingRow = manifest.Whitelist.SingleOrDefault(p => p.NodeId == manifest.ProducerNodeId)
            ?? throw new InvalidDataException("The blind manifest does not list the node that serves it.");
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(servingRow.PublicKeyB64), servingKey))
            throw new InvalidDataException("The blind manifest names the serving node with another key than the one that signed it.");
        if (manifest.CutInServerLog < 0 || manifest.CutInServerLog > manifest.CpSequence || manifest.DeltaCount < 0)
            throw new InvalidDataException("The blind manifest's positions are inconsistent.");

        var full = manifest.Kind == BlindPackageKinds.Full;
        var hasControl = entries.Contains(BlindManifestV2.ControlDbFileName);
        var hasDeltas = entries.Contains(BlindManifestV2.DeltasFileName);
        var blobEntries = entries.Where(e => e.StartsWith(BlindManifestV2.BlobsDirectory + "/", StringComparison.Ordinal)).ToList();
        if (full != hasControl || full != hasDeltas)
            throw new InvalidDataException("A full package carries control.db and deltas.jsonl, a baseline package neither.");
        if (!manifest.ContainsBlobs && blobEntries.Count > 0)
            throw new InvalidDataException("The package carries blobs its manifest does not announce.");
        if (manifest.ContainsBlobs && !full)
            throw new InvalidDataException("Only a full package carries blobs.");

        // 3. The baseline: produced and signed by a superadmin THIS receiver trusts; container and blob list as signed.
        var baselineDir = Path.Combine(dir, BlindManifestV2.BaselineDirectory);
        foreach (var name in new[] { Format2Package.BaselineManifestEntry, Format2Package.BaselineSignatureEntry,
                     Format2Package.BaselineBlobListEntry, Format2Package.BaselineContainerEntry })
            if (!entries.Contains(name)) throw new InvalidDataException($"The package has no {name}.");
        var baselineBytes = await File.ReadAllBytesAsync(Path.Combine(baselineDir, BaselineManifest.FileName), ct);
        var baselineSignature = await File.ReadAllBytesAsync(Path.Combine(baselineDir, BaselineManifest.SignatureFileName), ct);
        var baseline = BaselineManifest.Parse(baselineBytes);
        VerifyBaselineSignature(baseline, baselineBytes, baselineSignature, trustedProducer);
        if (baseline.BaselineId != manifest.BaselineId)
            throw new InvalidDataException("The blind manifest names another baseline than the one the package carries.");
        var blobList = await VerifyBaselineFilesAsync(baselineDir, baseline, ct);

        // 4. Blobs: each hashes to its name; a package that announces blobs carries every one the baseline and deltas need.
        foreach (var entry in blobEntries)
        {
            var hash = entry[(BlindManifestV2.BlobsDirectory.Length + 1)..];
            if (!Core.Services.BlobHash.Matches(hash, await File.ReadAllBytesAsync(Path.Combine(dir, entry), ct)))
                throw new InvalidDataException($"Blob {hash} does not match its name.");
        }

        string? deltasPath = null;
        if (full)
        {
            deltasPath = Path.Combine(dir, BlindManifestV2.DeltasFileName);
            var needed = new HashSet<string>(blobList, StringComparer.Ordinal);
            var count = 0;
            await foreach (var evt in Format2Package.ReadVerifiedDeltasAsync(deltasPath, manifest.CutInServerLog, manifest.CpSequence, ct))
            {
                count++;
                foreach (var hash in Format2Package.BlobsOf(evt)) needed.Add(hash);
            }
            if (count != manifest.DeltaCount)
                throw new InvalidDataException($"The package carries {count} deltas; its manifest says {manifest.DeltaCount}.");
            if (manifest.ContainsBlobs)
                foreach (var hash in needed)
                    if (!entries.Contains(Format2Package.BlobEntry(hash)))
                        throw new InvalidDataException($"The package announces its blobs but lacks {hash}.");
            VerifyControlDb(Path.Combine(dir, BlindManifestV2.ControlDbFileName));
        }
        else if (manifest.DeltaCount != 0)
        {
            throw new InvalidDataException("A baseline package carries no deltas.");
        }

        return new VerifiedPackageV2(dir, manifest,
            new BaselineFiles(baselineDir, baseline, baselineBytes, baselineSignature),
            full ? Path.Combine(dir, BlindManifestV2.ControlDbFileName) : null,
            deltasPath,
            manifest.ContainsBlobs ? Path.Combine(dir, BlindManifestV2.BlobsDirectory) : null);
    }

    /// <summary>
    /// Spec 8.6 step 3, first half (also used for a baseline that arrives without a package): the producer is active and
    /// superadmin in the receiver's trust, and its key signed the manifest bytes as they are.
    /// </summary>
    public static void VerifyBaselineSignature(
        BaselineManifest baseline, byte[] manifestBytes, byte[] signature,
        Func<Guid, (byte[] PublicKey, bool IsSuperadmin)?> trustedProducer)
    {
        if (trustedProducer(baseline.ProducerNodeId) is not { } producer)
            throw new InvalidDataException("The baseline's producer is not a node this receiver trusts.");
        if (!producer.IsSuperadmin)
            throw new InvalidDataException("The baseline's producer is not a superadmin here.");
        if (signature.Length != 64
            || !Ed25519Signer.Verify(producer.PublicKey, BaselineManifest.SignaturePayload(manifestBytes), signature))
            throw new InvalidDataException("The baseline's signature does not verify under its producer's key.");
        if (baseline.KeyGeneration < 1 || !Metadata.SealedEnvelope.IsKeyId(baseline.KeyId))
            throw new InvalidDataException("The baseline names no valid metadata key.");
    }

    /// <summary>
    /// Spec 8.6 step 3, second half: the container's size and SHA-256 and the blob list's SHA-256 and count are what the
    /// signed manifest says. Returns the blob list.
    /// </summary>
    public static async Task<IReadOnlyList<string>> VerifyBaselineFilesAsync(string baselineDir, BaselineManifest baseline, CancellationToken ct = default)
    {
        var container = Path.Combine(baselineDir, BaselineManifest.ContainerFileName);
        if (!File.Exists(container) || new FileInfo(container).Length != baseline.ContainerBytes)
            throw new InvalidDataException("The baseline container's size is not the signed one.");
        if (!string.Equals(await Format2Package.HashFileAsync(container, ct), baseline.ContainerSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The baseline container does not match its signed hash.");
        BaselineContainerFraming.Validate(container, baseline.KeyId);
        var listPath = Path.Combine(baselineDir, BaselineManifest.BlobListFileName);
        var listBytes = File.Exists(listPath) ? await File.ReadAllBytesAsync(listPath, ct)
            : throw new InvalidDataException("The baseline has no blob list.");
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(listBytes)), baseline.BlobListSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The baseline's blob list does not match its signed hash.");
        var list = Format2Package.ParseBlobList(listBytes);
        if (list.Count != baseline.BlobCount)
            throw new InvalidDataException("The baseline's blob list does not hold the signed number of blobs.");
        return list;
    }

    /// <summary>
    /// <c>control.db</c> holds the tables of <see cref="SnapshotTables.Control"/> and nothing else (spec 8.4), all of them, and
    /// every row is one the live appliers could have written (<see cref="ControlDbValidator"/>): the import after the adoption
    /// takes the rows as they are, so a row it must not take is refused here, before anything is adopted.
    /// </summary>
    private static void VerifyControlDb(string path)
    {
        using (var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT type, name FROM sqlite_master WHERE type IN ('table', 'view', 'trigger')";
            var tables = new HashSet<string>(StringComparer.Ordinal);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var type = reader.GetString(0);
                var name = reader.GetString(1);
                if (type != "table" || !SnapshotTables.Control.Contains(name, StringComparer.Ordinal))
                    throw new InvalidDataException($"The control database holds {name}, which is not an allowed control table.");
                tables.Add(name);
            }

            foreach (var table in SnapshotTables.Control)
                if (!tables.Contains(table))
                    throw new InvalidDataException($"The control database table {table} is missing.");
        }
        ControlDbValidator.Validate(path);
    }

    /// <summary>Extracts every regular entry under <paramref name="dir"/>; only the names of spec 8.2, each once.</summary>
    private async Task<HashSet<string>> ExtractAsync(string archivePath, string dir, CancellationToken ct)
    {
        var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        var names = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        await using var file = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            if (entry.EntryType is TarEntryType.Directory) continue;
            // A regular entry of length zero (an empty blobs.txt: a baseline without blobs) has no data stream.
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException($"The package holds {entry.Name}, which is not a plain file.");
            if (!Format2Package.IsAllowedEntry(entry.Name))
                throw new InvalidDataException($"The package holds {entry.Name}, which a format-2 package never carries.");
            if (!names.Add(entry.Name))
                throw new InvalidDataException($"The package holds {entry.Name} twice.");
            var output = Path.GetFullPath(Path.Combine(dir, entry.Name));
            if (!output.StartsWith(root, StringComparison.Ordinal))
                throw new InvalidDataException($"{entry.Name} escapes the package directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous);
            var limit = Math.Min(_maximumEntryBytes, _maximumTotalBytes - total);
            if (entry.DataStream is not null)
                total += await Format2Package.CopyWithLimitAsync(entry.DataStream, target, limit, ct);
        }
        return names;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
