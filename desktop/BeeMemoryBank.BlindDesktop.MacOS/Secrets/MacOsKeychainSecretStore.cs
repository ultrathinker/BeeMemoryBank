using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Platforms.Apple.Keychain;

namespace BeeMemoryBank.BlindDesktop.MacOS;

/// <summary>Why a Keychain operation failed. Never carries secret material.</summary>
public enum BlindSecretStoreFailure
{
    /// <summary>The system refused or could not do the call: locked keychain, denied access, no such keychain, a prompt that was not allowed.</summary>
    Os,
    /// <summary>An item exists but is not what this app wrote (changed or replaced from outside, or moved to another purpose).</summary>
    Corrupt,
}

public sealed class BlindSecretStoreException : IOException
{
    public BlindSecretStoreFailure Failure { get; }
    /// <summary>The macOS OSStatus for <see cref="BlindSecretStoreFailure.Os"/>, otherwise 0.</summary>
    public int OsStatus { get; }

    public BlindSecretStoreException(BlindSecretStoreFailure failure, string message, int osStatus = 0)
        : base(message)
    {
        Failure = failure;
        OsStatus = osStatus;
    }
}

/// <summary>
/// The blind app's three secrets in the macOS Keychain: the identity seed, the pairing secret and the backup key (the same three the Android
/// Keystore class holds). One generic-password item per secret, service = the blind app's service name, account = the secret's name. A
/// secret never goes to SQLite, a file or a log, and the buffers made here are zeroed after use.
///
/// <para>Fail closed, as on Android: a secret that is not there is reported as <c>null</c> and nothing here ever makes a replacement; the
/// callers (BlindMobilePairing) decide what a missing secret means. A Keychain error that is NOT "no such item" (locked keychain, access
/// denied, a prompt that may not be shown) is an exception, never <c>null</c>: reporting a transient failure as "the key is lost" would
/// push the person toward a wipe.</para>
///
/// <para>Each item holds <c>version(1) | SHA-256(purpose label | account | secret)(32) | secret</c>. That is an integrity check, not
/// authentication: it makes an item that was edited or swapped from outside (a different account's secret copied over, a plain string put
/// there with the security tool) fail loudly as <see cref="BlindSecretStoreFailure.Corrupt"/> instead of being used - the equivalent of the
/// Android blob's GCM tag and purpose-binding AAD. Who may read an item is the Keychain's access list: the item is created by this
/// process, so only this app (as signed) reads it without a prompt.</para>
/// </summary>
public sealed class MacOsKeychainSecretStore : IBlindSecretStore, IDisposable
{
    /// <summary>The Keychain service name of the blind app (never the full app's).</summary>
    public const string DefaultService = "com.beememorybank.blind";

    internal const string IdentitySeedAccount = "identity-seed-v1";
    internal const string PairingSecretAccount = "pairing-secret-v1";
    internal const string BackupKeyAccount = "backup-key-v1";

    private const byte EnvelopeVersion = 1;
    private const int DigestLength = 32;
    private static readonly byte[] Domain = "bmb-blind-keychain-item-v1\0"u8.ToArray();

    private readonly IKeychainBackend _backend;
    private readonly string _service;
    private readonly object _gate = new();

    /// <summary>The user's default (login) keychain.</summary>
    public MacOsKeychainSecretStore(string service = DefaultService)
        : this(new SecurityFrameworkKeychain(), service) { }

    /// <summary>A keychain file: every call is confined to it. For tests and tools; never the login keychain.</summary>
    public MacOsKeychainSecretStore(string keychainFilePath, string service)
        : this(new SecurityFrameworkKeychain(keychainFilePath, allowUserInteraction: false), service) { }

