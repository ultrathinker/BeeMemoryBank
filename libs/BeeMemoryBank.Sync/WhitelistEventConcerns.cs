using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Which concerns of a peer's row (<see cref="WhitelistConcerns"/>) a whitelist event writes, as every node of this version reads it: a
/// <c>whitelist_add</c> all of them, a <c>whitelist_revoke</c> membership, a <c>whitelist_update</c> each field it names that this node takes.
/// The applier judges each of those by its own version; a node that writes its own row with the version its event minted stamps the same
/// concerns (<see cref="ForLocalUpdate"/>), so it compares a later event exactly as the nodes that receive its event do.
/// </summary>
public static class WhitelistEventConcerns
{
    /// <summary>A superadmin flag an update names that no node of this version takes.</summary>
    public enum Refusal { None, BlindSuperadmin, SelfPromotion }

    public const WhitelistConcerns OfAdd = WhitelistConcerns.All;
    public const WhitelistConcerns OfRevoke = WhitelistConcerns.Membership;

    /// <summary>
    /// The concerns <paramref name="p"/> writes. <paramref name="storedAddress"/> is its address once through the address policy (null: none,
    /// or refused). The superadmin flag is refused (not written, whatever the row says, so the answer does not depend on the order of arrival)
    /// when it would make a blind node a superadmin, or when a node raises its own: a node never has its own row, so no node of this version
    /// ever sends that, and a raise must not come from the node raised.
    /// </summary>
    public static WhitelistConcerns OfUpdate(WhitelistUpdatePayload p, Guid author, string? storedAddress, out Refusal refusal)
    {
        var concerns = WhitelistConcerns.None;
        if (storedAddress is not null) concerns |= WhitelistConcerns.Address;
        if (p.DisplayName is not null) concerns |= WhitelistConcerns.Name;
        if (CarriesTls(p.TlsTrust, p.TlsSpki)) concerns |= WhitelistConcerns.Tls;
        refusal = Refusal.None;
        if (p.IsSuperadmin is { } flag)
        {
            if (flag && BlindNodeId.IsBlind(p.NodeId)) refusal = Refusal.BlindSuperadmin;
            else if (flag && author == p.NodeId) refusal = Refusal.SelfPromotion;
            else concerns |= WhitelistConcerns.Superadmin;
        }
        return concerns;
    }

    /// <summary>
    /// The concerns of the <c>whitelist_update</c> this node just logged with these arguments (<see cref="Core.Interfaces.IEventLogger.LogWhitelistUpdateAsync"/>):
    /// what the row it writes stamps with the version the event minted.
    /// </summary>
    public static WhitelistConcerns ForLocalUpdate(Guid nodeId, Guid author, string? apiAddress, string? displayName, bool? isSuperadmin = null,
        string? tlsSpki = null, string? tlsTrust = null) =>
        OfUpdate(new WhitelistUpdatePayload(nodeId, apiAddress, displayName, isSuperadmin, tlsSpki, tlsTrust), author, StoredAddress(apiAddress), out _);

    /// <summary>The value of one field concern of <paramref name="from"/> onto <paramref name="to"/> (TLS: the mode and the pin together).</summary>
    public static void CopyField(WhitelistConcerns concern, WhitelistEntry from, WhitelistEntry to)
    {
        switch (concern)
        {
            case WhitelistConcerns.Superadmin: to.IsSuperadmin = from.IsSuperadmin; break;
            case WhitelistConcerns.Name: to.DisplayName = from.DisplayName; break;
            case WhitelistConcerns.Address: to.ApiAddress = from.ApiAddress; break;
            case WhitelistConcerns.Tls: (to.TlsSpki, to.TlsTrust) = (from.TlsSpki, from.TlsTrust); break;
        }
    }

    /// <summary>
    /// A row this node writes again from an add it logged before (a retry after a crash, with that add's version <paramref name="add"/>): each
    /// field of <paramref name="existing"/> newer than the add (an update applied since) keeps its value and version on <paramref name="written"/>,
    /// as the applier judges an add on every other node. The row's own version becomes the newest of them.
    /// </summary>
    public static void KeepNewerFields(WhitelistEntry existing, WhitelistEntry written, RowVersion add)
    {
        var newest = add;
        foreach (var concern in WhitelistConcernSet.Each)
        {
            if (concern == WhitelistConcerns.Membership || !ConflictResolver.IncomingWins(add, existing.VersionOf(concern))) continue;
            CopyField(concern, existing, written);
            written.SetVersionOf(concern, existing.VersionOf(concern));
            if (existing.IsUndated(concern)) written.MarkUndated(concern);
            if (ConflictResolver.IncomingWins(newest, existing.VersionOf(concern))) newest = existing.VersionOf(concern);
        }
        written.SetRowVersion(newest);
    }

    /// <summary>The address an event may put on a row: the policy's canonical form, or none.</summary>
    public static string? StoredAddress(string? incoming) => string.IsNullOrWhiteSpace(incoming) ? null : PeerAddressPolicy.ForStorage(incoming);

    /// <summary>
    /// Whether an update's <c>tls_trust</c> and <c>tls_spki</c> change the row's TLS trust (EventApplier.ApplyTlsTrust): a mode this node knows
    /// or keeps, a pin with its key, or (an older sender, no mode) any pin at all. A <c>pin</c> mode without a key and neither field change nothing.
    /// </summary>
    public static bool CarriesTls(string? trust, string? tlsSpki) => trust switch
    {
        BlindTrust.PublicCa or BlindTrust.None => true,
        BlindTrust.Pin when !string.IsNullOrEmpty(tlsSpki) => true,
        { Length: > 0 } t when t != BlindTrust.Pin => true,
        _ => tlsSpki is not null,
    };
}
