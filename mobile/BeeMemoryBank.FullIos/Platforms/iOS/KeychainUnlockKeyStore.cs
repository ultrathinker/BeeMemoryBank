using BeeMemoryBank.FullIos.Services;
using Foundation;
using LocalAuthentication;
using Security;

namespace BeeMemoryBank.FullIos.Platforms.iOS;

/// <summary>
/// The quick unlock's two Keychain items (generic passwords of this app's own access group, service <see cref="Service"/>):
/// <list type="bullet">
/// <item><c>unlock-key-v1</c>, the random unlock key: access control "biometry, current set" with "when passcode set, this device only" -
/// iOS releases it only after Face ID or Touch ID succeeds, drops it when faces or fingers are added or removed or the passcode is turned
/// off, and never puts it in a backup or the iCloud Keychain;</item>
/// <item><c>unlock-slot-v1</c>, the master key wrapped under that unlock key: "when unlocked, this device only", not synchronisable.</item>
/// </list>
/// Neither item ever holds the password, and neither is written to a file or a log.
/// </summary>
internal sealed class KeychainUnlockKeyStore : IUnlockKeyStore
{
    public const string Service = "com.beememorybank.mobile.quick-unlock";
    private const string KeyAccount = "unlock-key-v1";
    private const string SlotAccount = "unlock-slot-v1";

    public bool IsAvailable
    {
        get
        {
            using var context = new LAContext();
            return context.CanEvaluatePolicy(LAPolicy.DeviceOwnerAuthenticationWithBiometrics, out _);
        }
    }

    public string BiometryName
    {
        get
        {
            using var context = new LAContext();
            context.CanEvaluatePolicy(LAPolicy.DeviceOwnerAuthenticationWithBiometrics, out _);
            return context.BiometryType switch
            {
                LABiometryType.FaceId => "Face ID",
                LABiometryType.TouchId => "Touch ID",
                _ => "Face ID or Touch ID",
            };
        }
    }

    public bool IsEnrolled => ReadSlot() is not null && Exists(KeyAccount);

    public void Save(byte[] unlockKey, byte[] slot)
    {
        Forget();
        using var access = new SecAccessControl(SecAccessible.WhenPasscodeSetThisDeviceOnly, SecAccessControlCreateFlags.BiometryCurrentSet);
        using (var keyData = NSData.FromArray(unlockKey))
        using (var keyRecord = new SecRecord(SecKind.GenericPassword)
        {
            Service = Service,
            Account = KeyAccount,
            Label = "Bee Memory Bank quick unlock",
            AccessControl = access,
            Synchronizable = false,
            ValueData = keyData,
        })
            Check(SecKeyChain.Add(keyRecord), "store the unlock key");

        using var slotData = NSData.FromArray(slot);
        using var slotRecord = new SecRecord(SecKind.GenericPassword)
        {
            Service = Service,
            Account = SlotAccount,
            Label = "Bee Memory Bank quick unlock",
            Accessible = SecAccessible.WhenUnlockedThisDeviceOnly,
            Synchronizable = false,
            ValueData = slotData,
        };
        Check(SecKeyChain.Add(slotRecord), "store the wrapped master key");
    }

    public byte[]? ReadSlot()
    {
        using var query = Query(SlotAccount);
        using var data = SecKeyChain.QueryAsData(query, false, out var status);
        return status == SecStatusCode.Success && data is not null ? data.ToArray() : null;
    }

    public async Task<byte[]?> ReadUnlockKeyAsync(string reason)
    {
        // The face or finger is asked for once, here; the same context then opens the item without a second prompt.
        using var context = new LAContext { LocalizedFallbackTitle = "" };
        var (ok, _) = await context.EvaluatePolicyAsync(LAPolicy.DeviceOwnerAuthenticationWithBiometrics, reason);
        if (!ok) return null;
        return await Task.Run(() =>
        {
            using var query = Query(KeyAccount);
            query.AuthenticationContext = context;
            using var data = SecKeyChain.QueryAsData(query, false, out var status);
            return status == SecStatusCode.Success && data is not null ? data.ToArray() : null;
        });
    }

    public void Forget()
    {
        foreach (var account in new[] { KeyAccount, SlotAccount })
        {
            using var query = Query(account);
            var status = SecKeyChain.Remove(query);
            if (status != SecStatusCode.Success && status != SecStatusCode.ItemNotFound) Check(status, "remove the quick unlock");
        }
    }

    private static bool Exists(string account)
    {
        // Asks only whether the item is there: no data is returned, so no Face ID prompt.
        using var query = Query(account);
        query.AuthenticationUI = SecAuthenticationUI.Skip;
        SecKeyChain.QueryAsRecord(query, out var status)?.Dispose();
        return status == SecStatusCode.Success || status == SecStatusCode.InteractionNotAllowed;
    }

    private static SecRecord Query(string account) =>
        new(SecKind.GenericPassword) { Service = Service, Account = account, Synchronizable = false };

    private static void Check(SecStatusCode status, string what)
    {
        if (status != SecStatusCode.Success) throw new InvalidOperationException($"The Keychain refused to {what} ({status}).");
    }
}
