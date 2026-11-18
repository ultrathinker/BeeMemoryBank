using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.FullIos.Services;

/// <summary>A key slot as the Debug slots card lists it.</summary>
/// <param name="Owner">The display name of the person the door belongs to (2.12: a recovery key is personal); null when none is recorded.</param>
public sealed record SlotView(int SlotId, string SlotType, DateTime CreatedAt, bool CanDelete, string? Owner = null);

/// <summary>One recent sync event as the sync page lists it (summary key + time are the page's).</summary>
public sealed record EventView(string EventType, DateTime CreatedAt);

/// <summary>
/// The settings / security / sync operations of parity part 3, as calls of the product's own services
/// (UserService, KeyManagementService, the event log and the whitelist), the same ones the Android
/// SecurityPage and StatusPage use. Platform-free, so the tests run them on a real vault. The wipe's
/// file list is computed here; the actual delete of the app's own container is the platform's job.
/// </summary>
public sealed class SecurityOps(IServiceProvider services)
{
    /// <summary>
    /// Changes the superadmin's master password through UserService (the key is re-wrapped under the
    /// new password, the hint kept under the same policy, and a sealed node's pre-unlock mirror is
    /// rewritten by the core). Throws UnauthorizedAccessException for a wrong current password.
    /// </summary>
    public async Task ChangeMasterAsync(string oldPassword, string newPassword, string? hint)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var users = await sp.GetRequiredService<IUserRepository>().ListActiveAsync();
        var current = users.FirstOrDefault(u => u.Role == UserRoles.Superadmin)
            ?? throw new InvalidOperationException("No superadmin user found");
        await sp.GetRequiredService<UserService>().ChangePasswordAsync(current.Id, oldPassword, newPassword, passwordHint: hint);
    }

    /// <summary>The key slots; a slot is deletable when no account signs in with it and it is not the last one.</summary>
    public async Task<List<SlotView>> SlotsAsync()
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var slots = await sp.GetRequiredService<IKeySlotRepository>().GetAllAsync();
        var users = await sp.GetRequiredService<IUserRepository>().ListActiveAsync();
        var loginSlots = users.Where(u => u.KeySlotId.HasValue).Select(u => u.KeySlotId!.Value).ToHashSet();
        var names = users.ToDictionary(u => u.Id, u => string.IsNullOrWhiteSpace(u.DisplayName) ? u.Username : u.DisplayName);
        return slots.Select(s => new SlotView(s.SlotId, s.SlotType, s.CreatedAt,
            CanDelete: !loginSlots.Contains(s.SlotId) && slots.Count > 1,
            Owner: s.OwnerUserId is { } owner && names.TryGetValue(owner, out var name) ? name : null)).ToList();
    }

    /// <summary>
    /// Adds the Debug testing password as an extra slot. The core's AddPasswordSlotAsync only accepts
    /// "user" / "recovery" (a "dev" type would need a libs change), so the testing password is an extra
    /// "user" slot that no account points at - deletable, while the login slot and the last one are not.
    /// </summary>
    public async Task AddDevSlotAsync(string password)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<KeyManagementService>().AddPasswordSlotAsync("user", password);
    }

    public async Task RemoveSlotAsync(int slotId)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<KeyManagementService>().RemoveSlotAsync(slotId);
    }

    /// <summary>The last sync events, newest first (the sync page shows five).</summary>
    public async Task<List<EventView>> RecentEventsAsync(int count)
    {
        using var scope = services.CreateScope();
        var events = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetRecentAsync(count);
        return events.Select(e => new EventView(e.EventType, e.CreatedAt)).ToList();
    }

    /// <summary>Removes a device: log the revoke first, then stamp the whitelist row with that version (a stale add must lose).</summary>
    public async Task RevokePeerAsync(Guid nodeId)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var version = await sp.GetRequiredService<IEventLogger>().LogWhitelistRevokeAsync(nodeId);
        await sp.GetRequiredService<IWhitelistRepository>().RevokeAsync(nodeId, version);
    }

    /// <summary>
    /// Every file the wipe deletes: the whole of the app's own data directory (the database and its
    /// -wal/-shm/-journal sidecars, the sealed boot/journal/mirror files, and the media folder). Nothing
    /// outside this directory is ever touched.
    /// </summary>
    public static List<string> WipeFiles(string dataDirectory)
    {
        if (!Directory.Exists(dataDirectory)) return [];
        return Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The files of the danger zone's wipe (<c>AppFlow.WipeAsync</c> stops the sync and clears the Face ID secret first): the session locks and a
    /// sealed database closes, the app's own data directory goes (the database and its sidecars, the sealed boot/journal/mirror files, the media
    /// folder - the set of <see cref="WipeFiles"/>), and a fresh install's empty database is prepared for the Welcome gate. False when the folder
    /// could not be deleted: nothing is prepared over data that is still there, and the caller says the failure.
    /// </summary>
    public async Task<bool> WipeDataAsync(FullNodePaths paths)
    {
        if (services.GetService<BeeMemoryBank.Vault.Sealed.PhoneVaultLifecycle>() is { } lifecycle)
            await lifecycle.OnBackgroundAsync();
        new FullVault(services).Lock();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Delete the app's own folder (database + sidecars + sealed files + media). A lingering
        // SQLite handle can recreate a -wal for an instant, so retry the recursive delete briefly.
        for (var attempt = 0; attempt < 5 && Directory.Exists(paths.DataDirectory); attempt++)
        {
            try { Directory.Delete(paths.DataDirectory, true); }
            catch (IOException) { await Task.Delay(100); }
            catch (UnauthorizedAccessException) { await Task.Delay(100); }
        }
        if (Directory.Exists(paths.DataDirectory)) return false;
        Directory.CreateDirectory(paths.Media);
        // The boot store, the doors and the encrypted database are gone: the gate in this process is a plain gate again, as after the node's reset
        // (spec 13.1). Left sealed and closed, it refused the fresh schema below - "Wipe failed" after a wipe that had worked, and an unlock page
        // with no door until the app was force-quit.
        services.GetService<BeeMemoryBank.Vault.Sealed.VaultGate>()?.SwitchToPlain();
        // A fresh install prepares its (empty) database before the first screen; do the same so the
        // Welcome gate and a new vault see a claimed-free schema, not a deleted file.
        await FullNodeServices.PrepareAsync(services);
        return true;
    }
}
