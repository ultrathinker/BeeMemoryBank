using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync.Blind;

/// <summary>
/// <see cref="IRestoreInitiator"/> of a blind node (plan 5.3). A network restore needs the DEK to
/// check what it downloads, which a blind node never has — so instead of restoring it records
/// "reseed needed", and the first superadmin full node that syncs with it reseeds it.
///
/// <para>Durable: EventApplier runs this inline, before the restore_network event is recorded, and
/// the flag is set before the state row is closed — so a crash anywhere in between leaves either
/// the event to be delivered again or a Pending row that <see cref="RetryPendingRestoresAsync"/>
/// finishes at the next start.</para>
/// </summary>
public sealed class BlindRestoreInitiator(
    BlindState state,
    IServiceScopeFactory scopeFactory,
    ILogger<BlindRestoreInitiator> logger) : IRestoreInitiator
{
    /// <summary>
    /// No admin sits at a blind node to approve a restore, and flagging is harmless — it only asks
    /// for a reseed by a superadmin, which is authorized on its own. So every restore_network flags,
    /// whatever the auto-accept flag on the originator's row says.
    /// </summary>
    public bool RequiresApproval => false;

    public async Task AcceptRestoreAsync(string eventId, RestoreNetworkEventPayload payload, SyncEvent restoreEvent)
    {
        await FlagAsync(eventId);
        logger.LogWarning(
            "restore_network {EventId} from {NodeId}: a blind node cannot restore; flagged for reseed by a superadmin node",
            eventId, restoreEvent.NodeId);
    }

    /// <summary>Finishes restore_network events whose flag a crash may have lost (called at startup).</summary>
    public async Task RetryPendingRestoresAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IRestoreEventStateRepository>()
            .GetByStateAsync(RestoreEventState.Pending);
        foreach (var row in rows)
        {
            await FlagAsync(row.EventId);
            logger.LogWarning("restore_network {EventId} was left pending; flagged for reseed now", row.EventId);
        }
    }

    private async Task FlagAsync(string eventId)
    {
        await state.SetReseedNeededAsync($"restore_network {eventId}");
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IRestoreEventStateRepository>()
            .UpdateStateAsync(eventId, RestoreEventState.Applied);
    }
}
