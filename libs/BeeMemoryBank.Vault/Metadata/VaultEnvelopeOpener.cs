using System.Security.Cryptography;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync.Metadata;

namespace BeeMemoryBank.Vault.Metadata;

/// <summary>
/// The opener of a full node (docs/design/step-a-spec.md 3.8, 6.4). A control event and protocol-1..3 content apply as they
/// are. A sealed event opens with the event key of the MEK it names: held as <c>locked</c> while the vault is, held as
/// <c>key</c> while this node cannot open that MEK (it has not got the row yet, or the DEK the row is wrapped under), and
/// refused as <see cref="InvalidDataException"/> when it does not open with a key this node holds. A restore replay brings
/// its own event keys (<see cref="ApplyContext.EventKeys"/>) and never reads the ring.
/// </summary>
public sealed class VaultEnvelopeOpener(SessionService session, IMetadataKeyRing ring) : IEnvelopeOpener
{
    public ValueTask<EnvelopeOutcome> OpenAsync(SyncEvent verified, ApplyContext? context, CancellationToken ct = default)
    {
        if (verified.EventType != EventClasses.Sealed)
            return ValueTask.FromResult(EnvelopeOutcome.AsIs(verified));

        var envelope = SealedEnvelope.Parse(verified.Payload);
        if (context?.EventKeys is { } eventKeys)
        {
            if (eventKeys(envelope.KeyId) is not { } replayKey) return ValueTask.FromResult(EnvelopeOutcome.HoldFor(HoldReasons.Key));
            return ValueTask.FromResult(EnvelopeOutcome.OpenedAs(MetadataEnvelope.Open(verified, envelope, replayKey)));
        }

        if (!session.IsUnlocked) return ValueTask.FromResult(EnvelopeOutcome.HoldFor(HoldReasons.Locked));
        byte[] eventKey;
        try
        {
            if (!ring.CanOpen(envelope.KeyId))
                return ValueTask.FromResult(EnvelopeOutcome.HoldFor(session.IsUnlocked ? HoldReasons.Key : HoldReasons.Locked));
            eventKey = ring.DeriveKey(envelope.KeyId, MetadataKeyCrypto.EventKeyInfo, ReadOnlySpan<byte>.Empty);
        }
        catch (MetadataKeyUnavailableException)
        {
            // Locked between the two calls.
            return ValueTask.FromResult(EnvelopeOutcome.HoldFor(session.IsUnlocked ? HoldReasons.Key : HoldReasons.Locked));
        }

        try
        {
            return ValueTask.FromResult(EnvelopeOutcome.OpenedAs(MetadataEnvelope.Open(verified, envelope, eventKey)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(eventKey);
        }
    }
}
