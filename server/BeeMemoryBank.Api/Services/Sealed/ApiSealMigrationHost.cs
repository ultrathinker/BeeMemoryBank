using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Infrastructure.AutoUnlock;
using BeeMemoryBank.Infrastructure.OsAutoUnlock;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Vault.Sealed;

namespace BeeMemoryBank.Api.Services.Sealed;

/// <summary>
/// What the Api adds to "Encrypt this node" (docs/design/step-b-spec.md 10.4 step 3, 10.7): the pre-encryption snapshot. It is the product's own
/// snapshot (BMBDB2, the whole database under the master DEK) plus the key envelope - copies of the password and recovery slots of that day - in
/// a folder the snapshot listing, the retention and the delete button do not read; its plain working copy is wiped, not just deleted; and it is
/// proven to open before anything else is touched: with the session's DEK, and, through the envelope, with the master password the owner typed.
/// </summary>
public sealed class ApiSealMigrationHost(string dataPath, SnapshotService snapshots, DbConnectionFactory vault, IServiceProvider services)
    : ISealMigrationHost
{
    public const string FolderName = VaultStoreFiles.PreEncryptionDir;

    public bool CanMakeWayBackSnapshot => true;

    public bool FitsWayBackSnapshot(long databaseBytes)
    {
        try
        {
            SnapshotService.EnsureDbSizeWithinEncryptableLimit(databaseBytes);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public async Task<WayBackSnapshot> CreateWayBackSnapshotAsync(string masterPassword, CancellationToken ct)
    {
        byte[]? envelope;
        using (var connection = vault.CreateConnection())
            envelope = SnapshotKeyEnvelope.Build(connection, DateTime.UtcNow);
        if (envelope is null)
            throw new SealMigrationException(SealFailures.Snapshot, "The way-back snapshot needs a password or recovery key slot to carry, and this node has none.");

        var folder = Path.Combine(dataPath, FolderName);
        SnapshotInfo info;
        try
        {
            info = await snapshots.CreateAsync(
                filterSecrets: false, sign: false, encryptDb: true,
                additions: new SnapshotAdditions([], _ => { }, new Dictionary<string, byte[]> { [SnapshotKeyEnvelope.EntryName] = envelope }),
                outputDirectory: folder, wipeStaging: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SealMigrationException(SealFailures.Snapshot, "The way-back snapshot could not be made (" + ex.GetType().Name + "); nothing was changed.");
        }

        var path = Path.Combine(folder, info.FileName);
        try
        {
            await SelfCheckAsync(path, masterPassword, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDelete(path);
            TryDelete(path + ".sig");
            throw ex as SealMigrationException ?? new SealMigrationException(SealFailures.Snapshot,
                "The way-back snapshot did not pass its check (" + ex.GetType().Name + "); it was removed and nothing else was changed.");
        }
        return new WayBackSnapshot(FolderName + "/" + info.FileName, info.SizeBytes, info.CreatedAt);
    }

    /// <summary>
    /// The container decrypts with the session's DEK to an image that starts with the SQLite magic and is as long as its header says, and the key
    /// envelope opens it: the typed password gives, through the envelope's slots, a DEK that passes the sentinel check and decrypts the same image.
    /// </summary>
    private async Task SelfCheckAsync(string archivePath, string masterPassword, CancellationToken ct)
    {
        byte[]? container = null;
        byte[]? envelope = null;
        await using (var file = File.OpenRead(archivePath))
        await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
        using (var tar = new TarReader(gzip))
        {
            while (await tar.GetNextEntryAsync(copyData: true, ct) is { } entry)
            {
                if (entry.DataStream is null) continue;
                using var buffer = new MemoryStream();
                await entry.DataStream.CopyToAsync(buffer, ct);
                if (entry.Name == "beememorybank.db") container = buffer.ToArray();
                else if (entry.Name == SnapshotKeyEnvelope.EntryName) envelope = buffer.ToArray();
            }
        }
        if (container is null || envelope is null)
            throw new SealMigrationException(SealFailures.Snapshot, "The way-back snapshot lacks its database or its key envelope.");

        var session = services.GetRequiredService<SessionService>();
        var sessionDek = session.GetMasterDek();
        byte[]? image = null;
        byte[]? envelopeDek = null;
        byte[]? image2 = null;
        try
        {
            image = await Bmbdb2Container.DecryptAsync(new MemoryStream(container, writable: false), container.Length, sessionDek, ct);
            RequireWholeSqliteImage(image);

            envelopeDek = SnapshotKeyEnvelope.TryOpen(envelope, masterPassword)
                ?? throw new SealMigrationException(SealFailures.Snapshot, "The key envelope of the way-back snapshot does not open with the master password.");
            image2 = await Bmbdb2Container.DecryptAsync(new MemoryStream(container, writable: false), container.Length, envelopeDek, ct);
            if (image2.Length != image.Length)
                throw new SealMigrationException(SealFailures.Snapshot, "The way-back snapshot opened through its key envelope is not the same database.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionDek);
            if (image is not null) CryptographicOperations.ZeroMemory(image);
            if (image2 is not null) CryptographicOperations.ZeroMemory(image2);
            if (envelopeDek is not null) CryptographicOperations.ZeroMemory(envelopeDek);
        }
    }

    private static void RequireWholeSqliteImage(byte[] image)
    {
        if (image.Length < 100 || !image.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8))
            throw new SealMigrationException(SealFailures.Snapshot, "The way-back snapshot does not hold a SQLite database.");
        var pageSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(16));
        var pages = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(28));
        var size = pageSize == 1 ? 65536 : pageSize;
        if (pages == 0 || (long)pages * size != image.Length)
            throw new SealMigrationException(SealFailures.Snapshot, "The way-back snapshot is cut off.");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public Task<IReadOnlyList<SealBlocker>> HostBlockersAsync(SealOptions options, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SealBlocker>>([]);

    public async Task<IReadOnlyList<SealWarning>> HostWarningsAsync(CancellationToken ct)
    {
        var on = false;
        try
        {
            if (services.GetService<ServerAutoUnlockService>() is { } server) on |= (await server.GetStatusAsync(ct)).Enabled;
            if (services.GetService<OsAutoUnlockService>() is { IsSupported: true } os) on |= await os.IsEnabledAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A secret store that cannot answer is not a reason to hide the other half of the answer.
        }
        return on
            ? [new SealWarning("auto_unlock_on", "Auto-unlock is on: this node will open its vault by itself, so encrypting it does not protect against a stolen disk.")]
            : [];
    }
}
