using System.Globalization;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

// Appliers of the two control events of the metadata key (docs/design/step-a-spec.md 4.3, 5.4; tables in migration 037).
// Like the recovery appliers they need no DEK and run on every host: a blind host stores the wrapped key and the activation
// exactly as a full node does, and can open neither. What they check is shape; who may send them is the superadmin gate.
public partial class EventApplier
{
    private static readonly Regex MetadataKeyIdShape = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    private const int MetadataKeyIvBytes = 12;
    private const int MetadataKeyWrappedBytes = 32 + 16;

    /// <summary>
    /// Writes one of this node's OWN metadata events into its tables right after it logged it (the activation and the new
    /// generation of a DEK rotation). A node never pulls its own events back, and the rows must follow the same rules
    /// either way, so the author comes through here, as <see cref="ApplyOwnRecoveryEventAsync"/> does.
    /// </summary>
    public Task ApplyOwnMetadataEventAsync(SyncEvent evt) => evt.EventType switch
    {
        EventTypes.MetadataKeySet => ApplyMetadataKeySetAsync(evt),
        EventTypes.MetadataActivation => ApplyMetadataActivationAsync(evt),
        _ => throw new ArgumentException($"{evt.EventType} is not a metadata event.", nameof(evt)),
    };

    private async Task ApplyMetadataKeySetAsync(SyncEvent evt)
    {
        var p = Deserialize<MetadataKeySetPayload>(evt.Payload);
        if (!MetadataKeyIdShape.IsMatch(p.KeyId ?? ""))
            throw new InvalidDataException($"metadata_key_set {evt.EventId}: bad key id.");
        if (p.Generation < 1)
            throw new InvalidDataException($"metadata_key_set {evt.EventId}: bad generation.");
        if (!HexSha256.IsMatch(p.DekFingerprint ?? ""))
            throw new InvalidDataException($"metadata_key_set {evt.EventId}: bad DEK fingerprint.");
        var wrapped = FromBase64OrNull(p.Wrapped);
        var iv = FromBase64OrNull(p.Iv);
        if (wrapped is not { Length: MetadataKeyWrappedBytes } || iv is not { Length: MetadataKeyIvBytes })
            throw new InvalidDataException($"metadata_key_set {evt.EventId}: malformed key material.");
        if (!DateTime.TryParse(p.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
            throw new InvalidDataException($"metadata_key_set {evt.EventId}: bad creation time.");

        // Rows are immutable: a key id is written once, and old generations stay (they open old events and baselines).
        using var conn = connFactory.CreateConnection();
        await conn.ExecuteAsync(
            @"INSERT OR IGNORE INTO tbl_metadata_key
                (key_id, generation, dek_fingerprint, epoch_hint, wrapped, iv, author_node_id, created_at, lamport_ts, event_id)
              VALUES (@KeyId, @Generation, @Fingerprint, @Epoch, @Wrapped, @Iv, @Author, @CreatedAt, @Lamport, @EventId)",
            new
            {
                p.KeyId, p.Generation, Fingerprint = p.DekFingerprint, Epoch = p.EpochHint, Wrapped = wrapped, Iv = iv,
                Author = Id(evt.NodeId), p.CreatedAt, Lamport = evt.LamportTs, EventId = Id(evt.EventId)
            });

        // Sealed events waiting for this key may open now (spec 6.4, hold reason "key").
        heldEventDrain.RequestDrain();
    }

    private async Task ApplyMetadataActivationAsync(SyncEvent evt)
    {
        var p = Deserialize<MetadataActivationPayload>(evt.Payload);
        if (!Guid.TryParseExact(p.ActivationId, "D", out var activationId))
            throw new InvalidDataException($"metadata_activation {evt.EventId}: bad activation id.");
        if (!MetadataKeyIdShape.IsMatch(p.KeyId ?? ""))
            throw new InvalidDataException($"metadata_activation {evt.EventId}: bad key id.");
        if (!DateTime.TryParse(p.ActivatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var activatedAt))
            throw new InvalidDataException($"metadata_activation {evt.EventId}: bad activation time.");

        using var conn = connFactory.CreateConnection();
        // The key travels just before the activation; until it is here the activation waits (deferred, retried).
        var generation = await conn.ExecuteScalarAsync<long?>(
            "SELECT generation FROM tbl_metadata_key WHERE key_id = @KeyId", new { p.KeyId });
        if (generation is null)
            throw new MetadataKeyMissingException(p.KeyId);
        if (generation != p.Generation)
            throw new InvalidDataException($"metadata_activation {evt.EventId}: the key's generation is {generation}, not {p.Generation}.");

        // Activation happens once and is one way: the first row stays, a second one (a race of two superadmins) changes
        // nothing. The same activation again (a crash before its event was logged) runs the observers again; they are
        // written to tolerate that.
        var inserted = await conn.ExecuteAsync(
            @"INSERT INTO tbl_metadata_activation
                (activation_id, key_id, generation, activated_at, author_node_id, event_id, lamport_ts, applied_at)
              SELECT @ActivationId, @KeyId, @Generation, @ActivatedAt, @Author, @EventId, @Lamport, @AppliedAt
              WHERE NOT EXISTS (SELECT 1 FROM tbl_metadata_activation)",
            new
            {
                ActivationId = Id(activationId), p.KeyId, p.Generation, p.ActivatedAt, Author = Id(evt.NodeId),
                EventId = Id(evt.EventId), Lamport = evt.LamportTs, AppliedAt = DateTime.UtcNow.ToString("O")
            });
        protocolPolicy.MarkActivated();
        if (inserted == 0)
        {
            var existing = await conn.ExecuteScalarAsync<string?>("SELECT activation_id FROM tbl_metadata_activation LIMIT 1");
            if (!string.Equals(existing, Id(activationId), StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("metadata_activation {EventId} ignored: this node is already activated by {Existing}",
                    evt.EventId, existing);
                return;
            }
        }

        var activation = new MetadataActivation(activationId, p.KeyId, p.Generation,
            activatedAt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(activatedAt, DateTimeKind.Utc) : activatedAt.ToUniversalTime(),
            evt.NodeId);
        foreach (var observer in activationObservers)
        {
            try
            {
                await observer.OnActivatedAsync(activation);
            }
            catch (Exception ex)
            {
                // The activation itself is applied; a host effect that failed (a reseed flag, an early baseline) is
                // retried by its own owner, never by refusing the event.
                logger.LogWarning(ex, "Activation observer {Observer} failed for activation {ActivationId}",
                    observer.GetType().Name, activationId);
            }
        }
        logger.LogInformation("Metadata activation {ActivationId} applied (key generation {Generation})", activationId, p.Generation);
    }
}
