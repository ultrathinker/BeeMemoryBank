using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Channels;

namespace BeeMemoryBank.Api.Endpoints;

/// <summary>One way to reach a peer, as the device page lists it.</summary>
/// <param name="Address">The address.</param>
/// <param name="Source"><c>primary</c>, <c>announced</c>, <c>mdns</c> or <c>manual</c> (the most explicit one when several named it).</param>
/// <param name="Scope"><c>lan</c>, <c>vpn</c> or <c>internet</c>.</param>
/// <param name="OnCurrentNetwork">This computer is on its network now.</param>
/// <param name="Dialled">The sync loop tries it (false: off this network, or it showed another node within the hour).</param>
/// <param name="UsedFirst">It worked last and is tried first.</param>
/// <param name="ThisDeviceOnly">Typed on this node: no other node knows it.</param>
public sealed record PeerChannelDto(
    string Address, string Source, string Scope, bool OnCurrentNetwork, bool Dialled, bool UsedFirst, bool ThisDeviceOnly,
    DateTime? LastOkAt, DateTime? LastFailAt, string? LastFailKind);

/// <param name="Channels">Every way this node knows to reach the peer.</param>
/// <param name="CaPinned">The peer announced its local CA's key: a new leaf it issues is accepted without re-pinning (<c>ca-pin</c>).</param>
/// <param name="AnnouncedAt">When the peer last announced its endpoints (null: never, or only what the join told).</param>
public sealed record PeerChannelsResponse(Guid NodeId, IReadOnlyList<PeerChannelDto> Channels, bool CaPinned, DateTime? AnnouncedAt);

/// <param name="Address">An IP, a host name or a URL; <c>https://</c> and the LAN port are implied.</param>
/// <param name="SaveWithoutAnswer">Keep it even when nothing answers there now (the person was told).</param>
public sealed record AddPeerChannelRequest(string Address, bool SaveWithoutAnswer = false);

/// <summary>
/// The ways to reach a peer, on its device page (docs/design/lan-peer-sync.md 15.3): the list, and the addresses a person adds for it on THIS node
/// only. An address is checked by <see cref="PeerAddressPolicy"/> first and then asked who it is: the node id, the key on record and the peer's TLS
/// anchor must be THIS peer's. Superadmin and internal key, like the rest of the whitelist; never an agent for a change.
/// </summary>
public static class PeerChannelEndpoints
{
    public static void MapPeerChannelEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/whitelist/{nodeId:guid}/channels").WithTags("Whitelist")
            .RequireInternalKey().RequireSuperadmin();

        group.MapGet("", async (Guid nodeId, IServiceProvider services, IWhitelistRepository whitelist) =>
        {
            if (await whitelist.GetByNodeIdAsync(nodeId) is not { } peer) return Results.NotFound(new ErrorResponse("This node is not in the whitelist."));
            var (plan, announced) = await PlanAsync(services, peer);
            var rows = await services.GetRequiredService<PeerChannelStore>().GetAsync(nodeId);
            var dialled = plan.Dialable.Select(c => c.Url).ToHashSet(StringComparer.Ordinal);
            var first = plan.First.FirstOrDefault()?.Url;
            var list = plan.All.Select(c => new PeerChannelDto(c.Url, c.Source, c.Scope, c.OnCurrentNetwork, dialled.Contains(c.Url), c.Url == first,
                rows.Any(r => r.Source == ChannelSources.Manual && r.Address == c.Url), c.LastOkAt, c.LastFailAt, c.LastFailKind)).ToList();
            return Results.Ok(new PeerChannelsResponse(nodeId, list, announced?.CaKeyBytes is not null,
                announced is { LamportTs: > 0 } ? announced.UpdatedAt : null));
        });

