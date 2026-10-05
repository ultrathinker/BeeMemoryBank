using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Infrastructure.Secrets;

/// <summary>Current-user DPAPI store with per-purpose entropy and atomic-value copies.</summary>
public sealed class WindowsDpapiUserSecretStore(string dataPath) : IUserSecretStore
{
    private const string EntropyPrefix = "BeeMemoryBank.UserSecretStore.v1/";
    private readonly string _dataPath = dataPath ?? throw new ArgumentNullException(nameof(dataPath));

    public bool IsSupported => OperatingSystem.IsWindows();

    public byte[]? Read(string purpose, string account)
    {
        Validate(purpose, account);
        EnsureSupported();
        var path = PathFor(purpose, account);
        if (!File.Exists(path)) return null;
        byte[] blob;
        try { blob = File.ReadAllBytes(path); }
        catch (UnauthorizedAccessException ex)
        {
            throw new UserSecretStoreException(UserSecretStoreFailureKind.Denied, "The user secret cannot be read.", ex);
        }
        catch (IOException ex)
        {
            throw new UserSecretStoreException(UserSecretStoreFailureKind.Unavailable, "The user secret cannot be read.", ex);
        }

        var entropy = EntropyFor(purpose);
        try
        {
            try
            {
                return ProtectedData.Unprotect(blob, entropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException) when (IsWrittenWithoutEntropyByOlderVersions(purpose))
            {
                // Files of the local CA keys written by earlier versions were protected without entropy. Try that once; if it does not
                // open either, the failure below is the one the strong attempt raised, exactly as before this fallback existed.
                var plain = TryUnprotectWithoutEntropy(blob);
                if (plain is null) throw;
                MigrateToPurposeEntropy(path, plain, entropy);
                return plain;
            }
        }
        catch (CryptographicException ex)
        {
            throw new UserSecretStoreException(UserSecretStoreFailureKind.Malformed, "The user secret cannot be opened.", ex);
        }
        finally
        {
            if (entropy is not null) CryptographicOperations.ZeroMemory(entropy);
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    private static byte[]? TryUnprotectWithoutEntropy(byte[] blob)
    {
        try { return ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { return null; }
    }

    /// <summary>
    /// Rewrites a legacy file with the purpose entropy so the next read takes the strong path. Best effort and never at the secret's
    /// expense: the new blob is written to a temporary file next to it, checked by opening it again, and only then moved over the old
    /// file in one step, so a crash, a full disk or a second process doing the same leaves either the old file or the new one, never a
    /// torn one. Any failure is ignored: the secret has been read, and the legacy file still opens next time.
    /// </summary>
    private static void MigrateToPurposeEntropy(string path, byte[] plain, byte[]? entropy)
    {
        byte[]? blob = null;
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            blob = ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser);
            var check = ProtectedData.Unprotect(blob, entropy, DataProtectionScope.CurrentUser);
            var same = CryptographicOperations.FixedTimeEquals(check, plain);
            CryptographicOperations.ZeroMemory(check);
            if (!same) return;
            File.WriteAllBytes(temp, blob);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort: only our own temporary file */ }
        }
        finally
        {
            if (blob is not null) CryptographicOperations.ZeroMemory(blob);
        }
    }

    public void Write(string purpose, string account, ReadOnlySpan<byte> value)
    {
        Validate(purpose, account);
        EnsureSupported();
        var plain = value.ToArray();
        var entropy = EntropyFor(purpose);
        try
        {
            var blob = ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathFor(purpose, account))!);
                File.WriteAllBytes(PathFor(purpose, account), blob);
            }
            finally { CryptographicOperations.ZeroMemory(blob); }
        }
        catch (CryptographicException ex)
        {
            throw new UserSecretStoreException(UserSecretStoreFailureKind.Unavailable, "The user secret cannot be stored.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UserSecretStoreException(UserSecretStoreFailureKind.Denied, "The user secret cannot be stored.", ex);
        }
        catch (IOException ex)
        {
            throw new UserSecretStoreException(UserSecretStoreFailureKind.Unavailable, "The user secret cannot be stored.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (entropy is not null) CryptographicOperations.ZeroMemory(entropy);
        }
    }

    public void Delete(string purpose, string account)
    {
        Validate(purpose, account);
        EnsureSupported();
        var path = PathFor(purpose, account);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UserSecretStoreException(UserSecretStoreFailureKind.Denied, "The user secret cannot be deleted.", ex);
        }
        catch (IOException ex)
        {
            throw new UserSecretStoreException(UserSecretStoreFailureKind.Unavailable, "The user secret cannot be deleted.", ex);
        }
    }

    private static byte[] Entropy(string purpose) => Encoding.UTF8.GetBytes(EntropyPrefix + purpose);

    private static byte[] EntropyFor(string purpose) => purpose switch
    {
        "os-auto-unlock" => "BeeMemoryBank.OsAutoUnlock.v1"u8.ToArray(),
        "update-unlock" => "BeeMemoryBank.UpdateUnlockHandoff.v1"u8.ToArray(),
        _ => Entropy(purpose)
    };

    /// <summary>The purposes whose files earlier versions protected with no entropy at all (still readable, see <see cref="Read"/>).</summary>
    private static bool IsWrittenWithoutEntropyByOlderVersions(string purpose) => purpose is "local-ca" or "local-leaf";

    private string PathFor(string purpose, string account)
    {
        var legacy = (purpose, account) switch
        {
            ("os-auto-unlock", "default") => Path.Combine(_dataPath, "os-auto-unlock.dat"),
            ("update-unlock", "default") => Path.Combine(_dataPath, "update-unlock.dat"),
            ("local-ca", "default") => Path.Combine(_dataPath, "certs", "ca.key"),
            ("local-leaf", "default") => Path.Combine(_dataPath, "certs", "leaf.key"),
            _ => null
        };
        if (legacy is not null) return legacy;

        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(purpose + "\0" + account))).ToLowerInvariant();
        return Path.Combine(_dataPath, "secrets", id + ".dat");
    }

    private static void Validate(string purpose, string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
    }

    private void EnsureSupported()
    {
        if (!IsSupported)
            throw new UserSecretStoreException(UserSecretStoreFailureKind.Unavailable, "Windows DPAPI is unavailable on this platform.");
    }
}
