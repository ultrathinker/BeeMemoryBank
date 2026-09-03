using System.Data;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Interfaces;

public interface IKeySlotRepository
{
    Task<List<MasterKeyStore>> GetAllAsync();

    /// <summary>
    /// Creates a new key slot. <paramref name="transaction"/> follows the same optional-transaction
    /// contract documented on <see cref="IArticleRepository"/>: pass null (the default) and this
    /// opens/commits/disposes its own connection; pass a non-null transaction and it executes
    /// against that transaction's connection without committing or disposing anything, so a
    /// caller can atomically pair this with another write (e.g. <see cref="DeleteAsync"/> — see
    /// <c>KeyManagementService.ChangePasswordAsync</c>'s create-new/delete-old rotation).
    /// </summary>
    Task<int> CreateAsync(MasterKeyStore slot, IDbTransaction? transaction = null);

    /// <summary>Deletes a slot. See <see cref="CreateAsync"/> for the <paramref name="transaction"/> contract.</summary>
    Task DeleteAsync(int slotId, IDbTransaction? transaction = null);
    Task UpdateSlotKeyAsync(int slotId, byte[] encryptedDek, byte[] iv);
}
