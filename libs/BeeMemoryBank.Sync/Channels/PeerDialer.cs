using System.Security.Authentication;
using BeeMemoryBank.Core.Models;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync.Channels;

/// <summary>The address a session goes to, or why none answered.</summary>
/// <param name="Chosen">The candidate that answered with the right identity, key and TLS anchor; null when none did.</param>
/// <param name="Failure">The failure that explains it best when none did (a wrong peer before a timeout, a timeout before nothing).</param>
public sealed record DialResult(ChannelCandidate? Chosen, Exception? Failure);

/// <summary>
/// Picks the address of a session with one peer (docs/design/lan-peer-sync.md section 7): asks <c>/api/sync/identity</c> at the candidates of a
/// <see cref="ChannelPlan"/> in its order - the one that worked last, then the LAN ones all at once (each started <see cref="Stagger"/> after the one
/// before, each with <see cref="LanTimeout"/>), then the rest one by one - and takes the first that answers with the peer's node id and key, over TLS
/// its anchor accepts (the client the caller passes carries the anchor on every request: <c>PeerAnchorStamp</c>). A wrong peer at an address is never
/// taken, and is not asked again for <see cref="ChannelPlanner.WrongPeerPause"/>. What each attempt gave is written to <see cref="PeerChannelStore"/>.
/// <para>With ONE dialable address nothing is asked here: the session itself is the attempt (no extra request for a peer with one address).</para>
/// </summary>
public sealed class PeerDialer(PeerChannelStore store, ILogger<PeerDialer>? logger = null)
{
    /// <summary>How long a LAN or VPN address has to answer: a node on the same network answers in milliseconds, a dead address must not cost more.</summary>
    public static TimeSpan LanTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long an internet address has to answer the identity question.</summary>
    public static TimeSpan InternetTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>The pause between the starts of two LAN attempts (Happy Eyeballs).</summary>
    public static TimeSpan Stagger { get; set; } = TimeSpan.FromMilliseconds(250);

    public async Task<DialResult> DialAsync(HttpClient http, WhitelistEntry peer, ChannelPlan plan, CancellationToken ct)
    {
        var dialable = plan.Dialable;
        if (dialable.Count == 0) return new DialResult(null, null);
        if (dialable.Count == 1) return new DialResult(dialable[0], null);

        var failures = new List<(ChannelCandidate Candidate, Exception Error)>();
        foreach (var candidate in plan.First)
        {
            var (ok, error) = await ProbeAsync(http, peer, candidate, ct);
            if (ok) return new DialResult(candidate, null);
            failures.Add((candidate, error!));
        }

        if (plan.Parallel.Count > 0)
        {
            var winner = await RaceAsync(http, peer, plan.Parallel, failures, ct);
            if (winner is not null) return new DialResult(winner, null);
        }

        foreach (var candidate in plan.Sequential)
        {
            ct.ThrowIfCancellationRequested();
            var (ok, error) = await ProbeAsync(http, peer, candidate, ct);
            if (ok) return new DialResult(candidate, null);
            failures.Add((candidate, error!));
        }

        return new DialResult(null, Explain(failures.Select(f => f.Error)));
    }

