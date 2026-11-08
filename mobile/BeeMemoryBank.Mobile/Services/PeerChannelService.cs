using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Mobile.Services;

public enum PeerChannelOutcome
{
    /// <summary>The address is stored for this peer, on this phone only.</summary>
    Added,

    /// <summary>The address is not acceptable (<see cref="PeerAddressPolicy"/>), or this peer is reached another way (a blind node).</summary>
    Refused,

    /// <summary>A sync server answers, but another node's, or with another key, or a certificate that is not this peer's. Never saved.</summary>
    WrongPeer,

    /// <summary>Something answers, but it is not a sync server. Saved only after the person said so (<c>saveWithoutAnswer</c>).</summary>
    NotSyncServer,

    /// <summary>Nothing answered. Saved only after the person said so (<c>saveWithoutAnswer</c>): the device may be off or on another network.</summary>
    NoAnswer,

    /// <summary>The peer already has <see cref="PeerChannelStore.MaxManualPerPeer"/> typed addresses.</summary>
    TooMany,

    NotFound,
    OwnNode,
}

/// <param name="Outcome">What happened.</param>
/// <param name="Message">A sentence for the person; it never quotes the address.</param>
/// <param name="Address">The canonical address that was stored, when one was.</param>
/// <param name="Answered">The address answered as this peer (false: it was saved without an answer).</param>
public sealed record PeerChannelResult(PeerChannelOutcome Outcome, string Message, string? Address = null, bool Answered = false)
{
    public bool Succeeded => Outcome == PeerChannelOutcome.Added;

    /// <summary>The person may be asked "Save anyway": nothing was wrong with the address except that nothing there answered as a sync server.</summary>
    public bool CanSaveAnyway => Outcome is PeerChannelOutcome.NoAnswer or PeerChannelOutcome.NotSyncServer;
}

/// <summary>One way to reach a peer, as the peer card lists it under the peer.</summary>
/// <param name="PeerNodeId">The peer.</param>
/// <param name="Address">The address.</param>
/// <param name="Detail">Where it came from and when it last worked, in one line.</param>
/// <param name="CanRemove">Typed on this phone: it can be removed here (the others are not this phone's to remove).</param>
public sealed record PeerChannelLine(Guid PeerNodeId, string Address, string Detail, bool CanRemove);

/// <summary>A peer as the Status page's list draws it.</summary>
/// <param name="Entry">The whitelist row (the buttons act on it).</param>
/// <param name="Status">"Direct, on this network", "Not on this network" ...; null when there is nothing to say yet.</param>
public sealed record PeerCard(WhitelistEntry Entry, string AddressLine, string? Status, IReadOnlyList<PeerChannelLine> Channels)
{
    public string DisplayName => Entry.DisplayName;
    public bool HasStatus => Status is not null;
    public bool HasChannels => Channels.Count > 0;
}

