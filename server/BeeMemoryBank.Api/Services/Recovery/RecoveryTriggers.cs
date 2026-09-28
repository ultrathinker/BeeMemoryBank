using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>
/// The moments the master password is in memory on this node, and what recovery work each one starts
/// (plan 6.3, 6.5). Every method returns at once; the work runs in the background and its task is
/// returned only so a caller that wants to (a test) can wait for it. Nothing here is awaited by a
/// request handler.
/// </summary>
public class RecoveryTriggers(
    IServiceScopeFactory scopes,
    StrongBoxService strongBoxes,
    RecoveryCleanupService cleanup,
    SessionService session,
    ILogger<RecoveryTriggers> logger)
{
    /// <summary>A superadmin signed in and the vault is open: strong box for the current key, then cleanup.</summary>
    public Task OnSuperadminLogin(User user, string password)
    {
        if (user.Role != UserRoles.Superadmin || user.KeySlotId is not { } slotId || !session.IsUnlocked)
            return Task.CompletedTask;

        return Task.Run(async () =>
        {
            try
            {
                await strongBoxes.EnsureForCurrentKeyAsync(password);
                await cleanup.RunAsync(slotId, password);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Recovery work at login failed; it runs again at the next login");
            }
        });
    }

    /// <summary>This node's own master password was just changed to <paramref name="newPassword"/>.</summary>
    public Task OnPasswordChanged(string newPassword) => strongBoxes.RequestBuild(newPassword);

    /// <summary>
    /// A rotation started here was accepted (after the rewrap committed; the session holds the new
    /// DEK). The initiator's slot was rewrapped inside the rotation, so it is published like any other
    /// slot change; links and seals are brought up to date; a strong box for the new key is built.
    /// </summary>
    public Task OnRotationAccepted(int initiatorSlotId, string masterPassword) => Task.Run(async () =>
    {
        try
        {
            using (var scope = scopes.CreateScope())
            {
                var slot = (await scope.ServiceProvider.GetRequiredService<IKeySlotRepository>().GetAllAsync())
                    .FirstOrDefault(s => s.SlotId == initiatorSlotId);
                if (slot != null)
                {
                    var dek = session.GetMasterDek();
                    try
                    {
                        await scope.ServiceProvider.GetRequiredService<IRecoveryBoxPublisher>().PublishDeviceBoxAsync(slot, dek);
                    }
                    finally
                    {
                        Array.Clear(dek);
                    }
                }
                await scope.ServiceProvider.GetRequiredService<IRecoveryReconciler>().ReconcileAsync();
            }
            await strongBoxes.RequestBuild(masterPassword);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Recovery work after the rotation failed; reconciliation retries at the next unlock");
        }
    });
}
