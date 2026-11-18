using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync.DekRotation;

/// <summary>
/// The recovery work a peer does right after it applied a rotation (2.12): the rotation's own retired link is published (the reconciler), and
/// the device box is published again from the door it should copy, sealed to the new key (plan WS-A4). Fire and forget: both are retried at
/// the next unlock or sign-in.
/// </summary>
public static class PostRotationRecovery
{
    public static void Start(IServiceScopeFactory scopes, SessionService session, ILogger logger) => _ = Task.Run(async () =>
    {
        try
        {
            using var scope = scopes.CreateScope();
            if (scope.ServiceProvider.GetService<IRecoveryReconciler>() is { } reconciler) await reconciler.ReconcileAsync();
            if (session.IsUnlocked && scope.ServiceProvider.GetService<IRecoveryBoxPublisher>() is { } boxes)
            {
                var dek = session.GetMasterDek();
                try { await boxes.RepublishForDoorsAsync(dek); }
                finally { Array.Clear(dek); }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The recovery work after the rotation failed; it runs again at the next unlock");
        }
    });
}
