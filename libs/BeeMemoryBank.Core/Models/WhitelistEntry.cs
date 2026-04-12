namespace BeeMemoryBank.Core.Models;

public class WhitelistEntry
{
    public Guid NodeId { get; set; }
    public string DisplayName { get; set; } = "";
    public byte[] Ed25519PublicKey { get; set; } = [];
    public string? ApiAddress { get; set; }
    public bool CanGenerateEmbeddings { get; set; }
    public string Status { get; set; } = "A";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}
