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

    // Defaults true: the ONNX model ships with every build/Docker image, so semantic search
    // should work out of the box. Every call site that constructs a NodeIdentity without
    // explicitly setting this (e.g. JoinCommand.cs, InitEndpoints.cs's web join handler) picks
    // up this default -- historically every one of them ended up false because bool's own
    // implicit default is false and nothing here overrode it, silently disabling semantic
    // search on every node ever created with no way to turn it back on (see
    // InitializationService.InitializeAsync for the explicit-init path's own default).
    public bool CanGenerateEmbeddings { get; set; } = true;
    public bool InitialSyncCompleted { get; set; } = true;
    public DateTime CreatedAt { get; set; }
}
