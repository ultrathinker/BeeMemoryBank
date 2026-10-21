using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Core.Services.BlindPhone;

/// <summary>What Windows does with an Android blind node's code, as values its page then applies.</summary>
/// <param name="Entry">The whitelist row to add with <c>whitelist_add</c> — a plain peer, never a superadmin.</param>
/// <param name="SealedSecretName">Name to seal <paramref name="Seal"/> under the DEK as (<c>android-backup:&lt;id&gt;</c>).</param>
/// <param name="Seal">The phone's backup key with the node it calls — the producer of its backups' package. The
/// caller seals <see cref="BlindPhoneBackupSeal.Encode"/> and clears the key.</param>
/// <param name="CallCode">The "where to call" code to show the phone.</param>
public sealed record BlindPhoneEnrollment(WhitelistEntry Entry, string SealedSecretName, BlindPhoneBackupSeal Seal, BlindCallCode CallCode)
{
    /// <summary>
    /// Windows side of pairing an Android blind node (plan section 10, the "add Android blind node"
    /// flow on the Blind nodes page). Returns null and an error for a code that is not a phone code;
    /// the page then calls <c>whitelist_add</c>, seals the key (<c>SealedSecretService.PublishAsync</c>)
    /// and shows <see cref="CallCode"/>.
    /// </summary>
    /// <param name="listeningAddress">https address of the node the phone will call (hub or server blind node).</param>
    /// <param name="listeningSpkiPin">SPKI pin of that node's TLS key; ignored (the code carries none) for <see cref="BlindTrust.PublicCa"/>.</param>
    /// <param name="listeningPublicKey">Ed25519 key of that node — the phone checks the blind package with it.</param>
    /// <param name="listeningTrust">How the phone trusts that node's TLS endpoint: <see cref="BlindTrust.Pin"/> (the default)
    /// or <see cref="BlindTrust.PublicCa"/>.</param>
    public static BlindPhoneEnrollment? Prepare(string phoneCodeText, string listeningAddress, Guid listeningNodeId,
        string listeningSpkiPin, byte[] listeningPublicKey, DateTime now, out string? error, string listeningTrust = BlindTrust.Pin)
    {
        if (!BlindPhoneCode.TryParse(phoneCodeText, out var phone))
        {
            error = "This is not the code of an Android blind copy. Copy it again from the phone.";
            return null;
        }
        if (!BlindTrust.IsKnown(listeningTrust))
        {
            error = "This node has no way to be trusted by a phone that this version understands.";
            return null;
        }

        var entry = NewEntry(phone.NodeId, phone.DisplayName, phone.PublicKey, now);
        var publicCa = listeningTrust == BlindTrust.PublicCa;
        var call = publicCa
            ? BlindCallCode.CreatePublicCa(listeningAddress, listeningNodeId, listeningPublicKey, phone.Secret)
            : BlindCallCode.Create(listeningAddress, listeningNodeId, listeningSpkiPin, listeningPublicKey, phone.Secret);
        error = null;
        var seal = NewSeal(phone.BackupKey, listeningNodeId, listeningPublicKey, call.Address, publicCa ? "" : listeningSpkiPin, listeningTrust);
        return new BlindPhoneEnrollment(entry, $"android-backup:{phone.NodeId}", seal, call);
    }

    /// <summary>
    /// The same enrolment for a device that was invited (release 2.7): it redeemed an invitation, the owner confirmed it, and the hub is the node it
    /// calls. There is no code to answer — the invitation already told the device where to call — so the result is the row and the seal only.
    /// The row is a plain peer, never a superadmin; the seal is unsigned here, the confirming node signs it before sealing.
    /// </summary>
    /// <param name="hubAddress">The https origin the invitation named (this node's own public address).</param>
    public static BlindInvitedEnrollment? PrepareInvited(Guid nodeId, byte[] publicKey, string displayName, byte[] backupKey,
        string hubAddress, Guid hubNodeId, string hubSpkiPin, byte[] hubPublicKey, string hubTrust, DateTime now, out string? error)
    {
        if (!BlindNodeId.IsBlind(nodeId) || publicKey.Length != CryptoConstants.Ed25519PublicKeySize || backupKey.Length != 32)
        {
            error = "The device that redeemed this invitation is not a blind copy this node can enrol.";
            return null;
        }
        if (!BlindInviteText.IsPlain(displayName, BlindPhoneCode.MaxDisplayNameLength))
        {
            error = $"The name of this device is not a name a peer can have: {BlindPhoneCode.MaxDisplayNameLength} characters at most, without control characters.";
            return null;
        }
        if (!BlindTrust.IsKnown(hubTrust) || CodeText.HttpsOrigin(hubAddress) is not { } origin)
        {
            error = "This invitation names no address and certificate trust that a blind copy can call.";
            return null;
        }
        error = null;
        var seal = NewSeal(backupKey, hubNodeId, hubPublicKey, origin, hubTrust == BlindTrust.PublicCa ? "" : hubSpkiPin, hubTrust);
        return new BlindInvitedEnrollment(NewEntry(nodeId, displayName, publicKey, now), $"android-backup:{nodeId}", seal);
    }

    private static WhitelistEntry NewEntry(Guid nodeId, string displayName, byte[] publicKey, DateTime now) => new()
    {
        NodeId = nodeId,
        DisplayName = displayName,
        Ed25519PublicKey = publicKey,
        Status = "A",
        CreatedAt = now,
        UpdatedAt = now,
        IsSuperadmin = false,
    };

    // Unsigned here: the page's node signs it (PairedBy / PairingSignature) before sealing.
    private static BlindPhoneBackupSeal NewSeal(byte[] backupKey, Guid listeningNodeId, byte[] listeningPublicKey, string address, string pin, string trust) =>
        new(backupKey.ToArray(), listeningNodeId, listeningPublicKey.ToArray(), address, pin, Guid.Empty, [], trust);
}

/// <summary>What the hub does with a device whose invitation the owner confirmed: the row to add and the seal to publish (see <see cref="BlindPhoneEnrollment.PrepareInvited"/>).</summary>
public sealed record BlindInvitedEnrollment(WhitelistEntry Entry, string SealedSecretName, BlindPhoneBackupSeal Seal);
