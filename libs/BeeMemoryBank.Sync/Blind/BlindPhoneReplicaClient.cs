using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>
/// Downloads and installs a phone replica. The partial archive and its authenticated metadata stay
/// in the caller-owned work directory, so a stopped Android worker resumes with a byte range rather
/// than accepting a new package in the middle of an old one.
/// </summary>
public sealed class BlindPhoneReplicaClient(
    DbConnectionFactory liveFactory,
    INodeIdentityRepository identityRepo,
    INodeAuthSigner signer,
    string dataDirectory,
    string databasePath,
    ILogger<BlindPhoneReplicaClient> logger)
{
    private const string PackageHashHeader = "X-BMB-Package-Sha256";
    private const string SignatureHeader = "X-BMB-Snapshot-Signature";
    private static readonly byte[] SignatureDomain = "BMB-MANIFEST-FILE-V1\0"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task FetchAndInstallAsync(
        HttpClient http, BlindCallCode target, string workDirectory, IProgress<double>? progress, CancellationToken ct)
    {
        var identity = await identityRepo.GetAsync()
            ?? throw new InvalidOperationException("The blind phone identity has not been recorded.");
        if (identity.Ed25519PrivateKeyV != NodeIdentityCrypto.ExternalKeyVersion)
            throw new InvalidOperationException("The blind phone requires its v=2 external-key identity before replica download.");

        Directory.CreateDirectory(workDirectory);
        var token = await PeerAuthenticator.AuthenticateAsync(signer, http, target.Address, identity, target.NodeId, ct);
        var partPath = Path.Combine(workDirectory, "replica.part");
        var metadataPath = Path.Combine(workDirectory, "replica.part.json");
        try
        {
            var current = await DownloadAsync(http, target, token, partPath, metadataPath, progress, ct);
            var extraction = Path.Combine(workDirectory, "verified-" + current.Sha256.ToLowerInvariant());
            await VerifyAndExtractAsync(partPath, extraction, current, target, ct);
            await BuildAndSwitchDatabaseAsync(extraction, target, ct);
            CleanupDownloadArtifacts(workDirectory, partPath, metadataPath);
            progress?.Report(1);
            logger.LogInformation("Installed verified blind replica {Hash} from {NodeId}", current.Sha256, target.NodeId);
        }
        catch (InvalidDataException)
        {
            // A complete archive that did not verify must not turn the next attempt into an invalid
            // range request. Network and cancellation errors deliberately retain their partial bytes.
            CleanupDownloadArtifacts(workDirectory, partPath, metadataPath);
            throw;
        }
    }

    private static async Task<PackageMetadata> DownloadAsync(
        HttpClient http, BlindCallCode target, string token, string partPath, string metadataPath,
        IProgress<double>? progress, CancellationToken ct)
    {
        // A restart is only needed when authenticated resume state cannot describe the response.
        // A transport failure remains resumable: it is intentionally not caught here.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var partExists = File.Exists(partPath);
            var offset = partExists ? new FileInfo(partPath).Length : 0;
            PackageMetadata? prior;
            try
            {
                prior = await ReadMetadataAsync(metadataPath, ct);
            }
            catch (InvalidDataException)
            {
                ResetPartial(partPath, metadataPath);
                continue;
            }

            if ((partExists && offset == 0) ||
                (offset > 0 && (prior is null || offset >= prior.Length)))
            {
                ResetPartial(partPath, metadataPath);
                continue;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{target.Address.TrimEnd('/')}/api/blind/replica");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                ResetPartial(partPath, metadataPath);
                continue;
            }
            response.EnsureSuccessStatusCode();

            PackageMetadata current;
            try
            {
                current = ReadMetadata(response, offset);
            }
            catch (InvalidDataException) when (offset > 0)
            {
                ResetPartial(partPath, metadataPath);
                continue;
            }

            if ((prior is not null && !prior.Matches(current)) ||
                (offset > 0 && response.Content.Headers.ContentRange?.From != offset))
            {
                ResetPartial(partPath, metadataPath);
                continue;
            }
            await WriteMetadataAsync(metadataPath, current, ct);

            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(partPath, offset == 0 ? FileMode.Create : FileMode.Open,
                             FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous))
            {
                if (offset > 0) output.Position = offset;
                var buffer = new byte[81_920];
                var copied = offset;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, ct);
                    if (count == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    copied += count;
                    progress?.Report(Math.Min(1, (double)copied / current.Length));
                }
                await output.FlushAsync(ct);
            }

            if (new FileInfo(partPath).Length != current.Length)
                throw new InvalidDataException("The replica download ended before its signed content length.");
            var hash = await HashFileAsync(partPath, ct);
            if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(current.Sha256)))
                throw new InvalidDataException("The replica package SHA-256 does not match its signed response header.");
            return current;
        }

        throw new InvalidDataException("The replica could not be resumed from its authenticated partial state.");
    }

    private static PackageMetadata ReadMetadata(HttpResponseMessage response, long offset)
    {
        var sha = ReadSingleHeader(response, PackageHashHeader);
        var signature = ReadSingleHeader(response, SignatureHeader);
        if (sha is null || sha.Length != 64 || !sha.All(Uri.IsHexDigit))
            throw new InvalidDataException($"Replica response has no valid {PackageHashHeader} header.");
        if (signature is null) throw new InvalidDataException($"Replica response has no {SignatureHeader} header.");
        try { _ = Convert.FromBase64String(signature); }
        catch (FormatException ex) { throw new InvalidDataException("Replica signature header is not Base64.", ex); }

        var length = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
        if (length is not > 0 || (offset > 0 && length <= offset))
            throw new InvalidDataException("Replica response has no valid complete content length.");
        return new PackageMetadata(sha.ToUpperInvariant(), signature, length.Value);
    }

    private static string? ReadSingleHeader(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var values)) return null;
        var firstTwo = values.Take(2).ToArray();
        return firstTwo.Length == 1 ? firstTwo[0] : null;
    }

    private static async Task<PackageMetadata?> ReadMetadataAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        try { return await JsonSerializer.DeserializeAsync<PackageMetadata>(stream, Json, ct); }
        catch (JsonException ex) { throw new InvalidDataException("Partial replica metadata is invalid.", ex); }
    }

    private static async Task WriteMetadataAsync(string path, PackageMetadata value, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
            4_096, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, value, Json, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream, ct);
    }

    private static void ResetPartial(string partPath, string metadataPath)
    {
        if (File.Exists(partPath)) File.Delete(partPath);
        if (File.Exists(metadataPath)) File.Delete(metadataPath);
    }

    private static void CleanupDownloadArtifacts(string workDirectory, string partPath, string metadataPath)
    {
        ResetPartial(partPath, metadataPath);
        foreach (var directory in Directory.GetDirectories(workDirectory, "verified-*"))
            Directory.Delete(directory, recursive: true);
    }

    private static async Task VerifyAndExtractAsync(
        string archivePath, string extraction, PackageMetadata metadata, BlindCallCode target, CancellationToken ct)
    {
        Directory.CreateDirectory(extraction);
        var manifest = await ReadEntryAsync(archivePath, "manifest.json", ct)
            ?? throw new InvalidDataException("Replica package has no manifest.json.");
        var signaturePayload = await SignaturePayloadAsync(manifest, archivePath, ct);
        if (!Ed25519Signer.Verify(target.PublicKey, signaturePayload, Convert.FromBase64String(metadata.SignatureB64)))
            throw new InvalidDataException("Replica detached signature does not verify under the call-code node key.");

        await ExtractAsync(archivePath, extraction, ct);
        await VerifySnapshotHashesAsync(extraction, ct);
        var blindManifestBytes = await File.ReadAllBytesAsync(Path.Combine(extraction, BlindManifest.FileName), ct);
        var blindManifest = BlindManifest.Parse(blindManifestBytes);
        if (blindManifest.ProducerNodeId != target.NodeId)
            throw new InvalidDataException("Replica package producer does not match the call-code node.");
        var producer = blindManifest.Whitelist.SingleOrDefault(p => p.NodeId == target.NodeId)
            ?? throw new InvalidDataException("Replica package does not whitelist its producer.");
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(producer.PublicKeyB64), target.PublicKey))
            throw new InvalidDataException("Replica package producer key does not match the call-code key.");
    }

    private async Task BuildAndSwitchDatabaseAsync(string extraction, BlindCallCode target, CancellationToken ct)
    {
        var sourceDb = Path.Combine(extraction, "beememorybank.db");
        if (!File.Exists(sourceDb)) throw new InvalidDataException("Replica package does not contain a database.");
        var blindManifest = BlindManifest.Parse(await File.ReadAllBytesAsync(Path.Combine(extraction, BlindManifest.FileName), ct));
        var candidatePath = Path.Combine(Path.GetDirectoryName(databasePath)!,
            $"beememorybank.replica-{blindManifest.SeedId:N}.db");

        var self = await identityRepo.GetAsync() ?? throw new InvalidOperationException("Blind phone identity disappeared during install.");
        DeleteCandidateIfPresent(candidatePath);
        try
        {
            using (var candidateFactory = new DbConnectionFactory(candidatePath))
            {
                await new MigrationRunner(candidateFactory).RunMigrationsAsync();
                await new NodeIdentityRepository(candidateFactory).CreateAsync(self);
                ImportReplicaTables(candidateFactory, sourceDb);
                var whitelist = new WhitelistRepository(candidateFactory);
                foreach (var peer in blindManifest.Whitelist.Where(p => p.NodeId != self.NodeId))
                {
                    await whitelist.CreateAsync(new WhitelistEntry
                    {
                        NodeId = peer.NodeId,
                        DisplayName = peer.DisplayName,
                        Ed25519PublicKey = Convert.FromBase64String(peer.PublicKeyB64),
                        ApiAddress = peer.ApiAddress,
                        IsSuperadmin = peer.IsSuperadmin,
                        TlsSpki = peer.TlsSpki,
                        Status = WhitelistStatuses.Active,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                        LamportTs = peer.LamportTs,
                        SourceNodeId = peer.SourceNodeId
                    });
                }
                await new SyncPositionRepository(candidateFactory).UpsertAsync(new SyncPosition
                {
                    RemoteNodeId = target.NodeId,
                    LastSequenceNum = blindManifest.CpSequence,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            using var writeGate = await EventWriteGate.Instance.QuiesceAsync(ct);
            using var ownerFlow = EventWriteGate.EnterOwnerFlow();
            using var quiesced = liveFactory.BeginQuiesce();
            await liveFactory.WaitDrainedAsync(TimeSpan.FromSeconds(30), ct);

            // A media copy failure must leave the old database live. Encrypted blobs are content-addressed,
            // so copying the new blobs before the database switch cannot invalidate the old replica.
            var mediaSource = Path.Combine(extraction, "media");
            var mediaTarget = Path.Combine(dataDirectory, "media");
            if (Directory.Exists(mediaSource))
            {
                Directory.CreateDirectory(mediaTarget);
                foreach (var file in Directory.GetFiles(mediaSource, "*.enc"))
                    File.Copy(file, Path.Combine(mediaTarget, Path.GetFileName(file)), overwrite: true);
            }

            if (File.Exists(databasePath))
            {
                var backup = databasePath + $".before-replica-{blindManifest.SeedId:N}";
                File.Replace(candidatePath, databasePath, backup, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(candidatePath, databasePath);
            }
        }
        finally
        {
            DeleteCandidateIfPresent(candidatePath);
        }
    }

    private static void DeleteCandidateIfPresent(string candidatePath)
    {
        if (!File.Exists(candidatePath)) return;
        // A failed migration/import has disposed its connections, but Microsoft.Data.Sqlite may still
        // retain one in this candidate database's pool. Clear only that path's pool before replacing it.
        using var candidate = new SqliteConnection($"Data Source={candidatePath}");
        SqliteConnection.ClearPool(candidate);
        File.Delete(candidatePath);
    }

    private static void ImportReplicaTables(DbConnectionFactory factory, string sourceDb)
    {
        using var conn = (SqliteConnection)factory.CreateConnection();
        conn.Execute("PRAGMA foreign_keys = OFF");
        conn.Execute($"ATTACH DATABASE '{sourceDb.Replace("'", "''")}' AS snap");
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var table in SnapshotTables.Replicated.Concat(["tbl_hard_delete_audit"]))
                if (SnapshotTableImport.SnapshotHasTable(conn, tx, table))
                    SnapshotTableImport.CopyTable(conn, tx, table, orIgnore: true);
            SnapshotTableImport.AdoptLegacyInlineCiphertext(conn, tx);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        finally
        {
            conn.Execute("DETACH DATABASE snap");
            conn.Execute("PRAGMA foreign_keys = ON");
        }
    }

    private static async Task<byte[]?> ReadEntryAsync(string archive, string name, CancellationToken ct)
    {
        await using var file = File.OpenRead(archive);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync() is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Name != name || entry.DataStream is null) continue;
            await using var input = entry.DataStream;
            using var output = new MemoryStream();
            await input.CopyToAsync(output, ct);
            return output.ToArray();
        }
        return null;
    }

    private static async Task ExtractAsync(string archive, string destination, CancellationToken ct)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        await using var file = File.OpenRead(archive);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync() is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.EntryType != TarEntryType.RegularFile || entry.DataStream is null) continue;
            var outputPath = Path.GetFullPath(Path.Combine(destination, entry.Name));
            if (!outputPath.StartsWith(root, StringComparison.Ordinal))
                throw new InvalidDataException($"Replica archive path escapes its staging directory: {entry.Name}");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
                81_920, FileOptions.Asynchronous);
            await entry.DataStream.CopyToAsync(output, ct);
        }
    }

    private static async Task VerifySnapshotHashesAsync(string directory, CancellationToken ct)
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "manifest.json"), ct));
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateObject())
        {
            var path = Path.GetFullPath(Path.Combine(directory, file.Name));
            if (!path.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(path))
                throw new InvalidDataException($"Replica manifest file is missing: {file.Name}");
            var actual = Convert.ToHexStringLower(await HashFileAsync(path, ct));
            if (!string.Equals(actual, file.Value.GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Replica manifest hash mismatch: {file.Name}");
        }
    }

    private static async Task<byte[]> SignaturePayloadAsync(byte[] manifest, string archive, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(SignatureDomain);
        hash.AppendData(manifest);
        await using var stream = File.OpenRead(archive);
        var buffer = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0) hash.AppendData(buffer, 0, read);
        return hash.GetHashAndReset();
    }

    private sealed record PackageMetadata(string Sha256, string SignatureB64, long Length)
    {
        public bool Matches(PackageMetadata other) =>
            Length == other.Length &&
            string.Equals(Sha256, other.Sha256, StringComparison.OrdinalIgnoreCase) &&
            CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(SignatureB64), Convert.FromBase64String(other.SignatureB64));
    }
}
