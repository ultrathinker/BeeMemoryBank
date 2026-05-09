namespace BeeMemoryBank.Core.Models;

public class NodeIdentity
{
    public Guid NodeId { get; set; }
    public string DisplayName { get; set; } = "";
    public byte[] Ed25519PublicKey { get; set; } = [];

    /// <summary>
    /// When <see cref="Ed25519PrivateKeyV"/> == 0: raw 32-byte Ed25519 seed (plaintext, legacy).
    /// When <see cref="Ed25519PrivateKeyV"/> == 1: master-DEK-wrapped seed (49-byte versioned blob);
    /// callers must decrypt via <c>NodeIdentityRepository.GetDecryptedPrivateKey</c> before signing.
    /// </summary>
    public byte[] Ed25519PrivateKey { get; set; } = [];

    /// <summary>IV for v=1 wrapped private key. NULL for v=0 (legacy).</summary>
    public byte[]? Ed25519PrivateKeyIV { get; set; }

    /// <summary>0 = legacy plaintext, 1 = master-DEK-wrapped (AAD = "bmb-node-pk" || node_id bytes).</summary>
    public int Ed25519PrivateKeyV { get; set; }

    public bool CanGenerateEmbeddings { get; set; }
    public bool InitialSyncCompleted { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}
