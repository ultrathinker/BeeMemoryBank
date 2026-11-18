using System.Collections.Concurrent;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync;

/// <summary>
/// What this node last could not do when a removal asked it for a new identity key (release 2.12.0, step S1), per vault: the code of the refusal
/// (<see cref="NodeStateExtraKeys.RekeyDevices"/>, <see cref="NodeStateExtraKeys.RekeyBusy"/>, <see cref="NodeStateExtraKeys.RekeyEvents"/>).
/// Process memory only: a restart forgets it, and the next request it serves says it again. Cleared by a re-key that went through.
/// </summary>
public static class IdentityRekeyRefusals
{
    private static readonly ConcurrentDictionary<string, string> Last = new(StringComparer.Ordinal);

    public static void Record(string databaseId, string code) => Last[databaseId] = code;

    public static void Clear(string databaseId) => Last.TryRemove(databaseId, out _);

    public static string? Of(string databaseId) => Last.TryGetValue(databaseId, out var code) ? code : null;

    /// <summary>The code of a refusal sentence of <see cref="NodeIdentityRekeyService.RekeyAsync"/> (none for "locked": the page says that itself).</summary>
    public static string? CodeOf(string? refusal) => refusal switch
    {
        null => null,
        NodeIdentityRekeyService.ExternalKeyRefusal => NodeStateExtraKeys.RekeyExternal,
        NodeIdentityRekeyService.LockedRefusal => null,
        _ when refusal.StartsWith("Not every device can follow", StringComparison.Ordinal) => NodeStateExtraKeys.RekeyDevices,
        _ when refusal.Contains("own events since its last checkpoint", StringComparison.Ordinal) => NodeStateExtraKeys.RekeyEvents,
        _ => NodeStateExtraKeys.RekeyBusy,
    };
}

/// <summary>
/// Says in <c>node_state</c> why this full node cannot give itself a new identity key when a removal asks (release 2.12.0, step S1): its seed is kept
/// outside the database (<see cref="NodeStateExtraKeys.RekeyExternal"/>: it never can, the node has to be paired again or revoked), or the last
/// refusal (<see cref="IdentityRekeyRefusals"/>). The removal's driver reads it to tell the administrator what to do instead of waiting silently.
/// </summary>
public sealed class IdentityRekeyNodeStateContributor(INodeIdentityRepository nodeRepo, IDbConnectionFactory db) : INodeStateContributor
{
    public int Order => 25;

    public async Task ContributeAsync(NodeStateDraft draft, CancellationToken ct = default)
    {
        var identity = await nodeRepo.GetAsync();
        if (identity is null || BlindNodeId.IsBlind(identity.NodeId)) return;
        if (identity.Ed25519PrivateKeyV is not (0 or 1))
            draft.Extra[NodeStateExtraKeys.IdentityRekey] = NodeStateExtraKeys.RekeyExternal;
        else if (IdentityRekeyRefusals.Of(db.DatabaseId) is { } code)
            draft.Extra[NodeStateExtraKeys.IdentityRekey] = code;
    }
}