/// <summary>
/// The phone's own addresses for its peers (docs/design/lan-peer-sync.md 15.3, 16): "Add an address" on the peer card, the list of a peer's channels and
/// "Remove" for the typed ones. An address is made canonical by <see cref="PeerAddressPolicy"/>, then asked who it is, with THIS peer's TLS anchor: the node
/// id, the key on record and the certificate must all be the peer's, or nothing is saved. An address that does not answer may be saved after the person
/// says so. Stored for this phone only (<see cref="PeerChannelStore"/>), so no event is written and no other node hears of it. "Change address" on the
/// card stays the primary address and goes through <see cref="PeerAddressService"/> as before.
/// </summary>
public sealed class PeerChannelService(
    IWhitelistRepository whitelist,
    INodeIdentityRepository identityRepo,
    PeerEndpointStore endpoints,
    PeerChannelStore channels,
    IAuditLogRepository audit,
    IHttpClientFactory httpFactory,
    IServiceProvider services,
    ILogger<PeerChannelService>? logger = null)
{
    /// <summary>The LAN port a bare address implies when the peer never announced one (the same as <c>LanPorts.Default</c> of the desktop node).</summary>
    public const int DefaultLanPort = 5311;

    /// <summary>An address typed without a port means the peer's LAN port (<c>192.168.1.20</c> is <c>https://192.168.1.20:5311</c>); one with a scheme is left alone.</summary>
    public static string WithLanPort(string? typed, int? port = null)
    {
        var text = typed?.Trim() ?? "";
        if (text.Length == 0 || text.Contains("://", StringComparison.Ordinal)) return text;
        var hostAndRest = text.Split('/', 2);
        var host = hostAndRest[0];
        var hasPort = host.StartsWith('[') ? host.Contains("]:", StringComparison.Ordinal) : host.Count(c => c == ':') == 1;
        return hasPort ? text : $"{host}:{port ?? DefaultLanPort}" + (hostAndRest.Length > 1 ? "/" + hostAndRest[1] : "");
    }

    /// <param name="peerId">The peer.</param>
    /// <param name="typed">An IP address, a name or a URL.</param>
    /// <param name="saveWithoutAnswer">True only after the person was told that nothing answers there (or not a sync server) and chose to save it anyway.</param>
    public async Task<PeerChannelResult> AddAsync(Guid peerId, string? typed, bool saveWithoutAnswer = false, CancellationToken ct = default)
    {
        var peer = await whitelist.GetByNodeIdAsync(peerId);
        if (peer is null || peer.Status != "A") return new(PeerChannelOutcome.NotFound, "This device is not one of the peers.");
        if (await identityRepo.GetAsync() is { } self && self.NodeId == peerId) return new(PeerChannelOutcome.OwnNode, "This is the phone itself.");
        if (BlindNodeId.IsBlind(peerId))
            return new(PeerChannelOutcome.Refused, "A blind node is reached at the address of its pairing; pair it again to move it.");

        // The port a bare address implies is the one the peer announced, else the default.
        var announced = await endpoints.GetAsync(peerId);
        var canonical = PeerAddressPolicy.Canonicalize(WithLanPort(typed, announced?.Port));
        if (!canonical.Ok) return new(PeerChannelOutcome.Refused, canonical.Refusal!);
        var address = canonical.Address!;

        var verdict = await ProbeAsync(peer, announced, address, ct);
        if (verdict == ChannelFailures.WrongPeer)
            return new(PeerChannelOutcome.WrongPeer, "Another device answers at this address, or one whose key or certificate is not this device's. Nothing was saved.");
        if (verdict is not null && !saveWithoutAnswer)
            return verdict == ChannelFailures.NotSyncServer
                ? new(PeerChannelOutcome.NotSyncServer, "Something answers at this address, but it is not a Bee Memory Bank node. Save it anyway only if the device is not running right now.")
                : new(PeerChannelOutcome.NoAnswer, "Nothing answered at this address. Save it anyway if the device is off or on another network now.");

        if (!await channels.AddManualAsync(peerId, address, DateTime.UtcNow))
            return new(PeerChannelOutcome.TooMany, $"This device already has {PeerChannelStore.MaxManualPerPeer} addresses typed for it; remove one first.");
        if (verdict is null)
            await channels.NoteOkAsync(peerId, address, ChannelSources.Manual, ChannelScopes.Of(address), DateTime.UtcNow);
        await AuditAsync(peerId, "peer_channel_added",
            $"An address ({PeerAddressPolicy.Fingerprint(address)}) was added for peer {peer.DisplayName} on this phone");
        return new(PeerChannelOutcome.Added, verdict is null ? "Address added." : "Address added. Nothing answers there yet.", address, verdict is null);
    }

    /// <summary>Removes an address typed on this phone. False when there was no such address.</summary>
    public async Task<bool> RemoveAsync(Guid peerId, string address)
    {
        if (!await channels.RemoveManualAsync(peerId, address)) return false;
        await AuditAsync(peerId, "peer_channel_removed", $"An address ({PeerAddressPolicy.Fingerprint(address)}) was removed for peer {peerId} on this phone");
        return true;
    }

    /// <summary>The peer as its card draws it: the address line, how it stands, and every way to reach it.</summary>
    /// <param name="syncInterval">How often the phone syncs now (see <see cref="PhonePeers.StatusText"/>).</param>
    /// <param name="ownCycleRecent">The phone itself synced lately.</param>
    public async Task<PeerCard> CardAsync(WhitelistEntry peer, TimeSpan syncInterval, bool ownCycleRecent)
    {
        var now = DateTime.UtcNow;
        var (plan, _) = await PeerSessions.PlanAsync(services, peer);
        var rows = await channels.GetAsync(peer.NodeId);
        var contact = services.GetService<PeerContactLog>()?.Get(peer.NodeId, peer.ApiAddress);
        var status = PhonePeers.StatusText(peer, plan, rows, services.GetService<PeerReachability>(), contact, syncInterval, ownCycleRecent, now);

        var dialled = plan.Dialable.Select(c => c.Url).ToHashSet(StringComparer.Ordinal);
        var first = plan.First.FirstOrDefault()?.Url;
        var lines = plan.All.Select(c => new PeerChannelLine(
            peer.NodeId, c.Url, Detail(c, dialled.Contains(c.Url), c.Url == first, now),
            rows.Any(r => r.Source == ChannelSources.Manual && r.Address == c.Url))).ToList();
        return new PeerCard(peer, string.IsNullOrWhiteSpace(peer.ApiAddress) ? "No address of its own" : peer.ApiAddress, status, lines);
    }

    private static string Detail(ChannelCandidate c, bool dialled, bool usedFirst, DateTime now)
    {
        var source = c.Source switch
        {
            ChannelSources.Primary => "address",
            ChannelSources.Announced => "announced by the device",
            ChannelSources.Mdns => "found on the Wi-Fi",
            ChannelSources.Manual => "added on this phone",
            _ => c.Source,
        };
        var worked = c.LastOkAt is { } ok ? $"worked {PhonePeers.Ago(ok, now)}" : "never worked";
        var note = !dialled ? (c.IsLanLike && !c.OnCurrentNetwork ? " · not on this network" : " · paused") : usedFirst ? " · used first" : "";
        return $"{source} · {worked}{note}";
    }

    /// <summary>Asks the address who it is, held to this peer's TLS anchor; null when it is this peer, else the kind of failure (<see cref="ChannelFailures"/>).</summary>
    private async Task<string?> ProbeAsync(WhitelistEntry peer, PeerEndpointRecord? announced, string address, CancellationToken ct)
    {
        using var baseHttp = httpFactory.CreateClient(PeerAddressService.HttpClientName);
        baseHttp.Timeout = PeerAddressService.ProbeTimeout;
        // The client carries the peer's anchor on every request, so the address is held to THIS peer's keys, not to whatever row has that host and port.
        using var session = PeerSessions.ClientFor(httpFactory, baseHttp, PeerTlsAnchor.For(peer, announced), PeerAddressService.HttpClientName);
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

    private async Task AuditAsync(Guid peerId, string action, string details)
    {
        try { await audit.LogAsync("whitelist", peerId.ToString(), action, "phone", details); }
        catch (Exception ex)
        {
            // The address is stored or removed; a failed audit line must not turn that into an error the person would repeat.
            logger?.LogWarning(ex, "The audit line of the address change of {NodeId} could not be written", peerId);
        }
    }
}
