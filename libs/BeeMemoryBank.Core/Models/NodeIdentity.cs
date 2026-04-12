namespace BeeMemoryBank.Core.Models;

public class NodeIdentity
{
    public Guid NodeId { get; set; }
    public string DisplayName { get; set; } = "";
    public byte[] Ed25519PublicKey { get; set; } = [];
    public byte[] Ed25519PrivateKey { get; set; } = [];
    public bool CanGenerateEmbeddings { get; set; }
    public DateTime CreatedAt { get; set; }
}
