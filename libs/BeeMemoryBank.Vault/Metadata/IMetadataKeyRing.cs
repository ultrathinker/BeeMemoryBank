namespace BeeMemoryBank.Vault.Metadata;

public sealed record MetadataKeyInfo(string KeyId, int Generation);

/// <summary>A row of tbl_metadata_key as stored (spec 4.1).</summary>
public sealed record MetadataKeyRow(string KeyId, int Generation, string DekFingerprint, byte[] Wrapped, byte[] Iv);

public sealed class MetadataKeyUnavailableException(string keyId)
    : InvalidOperationException($"Metadata key {keyId} cannot be opened on this node now.")
{
    public string KeyId { get; } = keyId;
}

/// <summary>Unwrapped MEKs of an unlocked full node, in memory only (spec 4.5). Cleared when the vault locks.</summary>
public interface IMetadataKeyRing
{
    /// <summary>The key that seals now (spec 4.8); null when locked or none can be opened.</summary>
    MetadataKeyInfo? Current { get; }

    bool CanOpen(string keyId);

    /// <summary>
    /// HKDF-SHA256 subkey of the MEK <paramref name="keyId"/> with <paramref name="info"/> and <paramref name="salt"/>, 32 bytes.
    /// The caller clears it. Throws <see cref="MetadataKeyUnavailableException"/>.
    /// </summary>
    byte[] DeriveKey(string keyId, string info, ReadOnlySpan<byte> salt);
}
