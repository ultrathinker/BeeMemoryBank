using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindIos.Services;

/// <summary>The OSStatus values of the Keychain calls that the store reacts to (Security.framework, the same numbers on every Apple OS).</summary>
public static class IosKeychainStatus
{
    public const int Success = 0;
    public const int DuplicateItem = -25299;
    public const int ItemNotFound = -25300;
    public const int InteractionNotAllowed = -25308;
    public const int MissingEntitlement = -34018;
}

/// <summary>
/// The three Keychain operations the store needs on generic-password items of this app, by account name. Returns the OSStatus of the
/// system call; the store decides what it means. A seam, so that the store's logic is tested anywhere with a fake and the real
/// Security.framework calls (SecKeychainBackend) run on the phone.
/// </summary>
public interface IIosKeychain
{
    /// <summary>The item's data, or null with <see cref="IosKeychainStatus.ItemNotFound"/>.</summary>
    int Read(string account, out byte[]? data);

    /// <summary>Adds the item, or replaces the data of the one that is there (in one call, never delete-then-add).</summary>
    int Write(string account, byte[] data);

    int Delete(string account);
}

/// <summary>
/// A Keychain call that failed for another reason than "no such item". Never carries secret material. Its message is a sentence of this code (the <see cref="IBlindOwnSentence"/> mark: the
/// closed failure text says it as it is) made of fixed words and the OSStatus number ONLY - never the name of the item, which holds the account's id.
/// </summary>
public sealed class IosKeychainException(string message, int status = 0) : IOException(message), IBlindOwnSentence
{
    /// <summary>The OSStatus, or 0 when the item was there but is not what this app wrote.</summary>
    public int Status { get; } = status;
}

/// <summary>
/// The iPhone blind copy's three secrets of ONE account - the identity seed, the pairing secret and the backup key, the same three the Android Keystore
/// class and the macOS Keychain store hold - each in one generic-password item of the iOS Keychain. The item names are
/// <see cref="BlindAccountNames.KeychainAccount"/>: account 1 (the 2.6 install) keeps <c>identity-seed-v1</c> and the others, a new account
/// <c>&lt;id&gt;/identity-seed-v1</c>, all under the one service; the envelope below binds the NAME, so an item copied into another account's slot is refused. The items are made
/// "after first unlock, this device only" by the backend: the background rounds can read them while the phone is locked, and they never
/// leave the phone (no iCloud Keychain, not in a backup restored on another device). A secret never goes to SQLite, a file or a log, and
/// the buffers made here are zeroed after use.
///
/// <para>Fail closed, as on Android and macOS: a secret that is not there is <c>null</c> and nothing here makes a replacement (the callers
/// in Blind.AppCore decide what a missing secret means); any other Keychain error is an exception, never <c>null</c>, because reporting a
/// passing failure as "the key is lost" would push the person toward a wipe.</para>
///
/// <para>Each item holds <c>version(1) | SHA-256(domain | account | 0 | secret)(32) | secret</c>, the macOS store's envelope: an item that
/// was replaced by another account's secret or by anything this app did not write fails loudly instead of being used. Who may read an
/// item is the Keychain's own rule: only this app (its access group).</para>
/// </summary>
public sealed class IosKeychainSecretStore : IBlindNodeKeys
{
    /// <summary>The Keychain service the items are filed under.</summary>
    public const string DefaultService = BlindAccountNames.KeychainService;

    /// <summary>The 2.6 names (account 1). Kept as constants so that a test can prove they are what <see cref="BlindAccountNames"/> gives the legacy account, byte for byte.</summary>
    internal const string IdentitySeedAccount = "identity-seed-v1";
    internal const string PairingSecretAccount = "pairing-secret-v1";
    internal const string BackupKeyAccount = "backup-key-v1";

    /// <summary>The entry of the account a 2.6 install is, whose names are the 2.6 ones.</summary>
    internal static readonly BlindAccountEntry LegacyAccount = new(BlindAccountEntry.LegacyId, BlindAccountLayouts.Root, "My copy");

    private const byte EnvelopeVersion = 1;
    private const int DigestLength = 32;
    private static readonly byte[] Domain = "bmb-blind-ios-keychain-item-v1\0"u8.ToArray();

    private readonly IIosKeychain _keychain;
    private readonly string _identitySeedAccount;
    private readonly string _pairingSecretAccount;
    private readonly string _backupKeyAccount;

    /// <summary>The store of the 2.6 install (account 1).</summary>
    public IosKeychainSecretStore(IIosKeychain keychain) : this(keychain, LegacyAccount) { }