    private async Task<ChannelCandidate?> RaceAsync(HttpClient http, WhitelistEntry peer, IReadOnlyList<ChannelCandidate> candidates,
        List<(ChannelCandidate, Exception)> failures, CancellationToken ct)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = new List<Task<(ChannelCandidate Candidate, bool Ok, Exception? Error)>>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var delay = TimeSpan.FromTicks(Stagger.Ticks * i);
            running.Add(Task.Run(async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, race.Token);
                    var (ok, error) = await ProbeAsync(http, peer, candidate, race.Token);
                    return (candidate, ok, error);
                }
                catch (OperationCanceledException ex) { return (candidate, false, (Exception?)ex); }
            }, CancellationToken.None));
        }

        ChannelCandidate? winner = null;
        while (running.Count > 0)
        {
            var done = await Task.WhenAny(running);
            running.Remove(done);
            var (candidate, ok, error) = await done;
            if (ok && winner is null)
            {
                winner = candidate;
                race.Cancel();   // the others are not needed; their cancellations are not failures of their addresses
                continue;
            }
            if (!ok && winner is null && error is not null) failures.Add((candidate, error));
        }
        return winner;
    }

    /// <summary>One identity question at one address, bounded by its scope's timeout; the outcome goes to the channel's row.</summary>
    private async Task<(bool Ok, Exception? Error)> ProbeAsync(HttpClient http, WhitelistEntry peer, ChannelCandidate candidate, CancellationToken ct)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(candidate.IsLanLike ? LanTimeout : InternetTimeout);
        try
        {
            var identity = await SyncAddress.ReadIdentityAsync(http, candidate.Url, bound.Token);
            if (identity.NodeId != peer.NodeId)
                throw new PeerIdentityMismatchException(peer.NodeId, identity.NodeId);
            if (peer.Ed25519PublicKey is { Length: 32 } && !PeerKey.Matches(peer.Ed25519PublicKey, identity.Ed25519PublicKeyB64))
                throw new PeerKeyMismatchException(peer.NodeId);
            return (true, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var kind = Classify(ex);
            try { await store.NoteFailAsync(peer.NodeId, candidate.Url, candidate.Source, candidate.Scope, kind, DateTime.UtcNow); }
            catch (Exception noteFailure) { logger?.LogDebug(noteFailure, "Could not note a failed channel of {NodeId}", peer.NodeId); }
            logger?.LogDebug("Channel {Source} of {NodeId} did not answer as the peer ({Kind})", candidate.Source, peer.NodeId, kind);
            // A timeout of the probe is "no answer" for the loop (its growing pause), like a connection that was never made.
            return (false, ex is OperationCanceledException
                ? new HttpRequestException("The address did not answer in time.", new TimeoutException("The address did not answer in time.", ex))
                : ex);
        }
    }

    /// <summary>The kind of a channel's failure (<see cref="ChannelFailures"/>).</summary>
    public static string Classify(Exception ex) => ex switch
    {
        PeerIdentityMismatchException or PeerKeyMismatchException => ChannelFailures.WrongPeer,
        HttpRequestException { InnerException: AuthenticationException } => ChannelFailures.WrongPeer,
        AuthenticationException => ChannelFailures.WrongPeer,
        NoSyncServerException => ChannelFailures.NotSyncServer,
        HttpRequestException { StatusCode: not null } => ChannelFailures.Error,
        _ => ChannelFailures.NoAnswer,
    };

    /// <summary>The failure worth reporting when no address answered: one that says something (a wrong peer, an answer) before a bare timeout.</summary>
    private static Exception? Explain(IEnumerable<Exception> failures)
    {
        var list = failures.ToList();
        return list.FirstOrDefault(e => Classify(e) == ChannelFailures.WrongPeer)
            ?? list.FirstOrDefault(e => Classify(e) is ChannelFailures.NotSyncServer or ChannelFailures.Error)
            ?? list.FirstOrDefault(e => e is HttpRequestException)
            ?? list.FirstOrDefault();
    }

    /// <summary>A session through <paramref name="candidate"/> went through: it is tried first next time.</summary>
    public async Task NoteSessionOkAsync(Guid peer, ChannelCandidate candidate)
    {
        try { await store.NoteOkAsync(peer, candidate.Url, candidate.Source, candidate.Scope, DateTime.UtcNow); }
        catch (Exception ex) { logger?.LogDebug(ex, "Could not note the channel that worked for {NodeId}", peer); }
    }

    /// <summary>A session through <paramref name="candidate"/> failed before it reached the peer (no connection, a wrong peer).</summary>
    public async Task NoteSessionFailedAsync(Guid peer, ChannelCandidate candidate, Exception ex)
    {
        try { await store.NoteFailAsync(peer, candidate.Url, candidate.Source, candidate.Scope, Classify(ex), DateTime.UtcNow); }
        catch (Exception noteFailure) { logger?.LogDebug(noteFailure, "Could not note the channel that failed for {NodeId}", peer); }
    }
}