        group.MapPost("", async (Guid nodeId, AddPeerChannelRequest req, IServiceProvider services, IWhitelistRepository whitelist,
            IAuditLogRepository audit, HttpContext ctx, CancellationToken ct) =>
        {
            if (await whitelist.GetByNodeIdAsync(nodeId) is not { } peer) return Results.NotFound(new ErrorResponse("This node is not in the whitelist."));
            if (BlindNodeId.IsBlind(nodeId))
                return Results.BadRequest(new ErrorResponse("A blind node is reached at the address of its pairing; pair it again to move it."));

            // The port a bare address implies is the one the peer announced, else the default.
            var announced = await services.GetRequiredService<PeerEndpointStore>().GetAsync(nodeId);
            var canonical = PeerAddressPolicy.Canonicalize(WithLanPort(req.Address, announced?.Port));
            if (!canonical.Ok) return Results.BadRequest(new ErrorResponse(canonical.Refusal!));
            var address = canonical.Address!;

            var verdict = await ProbeAsync(services, peer, announced, address, ct);
            if (verdict == ChannelFailures.WrongPeer)
                return Results.BadRequest(new ErrorResponse("Another device answers at this address, or one whose key or certificate is not this device's. Nothing was saved."));
            if (verdict is not null && !req.SaveWithoutAnswer)
                return Results.Json(new ErrorResponse(verdict == ChannelFailures.NotSyncServer
                    ? "Something answers at this address, but it is not a Bee Memory Bank node. Save it anyway only if the device is not running right now."
                    : "Nothing answered at this address. Save it anyway if the device is off or on another network now."), statusCode: 422);

            var store = services.GetRequiredService<PeerChannelStore>();
            if (!await store.AddManualAsync(nodeId, address, DateTime.UtcNow))
                return Results.BadRequest(new ErrorResponse($"This device already has {PeerChannelStore.MaxManualPerPeer} addresses typed for it; remove one first."));
            if (verdict is null)
                await store.NoteOkAsync(nodeId, address, ChannelSources.Manual, ChannelScopes.Of(address), DateTime.UtcNow);
            var actor = ctx.Request.Headers["X-User-Id"].FirstOrDefault() ?? "system";
            await audit.LogAsync("whitelist", nodeId.ToString(), "peer_channel_added", "web",
                $"An address ({PeerAddressPolicy.Fingerprint(address)}) was added for peer {peer.DisplayName} on this node by user {actor}");
            services.GetService<IPeerStateResetter>()?.PeerChanged(nodeId);
            return Results.Ok(new { address, answered = verdict is null });
        }).RequireNonAgent();

        group.MapDelete("", async (Guid nodeId, string address, IServiceProvider services, IAuditLogRepository audit, HttpContext ctx) =>
        {
            var removed = await services.GetRequiredService<PeerChannelStore>().RemoveManualAsync(nodeId, address);
            if (!removed) return Results.NotFound(new ErrorResponse("There is no such address typed for this device."));
            var actor = ctx.Request.Headers["X-User-Id"].FirstOrDefault() ?? "system";
            await audit.LogAsync("whitelist", nodeId.ToString(), "peer_channel_removed", "web",
                $"An address ({PeerAddressPolicy.Fingerprint(address)}) was removed for peer {nodeId} on this node by user {actor}");
            services.GetService<IPeerStateResetter>()?.PeerChanged(nodeId);
            return Results.Ok(new { removed = true });
        }).RequireNonAgent();
    }

    /// <summary>An address typed without a port means the LAN port (<c>192.168.1.20</c> is <c>https://192.168.1.20:5311</c>).</summary>
    internal static string WithLanPort(string? typed, int? port = null)
    {
        var text = typed?.Trim() ?? "";
        if (text.Length == 0 || text.Contains("://", StringComparison.Ordinal)) return text;
        var hostAndRest = text.Split('/', 2);
        var host = hostAndRest[0];
        var hasPort = host.StartsWith('[') ? host.Contains("]:", StringComparison.Ordinal) : host.Count(c => c == ':') == 1;
        return hasPort ? text : $"{host}:{port ?? BeeMemoryBank.Infrastructure.Network.LanPorts.Default}" + (hostAndRest.Length > 1 ? "/" + hostAndRest[1] : "");
    }

    private static async Task<(ChannelPlan Plan, PeerEndpointRecord? Announced)> PlanAsync(IServiceProvider services, WhitelistEntry peer)
    {
        var announced = BlindNodeId.IsBlind(peer.NodeId) ? null : await services.GetRequiredService<PeerEndpointStore>().GetAsync(peer.NodeId);
        var rows = await services.GetRequiredService<PeerChannelStore>().GetAsync(peer.NodeId);
        var discovered = services.GetService<IPeerDiscovery>()?.Recent(peer.NodeId) ?? [];
        var local = services.GetService<ILocalNetworks>()?.Current() ?? [];
        return (ChannelPlanner.Plan(peer, announced, discovered, rows, local, DateTime.UtcNow), announced);
    }

    /// <summary>Asks the address who it is, held to this peer's TLS anchor; null when it is this peer, else the kind of failure (<see cref="ChannelFailures"/>).</summary>
    private static async Task<string?> ProbeAsync(IServiceProvider services, WhitelistEntry peer, PeerEndpointRecord? announced, string address, CancellationToken ct)
    {
        var anchor = PeerTlsAnchor.For(peer, announced);
        var handlers = services.GetRequiredService<IHttpMessageHandlerFactory>();
        using var http = new HttpClient(new PeerAnchorStamp(anchor) { InnerHandler = handlers.CreateHandler(SyncScheduler.HttpClientName) }, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        try
        {
            var identity = await SyncAddress.ReadIdentityAsync(http, address, ct);
            if (identity.NodeId != peer.NodeId) return ChannelFailures.WrongPeer;
            if (peer.Ed25519PublicKey is { Length: 32 } && !PeerKey.Matches(peer.Ed25519PublicKey, identity.Ed25519PublicKeyB64)) return ChannelFailures.WrongPeer;
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return PeerDialer.Classify(ex);
        }
    }
}