    /// <summary>The store of one account: its three item names come from <see cref="BlindAccountNames.KeychainAccount"/> and from nowhere else.</summary>
    public IosKeychainSecretStore(IIosKeychain keychain, BlindAccountEntry account)
    {
        ArgumentNullException.ThrowIfNull(keychain);
        ArgumentNullException.ThrowIfNull(account);
        _keychain = keychain;
        _identitySeedAccount = BlindAccountNames.KeychainAccount(account, BlindAccountNames.Role.IdentitySeed);
        _pairingSecretAccount = BlindAccountNames.KeychainAccount(account, BlindAccountNames.Role.PairingSecret);
        _backupKeyAccount = BlindAccountNames.KeychainAccount(account, BlindAccountNames.Role.BackupKey);
    }

    public void SaveIdentitySeed(byte[] seed) => Save(_identitySeedAccount, seed);
    public byte[]? LoadIdentitySeed() => Load(_identitySeedAccount);
    public void SaveBackupKey(byte[] key) => Save(_backupKeyAccount, key);
    public byte[]? LoadBackupKey() => Load(_backupKeyAccount);
    public void SavePairingSecret(byte[] secret) => Save(_pairingSecretAccount, secret);
    public byte[]? LoadPairingSecret() => Load(_pairingSecretAccount);
    public void ClearPairingSecret() => Delete(_pairingSecretAccount);

    /// <summary>Forgets all three of this account (Disconnect and wipe, removal of the account). Every item is tried; the failures are reported together. Another account's items are not touched.</summary>
    public void Clear()
    {
        var failures = new List<Exception>();
        foreach (var account in new[] { _identitySeedAccount, _pairingSecretAccount, _backupKeyAccount })
        {
            try { Delete(account); }
            catch (IosKeychainException ex) { failures.Add(ex); }
        }
        if (failures.Count > 0)
            throw new AggregateException("Not every Keychain item could be removed: " + string.Join("; ", failures.Select(f => f.Message)), failures);
    }

    private void Save(string account, byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var item = Wrap(account, secret);
        try
        {
            var status = _keychain.Write(account, item);
            if (status != IosKeychainStatus.Success)
                throw new IosKeychainException($"The Keychain did not store a secret of this copy (OSStatus {status}).", status);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(item);
        }
    }

    private byte[]? Load(string account)
    {
        var status = _keychain.Read(account, out var item);
        if (status == IosKeychainStatus.ItemNotFound) return null;
        if (status != IosKeychainStatus.Success || item is null)
            throw new IosKeychainException($"The Keychain did not answer for a secret of this copy (OSStatus {status}).", status);
        try
        {
            return Unwrap(account, item);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(item);
        }
    }

    private void Delete(string account)
    {
        var status = _keychain.Delete(account);
        if (status is not (IosKeychainStatus.Success or IosKeychainStatus.ItemNotFound))
            throw new IosKeychainException($"The Keychain did not remove a secret of this copy (OSStatus {status}).", status);
    }

    internal static byte[] Wrap(string account, ReadOnlySpan<byte> secret)
    {
        var item = new byte[1 + DigestLength + secret.Length];
        item[0] = EnvelopeVersion;
        Digest(account, secret, item.AsSpan(1, DigestLength));
        secret.CopyTo(item.AsSpan(1 + DigestLength));
        return item;
    }

    internal static byte[] Unwrap(string account, ReadOnlySpan<byte> item)
    {
        if (item.Length <= 1 + DigestLength || item[0] != EnvelopeVersion)
            throw new IosKeychainException("A Keychain item of this copy is not one this app wrote.");
        var secret = item[(1 + DigestLength)..];
        Span<byte> expected = stackalloc byte[DigestLength];
        Digest(account, secret, expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, item.Slice(1, DigestLength)))
            throw new IosKeychainException("A Keychain item of this copy was changed or belongs to another purpose.");
        return secret.ToArray();
    }

    private static void Digest(string account, ReadOnlySpan<byte> secret, Span<byte> destination)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Domain);
        hash.AppendData(Encoding.UTF8.GetBytes(account));
        hash.AppendData([0]);
        hash.AppendData(secret);
        if (!hash.TryGetHashAndReset(destination, out _)) throw new CryptographicException("SHA-256 failed.");
    }
}

/// <summary>
/// Gives each account its own Keychain items: the one <see cref="IIosKeychain"/> (one service) is shared, the item names differ by account
/// (<see cref="BlindAccountNames.KeychainAccount"/>). The stores hold no state of their own, so a new one per call is fine. The CONTRACT every head runs
/// (<c>BlindSecretStoreContract</c>) is proved over this class and a fake Keychain in <c>IosKeychainSecretStoreFactoryContractTests</c>.
/// </summary>
public sealed class IosKeychainSecretStoreFactory(IIosKeychain keychain) : IBlindSecretStoreFactory
{
    public IBlindSecretStore For(BlindAccountEntry account) => new IosKeychainSecretStore(keychain, account);
}
