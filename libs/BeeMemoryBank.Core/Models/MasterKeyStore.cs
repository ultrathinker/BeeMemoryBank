namespace BeeMemoryBank.Core.Models;

public class MasterKeyStore
{
    public int SlotId { get; set; }
    public string SlotType { get; set; } = "password";
    public byte[] EncryptedMasterDek { get; set; } = [];
    public byte[] IV { get; set; } = [];
    public byte[]? Salt { get; set; }
    public int? ArgonMemory { get; set; }
    public int? ArgonIterations { get; set; }
    public int? ArgonParallelism { get; set; }
    public DateTime CreatedAt { get; set; }
}
