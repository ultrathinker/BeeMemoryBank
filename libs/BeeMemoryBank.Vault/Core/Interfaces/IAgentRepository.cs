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

    /// <summary>
    /// Strips the wrapped master DEK (encrypted_dek/dek_iv/salt, and resets kdf_version) from
    /// every active agent owned by <paramref name="ownerUserId"/>. The repository-level primitive of
    /// "this owner may no longer hold vault keys". A demotion or a deletion (UserService) does not call
    /// it any more: it revokes the person's agents (status and wrapped key together) inside the same
    /// transaction as the change itself. The key itself keeps authenticating -- only its
    /// ability to unwrap the master DEK is removed. Returns the number of rows actually
    /// changed (rows that already had no wrapped DEK are left alone and not counted).
    /// </summary>
    Task<int> ClearWrappedDekForOwnerAsync(int ownerUserId);
}
