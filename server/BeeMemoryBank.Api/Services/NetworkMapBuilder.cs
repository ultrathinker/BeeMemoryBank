using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Makes the network map (<see cref="NetworkResponse"/>) of what this node knows: itself, the nodes of the whitelist, and who calls whom. One place, so the page and
/// the tests read the same rules.
///
/// <para>What is measured and what is expected. This node can measure only the calls it is one end of: the ones it makes (<see cref="PeerContactLog"/>: an answer,
/// or since when it does not answer) and the ones it receives (the peer authenticated here). Between two OTHER nodes it knows only the network's rule - every node
/// calls every node that has an address (the sync loop of a full node and of a phone does, and a blind node calls only where nobody can call it) - and that is
/// given as <see cref="Expected"/>, never as something seen.</para>
/// </summary>
public static class NetworkMapBuilder
{
    public const string Observed = "observed";
    public const string Expected = "expected";

    public const string KindFull = "full";
    public const string KindDevice = "device";
    public const string KindBlind = "blind";

    /// <summary>A node of the whitelist with what this process knows about it.</summary>
    public sealed record Peer(WhitelistEntry Row, DeliveryNodeStatus Status, PeerContactLog.Contact Contact);

    /// <summary>This node: its own row of the whitelist (for its address) is optional, the counts are null when they could not be made.</summary>
    public sealed record SelfInfo(
        Guid NodeId, string DisplayName, bool Blind, WhitelistEntry? OwnRow, string? Platform, bool InContainer, string AppVersion, int? Protocol,
        int? Notes, int? Folders, long? Changes);

    public static NetworkResponse Build(SelfInfo self, IReadOnlyList<Peer> peers, bool isInvisible, DateTime nowUtc, TimeSpan? syncInterval = null)
    {
        var selfAddress = Address(self.OwnRow?.ApiAddress);
        var selfNode = new NetworkNode(
            self.NodeId, self.DisplayName, IsSelf: true, self.Blind ? KindBlind : selfAddress.Host is not null ? KindFull : KindDevice,
            self.Platform, self.InContainer, self.AppVersion, self.Protocol, selfAddress.Scheme, selfAddress.Host, selfAddress.Port,
            State: "self", Online: true, LastContactAt: null, LastContactDirection: null, LastPushAt: null, UnsyncedCount: 0, IsSynced: true,
            LastPushedSeq: 0, HeadSeq: self.Changes ?? 0, NotAnsweringSince: null, NotAnsweringReason: null, SeenFrom: null,
            IsSuperadmin: true, CanGenerateEmbeddings: self.OwnRow?.CanGenerateEmbeddings ?? false, TlsTrust: self.OwnRow?.EffectiveTlsTrust,
            AddedAt: self.OwnRow?.CreatedAt is { } c ? Utc(c) : null, Notes: self.Notes, Folders: self.Folders, Changes: self.Changes);

        var nodes = new List<NetworkNode>();
        var links = new List<NetworkLink>();
        foreach (var peer in peers) nodes.Add(PeerNode(peer));

        // What this node measures: its own calls to the nodes that have an address, and the calls it has heard.
        foreach (var peer in peers)
        {
            var status = peer.Status;
            var there = Address(peer.Row.ApiAddress);
            if (status.CanBeCalled)
                links.Add(new NetworkLink(self.NodeId, peer.Row.NodeId, Observed, status.State, there.Scheme, there.Host, there.Port, null,
                    status.LastOutboundAt, status.NotAnsweringSince, status.NotAnsweringReason));
            if (status.LastInboundAt is { } heardAt)
            {
                var fresh = nowUtc - heardAt < PeerContactView.OnlineWindowFor(syncInterval, callsUs: !status.CanBeCalled);
                links.Add(new NetworkLink(peer.Row.NodeId, self.NodeId, Observed, fresh ? "heard" : "stale", selfAddress.Scheme, selfAddress.Host, selfAddress.Port,
                    peer.Contact.InboundFrom, heardAt, null, null));
            }
        }

        // What it cannot measure: the calls between the other nodes, by the network's rule.
        foreach (var caller in peers)
        {
            if (BlindNodeId.IsBlind(caller.Row.NodeId)) continue;   // a blind node calls only where nobody can call it
            foreach (var callee in peers)
            {
                if (callee.Row.NodeId == caller.Row.NodeId || !callee.Status.CanBeCalled) continue;
                var there = Address(callee.Row.ApiAddress);
                links.Add(new NetworkLink(caller.Row.NodeId, callee.Row.NodeId, Expected, "expected", there.Scheme, there.Host, there.Port, null, null, null, null));
            }
        }

        return new NetworkResponse(selfNode, nodes, links, isInvisible);
    }

    private static NetworkNode PeerNode(Peer peer)
    {
        var row = peer.Row;
        var status = peer.Status;
        var address = Address(row.ApiAddress);
        var kind = BlindNodeId.IsBlind(row.NodeId) ? KindBlind : status.CanBeCalled ? KindFull : KindDevice;
        return new NetworkNode(
            row.NodeId, status.DisplayName, IsSelf: false, kind, peer.Contact.Platform, peer.Contact.InContainer == true, row.LastAppVersion, row.LastProtocolVersion,
            address.Scheme, address.Host, address.Port, status.State, status.Online, status.LastContactAt, status.LastContactDirection, status.LastPushAt,
            status.UnsyncedCount, status.IsSynced, status.LastPushedSeq, status.HeadSeq, status.NotAnsweringSince, status.NotAnsweringReason,
            peer.Contact.InboundFrom, row.IsSuperadmin, row.CanGenerateEmbeddings, row.EffectiveTlsTrust, Utc(row.CreatedAt),
            PausedText: status.PausedText, LeftAt: status.LeftAt);
    }

    /// <summary>
    /// The scheme, host and port of an address as the map shows them. Only those: a path, a query or credentials in an address are never echoed, and an address
    /// that is not an http(s) URL gives nothing (the node is drawn without one).
    /// </summary>
    public static (string? Scheme, string? Host, int? Port) Address(string? apiAddress)
    {
        if (string.IsNullOrWhiteSpace(apiAddress) || !Uri.TryCreate(apiAddress.Trim(), UriKind.Absolute, out var uri)) return (null, null, null);
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return (null, null, null);
        return string.IsNullOrEmpty(uri.Host) ? (null, null, null) : (uri.Scheme, uri.Host, uri.Port);
    }

    /// <summary>
    /// A caller's address as the map shows it: an IPv4 address that a dual-stack socket reports as <c>::ffff:a.b.c.d</c> is given as <c>a.b.c.d</c>. Null when
    /// the connection has none (a test server).
    /// </summary>
    public static string? CallerAddress(System.Net.IPAddress? address) =>
        address is null ? null : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

    private static DateTime? Utc(DateTime time) =>
        time == default || time.Year < 2000 ? null : DateTime.SpecifyKind(time, DateTimeKind.Utc);
}
