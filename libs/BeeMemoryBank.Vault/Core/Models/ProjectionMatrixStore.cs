namespace BeeMemoryBank.Core.Models;

public class ProjectionMatrixStore
{
    public int Id { get; set; }
    public byte[] EncryptedMatrix { get; set; } = [];
    public byte[] IV { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}
