using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Metadata;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// The Docker blind node's effect of the activation (docs/design/step-a-spec.md 5.5): it asks for a reseed, so every
/// superadmin full node that syncs with it sends it a format-2 baseline (the reseed flag of my-standing); its format-1
/// content is purged at that first adoption (9.3), and the format-1 package a phone may have cached is retired now (it is a readable copy on the
/// disk too). Never throws: the applier calls it after the activation row is written.
/// </summary>
public sealed class BlindActivationObserver(BlindState state, BlindReplicaPackageCache replicaPackages, ILogger<BlindActivationObserver> logger) : IMetadataActivationObserver
{
    public const string ReseedReason = "metadata activation";

    public async Task OnActivatedAsync(MetadataActivation activation, CancellationToken ct = default)
    {
        try
        {
            await state.SetReseedNeededAsync(ReseedReason);
            logger.LogInformation("Metadata activation {ActivationId} applied; this blind node asks for a format-2 reseed", activation.ActivationId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Metadata activation {ActivationId}: could not record the reseed request", activation.ActivationId);
        }

        try
        {
            await replicaPackages.RetireFormat1Async(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Metadata activation {ActivationId}: could not retire the cached format-1 replica package", activation.ActivationId);
        }
    }
}
