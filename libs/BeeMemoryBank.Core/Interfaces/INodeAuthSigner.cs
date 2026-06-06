using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

/// <summary>
/// Signs the sync authentication challenge with the node's Ed25519 identity key.
/// Abstracts WHERE the signing key comes from so the sync path is decoupled from the
/// content master DEK:
///   - Server / CLI / unlocked mobile foreground: the default implementation derives
///     the key via the master DEK (existing behaviour).
///   - Mobile background "ingest" path: a platform implementation can sign using a
///     hardware-backed key (Android Keystore) WITHOUT the master DEK, so the encrypted
///     backup replica keeps receiving events while the vault is locked and across reboots.
/// </summary>
public interface INodeAuthSigner
{
    /// <summary>
    /// Signs <paramref name="challengePayload"/> with the node's Ed25519 private key.
    /// Implementations must clear any plaintext key material they materialise.
    /// </summary>
    byte[] SignChallenge(NodeIdentity identity, byte[] challengePayload);
}
