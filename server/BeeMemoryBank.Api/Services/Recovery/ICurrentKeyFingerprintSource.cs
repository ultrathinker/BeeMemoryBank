namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>
/// Where <see cref="RecoveryStatusService"/> gets "the current key" fingerprint from when this node holds a key of its own.
/// A full node answers with the fingerprint of the session's master DEK (<c>SessionKeyFingerprintSource</c>, vault side);
/// a blind node holds no DEK and registers none, so the status falls back to the key the newest anchor vouches for.
/// </summary>
public interface ICurrentKeyFingerprintSource
{
    /// <summary>The fingerprint of the key this node holds right now, or null when it holds none (locked, or no key at all).</summary>
    string? Current();
}
