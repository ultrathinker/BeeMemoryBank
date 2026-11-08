using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.FullIos.Services;

/// <summary>One way this iPhone knows to reach a peer, as the peer page lists it.</summary>
/// <param name="Address">The address.</param>
/// <param name="Source">Where it came from, in words.</param>
/// <param name="Detail">When it worked last, and why it is not used now if it is not.</param>
/// <param name="ThisIPhoneOnly">Typed on this iPhone: no other device knows it, and it can be removed here.</param>
public sealed record PeerChannelItem(string Address, string Source, string Detail, bool ThisIPhoneOnly)
{
    public string Line => Source + " · " + Detail;
}

public enum AddAddressOutcome
{
    /// <summary>The address is saved for this peer, on this iPhone only.</summary>
    Added,

    /// <summary>The address is not acceptable (<see cref="PeerAddressPolicy"/>), or the peer is a blind node.</summary>
    Refused,

    /// <summary>Another device answers there, or one whose key or certificate is not this peer's. Never saved.</summary>
    WrongPeer,

    /// <summary>Nothing answered. May be saved anyway.</summary>
    NoAnswer,

    /// <summary>Something answers, but it is not a Bee Memory Bank node. May be saved anyway.</summary>
    NotSyncServer,

    /// <summary>The peer already has the most addresses typed for it.</summary>
    TooMany,

    /// <summary>The peer is not an active peer of this memory bank.</summary>
    NotFound,
}

/// <param name="Outcome">What happened.</param>
/// <param name="Text">A sentence for the person; it never quotes the address.</param>
/// <param name="Address">The canonical address, when it was saved.</param>
public sealed record AddAddressResult(AddAddressOutcome Outcome, string Text, string? Address = null)
{
    public bool Succeeded => Outcome == AddAddressOutcome.Added;

    /// <summary>The person may keep the address although it did not answer (the device is off, or on another network now).</summary>
    public bool CanSaveAnyway => Outcome is AddAddressOutcome.NoAnswer or AddAddressOutcome.NotSyncServer;
}

/// <summary>
/// The ways to reach a peer from this iPhone (docs/design/lan-peer-sync.md 15.3, 16): the list for the peer page, and "Add an address" - an address
/// the person types for ONE peer, kept on this iPhone only. The rules are the node's own (<c>PeerChannelEndpoints</c>): the address passes
/// <see cref="PeerAddressPolicy"/>, is asked who it is with THIS peer's TLS anchor on the request, and is kept only when node id and key are the
/// peer's; "no answer" can be saved on purpose.
/// </summary>
public static class PeerChannels
{
    /// <summary>
    /// The port of an address typed without one: the LAN port the nodes listen on by default. The same number as <c>LanPorts.Default</c> in the
    /// Infrastructure library, which the phone app must not reference (PeerChannelsTests holds the two together).
    /// </summary>
    public const int DefaultLanPort = 5311;

