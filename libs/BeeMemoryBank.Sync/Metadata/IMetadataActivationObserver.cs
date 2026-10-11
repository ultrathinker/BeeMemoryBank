namespace BeeMemoryBank.Sync.Metadata;

public sealed record MetadataActivation(Guid ActivationId, string KeyId, int Generation, DateTime ActivatedAt, Guid AuthorNodeId);

/// <summary>Host effects of the activation (spec 5.5). Called once, after the activation row is written; must not throw for a retry.</summary>
public interface IMetadataActivationObserver
{
    Task OnActivatedAsync(MetadataActivation activation, CancellationToken ct = default);
}