    internal MacOsKeychainSecretStore(IKeychainBackend backend, string service = DefaultService)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        _backend = backend;
        _service = service;
    }

    public void SaveIdentitySeed(byte[] seed) => Save(IdentitySeedAccount, "identity seed", seed);
    public byte[]? LoadIdentitySeed() => Load(IdentitySeedAccount);
    public void SaveBackupKey(byte[] key) => Save(BackupKeyAccount, "backup key", key);
    public byte[]? LoadBackupKey() => Load(BackupKeyAccount);
    public void SavePairingSecret(byte[] secret) => Save(PairingSecretAccount, "pairing secret", secret);
    public byte[]? LoadPairingSecret() => Load(PairingSecretAccount);
    public void ClearPairingSecret() => Delete(PairingSecretAccount);

    /// <summary>Forgets every secret. Every account is tried; failures are reported together once all were tried.</summary>
    public void Clear()
    {
        var failures = new List<Exception>();
        foreach (var account in new[] { IdentitySeedAccount, PairingSecretAccount, BackupKeyAccount })
        {
            try { Delete(account); }
            catch (BlindSecretStoreException ex) { failures.Add(ex); }
        }
        if (failures.Count > 0)
            throw new AggregateException("Not every blind secret could be removed from the Keychain: " +
                string.Join("; ", failures.Select(f => f.Message)), failures);
    }

    public void Dispose()
    {
        if (_backend is IDisposable disposable) disposable.Dispose();
    }

    private void Save(string account, string what, byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var item = Seal(account, secret);
        try
        {
            lock (_gate)
            {
                var label = "Bee Memory Bank blind app: " + what;
                var status = _backend.Add(_service, account, label, item);
                if (status == KeychainStatus.DuplicateItem)
                    status = _backend.Update(_service, account, item);
                if (status != KeychainStatus.Success)
                    throw OsFailure($"The {what} could not be saved in the Keychain", status);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(item);
        }
    }

    private byte[]? Load(string account)
    {
        byte[]? item;
        int status;
        lock (_gate) status = _backend.Copy(_service, account, out item);
        if (status == KeychainStatus.ItemNotFound) return null;
        if (status != KeychainStatus.Success || item is null)
            throw OsFailure($"The Keychain item '{account}' could not be read", status);
        try
        {
            return Open(account, item);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(item);
        }
    }

    private void Delete(string account)
    {
        int status;
        lock (_gate) status = _backend.Delete(_service, account);
        if (status is KeychainStatus.Success or KeychainStatus.ItemNotFound) return;
        throw OsFailure($"The Keychain item '{account}' could not be removed", status);
    }

    private BlindSecretStoreException OsFailure(string what, int status)
    {
        var detail = _backend.Describe(status);
        return new BlindSecretStoreException(BlindSecretStoreFailure.Os,
            $"{what} (Keychain status {status}{(detail.Length > 0 ? ": " + detail : "")}).", status);
    }

    private static byte[] Digest(string account, ReadOnlySpan<byte> secret)
    {
        var accountBytes = Encoding.UTF8.GetBytes(account);
        var input = new byte[Domain.Length + accountBytes.Length + 1 + secret.Length];
        try
        {
            Domain.CopyTo(input, 0);
            accountBytes.CopyTo(input, Domain.Length);
            secret.CopyTo(input.AsSpan(Domain.Length + accountBytes.Length + 1));
            return SHA256.HashData(input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    internal static byte[] Seal(string account, byte[] secret)
    {
        var item = new byte[1 + DigestLength + secret.Length];
        item[0] = EnvelopeVersion;
        var digest = Digest(account, secret);
        digest.CopyTo(item, 1);
        CryptographicOperations.ZeroMemory(digest);
        secret.CopyTo(item, 1 + DigestLength);
        return item;
    }

    internal static byte[] Open(string account, byte[] item)
    {
        if (item.Length < 1 + DigestLength || item[0] != EnvelopeVersion)
            throw Corrupt(account);
        var secret = item.AsSpan(1 + DigestLength).ToArray();
        var expected = Digest(account, secret);
        var intact = CryptographicOperations.FixedTimeEquals(expected, item.AsSpan(1, DigestLength));
        CryptographicOperations.ZeroMemory(expected);
        if (!intact)
        {
            CryptographicOperations.ZeroMemory(secret);
            throw Corrupt(account);
        }
        return secret;
    }

    private static BlindSecretStoreException Corrupt(string account) =>
        new(BlindSecretStoreFailure.Corrupt,
            // Short on purpose: the screen shows at most about 120 characters of a key store problem, and the advice must be in them.
            $"Keychain item '{account}' was altered outside the app and is not used; wipe and pair again.");
}
