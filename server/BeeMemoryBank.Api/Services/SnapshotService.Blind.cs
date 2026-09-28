using System.Text.Json;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Api.Services;

/// <summary>An extracted snapshot whose every file matched its hash in manifest.json.</summary>
/// <param name="EmbeddedSignature">manifest.json.sig, or null for an unsigned archive.</param>
/// <param name="MigrationVersion">The producer's schema version, when the manifest records it.</param>
public sealed record VerifiedSnapshot(
    string Directory, string DatabasePath, byte[] ManifestBytes, byte[]? EmbeddedSignature, int? MigrationVersion)
{
    /// <summary>True if <paramref name="publicKey"/> signed this manifest (and so, through the
    /// hashes in it, every file).</summary>
    public bool IsSignedBy(byte[] publicKey) =>
        EmbeddedSignature is { } sig
        && Ed25519Signer.Verify(publicKey, SnapshotService.BuildSigPayloadEmbedded(ManifestBytes), sig);
}

public partial class SnapshotService
{
    /// <summary>
    /// Extracts an archive into <paramref name="destDir"/> and checks it against its manifest —
    /// the same extraction limits and verification every restore path uses — without importing
    /// anything. For the blind node's seed (plan 4.3), which builds its new database itself.
    /// </summary>
    public async Task<VerifiedSnapshot> ExtractVerifiedAsync(string archivePath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        await ExtractTarGzAsync(archivePath, destDir, new FileInfo(archivePath).Length);
        await VerifyManifestAsync(destDir);

        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(destDir, ManifestFileName));
        var sigPath = Path.Combine(destDir, ManifestFileName + ".sig");
        var signature = File.Exists(sigPath) ? await File.ReadAllBytesAsync(sigPath) : null;
        using var manifest = JsonDocument.Parse(manifestBytes);
        int? migrationVersion = manifest.RootElement.TryGetProperty("migrationVersion", out var mv)
            && mv.ValueKind == JsonValueKind.Number ? mv.GetInt32() : null;

        return new VerifiedSnapshot(destDir, Path.Combine(destDir, DbFileName), manifestBytes, signature, migrationVersion);
    }
}
