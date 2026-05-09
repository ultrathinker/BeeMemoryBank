namespace BeeMemoryBank.Core.Models;

public class Agent
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string KeyPrefix { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public byte[] EncryptedDek { get; set; } = [];
    public byte[] DekIV { get; set; } = [];

    /// <summary>V0 = SHA256(key + "bmb-encrypt"), V1 = HKDF-SHA256(key, salt, info)</summary>
    public int KdfVersion { get; set; }
    public byte[]? Salt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? LastAccessedAt { get; set; }
    public int RequestCount { get; set; }
    public string Status { get; set; } = "A";

    public int OwnerUserId { get; set; }
}
