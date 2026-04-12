using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IAgentRepository
{
    Task<Agent?> GetByKeyHashAsync(string keyHash);
    Task<List<Agent>> ListActiveAsync();
    Task<int> CreateAsync(Agent agent);
    Task DeleteAsync(int id);
    Task UpdateAccessAsync(int id);
}
