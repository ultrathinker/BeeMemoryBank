using System.Security.Cryptography;

namespace BeeMemoryBank.Api.Services;

// The signature framing of a snapshot package: pure byte layout, no key. Shared by every node that builds, serves or
// verifies a package (a blind node included); split out of SnapshotService.Crypto.cs, which holds the database encryption
// under the master DEK and belongs to the full node only.
public partial class SnapshotService
{
    // Domain separation tags. These prepend the signed bytes so a signature produced for
    // one purpose can NEVER verify against a different purpose, even if the underlying
    // hashes happen to collide. Forms a "fail-closed" structural defense against verifier
    // confusion bugs in future code.
    //
    // EMBEDDED tag — for `manifest.json.sig` inside tar.gz. Signs the manifest bytes only;
    // file integrity follows transitively from manifest's per-file SHA256 entries.
    //
    // SIDECAR tag — for `<file>.tar.gz.sig` next to the archive. Signs SHA256(manifest||file).
    // Used by sync-export RestoreForJoinAsync.
    //
    // Format: ASCII tag + single 0x00 separator + payload. The 0x00 prevents any
    // collision via prefix-extension since 0x00 cannot appear in our ASCII tag alphabet.
    private static readonly byte[] DomainTagEmbedded = "BMB-MANIFEST-V1\0"u8.ToArray();
    private static readonly byte[] DomainTagSidecar  = "BMB-MANIFEST-FILE-V1\0"u8.ToArray();

    public static byte[] BuildSigPayloadEmbedded(byte[] manifestBytes)
    {
        var buf = new byte[DomainTagEmbedded.Length + manifestBytes.Length];
        Buffer.BlockCopy(DomainTagEmbedded, 0, buf, 0, DomainTagEmbedded.Length);
        Buffer.BlockCopy(manifestBytes, 0, buf, DomainTagEmbedded.Length, manifestBytes.Length);
        return buf;
    }

    // Internal: the blind-package restore checks the whole archive against the sidecar before anything else.
    internal static async Task<byte[]> ComputeSignaturePayloadAsync(byte[] manifestBytes, string tarGzPath, CancellationToken ct = default)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(DomainTagSidecar);
        hasher.AppendData(manifestBytes);
        await using var fs = File.OpenRead(tarGzPath);
        var buffer = new byte[81920];
        int read;
        while ((read = await fs.ReadAsync(buffer, ct)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
        }
        return hasher.GetHashAndReset();
    }
}
