using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.Windows;

/// <summary>
/// The blind app's secrets (identity seed, backup key, one-time pairing secret) as DPAPI blobs, <see cref="DataProtectionScope.CurrentUser"/>:
/// only this Windows user, on this machine and profile, can open them. Each lives in its own file under
/// <see cref="WindowsBlindPaths.SecretsDirectory"/>; a key never goes into SQLite, the state file or the log.
///
/// <para>Every blob is protected with its own entropy (the app, the format version and the secret's role), so a blob of one role
/// cannot be opened as another one by swapping files. A blob that cannot be opened (tampered, truncated, made by another user or
/// by a profile that was reset) reads as "no such secret": AppCore treats a missing key next to an existing identity as a reason
/// to stop and offer "Disconnect and wipe", it never makes a replacement key over existing data.</para>
/// </summary>
public sealed class DpapiSecretStore(string secretsDirectory) : IBlindSecretStore
{
    private const string Identity = "identity-seed";
    private const string Backup = "backup-key";
    private const string Pairing = "pairing-secret";
    private static readonly string[] AllRoles = [Identity, Backup, Pairing];

    private readonly object _gate = new();

    public void SaveIdentitySeed(byte[] seed) => Save(Identity, seed);
    public byte[]? LoadIdentitySeed() => Load(Identity);
    public void SaveBackupKey(byte[] key) => Save(Backup, key);
    public byte[]? LoadBackupKey() => Load(Backup);
    public void SavePairingSecret(byte[] secret) => Save(Pairing, secret);
    public byte[]? LoadPairingSecret() => Load(Pairing);
    public void ClearPairingSecret()
    {
        lock (_gate) Remove(Pairing);
    }

    /// <summary>Forgets every secret: the blob files are removed, and the secrets folder with them when nothing else is in it.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            foreach (var role in AllRoles) Remove(role);
            RemoveFolderIfEmpty();
        }
    }

    /// <summary>Only an EMPTY folder, never recursively: a folder that holds anything else than what this store wrote stays as it is.</summary>
    private void RemoveFolderIfEmpty()
    {
        if (Directory.Exists(secretsDirectory) && !Directory.EnumerateFileSystemEntries(secretsDirectory).Any())
            Directory.Delete(secretsDirectory, recursive: false);
    }

    private string PathOf(string role) => Path.Combine(secretsDirectory, role + ".dpapi");

    private static byte[] Entropy(string role) => Encoding.UTF8.GetBytes("BeeMemoryBankBlind/v1/" + role);

    private void Save(string role, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var blob = ProtectedData.Protect(value, Entropy(role), DataProtectionScope.CurrentUser);
        lock (_gate) AtomicFile.WriteAllBytes(PathOf(role), blob);
    }

    private byte[]? Load(string role)
    {
        byte[] blob;
        lock (_gate)
        {
            var path = PathOf(role);
            if (!File.Exists(path)) return null;
            try { blob = File.ReadAllBytes(path); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        try
        {
            return ProtectedData.Unprotect(blob, Entropy(role), DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private void Remove(string role)
    {
        var path = PathOf(role);
        if (File.Exists(path)) File.Delete(path);
    }
}
