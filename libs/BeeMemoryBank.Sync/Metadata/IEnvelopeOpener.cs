using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync.Metadata;

/// <summary>What a host does with a verified event (docs/design/step-a-spec.md 6.3).</summary>
public enum EnvelopeDisposition { Plain, Opened, Relay, Hold, Drop }

public sealed record EnvelopeOutcome(EnvelopeDisposition Disposition, SyncEvent? View = null, string? Reason = null)
{
    /// <summary>Apply the event as it is (a control event, or protocol-1..3 content on a host that applies it).</summary>
    public static EnvelopeOutcome AsIs(SyncEvent evt) => new(EnvelopeDisposition.Plain, evt);
    /// <summary>A sealed event opened: apply <paramref name="view"/>, store the original.</summary>
    public static EnvelopeOutcome OpenedAs(SyncEvent view) => new(EnvelopeDisposition.Opened, view);
    /// <summary>A blind host: store the original, apply nothing.</summary>
    public static EnvelopeOutcome RelayOnly() => new(EnvelopeDisposition.Relay);
    /// <summary>A full node that cannot open it now: store it held (<see cref="HoldReasons"/>).</summary>
    public static EnvelopeOutcome HoldFor(string reason) => new(EnvelopeDisposition.Hold, Reason: reason);
    /// <summary>Accept without storing (an activated blind host offered clear content).</summary>
    public static EnvelopeOutcome DropFor(string reason) => new(EnvelopeDisposition.Drop, Reason: reason);
}

public static class HoldReasons
{
    public const string Locked = "locked";
    public const string Key = "key";
    public const string Order = "order";
}

/// <summary>Keys and flags of an originator, for an apply that must not read tbl_whitelist (restore replay).</summary>
public sealed record OriginatorInfo(byte[] PublicKey, bool IsSuperadmin);

/// <param name="ReceivedFrom">The peer that delivered the event (pull source or pusher); stored as origin_peer.</param>
/// <param name="ReceivedSeq">The event's sequence number in that peer's log; stored as origin_seq.</param>
/// <param name="Originators">Restore replay only: resolves originators instead of tbl_whitelist.</param>
/// <param name="EventKeys">Restore replay only: the 32-byte event key (spec 3.5) of a MEK key id, or null.</param>
public sealed record ApplyContext(
    Guid? ReceivedFrom = null,
    long? ReceivedSeq = null,
    Func<Guid, OriginatorInfo?>? Originators = null,
    Func<string, byte[]?>? EventKeys = null);

/// <summary>
/// Called by EventApplier after the invariants, the originator lookup, the signature and the envelope shape passed.
/// A full node registers the Vault opener, every blind host the relay opener; there is no default.
/// Throws <see cref="InvalidDataException"/> when a sealed event does not open with a key this host holds (permanent).
/// </summary>
public interface IEnvelopeOpener
{
    ValueTask<EnvelopeOutcome> OpenAsync(SyncEvent verified, ApplyContext? context, CancellationToken ct = default);
}
