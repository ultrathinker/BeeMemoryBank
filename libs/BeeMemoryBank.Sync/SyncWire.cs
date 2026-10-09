using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync;

/// <summary>
/// What leaves this node on the sync wire (release 2.5.2). The event envelope travels without the
/// actor fields (actorType, actorName, viaAgentName) and without the transported entity_id: none
/// of the four is covered by the event signature (<see cref="EventSignature.BuildPayload"/>), the
/// applier overwrites the actor fields from its own whitelist and re-derives the entity id from
/// the signed fields anyway (<see cref="EventApplier"/>), and their only readers are local
/// surfaces. Carrying them to a peer only leaks who worked on what, and by which agent, to every
/// node in the network — one of which may be a blind node holding everything in plaintext.
/// </summary>
public static class SyncWire
{
    /// <summary>
    /// Serializer for event bodies this node serves or pushes: camelCase like the default web
    /// options, but with nulls omitted — the stripped fields vanish from the body entirely
    /// instead of arriving as explicit nulls.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Clears, in place, the envelope fields that must not leave the node. The events are the
    /// request's own copies from the local log, so nulling them here changes nothing on disk.
    /// </summary>
    public static IReadOnlyList<SyncEvent> Strip(IReadOnlyList<SyncEvent> events)
    {
        foreach (var evt in events)
        {
            evt.ActorType = null;
            evt.ActorName = null;
            evt.ViaAgentName = null;
            evt.EntityId = null;
        }
        return events;
    }
}
