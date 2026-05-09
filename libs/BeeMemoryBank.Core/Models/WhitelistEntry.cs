namespace BeeMemoryBank.Core.Models;

public class WhitelistEntry
{
    public Guid NodeId { get; set; }
    public string DisplayName { get; set; } = "";
    public byte[] Ed25519PublicKey { get; set; } = [];
    public string? ApiAddress { get; set; }
    public bool CanGenerateEmbeddings { get; set; }
    public string Status { get; set; } = "A";
    public bool AutoAcceptRestore { get; set; }
    public bool AutoAcceptDekRotation { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// True if this peer is authorized to issue cluster-state-modifying sync events:
    /// whitelist add/revoke, hard-delete, restore_network. Default false. (Wave 2:
    /// gemini #1 / #2 / #3 — privilege escalation prevention.)
    /// </summary>
    public bool IsSuperadmin { get; set; }
}
