using BeeMemoryBank.BlindIos.Services;
using Foundation;
using Security;

namespace BeeMemoryBank.BlindIos.Platforms.iOS;

/// <summary>
/// The iOS Keychain through the SDK's Security bindings (SecItemAdd / SecItemUpdate / SecItemCopyMatching / SecItemDelete): generic-password
/// items of this app's own access group, service <see cref="IosKeychainSecretStore.DefaultService"/>. Every item is
/// <c>kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly</c> and not synchronizable: readable by the background rounds while the phone is
/// locked (after the first unlock since it started), never copied to iCloud Keychain or into a backup that could be restored on another
/// phone.
/// </summary>
internal sealed class SecKeychainBackend(string service) : IIosKeychain
{
    private const SecAccessible Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly;

    public int Read(string account, out byte[]? data)
    {
        data = null;
        using var query = Query(account);
        using var match = SecKeyChain.QueryAsData(query, false, out var status);
        if (status == SecStatusCode.Success && match is not null) data = match.ToArray();
        return (int)status;
    }

    public int Write(string account, byte[] data)
    {
        using var value = NSData.FromArray(data);
        using var record = new SecRecord(SecKind.GenericPassword)
        {
            Service = service,
            Account = account,
            Label = "Bee Memory Bank blind copy",
            Accessible = Accessible,
            Synchronizable = false,
            ValueData = value,
        };
        var status = SecKeyChain.Add(record);
        if (status != SecStatusCode.DuplicateItem) return (int)status;

        // The item is there: its data is replaced in place, never removed first (a crash between a delete and an add would lose the key).
        using var query = Query(account);
        using var update = new SecRecord { ValueData = value, Accessible = Accessible };
        return (int)SecKeyChain.Update(query, update);
    }

    public int Delete(string account)
    {
        using var query = Query(account);
        return (int)SecKeyChain.Remove(query);
    }

    private SecRecord Query(string account) => new(SecKind.GenericPassword) { Service = service, Account = account, Synchronizable = false };
}
