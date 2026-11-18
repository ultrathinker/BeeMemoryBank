using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Sync;

/// <summary>Contributes the unlocked vault's master DEK fingerprint to <c>node_state</c> (full nodes only).</summary>
public sealed class DekFingerprintNodeStateContributor(SessionService session) : INodeStateContributor
{
    public int Order => 10;

    public Task ContributeAsync(NodeStateDraft draft, CancellationToken ct = default)
    {
        if (!session.IsUnlocked) return Task.CompletedTask;
        var dek = session.GetMasterDek();
        try
        {
            draft.DekFp = DekFingerprint.Of(dek);
        }
        finally
        {
            Array.Clear(dek);
        }
        return Task.CompletedTask;
    }
}
