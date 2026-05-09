using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IAgentRepository
{
    Task<Agent?> GetByKeyHashAsync(string keyHash);
    Task<Agent?> GetByIdAsync(int id);
    Task<List<Agent>> ListActiveAsync();
    Task<int> CountByOwnerAsync(int userId);
    Task<int> CreateAsync(Agent agent);
    Task DeleteAsync(int id);
    Task UpdateAccessAsync(int id);
}
