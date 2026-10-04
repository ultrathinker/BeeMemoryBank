using System.Formats.Tar;
using System.IO.Compression;

namespace BeeMemoryBank.Api.Services;

// Full node only: a filtered variant of a snapshot archive decrypts the database, strips the secret tables and puts the
// encryption back, so it needs the key operations of a node that holds the master DEK. Split out of
// SnapshotService.Maintenance.cs, whose remaining members every node shares.
public partial class SnapshotService
{
    /// <summary>
    /// Extracts a snapshot archive, strips secret tables (identity, key slots, users, sessions, sync state),
    /// and repackages it. Used when distributing a network-wide restore so that the originator's identity
    /// and DEK wrapping never reach peer disks.
    /// </summary>
    public async Task CreateFilteredVariantAsync(string sourceArchivePath, string destinationArchivePath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bmb-filter-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            await ExtractTarGzAsync(sourceArchivePath, tempDir, new FileInfo(sourceArchivePath).Length);

            var dbPath = Path.Combine(tempDir, DbFileName);
            if (!File.Exists(dbPath))
                throw new InvalidOperationException($"Source archive does not contain {DbFileName}");

            await DecryptDbIfNeededAsync(dbPath);

            FilterSecretsFrom(dbPath);

            // Keeps the archive encrypted when this node can (unlocked); a locked vault leaves the variant plain, as before.
            if (_keys is not null)
                await _keys.ReEncryptDatabaseIfUnlockedAsync(dbPath);

            var mediaDir = Path.Combine(tempDir, "media");
            var mediaFiles = Directory.Exists(mediaDir)
                ? Directory.GetFiles(mediaDir, "*.enc")
                : Array.Empty<string>();

            var manifestPath = Path.Combine(tempDir, ManifestFileName);
            byte[]? manifestBytes = File.Exists(manifestPath) ? await File.ReadAllBytesAsync(manifestPath) : null;

            await using var fs = File.Create(destinationArchivePath);
            await using var gz = new GZipStream(fs, CompressionLevel.Optimal);
            using var tar = new TarWriter(gz, TarEntryFormat.Pax);

            await using (var dbStream = File.OpenRead(dbPath))
            {
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, DbFileName)
                {
                    DataStream = dbStream
                });
            }

            foreach (var encFile in mediaFiles)
            {
                await using var mediaStream = File.OpenRead(encFile);
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, $"media/{Path.GetFileName(encFile)}")
                {
                    DataStream = mediaStream
                });
            }

            if (manifestBytes != null)
            {
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, ManifestFileName)
                {
                    DataStream = new MemoryStream(manifestBytes)
                });
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
            }
        }
    }
}
