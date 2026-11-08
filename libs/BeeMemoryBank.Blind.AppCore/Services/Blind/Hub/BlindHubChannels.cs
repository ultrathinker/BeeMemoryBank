using System.Net;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>What a call to the hub is for, which decides the order of the addresses it tries.</summary>
public enum BlindHubCall
{
    /// <summary>The sync routes: a pull round, the position and pause reports, the goodbye. Every address of the hub's channels serves them; the one that worked last is asked first.</summary>
    Sync,

    /// <summary>
    /// The package routes: the first load, a baseline, the package of a backup. The sync surface of a desktop hub's LAN listener does not serve them (only the pairing address is
    /// known to), so the pairing address is asked first while it is on this network.
    /// </summary>
    Package,
}

/// <summary>
/// How a copy reaches its hub (docs/design/lan-peer-sync.md sections 7 and 16): the hub's candidates are its pairing address (the call code's, which stays what it is - nothing is
/// written to the hub and the copy's record of the pairing is not changed), the endpoints the hub announced in its signed <c>node_endpoints</c> event (the copy holds them in
/// <c>tbl_peer_endpoint</c> like any node) and the places mDNS saw it at (<see cref="BlindLanDirectory"/>). They are put in the channel planner's order and the first that answers as the
/// hub is called, through a client that carries the hub's TLS anchor on EVERY request (<see cref="PeerSessions.ClientFor"/>): the key pinned at the pairing, plus what the hub
/// announced about itself. An address that answers with another key is never called, whoever named it.
/// <para>A call whose hub has no pin (a hub on a public CA), or whose key is unknown, takes the pairing address through the account's pinned client exactly as before.</para>
/// <para>A hub that stops answering on a network this device is on is looked for again at once (mDNS), and the call is tried once more at an address it was not tried at.</para>
/// </summary>
public sealed class BlindHubChannels(IServiceProvider services, BlindLanDirectory lan, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger? _logger = services.GetService<ILogger<BlindHubChannels>>();
    private volatile string? _status;

    /// <summary>
    /// What the copy says about the hub's place, in its own words, or null when there is nothing to say (the hub is not on a LAN, or it did not answer on a network this device is on:
    /// the failure itself is told elsewhere, and this is no alarm). "Hub reached on this network at 192.168.1.231." after a call that went through a LAN address; "Hub not on this
    /// network." when every address the hub has is a LAN address of a network this device is not on.
    /// </summary>
    public string? Status => _status;

    /// <summary>True when the hub of <paramref name="target"/> is called through channels: it is pinned (so every address can be held to its key) and its key is known.</summary>
    public static bool IsRouted(BlindCallCode target) =>
        target.Trust == BlindTrust.Pin && !string.IsNullOrWhiteSpace(target.SpkiPin) && target.PublicKey is { Length: 32 };

    /// <summary>
    /// The hub's row as the channel planner wants it: built from the call code, which the pairing authenticated (the copy's own whitelist may be older, or may not hold the hub's
    /// address at all). Its pin is the pin of the pairing.
    /// </summary>
    public static WhitelistEntry HubRow(BlindCallCode target) => new()
    {
        NodeId = target.NodeId, DisplayName = "Hub", Ed25519PublicKey = target.PublicKey, ApiAddress = target.Address,
        TlsSpki = target.SpkiPin, TlsTrust = BlindTrust.Pin, Status = WhitelistStatuses.Active,
    };

    /// <summary>
    /// Runs <paramref name="work"/> with a client and the call code to use for this call: the hub's pairing address through the account's pinned client when the hub is not routed, otherwise
    /// the address of the first channel that answers as the hub (the call code carries it in place of the pairing address for this call only). What <paramref name="work"/> throws is thrown
    /// as it is.
    /// </summary>
    public async Task<T> RunAsync<T>(BlindHttpClientProvider http, BlindCallCode target, BlindHubCall kind, bool exempt,
        Func<HttpClient, BlindCallCode, Task<T>> work, CancellationToken ct)
    {
        if (!IsRouted(target))
        {
            using var plain = http.GetClient(target, exempt);
            return await work(plain, target).ConfigureAwait(false);
        }

        var row = HubRow(target);
        var pairing = PeerAddressPolicy.ForStorage(target.Address);
        var onHomeNetwork = IsHomeNetworkAddress(target.Address);
        var dialer = services.GetService<PeerDialer>();
        var reachability = services.GetService<PeerReachability>();
        lan.Watch(row.NodeId);
        if (onHomeNetwork) await lan.LookAsync(row.NodeId, urgent: false, ct).ConfigureAwait(false);

        var tried = new HashSet<string>(StringComparer.Ordinal);
        for (var round = 0; ; round++)
        {
            var (plan, anchor) = await PeerSessions.PlanAsync(services, row, _logger).ConfigureAwait(false);
            using var fallback = http.GetClient(target, exempt);
            using var session = PeerSessions.ClientFor(new AnchoredHandlers(http, exempt), fallback, anchor);
            plan = PeerSessions.Usable(plan, session);
            reachability?.NotePlan(row.NodeId, plan, _time.GetUtcNow().UtcDateTime);

            // No address a node may call (the pairing address does not pass the policy and nothing else is known): the account's own client, as before.
            if (!plan.HasDialable) return await work(fallback, target).ConfigureAwait(false);
            if (kind == BlindHubCall.Package) plan = PairingFirst(plan, pairing);

            ChannelCandidate chosen;
            try
            {
                chosen = await PeerSessions.ChooseAsync(dialer, session.Client, row, plan, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsUnreachable(ex, ct))
            {
                foreach (var c in plan.Dialable) tried.Add(c.Url);
                if (await LookAgainAsync(row.NodeId, round, onHomeNetwork, tried, ct).ConfigureAwait(false)) continue;
                _status = NotOnNetwork(plan);
                throw;
            }

            var routed = pairing is not null && string.Equals(chosen.Url, pairing, StringComparison.Ordinal) ? target : target with { Address = chosen.Url };
            try
            {
                var result = await work(session.Client, routed).ConfigureAwait(false);
                if (dialer is not null) await dialer.NoteSessionOkAsync(row.NodeId, chosen).ConfigureAwait(false);
                reachability?.NoteDirect(row.NodeId, chosen.Scope, _time.GetUtcNow().UtcDateTime);
                _status = chosen.IsLanLike ? $"Hub reached on this network at {HostOf(chosen.Url)}." : null;
                return result;
            }
            catch (Exception ex) when (IsUnreachable(ex, ct))
            {
                tried.Add(chosen.Url);
                if (dialer is not null) await dialer.NoteSessionFailedAsync(row.NodeId, chosen, ex).ConfigureAwait(false);
                if (await LookAgainAsync(row.NodeId, round, onHomeNetwork, tried, ct).ConfigureAwait(false)) continue;
                _status = NotOnNetwork(plan);
                throw;
            }
        }
    }

    /// <summary>
    /// The hub did not answer: mDNS is asked once, at once, and the call is worth another try when the hub's channels now hold an address it was not tried at (a DHCP change gave it a
    /// new one). False for the second round, for a hub that is not at a LAN address, and for a host that cannot browse.
    /// </summary>
    private async Task<bool> LookAgainAsync(Guid hub, int round, bool onHomeNetwork, HashSet<string> tried, CancellationToken ct)
    {
        if (round > 0 || !onHomeNetwork || !lan.CanBrowse) return false;
        await lan.LookAsync(hub, urgent: true, ct).ConfigureAwait(false);
        return lan.Discovery.Recent(hub).Any(d => !tried.Contains(d.Url));
    }

    /// <summary>The plan with the pairing address first, when it is on a network this device is on (a package is only known to be served there).</summary>
    internal static ChannelPlan PairingFirst(ChannelPlan plan, string? pairing)
    {
        var dialable = plan.Dialable;
        var primary = pairing is null ? null : dialable.FirstOrDefault(c => string.Equals(c.Url, pairing, StringComparison.Ordinal));
        if (primary is null || !primary.OnCurrentNetwork || (plan.First.Count == 1 && plan.First[0] == primary)) return plan;
        return new ChannelPlan([primary],
            [.. plan.First.Concat(plan.Parallel).Where(c => c != primary)],
            [.. plan.Sequential.Where(c => c != primary)],
            plan.Skipped);
    }

    private static string? NotOnNetwork(ChannelPlan plan) =>
        plan.OnlyLan && !plan.AnyLanOnCurrentNetwork ? "Hub not on this network." : null;

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    /// <summary>A pairing address on a home network: a private-range, VPN or <c>.local</c> address - not this computer itself, which has no network to roam.</summary>
    internal static bool IsHomeNetworkAddress(string address)
    {
        if (ChannelScopes.Of(address) is not (ChannelScopes.Lan or ChannelScopes.Vpn)) return false;
        return !(Uri.TryCreate(address, UriKind.Absolute, out var uri) && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
    }

    /// <summary>The hub could not be talked to at all (no connection, a timeout, an address that is not a sync server), as opposed to talking and being refused.</summary>
    internal static bool IsUnreachable(Exception ex, CancellationToken ct) => ex switch
    {
        HttpRequestException { StatusCode: null } => true,
        NoSyncServerException => true,
        TaskCanceledException when !ct.IsCancellationRequested => true,
        _ => false,
    };

    /// <summary>The factory <see cref="PeerSessions.ClientFor"/> wants: a handler that carries the account's counting and limits, whatever peer anchor a request is stamped with.</summary>
    private sealed class AnchoredHandlers(BlindHttpClientProvider http, bool exempt) : IHttpMessageHandlerFactory
    {
        public HttpMessageHandler CreateHandler(string name) => http.CreateAnchoredHandler(exempt);
    }
}
