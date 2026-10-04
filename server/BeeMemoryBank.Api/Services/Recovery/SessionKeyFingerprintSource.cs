using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>
/// <see cref="ICurrentKeyFingerprintSource"/> of a node that holds the master DEK: the fingerprint of the unlocked session's key.
/// Full-node code (it reads the session DEK); a blind node does not contain it.
/// </summary>
public sealed class SessionKeyFingerprintSource(SessionService session) : ICurrentKeyFingerprintSource
{
    public string? Current()
    {
        if (!session.IsUnlocked) return null;
        var dek = session.GetMasterDek();
        try { return DekFingerprint.Of(dek); }
        finally { Array.Clear(dek); }
    }
}
