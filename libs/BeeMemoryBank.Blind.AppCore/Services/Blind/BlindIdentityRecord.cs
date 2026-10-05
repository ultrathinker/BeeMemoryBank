namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>
/// Immutable snapshot of a node identity record read from the blind node's database.
/// </summary>
public sealed record BlindIdentityRecord(
    Guid NodeId,
    byte[] PublicKey,
    string DisplayName,
    int PrivateKeyV = 2,
    byte[]? PrivateKey = null,
    byte[]? PrivateKeyIV = null,
    bool CanGenerateEmbeddings = false);
