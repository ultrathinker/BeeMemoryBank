using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Mobile.Services;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Mobile.Platforms.Android;

/// <summary>
/// Mobile <see cref="INodeAuthSigner"/> that prefers the hardware-backed ingest key (Android
/// Keystore) so background backup-sync can authenticate while the vault is locked and after a
/// reboot — without the master DEK. Before enrolment (the first unlock hasn't happened yet on
/// this install) it falls back to the master-DEK path while the session is unlocked.
///
/// Signing here only authorises sync transport; it never yields the master DEK, so it cannot
/// decrypt article content.
/// </summary>
public sealed class KeystoreNodeAuthSigner(
    SessionService session,
    IIngestKeyStore ingest,
    ILogger<KeystoreNodeAuthSigner> logger) : INodeAuthSigner
{
    public byte[] SignChallenge(NodeIdentity identity, byte[] challengePayload)
    {
        if (ingest.HasEnrolledKey())
        {
            byte[]? seed = null;
            try
            {
                seed = ingest.UnwrapNodePrivateKey();
                return Ed25519Signer.Sign(seed, challengePayload);
            }
            catch (Exception ex)
            {
                // The Keystore blob is unusable (corruption, key lost on OS update / restore).
                // Wipe it so the NEXT foreground unlock re-enrols a fresh seed — otherwise
                // HasEnrolledKey() stays true and background sync is permanently DoS'd.
                logger.LogWarning(ex,
                    "Ingest-key signing failed; clearing the ingest key for re-enrolment and falling back to master DEK if unlocked.");
                try { ingest.Clear(); } catch { /* best-effort */ }
            }
            finally
            {
                if (seed != null) Array.Clear(seed);
            }
        }

        if (session.IsUnlocked)
            return NodeIdentityCrypto.SignWithIdentityOrGetDek(
                identity.Ed25519PrivateKey,
                identity.Ed25519PrivateKeyIV,
                identity.Ed25519PrivateKeyV,
                identity.NodeId,
                session.GetMasterDek,
                challengePayload);

        throw new InvalidOperationException(
            "Cannot sign sync auth: ingest key not enrolled and session is locked. Unlock once to enrol.");
    }
}
