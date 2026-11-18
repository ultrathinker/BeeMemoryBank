using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.FullIos.Services;

/// <summary>
/// Whose the personal things a phone makes are (release 2.12: a recovery key and the quick-unlock door belong to a person, not to the
/// node). A phone has no sign-in: the person is its administrator. One source for both phone apps (the Android app links this file).
/// </summary>
public static class PhoneOwner
{
    /// <summary>The phone's only active superadmin, or null when it has none or several (then nobody is guessed).</summary>
    public static async Task<int?> SoleSuperadminAsync(IUserRepository users)
    {
        var superadmins = (await users.ListActiveAsync()).Where(u => u.Role == UserRoles.Superadmin).Take(2).ToList();
        return superadmins.Count == 1 ? superadmins[0].Id : null;
    }

    /// <summary>
    /// The person a recovery key issued now belongs to: the phone's only active superadmin; with several, the one whose own door the master
    /// password just typed opens (one Argon2id per candidate, only on such a phone); otherwise null - the vault then refuses the key with its own
    /// sentence rather than give it to somebody it guessed.
    /// </summary>
    public static async Task<int?> ForPasswordAsync(IServiceProvider services, string? password)
    {
        var users = services.GetRequiredService<IUserRepository>();
        if (await SoleSuperadminAsync(users) is { } sole) return sole;
        if (string.IsNullOrEmpty(password)) return null;

        var slots = await services.GetRequiredService<IKeySlotRepository>().GetAllAsync();
        foreach (var user in (await users.ListActiveAsync()).Where(u => u.Role == UserRoles.Superadmin && u.KeySlotId is not null).OrderBy(u => u.Id))
        {
            var slot = slots.FirstOrDefault(s => s.SlotId == user.KeySlotId && s.SlotType == KeySlotTypes.User);
            if (slot?.Salt is null || slot.ArgonMemory is null || slot.ArgonIterations is null || slot.ArgonParallelism is null) continue;
            var kek = KeyDerivation.DeriveKek(password, slot.Salt, slot.ArgonMemory.Value, slot.ArgonIterations.Value, slot.ArgonParallelism.Value);
            try
            {
                CryptographicOperations.ZeroMemory(Doors.OpenSlot(slot, kek));
                return user.Id;
            }
            catch (CryptographicException)
            {
                // Another person's door: the next.
            }
            finally
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }
        return null;
    }

    /// <summary>
    /// A recovery key that belongs to <paramref name="ownerUserId"/>, labelled <paramref name="label"/>; returned to be shown once and never kept.
    /// With no owner the vault takes the node's only active superadmin and refuses when there are several (it never guesses).
    /// </summary>
    public static Task<string> IssueRecoveryKeyAsync(KeyManagementService keys, int? ownerUserId, string? label) =>
        keys.AddRecoveryKeyAsync(ownerUserId, label);
}
