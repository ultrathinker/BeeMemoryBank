using BeeMemoryBank.FullIos.Services;
using Foundation;
using LocalAuthentication;
using Security;

namespace BeeMemoryBank.FullIos.Platforms.iOS;

/// <summary>
/// The quick unlock's Keychain item (a generic password of this app's own access group, service <see cref="Service"/>):
/// <c>door-secret-v2</c>, the 32-byte secret of the vault's <c>quick_unlock</c> door (2.12), with access control "biometry, current set" and
/// "when passcode set, this device only" - iOS releases it only after Face ID or Touch ID succeeds, drops it when faces or fingers are added
/// or removed or the passcode is turned off, and never puts it in a backup or the iCloud Keychain. The master key is in the door, not here.
/// <para>A phone set up before 2.12 has <c>unlock-key-v1</c> (its unlock key, behind the biometry the same way) and <c>unlock-slot-v1</c> (the
/// master key wrapped under it) instead. They are only read (once, at the next unlock, which turns them into a door) and removed, never written
/// again - and removed only after the new secret is stored, so a Keychain that refuses the new item leaves the old way in working.</para>
/// None of the items ever holds the password, and none is written to a file or a log.
/// </summary>
internal sealed class KeychainUnlockKeyStore : IUnlockKeyStore
{
    public const string Service = "com.beememorybank.mobile.quick-unlock";
    private const string SecretAccount = "door-secret-v2";
    /// <summary>The unlock key of a phone set up before 2.12: read and removed, never written.</summary>
    private const string LegacyKeyAccount = "unlock-key-v1";
    /// <summary>The wrapped master key of a phone set up before 2.12: read and removed, never written.</summary>
    private const string LegacySlotAccount = "unlock-slot-v1";

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

    public bool IsEnrolled => Exists(SecretAccount) || (Exists(LegacyKeyAccount) && ReadLegacySlot() is not null);

    public void Save(byte[] secret)
    {
        // This store's own earlier secret goes (a renewal), the new one is added - the step a Keychain can refuse - and only then do an older
        // version's two items go: a refusal leaves them, and with them the old way in, working.
        Remove(SecretAccount);
        using var access = new SecAccessControl(SecAccessible.WhenPasscodeSetThisDeviceOnly, SecAccessControlCreateFlags.BiometryCurrentSet);
        using var keyData = NSData.FromArray(secret);
        using var keyRecord = new SecRecord(SecKind.GenericPassword)
        {
            Service = Service,
            Account = SecretAccount,
            Label = "Bee Memory Bank quick unlock",
            AccessControl = access,
            Synchronizable = false,
            ValueData = keyData,
        };
        Check(SecKeyChain.Add(keyRecord), "store the quick-unlock secret");
        RemoveLegacyItems();
    }

    public byte[]? ReadLegacySlot()
    {
        using var query = Query(LegacySlotAccount);
        using var data = SecKeyChain.QueryAsData(query, false, out var status);
        return status == SecStatusCode.Success && data is not null ? data.ToArray() : null;
    }

    public Task<byte[]?> ReadSecretAsync(string reason) => ReadBehindBiometryAsync(SecretAccount, reason);

    public Task<byte[]?> ReadLegacyKeyAsync(string reason) => ReadBehindBiometryAsync(LegacyKeyAccount, reason);

    public void Forget()
    {
        Remove(SecretAccount);
        RemoveLegacyItems();
    }

    private static async Task<byte[]?> ReadBehindBiometryAsync(string account, string reason)
    {
        // The face or finger is asked for once, here; the same context then opens the item without a second prompt.
        using var context = new LAContext { LocalizedFallbackTitle = "" };
        var (ok, _) = await context.EvaluatePolicyAsync(LAPolicy.DeviceOwnerAuthenticationWithBiometrics, reason);
        if (!ok) return null;
        return await Task.Run(() =>
        {
            using var query = Query(account);
            query.AuthenticationContext = context;
            using var data = SecKeyChain.QueryAsData(query, false, out var status);
            return status == SecStatusCode.Success && data is not null ? data.ToArray() : null;
        });
    }

    private static void RemoveLegacyItems()
    {
        Remove(LegacyKeyAccount);
        Remove(LegacySlotAccount);
    }

    private static void Remove(string account)
    {
        using var query = Query(account);
        var status = SecKeyChain.Remove(query);
        if (status != SecStatusCode.Success && status != SecStatusCode.ItemNotFound) Check(status, "remove the quick unlock");
    }

    private static bool Exists(string account)
    {
        // Asks only whether the item is there, without a Face ID prompt. "Fail", not "Skip": with Skip iOS leaves out every item that
        // needs Face ID, so the unlock key always looked missing - the quick unlock never counted as on, the unlock page offered no Face ID
        // and iOS was never asked for it (found on the wife's iPhone 15 Pro, 15.11). With Fail such an item answers "interaction not allowed".
        using var query = Query(account);
        query.AuthenticationUI = SecAuthenticationUI.Fail;
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
