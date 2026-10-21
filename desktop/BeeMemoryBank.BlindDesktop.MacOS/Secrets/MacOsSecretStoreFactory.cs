using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Platforms.Apple.Keychain;

namespace BeeMemoryBank.BlindDesktop.MacOS;

/// <summary>
/// Gives each account its own Keychain items, under the NAMING CONTRACT (<see cref="BlindAccountNames"/>): one service (<c>com.beememorybank.blind</c>) for the app; the legacy
/// account (the 2.6 install) keeps the accounts <c>identity-seed-v1</c>, <c>pairing-secret-v1</c> and <c>backup-key-v1</c> unchanged, any other account has
/// <c>&lt;id&gt;/identity-seed-v1</c> ... The integrity digest in each item binds the Keychain account name, so an item copied under another account's name is refused
/// (<see cref="BlindSecretStoreFailure.Corrupt"/>) and never used. Removing an account clears its three items (<see cref="MacOsKeychainSecretStore.Clear"/>).
/// The factory owns one Keychain backend and shares it with every store it makes; disposing the factory releases it.
/// </summary>
public sealed class MacOsSecretStoreFactory : IBlindSecretStoreFactory, IBlindSecretBlobAccess, IDisposable
{
    private readonly IKeychainBackend _backend;
    private readonly string _service;

    /// <summary>The user's default (login) keychain.</summary>
    public MacOsSecretStoreFactory(string service = MacOsKeychainSecretStore.DefaultService)
        : this(new SecurityFrameworkKeychain(), service) { }

    /// <summary>A keychain file: every call is confined to it. For tests and tools; never the login keychain.</summary>
    public MacOsSecretStoreFactory(string keychainFilePath, string service)
        : this(new SecurityFrameworkKeychain(keychainFilePath, allowUserInteraction: false), service) { }

    internal MacOsSecretStoreFactory(IKeychainBackend backend, string service = MacOsKeychainSecretStore.DefaultService)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        _backend = backend;
        _service = service;
    }

    public IBlindSecretStore For(BlindAccountEntry account) => new MacOsKeychainSecretStore(_backend, _service, Checked(account), ownsBackend: false);

    /// <summary>
    /// The id ends up in a Keychain account name next to the secret's role. The account host only makes ids of its own and refuses any other from a damaged list; this is the same refusal at the
    /// edge of the store, so that no name is ever built from an id this app did not make (and a legacy entry is the one with the legacy layout, and only that).
    /// </summary>
    private static BlindAccountEntry Checked(BlindAccountEntry account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!BlindAccountEntry.IsValidId(account.Id) || account.IsLegacy != (account.Id == BlindAccountEntry.LegacyId))
            throw new ArgumentException("This account has no usable id.", nameof(account));
        return account;
    }

    /// <summary>The raw item as the Keychain holds it (the integrity envelope included), or null when there is none; for the contract tests.</summary>
    public byte[]? ReadBlob(BlindAccountEntry account, BlindAccountNames.Role role)
    {
        var status = _backend.Copy(_service, BlindAccountNames.KeychainAccount(Checked(account), role), out var item);
        if (status == KeychainStatus.ItemNotFound) return null;
        if (status != KeychainStatus.Success) throw new BlindSecretStoreException(BlindSecretStoreFailure.Os, $"The Keychain item could not be read (status {status}).", status);
        return item;
    }

    /// <summary>Puts a raw item under an account's name, as copying it with another tool would; for the contract tests.</summary>
    public void WriteBlob(BlindAccountEntry account, BlindAccountNames.Role role, byte[] blob)
    {
        var name = BlindAccountNames.KeychainAccount(Checked(account), role);
        var status = _backend.Add(_service, name, "Bee Memory Bank blind app: test item", blob);
        if (status == KeychainStatus.DuplicateItem) status = _backend.Update(_service, name, blob);
        if (status != KeychainStatus.Success) throw new BlindSecretStoreException(BlindSecretStoreFailure.Os, $"The Keychain item could not be written (status {status}).", status);
    }

    public void Dispose()
    {
        if (_backend is IDisposable disposable) disposable.Dispose();
    }
}
