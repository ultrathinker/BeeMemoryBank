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
    /// <param name="listeningSpkiPin">SPKI pin of that node's TLS key.</param>
    /// <param name="listeningPublicKey">Ed25519 key of that node — the phone checks the blind package with it.</param>
    public static BlindPhoneEnrollment? Prepare(string phoneCodeText, string listeningAddress, Guid listeningNodeId,
        string listeningSpkiPin, byte[] listeningPublicKey, DateTime now, out string? error)
    {
        if (!BlindPhoneCode.TryParse(phoneCodeText, out var phone))
        {
            error = "This is not the code of an Android blind copy. Copy it again from the phone.";
            return null;
        }

        var entry = new WhitelistEntry
        {
            NodeId = phone.NodeId,
            DisplayName = phone.DisplayName,
            Ed25519PublicKey = phone.PublicKey,
            Status = "A",
            CreatedAt = now,
            UpdatedAt = now,
            IsSuperadmin = false,
        };
        var call = BlindCallCode.Create(listeningAddress, listeningNodeId, listeningSpkiPin, listeningPublicKey, phone.Secret);
        error = null;
        // Unsigned here: the page's node signs it (PairedBy / PairingSignature) before sealing.
        var seal = new BlindPhoneBackupSeal(phone.BackupKey.ToArray(), listeningNodeId, listeningPublicKey.ToArray(),
            call.Address, listeningSpkiPin, Guid.Empty, []);
        return new BlindPhoneEnrollment(entry, $"android-backup:{phone.NodeId}", seal, call);
    }
}
