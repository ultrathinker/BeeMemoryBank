using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync.Channels;

/// <summary>
/// One session with one peer over its channels (docs/design/lan-peer-sync.md section 7), for every host that dials peers: the desktop node's sync loop
/// (<see cref="SyncScheduler"/>), and the phone's and the blind apps' own loops. Plan the peer's addresses, put the peer's TLS anchor on every request,
/// pick the address that answers as the peer, run the session there, and note how it went.
/// </summary>
public static class PeerSessions
{
    /// <summary>The plan of a peer's channels and its TLS anchor. A store that cannot be read leaves the primary address alone, as before.</summary>
    /// <param name="ignorePause">A person's "Check now": the addresses paused after a wrong answer are dialled too.</param>
    public static async Task<(ChannelPlan Plan, PeerTlsAnchor? Anchor)> PlanAsync(IServiceProvider sp, WhitelistEntry node, ILogger? logger = null,
        bool ignorePause = false)
    {
        var now = DateTime.UtcNow;
        var local = sp.GetService<ILocalNetworks>()?.Current() ?? [];
        PeerEndpointRecord? announced = null;
        IReadOnlyList<PeerChannelRow> rows = [];
        try
        {
            // A blind node never announces (it authors nothing): its address is its pairing's, whatever a row says.
            if (!BlindNodeId.IsBlind(node.NodeId) && sp.GetService<PeerEndpointStore>() is { } endpoints)
                announced = await endpoints.GetAsync(node.NodeId);
            if (sp.GetService<PeerChannelStore>() is { } channels)
                rows = await channels.GetAsync(node.NodeId);
        }
        catch (Exception ex) when (ex is not Core.Interfaces.VaultSealedException)
        {
            logger?.LogDebug(ex, "The channels of {NodeId} could not be read; its primary address is used", node.NodeId);
        }
        var discovered = BlindNodeId.IsBlind(node.NodeId) ? [] : sp.GetService<IPeerDiscovery>()?.Recent(node.NodeId) ?? [];
        var plan = ChannelPlanner.Plan(node, announced, discovered, rows, local, now, ignorePause);
        return (plan, PeerTlsAnchor.For(node, announced));
    }

    /// <summary>The address of the session: the dialler's pick among several, or the only one. Throws the failure that explains it best when none answered.</summary>
    public static async Task<ChannelCandidate> ChooseAsync(PeerDialer? dialer, HttpClient http, WhitelistEntry node, ChannelPlan plan, CancellationToken ct)
    {
        if (plan.Dialable.Count == 0) throw new HttpRequestException("This device has no address this node can call now.");
        if (dialer is null) return plan.Dialable[0];
        var dial = await dialer.DialAsync(http, node, plan, ct);
        return dial.Chosen ?? throw (dial.Failure ?? new HttpRequestException("No address of this device answered."));
    }

    /// <summary>
    /// The client of one session with one peer: the pinned sync client (<paramref name="clientName"/>) with the peer's TLS anchor on every request, so
    /// whichever address is dialled is held to THIS peer's keys. Without an anchor, or without a handler factory (a test's own client factory), the
    /// caller's client and the lookup by address are used, as before, and the session is not <see cref="Session.Anchored"/>: only the addresses a
    /// person gave are dialled then (<see cref="Usable"/>). Dispose the result after the session.
    /// </summary>
    public static Session ClientFor(object? handlerFactory, HttpClient fallback, PeerTlsAnchor? anchor, string clientName = SyncScheduler.HttpClientName)
    {
        if (anchor is null || handlerFactory is not IHttpMessageHandlerFactory handlers) return new Session(fallback, owned: false, anchored: false);
        var client = new HttpClient(new PeerAnchorStamp(anchor) { InnerHandler = handlers.CreateHandler(clientName) }, disposeHandler: false)
        {
            Timeout = fallback.Timeout,
        };
        return new Session(client, owned: true, anchored: true);
    }

    /// <summary>The addresses of <paramref name="plan"/> this session may dial: all of them when its client carries the peer's anchor, else those a person gave.</summary>
    public static ChannelPlan Usable(ChannelPlan plan, Session session) => session.Anchored ? plan : plan.WithoutAutomatic();

    /// <summary>
    /// The whole session for a host that has no loop of its own around it (the phone, a blind app): plan, anchor, pick, sync, note. Returns the events
    /// applied. Throws what the session threw; the address it went to has its failure noted when it failed before the peer answered.
    /// </summary>
    public static async Task<int> SyncWithPeerAsync(IServiceProvider sp, HttpClient http, WhitelistEntry node, CancellationToken ct, ILogger? logger = null)
    {
        var (plan, anchor) = await PlanAsync(sp, node, logger);
        sp.GetService<PeerReachability>()?.NotePlan(node.NodeId, plan, DateTime.UtcNow);
        var dialer = sp.GetService<PeerDialer>();
        using var session = ClientFor(sp.GetService<IHttpMessageHandlerFactory>(), http, anchor);
        plan = Usable(plan, session);
        var chosen = await ChooseAsync(dialer, session.Client, node, plan, ct);
        try
        {
            var applied = await sp.GetRequiredService<SyncClient>().SyncWithPeerAsync(session.Client, chosen.Url, node.NodeId, ct);
            if (dialer is not null) await dialer.NoteSessionOkAsync(node.NodeId, chosen);
            sp.GetService<PeerReachability>()?.NoteDirect(node.NodeId, chosen.Scope, DateTime.UtcNow);
            return applied;
        }
        catch (Exception ex) when (SyncScheduler.IsUnreachable(ex, ct))
        {
            if (dialer is not null) await dialer.NoteSessionFailedAsync(node.NodeId, chosen, ex);
            if (plan.Dialable.Any(c => c.IsLanLike)) sp.GetService<IPeerDiscovery>()?.Prompt();
            throw;
        }
    }

    /// <summary>A session's client, disposed only when it was made for the session.</summary>
    public readonly struct Session(HttpClient client, bool owned, bool anchored) : IDisposable
    {
        public HttpClient Client { get; } = client;

        /// <summary>Every request of the client carries the peer's TLS anchor.</summary>
        public bool Anchored { get; } = anchored;

        public void Dispose() { if (owned) Client.Dispose(); }
    }
}
