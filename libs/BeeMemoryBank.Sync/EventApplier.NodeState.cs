using System.Text.Json;
using BeeMemoryBank.Core.Models;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

public partial class EventApplier
{
    private static readonly JsonSerializerOptions NodeStateJson = new(JsonSerializerDefaults.Web);

    /// <summary>Applies this node's own newly signed <c>node_state</c> event directly into <c>tbl_node_state</c>.</summary>
    public Task ApplyOwnNodeStateAsync(SyncEvent evt) =>
        evt.EventType == EventTypes.NodeState
            ? ApplyNodeStateAsync(evt)
            : throw new ArgumentException($"{evt.EventType} is not a node_state event.", nameof(evt));

    private async Task ApplyNodeStateAsync(SyncEvent evt)
    {
        var p = Deserialize<NodeStatePayload>(evt.Payload);

        if (!Guid.TryParseExact(p.NodeId, "D", out var subject) || subject != evt.NodeId)
            throw new UnauthorizedAccessException(
                $"node_state {evt.EventId}: node {evt.NodeId} may only report its own state, not '{p.NodeId}'.");

        if (BlindNodeId.IsBlind(subject) && !string.IsNullOrEmpty(p.DekFp))
            throw new InvalidDataException($"node_state {evt.EventId}: blind node {subject} must not report a DEK fingerprint.");

        var platform = Metadata.NodePlatform.Normalize(p.Platform) ?? "";
        var runtimeKind = NodeRuntimeKinds.Normalize(p.RuntimeKind);
        var flags = (p.AttentionFlags ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var flagsJson = JsonSerializer.Serialize(flags, NodeStateJson);
        string? extraJson = p.Extra is { Count: > 0 } ? JsonSerializer.Serialize(p.Extra, NodeStateJson) : null;
        var eventId = evt.EventId.ToString("D");

        using var conn = connFactory.CreateConnection();
        using var tx = conn.BeginTransaction();
        var stored = await conn.QueryFirstOrDefaultAsync<(long LamportTs, string? EventId)?>(
            "SELECT lamport_ts, event_id FROM tbl_node_state WHERE node_id = @Id COLLATE NOCASE",
            new { Id = Id(subject) }, tx);

        if (stored is { } row && (row.LamportTs > evt.LamportTs
                                  || (row.LamportTs == evt.LamportTs && string.CompareOrdinal(row.EventId, eventId) >= 0)))
        {
            tx.Commit();
            return;
        }

        await conn.ExecuteAsync(
            @"INSERT INTO tbl_node_state
                  (node_id, app_version, platform, runtime_kind, identity_key_fp, dek_fp, attention_flags_json, extra_json, lamport_ts, event_id, updated_at)
              VALUES
                  (@Id, @AppVersion, @Platform, @RuntimeKind, @IdentityKeyFp, @DekFp, @FlagsJson, @ExtraJson, @Ts, @EventId, @Now)
              ON CONFLICT(node_id) DO UPDATE SET
                  app_version = excluded.app_version,
                  platform = excluded.platform,
                  runtime_kind = excluded.runtime_kind,
                  identity_key_fp = excluded.identity_key_fp,
                  dek_fp = excluded.dek_fp,
                  attention_flags_json = excluded.attention_flags_json,
                  extra_json = excluded.extra_json,
                  lamport_ts = excluded.lamport_ts,
                  event_id = excluded.event_id,
                  updated_at = excluded.updated_at",
            new
            {
                Id = Id(subject),
                AppVersion = p.AppVersion ?? "",
                Platform = platform,
                RuntimeKind = runtimeKind,
                IdentityKeyFp = p.IdentityKeyFp ?? "",
                DekFp = string.IsNullOrWhiteSpace(p.DekFp) ? null : p.DekFp,
                FlagsJson = flagsJson,
                ExtraJson = extraJson,
                Ts = evt.LamportTs,
                EventId = eventId,
                Now = DateTime.UtcNow.ToString("O"),
            }, tx);
        tx.Commit();

        logger.LogDebug("Applied node_state for {NodeId} (runtime={Runtime}, ts={Ts})", subject, runtimeKind, evt.LamportTs);
    }
}
