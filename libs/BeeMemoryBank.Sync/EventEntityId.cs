using System.Text.Json;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync;

/// <summary>
/// The single rule for what an event's <c>EntityId</c> is.
///
/// <para><c>EntityId</c> travels on the wire but is NOT covered by
/// <see cref="EventSignature.BuildPayload"/> — the signature spans EventId, NodeId, LamportTs,
/// EventType, ArticleId, Payload, ProtocolVersion and CreatedAt, and nothing else. A relaying peer
/// can therefore rewrite it on an otherwise perfectly valid, correctly signed event, and the
/// signature still verifies.</para>
///
/// <para>That matters because the hard-delete gate in <c>EventApplier</c> looks an entity up by
/// this value: blank it out and an event for a hard-deleted entity walks straight through the gate
/// that exists to keep deleted content from coming back; point it at an unrelated id that HAS been
/// hard-deleted and an innocent event is silently dropped instead of applied. The forged value is
/// then persisted in <c>tbl_event</c> and relayed onward.</para>
///
/// <para>The fix is not to sign one more field — that would change the signature format and break
/// every peer that has not upgraded, while old events would still need verifying under the old
/// rule. It is that <c>EntityId</c> never needed to be transported at all: every event's entity is
/// already derivable from fields the signature DOES cover. So derive it, on both the writing and
/// the reading side, and let the transported value be advisory at most.</para>
/// </summary>
public static class EventEntityId
{
    /// <summary>
    /// The entity this event is about, computed only from signed fields. Returns null when the
    /// event has no entity (checkpoints, whitelist changes) or when the payload is unparseable —
    /// a null identifier means "no entity to gate on", which is the safe reading for both.
    /// </summary>
    public static string? Derive(string eventType, Guid? articleId, string? payloadJson) => eventType switch
    {
        // The deleted thing is named in the payload, not in ArticleId — a hard delete can target a
        // folder path as well as an article id, so ArticleId is left null for these.
        EventTypes.HardDelete =>
            ReadPayloadField<HardDeleteEventPayload>(payloadJson, p => p.EntityIdentifier),

        // A commit is "about" the proposal it commits, which the signed payload already names.
        EventTypes.DekRotationCommit =>
            ReadPayloadField<DekRotationCommitPayload>(payloadJson, p => p.ProposedEventId),

        // Folders are identified by path, not by id, because that is what a hard delete of a
        // folder names and therefore what the gate has to match against. A rename is about where
        // the folder ENDS UP: a later event for the new path has to be gated on the new path.
        EventTypes.FolderCreate =>
            ReadPayloadField<FolderCreatePayload>(payloadJson, p => p.Path),
        EventTypes.FolderRename =>
            ReadPayloadField<FolderRenamePayload>(payloadJson, p => p.NewPath),
        EventTypes.FolderDelete =>
            ReadPayloadField<FolderDeletePayload>(payloadJson, p => p.Path),

        _ => articleId?.ToString()
    };

    /// <summary>Convenience overload for a fully-populated event.</summary>
    public static string? Derive(SyncEvent evt) => Derive(evt.EventType, evt.ArticleId, evt.Payload);

    private static string? ReadPayloadField<T>(string? payloadJson, Func<T, string?> select)
    {
        if (string.IsNullOrEmpty(payloadJson)) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<T>(payloadJson);
            if (payload == null) return null;
            var value = select(payload);
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (JsonException)
        {
            // A malformed payload is a problem for whoever applies the event, not for us: the
            // applier reports it with far more context than this helper has. Returning null here
            // keeps a bad payload from also corrupting the gate identifier.
            return null;
        }
    }
}
