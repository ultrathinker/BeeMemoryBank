using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Services.BlindPhone;

/// <summary>An Android backup file opened: whose it is, its recovery set, and the decrypted package.</summary>
public sealed record AndroidBackupOpened(Guid NodeId, string RecoverySetJson, string PackagePath);

/// <summary>
/// The source side of restoring from an Android blind node's backup file on Windows (plan 6.8, section
/// 10): only the file and the master password survive.
///
/// <para>How the key is found: the file's open header carries the phone's recovery set, and that set
/// holds the sealed secret <c>android-backup:&lt;node id&gt;</c> — the backup key Windows sealed under the
/// DEK when it paired the phone, replicated to the phone like every sealed secret (and carried forward by
/// every rotation). The master password opens a box in the set, the chain gives every DEK, one of them
/// opens that sealed secret, and the key opens the body. The phone makes no backup before that secret
/// has reached it (<see cref="BlindPhoneBackupRunner"/>), so every file it writes can be opened this way.
/// There is no other path: without the master password nothing here opens.</para>
///
/// <para>The key resolution itself — the boxes, the chain, unsealing — belongs to the restore pipeline and
/// comes in as <c>backupKeyCandidates</c>; what this class guarantees is that the file is an Android
/// backup of the node it names, and that its body opens, whole and untouched, under one of those keys.</para>
/// </summary>
public static class AndroidBackupRestore
{
    public const string Extension = BlindPhoneBackupRunner.Extension;

    /// <summary>True if <paramref name="path"/> is a file that starts like an Android backup.</summary>
    public static bool LooksLikeBackup(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var file = File.OpenRead(path);
            var magic = new byte[8];
            return file.ReadAtLeast(magic, magic.Length, throwOnEndOfStream: false) == magic.Length
                && AndroidBackupFile.IsMagic(magic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens <paramref name="backupPath"/> into <paramref name="packagePath"/>. Throws
    /// <see cref="InvalidDataException"/> when the file is not an Android backup, names a key that is not
    /// its own node's, or its body opens under none of the candidate keys (damaged, cut short, or not
    /// this phone's); <see cref="UnauthorizedAccessException"/> passes through from the key side (wrong
    /// master password). Nothing is left at <paramref name="packagePath"/> on failure.
    /// </summary>
    /// <param name="backupKeyCandidates">
    /// Given the header, the keys the sealed secret <see cref="AndroidBackupHeader.BackupKeyName"/> opens to
    /// under the DEKs the master password recovered from <see cref="AndroidBackupHeader.RecoverySet"/>.
    /// They are cleared once tried.
    /// </param>
    public static async Task<AndroidBackupOpened> OpenAsync(
        string backupPath,
        Func<AndroidBackupHeader, CancellationToken, Task<IReadOnlyList<byte[]>>> backupKeyCandidates,
        string packagePath,
        CancellationToken ct = default,
        long? maxBodyBytes = null)
    {
        // One handle from the header check to the last chunk, and the decryption must see exactly the
        // preamble that was checked: a file swapped or rewritten while the keys are being resolved must
        // not be decrypted under the checked header's name. Sharing is left open on purpose — it is only
        // advisory on Android; the preamble digest is the guarantee.
        await using var file = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var (header, preambleSha) = await AndroidBackupFile.ReadHeaderWithDigestAsync(file, ct);

        // The header names the key that opens the body. It must be this node's own, and the node a blind
        // one: a header claiming another phone's key cannot be allowed to pick which secret is unsealed.
        if (!BlindNodeId.IsBlind(header.NodeId)
            || !string.Equals(header.BackupKeyName, $"android-backup:{header.NodeId}", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This backup's header does not belong to the phone it names.");

        var candidates = await backupKeyCandidates(header, ct);
        if (candidates.Count == 0)
            throw new InvalidDataException(
                $"The master password opened the backup's recovery set, but it holds no sealed key for this phone ({header.BackupKeyName}).");

        try
        {
            return await OpenWithAnyAsync(file, header, preambleSha, candidates, packagePath, maxBodyBytes, ct);
        }
        finally
        {
            foreach (var key in candidates) System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        }
    }

    private static async Task<AndroidBackupOpened> OpenWithAnyAsync(Stream file, AndroidBackupHeader header, byte[] preambleSha,
        IReadOnlyList<byte[]> candidates, string packagePath, long? maxBodyBytes, CancellationToken ct)
    {
        foreach (var key in candidates)
        {
            try
            {
                file.Position = 0;
                await using (var output = File.Create(packagePath))
                    await AndroidBackupFile.DecryptAsync(file, key, output, ct, expectedPreambleSha256: preambleSha,
                        maxPlaintextBytes: maxBodyBytes);
                return new AndroidBackupOpened(header.NodeId, header.RecoverySet.ToJsonString(), packagePath);
            }
            catch (InvalidDataException ex) when (!AndroidBackupFile.IsBodyTooLarge(ex))
            {
                // Not this key (or not a whole file) — try the next; nothing of it may stay behind.
                File.Delete(packagePath);
            }
            catch
            {
                File.Delete(packagePath);
                throw;
            }
        }
        throw new InvalidDataException(
            "The backup does not open with the key sealed for it: the file is damaged or cut short, or it is not this phone's.");
    }
}
