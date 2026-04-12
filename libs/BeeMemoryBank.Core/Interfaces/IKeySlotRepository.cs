using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IKeySlotRepository
{
    Task<List<MasterKeyStore>> GetAllAsync();
    Task<int> CreateAsync(MasterKeyStore slot);
    Task DeleteAsync(int slotId);
}
