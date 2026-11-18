using System.Text.Json;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync;

/// <summary>
/// The two blind-node rules no event may break, whoever relays it and however long it has sat in a
/// log (plan 3.1-3.2): a blind node never authors an event, and no DEK rotation seals the new DEK for
/// a blind node — its key sits in a plain file, which would then open the master DEK.
///
/// <para>Decided from the event alone — no whitelist, no DEK — so the applier can refuse before its
/// idempotency shortcut (an event a pre-fix build stored must not be waved through as "already
/// applied"), and <see cref="StoredEventRepair"/> finds stored violations by the same rule.</para>
/// </summary>
public static class EventInvariants
{
    /// <returns>Why the event is refused, or null when it breaks no invariant.</returns>
    public static string? Violation(Guid originator, string eventType, string? payload)
    {
        if (BlindNodeId.IsBlind(originator) && eventType != EventTypes.NodeState)
            return $"Node {originator} is a blind node; blind nodes never author events.";
        if (eventType == EventTypes.NodeState)
            return NodeStateViolation(originator, payload);
        if (BlindEnvelopeRecipient(eventType, payload) is { } blind)
            return eventType == EventTypes.SuperadminDoorsSet
                ? $"The superadmin doors event seals the list key for blind node {blind}; blind nodes never receive it."
                : $"The rotation seals the master DEK for blind node {blind}; blind nodes never receive the DEK.";
        return null;
    }

    private static string? NodeStateViolation(Guid originator, string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return "node_state payload must not be empty.";
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return "node_state payload must be a JSON object.";
            if (!doc.RootElement.TryGetProperty("node_id", out var nodeIdEl)
                || nodeIdEl.ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(nodeIdEl.GetString(), "D", out var subject)
                || subject != originator)
            {
                return $"node_state subject must equal its author {originator}.";
            }
            if (BlindNodeId.IsBlind(originator)
                && doc.RootElement.TryGetProperty("dek_fp", out var dekFpEl)
                && dekFpEl.ValueKind != JsonValueKind.Null
                && !string.IsNullOrEmpty(dekFpEl.GetString()))
            {
                return $"Blind node {originator} must not declare dek_fp in node_state.";
            }
            return null;
        }
        catch (JsonException)
        {
            return "node_state payload is not valid JSON.";
        }
    }

    public static string? Violation(SyncEvent evt) => Violation(evt.NodeId, evt.EventType, evt.Payload);

    /// <summary>
    /// The blind node a DEK rotation event seals the master DEK for, or null. dek_envelopes.peers is
    /// keyed by node id (DekEnvelopesPayload); a payload that does not parse is left to the applier's
    /// own validation — this only looks for a blind key.
    /// </summary>
    public static string? BlindEnvelopeRecipient(string eventType, string? payload)
    {
        var envelopeField = eventType switch
        {
            EventTypes.DekRotationProposed or EventTypes.DekRotationCommit => "dek_envelopes",
            EventTypes.SuperadminDoorsSet => "list_envelopes",
            _ => null,
        };
        if (envelopeField is null || string.IsNullOrEmpty(payload)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(envelopeField, out var envelopes)
                || envelopes.ValueKind != JsonValueKind.Object
                || !envelopes.TryGetProperty("peers", out var peers)
                || peers.ValueKind != JsonValueKind.Object)
                return null;
            foreach (var peer in peers.EnumerateObject())
                if (BlindNodeId.IsBlind(peer.Name)) return peer.Name;
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
