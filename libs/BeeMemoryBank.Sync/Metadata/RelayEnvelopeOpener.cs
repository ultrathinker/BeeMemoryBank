using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync.Metadata;

/// <summary>
/// The opener of every blind host (docs/design/step-a-spec.md 9.4): the Docker blind node, the Api started with
/// BMB_ROLE=blind and the AppCore blind copies. It never opens anything and holds no key. A sealed event is stored and
/// relayed as it is; a control event applies as today; protocol-1..3 content is materialised as 2.5.1 did before this
/// host applied the activation, and dropped after it (an activated blind host receives content only sealed or inside a
/// baseline, spec 5.7).
/// </summary>
public sealed class RelayEnvelopeOpener(ISyncProtocolPolicy policy) : IEnvelopeOpener
{
    public const string DropReason = "clear content offered to an activated blind host";

    public ValueTask<EnvelopeOutcome> OpenAsync(SyncEvent verified, ApplyContext? context, CancellationToken ct = default)
    {
        if (verified.EventType == EventClasses.Sealed)
            return ValueTask.FromResult(EnvelopeOutcome.RelayOnly());
        if (EventClasses.IsClearContent(verified.ProtocolVersion, verified.EventType) && policy.SealsMetadata)
            return ValueTask.FromResult(EnvelopeOutcome.DropFor(DropReason));
        return ValueTask.FromResult(EnvelopeOutcome.AsIs(verified));
    }
}
