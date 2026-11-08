using System.Net;
using BeeMemoryBank.Infrastructure.Network;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Channels;

namespace BeeMemoryBank.Api.Services.Lan;

/// <summary>
/// What a join exchanges about the LAN (docs/design/lan-peer-sync.md section 8, end): the joiner sends its endpoints and TLS anchors with
/// <c>POST /api/join</c>, the host answers with its own, both as optional JSON members an older node ignores. Each side stores what the other said as a
/// join-learned row of <c>tbl_peer_endpoint</c> (Lamport time 0, superseded by the first announcement), so the host can call the joiner - a row that had
/// no address and only "called us" - from the first minute, and each holds the other's CA key from the start. Only a desktop node (the one with a LAN
/// listener) has anything to say.
/// </summary>
public static class LanJoinExchange
{
    /// <summary>The data folder of this Api (the files of the LAN listener and of the local CA are there).</summary>
    public static string? DataPath(IServiceProvider services) =>
        services.GetService<IConfiguration>()?["BeeMemoryBank:DataPath"] ?? Environment.GetEnvironmentVariable("BMB_DATA_PATH");

    /// <summary>The Api of a desktop node is told so by its node (bmbd sets it).</summary>
    public static bool IsDesktopNode => string.Equals(Environment.GetEnvironmentVariable("BMB_MDNS_FOLLOWS_NETWORK_SETTING"), "1", StringComparison.Ordinal);

    /// <summary>What this node tells the other side of a join.</summary>
    public sealed record Offer(List<NodeEndpointEntry> Endpoints, string? TlsLeafSpki, string? TlsCaKey, int? LanPort);

    /// <summary>
    /// This node's offer: the addresses its listener serves the sync surface on, plus the address it has on the other side's network (the network of a
    /// join through a code is trusted once the join succeeded, decision D1, so the sync surface appears there seconds later); its anchors and port.
    /// Null on a node that is not a desktop node.
    /// </summary>
    public static Offer? OwnOffer(string dataPath, IPAddress? otherSide)
    {
        if (!IsDesktopNode) return null;
        var state = new LanListenerState(dataPath).Read();
        var port = state.Listening ? state.Port : new NodeNetworkSettingsStore(dataPath).CurrentPort();
        var endpoints = OwnTlsAnchors.Endpoints(state);
        if (otherSide is not null)
        {
            if (otherSide.IsIPv4MappedToIPv6) otherSide = otherSide.MapToIPv4();
            foreach (var lan in new SystemLanInterfaces().Current())
            {
                if (lan.Kind != LanInterfaceKind.Lan || !lan.Contains(otherSide) || !LanSubnets.IsPrivate(lan.Address)) continue;
                var url = $"https://{lan.Address}:{port}";
                if (endpoints.All(e => e.Url != url) && endpoints.Count < NodeEndpointsRules.MaxEndpoints)
                    endpoints.Add(new NodeEndpointEntry(url, ChannelScopes.Lan));
            }
        }
        var (leaf, ca) = OwnTlsAnchors.Read(dataPath);
        return new Offer(NodeEndpointsRules.Validate(endpoints, out _) ?? [], leaf, ca, port);
    }

    /// <summary>Stores what the other side of a join said about itself. Best effort: the join never fails for it.</summary>
    public static async Task NoteOtherSideAsync(IServiceProvider services, Guid nodeId, IReadOnlyList<NodeEndpointEntry>? endpoints,
        string? tlsLeafSpki, string? tlsCaKey, int? lanPort, ILogger logger)
    {
        if (endpoints is null && tlsLeafSpki is null && tlsCaKey is null) return;
        try
        {
            if (services.GetService<PeerEndpointStore>() is { } store)
                await store.NoteFromJoinAsync(nodeId, endpoints, tlsLeafSpki, tlsCaKey, lanPort);
            services.GetService<IPeerStateResetter>()?.PeerChanged(nodeId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "What the other side of the join said about its endpoints could not be stored; it announces them itself later");
        }
    }

    /// <summary>
    /// The network a device was paired through is trusted (decision D1): on a Mac or Linux desktop the person's answer is written for it; on Windows the
    /// category Windows gives it decides, and nothing is written. Best effort.
    /// </summary>
    public static void TrustPairingNetwork(string dataPath, IPAddress? otherSide, ILogger logger)
    {
        if (!IsDesktopNode || otherSide is null || OperatingSystem.IsWindows()) return;
        try
        {
            if (otherSide.IsIPv4MappedToIPv6) otherSide = otherSide.MapToIPv4();
            if (IPAddress.IsLoopback(otherSide) || !LanSubnets.IsPrivate(otherSide)) return;
            TrustedNetworks.ForThisComputer(dataPath).TrustNetworkOf(otherSide, "paired");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The network of the join could not be remembered as trusted; the Network settings card asks about it");
        }
    }
}