    /// <summary>How long the question to the typed address may take.</summary>
    public static TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>An address typed without a port means the LAN port (<c>192.168.1.20</c> is <c>https://192.168.1.20:5311</c>); the port the peer announced is used when it did.</summary>
    public static string WithLanPort(string? typed, int? port = null)
    {
        var text = typed?.Trim() ?? "";
        if (text.Length == 0 || text.Contains("://", StringComparison.Ordinal)) return text;
        var hostAndRest = text.Split('/', 2);
        var host = hostAndRest[0];
        var hasPort = host.StartsWith('[') ? host.Contains("]:", StringComparison.Ordinal) : host.Count(c => c == ':') == 1;
        return hasPort ? text : $"{host}:{port ?? DefaultLanPort}" + (hostAndRest.Length > 1 ? "/" + hostAndRest[1] : "");
    }

    /// <summary>Every way this iPhone knows to reach <paramref name="nodeId"/>, the one tried first at the top.</summary>
    public static async Task<IReadOnlyList<PeerChannelItem>> ListAsync(IServiceProvider services, Guid nodeId)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        if (await sp.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(nodeId) is not { } row) return [];
        var (plan, _) = await PeerSessions.PlanAsync(sp, row);
        var rows = await sp.GetRequiredService<PeerChannelStore>().GetAsync(nodeId);
        var dialled = plan.Dialable.Select(c => c.Url).ToHashSet(StringComparer.Ordinal);
        var first = plan.First.FirstOrDefault()?.Url;
        var now = DateTime.UtcNow;
        return plan.All
            .Select(c => new PeerChannelItem(
                c.Url,
                SourceText(c.Source),
                DetailOf(c, dialled.Contains(c.Url), c.Url == first, now),
                rows.Any(r => r.Source == ChannelSources.Manual && r.Address == c.Url)))
            .OrderByDescending(i => i.Detail.StartsWith("used first", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// Adds an address for a peer. Nothing is sent before <see cref="PeerAddressPolicy"/> has accepted it; then the address is asked who it is, over a
    /// client that carries the peer's TLS anchor, and it is kept when node id and key are the peer's, or when nobody answered and
    /// <paramref name="saveWithoutAnswer"/> says the person chose to keep it.
    /// </summary>
    public static async Task<AddAddressResult> AddAsync(
        IServiceProvider services, Guid nodeId, string? typed, bool saveWithoutAnswer = false, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        if (await sp.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(nodeId) is not { Status: "A" } peer)
            return new(AddAddressOutcome.NotFound, "This device is not one of the devices this iPhone syncs with.");
        if (BlindNodeId.IsBlind(nodeId))
            return new(AddAddressOutcome.Refused, "A blind node is reached at the address of its pairing; pair it again to move it.");

        // The port a bare address implies is the one the peer announced, else the default.
        var announced = await sp.GetRequiredService<PeerEndpointStore>().GetAsync(nodeId);
        var canonical = PeerAddressPolicy.Canonicalize(WithLanPort(typed, announced?.Port));
        if (!canonical.Ok) return new(AddAddressOutcome.Refused, canonical.Refusal!);
        var address = canonical.Address!;

        var verdict = await ProbeAsync(sp, peer, announced, address, ct);
        if (verdict == ChannelFailures.WrongPeer)
            return new(AddAddressOutcome.WrongPeer, "Another device answers at this address, or one whose key or certificate is not this device's. Nothing was saved.");
        if (verdict is not null && !saveWithoutAnswer)
            return verdict == ChannelFailures.NotSyncServer
                ? new(AddAddressOutcome.NotSyncServer, "Something answers at this address, but it is not a Bee Memory Bank node.")
                : new(AddAddressOutcome.NoAnswer, "Nothing answered at this address.");

        var store = sp.GetRequiredService<PeerChannelStore>();
        if (!await store.AddManualAsync(nodeId, address, DateTime.UtcNow))
            return new(AddAddressOutcome.TooMany, $"This device already has {PeerChannelStore.MaxManualPerPeer} addresses typed for it; remove one first.");
        if (verdict is null)
            await store.NoteOkAsync(nodeId, address, ChannelSources.Manual, ChannelScopes.Of(address), DateTime.UtcNow);

        // The new address is tried at once.
        sp.GetService<ISyncTrigger>()?.Signal();
        return new(AddAddressOutcome.Added, verdict is null ? "Saved. It answered as this device." : "Saved. Nothing answers there yet.", address);
    }

    /// <summary>Removes an address typed on this iPhone. True when there was one.</summary>
    public static async Task<bool> RemoveAsync(IServiceProvider services, Guid nodeId, string address)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PeerChannelStore>().RemoveManualAsync(nodeId, address);
    }

    /// <summary>Asks the address who it is, held to this peer's TLS anchor; null when it is this peer, else the kind of failure (<see cref="ChannelFailures"/>).</summary>
    private static async Task<string?> ProbeAsync(IServiceProvider sp, WhitelistEntry peer, PeerEndpointRecord? announced, string address, CancellationToken ct)
    {
        var anchor = PeerTlsAnchor.For(peer, announced);
        using var baseHttp = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
        baseHttp.Timeout = ProbeTimeout;
        using var session = PeerSessions.ClientFor(sp.GetService<IHttpMessageHandlerFactory>(), baseHttp, anchor);
        try
        {
            var identity = await SyncAddress.ReadIdentityAsync(session.Client, address, ct);
            if (identity.NodeId != peer.NodeId) return ChannelFailures.WrongPeer;
            if (peer.Ed25519PublicKey is { Length: 32 } && !PeerKey.Matches(peer.Ed25519PublicKey, identity.Ed25519PublicKeyB64)) return ChannelFailures.WrongPeer;
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return PeerDialer.Classify(ex);
        }
    }

    private static string SourceText(string source) => source switch
    {
        ChannelSources.Manual => "Added on this iPhone",
        ChannelSources.Primary => "Its address",
        ChannelSources.Announced => "Announced by it",
        ChannelSources.Mdns => "Seen on this Wi-Fi",
        _ => source,
    };

    private static string DetailOf(ChannelCandidate c, bool dialled, bool usedFirst, DateTime now)
    {
        var worked = c.LastOkAt is { } ok ? $"worked {Ago(ok, now)}" : "never worked";
        if (usedFirst) return "used first, " + worked;
        if (dialled) return worked;
        if (c.IsLanLike && !c.OnCurrentNetwork) return worked + ", not tried: not on its network";
        return worked + ", not tried: another device answered there";
    }

    /// <summary>"3 min ago", "2 h ago", "just now".</summary>
    internal static string Ago(DateTime at, DateTime now)
    {
        var span = now - at;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h ago";
        return $"{(int)span.TotalDays} d ago";
    }
}

/// <summary>The phone's own words for what the status of a peer and of the local network means (docs/design/lan-peer-sync.md section 10).</summary>
public static class LanStatusText
{
    /// <summary>Shown when iOS refused the local network to this app: the way back is in Settings.</summary>
    public const string LocalNetworkOff =
        "Local network access is off for Bee Memory Bank: direct sync with your computers is off; it syncs through your server. " +
        "To turn it on, open Settings > Privacy & Security > Local Network and switch on Bee Memory Bank.";

    /// <summary>The sentence for a state of <see cref="ReachabilityStates"/>; null where the card's own line (the last contact, the problem) says it.</summary>
    public static string? Of(string state, string peerName)
    {
        var name = string.IsNullOrWhiteSpace(peerName) ? "it" : peerName;
        return state switch
        {
            ReachabilityStates.DirectLan => "Direct, on this network",
            ReachabilityStates.DirectInternet => "Direct, over the internet",
            ReachabilityStates.Away => "Not on this network",
            ReachabilityStates.OldVersion => $"Direct sync on this network needs an update on {name}",
            ReachabilityStates.ViaRelay => "Not on this network; its changes arrive through another device",
            _ => null,
        };
    }
}
