using System.Security.Cryptography;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.Windows;

/// <summary>
/// The blind app's secrets (identity seed, backup key, one-time pairing secret) as DPAPI blobs, <see cref="DataProtectionScope.CurrentUser"/>:
/// only this Windows user, on this machine and profile, can open them. Each lives in its own file under
/// <see cref="WindowsBlindPaths.SecretsDirectory"/>; a key never goes into SQLite, the state file or the log.
///
/// <para>Every blob is protected with its own entropy (the app, the format version, the account and the secret's role: <see cref="BlindAccountNames.WindowsEntropy"/>),
/// so a blob of one role cannot be opened as another one, and a blob of one account not as another account's, by swapping files. The legacy account (the 2.6 install)
/// keeps today's entropy and file names byte for byte; any other account has its id in the entropy and its own folder. A blob that was read but cannot be opened (tampered, truncated, made by another
/// user or by a profile that was reset) reads as "no such secret"; a blob that cannot be READ for now (held by another process, access
/// denied) is an <see cref="IOException"/>, never "no such secret", so that a transient failure does not push the person toward a wipe. AppCore treats a missing key next to an existing identity as a reason
/// to stop and offer "Disconnect and wipe", it never makes a replacement key over existing data.</para>
/// </summary>
public sealed class DpapiSecretStore : IBlindSecretStore
{
    private const BlindAccountNames.Role Identity = BlindAccountNames.Role.IdentitySeed;
    private const BlindAccountNames.Role Backup = BlindAccountNames.Role.BackupKey;
    private const BlindAccountNames.Role Pairing = BlindAccountNames.Role.PairingSecret;
    private static readonly BlindAccountNames.Role[] AllRoles = [Identity, Backup, Pairing];
    private static readonly BlindAccountEntry LegacyAccount = new(BlindAccountEntry.LegacyId, BlindAccountLayouts.Root, "My copy");

    // One lock per secrets FOLDER, shared by every store object that is alive for it (the account host asks for an account's store more than once in its life). The table holds the locks
    // WEAKLY and each store holds its own lock: when the last store of a folder is gone (its account was removed) so is the lock, instead of one lock for every folder ever used staying for the
    // life of the process. Nobody needs the lock when no store of the folder is alive, and the next store makes a new one.
    private const int SweepAbove = 32;
    private static readonly Dictionary<string, WeakReference<object>> FolderLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly string secretsDirectory;
    private readonly BlindAccountEntry _account;
    private readonly object _gate;

    /// <summary>The 2.6 account's secrets (the legacy names).</summary>
    public DpapiSecretStore(string secretsDirectory) : this(secretsDirectory, LegacyAccount)
    {
    }

    /// <param name="secretsDirectory">The account's <c>secrets</c> folder.</param>
    /// <param name="account">Whose secrets these are: its entropy and file names come from <see cref="BlindAccountNames"/>.</param>
    public DpapiSecretStore(string secretsDirectory, BlindAccountEntry account)
    {
        this.secretsDirectory = secretsDirectory;
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _gate = LockOf(Path.GetFullPath(secretsDirectory));
    }

    private static object LockOf(string folder)
    {
        lock (FolderLocks)
        {
            if (FolderLocks.TryGetValue(folder, out var held) && held.TryGetTarget(out var live)) return live;
            if (FolderLocks.Count >= SweepAbove)
                foreach (var gone in FolderLocks.Where(pair => !pair.Value.TryGetTarget(out _)).Select(pair => pair.Key).ToList()) FolderLocks.Remove(gone);
            var made = new object();
            FolderLocks[folder] = new WeakReference<object>(made);
            return made;
        }
    }

    /// <summary>How many folders the lock table has an entry for, live or not yet swept (for the tests).</summary>
    internal static int TrackedFolderLocks
    {
        get { lock (FolderLocks) return FolderLocks.Count; }
    }

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

    private string PathOf(BlindAccountNames.Role role) => Path.Combine(secretsDirectory, BlindAccountNames.WindowsSecretFile(role));

    private byte[] Entropy(BlindAccountNames.Role role) => BlindAccountNames.WindowsEntropy(_account, role);

    /// <summary>The persisted blob of a role as it is on disk (null when there is none): for the contract tests, which move one blob to another account's slot.</summary>
    internal byte[]? ReadBlob(BlindAccountNames.Role role)
    {
        lock (_gate) return File.Exists(PathOf(role)) ? File.ReadAllBytes(PathOf(role)) : null;
    }

    /// <summary>Puts a blob in a role's slot, as copying the file would.</summary>
    internal void WriteBlob(BlindAccountNames.Role role, byte[] blob)
    {
        lock (_gate) AtomicFile.WriteAllBytes(PathOf(role), blob);
    }

    private void Save(BlindAccountNames.Role role, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var blob = ProtectedData.Protect(value, Entropy(role), DataProtectionScope.CurrentUser);
        lock (_gate) AtomicFile.WriteAllBytes(PathOf(role), blob);
    }

    private byte[]? Load(BlindAccountNames.Role role)
    {
        byte[] blob;
        lock (_gate)
        {
            var path = PathOf(role);
            if (!File.Exists(path)) return null;
            // Only a file that is not there is "no such secret". A file that cannot be read for now (another process holds it, access
            // denied) is a store that did not answer: the screen says so and changes nothing, instead of "key lost, wipe and pair again".
            try { blob = File.ReadAllBytes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            // A sentence of this code (marked: BlindFailureText passes it on), with no path in it; the framework's own failure rides along as the inner exception, for the log only.
            catch (UnauthorizedAccessException ex) { throw new BlindFileSentenceException("The secret store did not answer: the file cannot be read (access denied).", ex); }
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

    private void Remove(BlindAccountNames.Role role)
    {
        var path = PathOf(role);
        if (File.Exists(path)) File.Delete(path);
    }
}
