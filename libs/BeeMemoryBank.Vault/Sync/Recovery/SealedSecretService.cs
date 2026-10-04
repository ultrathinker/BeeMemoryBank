using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Sync.Recovery;

/// <summary>
/// Publishes a secret sealed under the current master DEK (plan 6.8): <c>restic:&lt;blind node id&gt;</c>
/// when a blind node is paired, <c>android-backup:&lt;node id&gt;</c> when an Android blind node is. The
/// pairing code calls <see cref="PublishAsync"/>; after a rotation <see cref="RecoveryReconciler"/>
/// carries every seal forward, so callers never re-seal themselves.
/// </summary>
public class SealedSecretService(SessionService session, RecoveryEventPublisher publisher)
{
    public static string ResticName(Guid blindNodeId) => $"restic:{blindNodeId}";
    public static string AndroidBackupName(Guid nodeId) => $"android-backup:{nodeId}";

    public async Task PublishAsync(string name, byte[] secret)
    {
        var dek = session.GetMasterDek();
        try
        {
            var (wrapped, iv) = SealedSecretCrypto.Seal(name, secret, dek);
            await publisher.PublishAsync(EventTypes.SealedSecretSet, new SealedSecretSetPayload(
                name.ToLowerInvariant(), DekFingerprint.Of(dek), Convert.ToBase64String(wrapped), Convert.ToBase64String(iv)));
        }
        finally
        {
            Array.Clear(dek);
        }
    }
}
