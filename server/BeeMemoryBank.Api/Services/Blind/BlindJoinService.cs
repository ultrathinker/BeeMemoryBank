using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Security.Cryptography;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Metadata;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// A join this node refuses or cannot make now; the message is written for the operator and carries no secret. <see cref="Status"/> is the HTTP status of the local route.
/// <see cref="KeepsJoin"/> is true when the redemption may be held by the full node although this node did not get its answer (a dropped connection, a timeout, a proxy's 5xx): the
/// record and the secret are kept, and only a definite refusal, the owner's time running out or the operator's cancel ends the join.
/// </summary>
public sealed class BlindJoinException(string message, int status = 409, bool keepsJoin = false) : InvalidOperationException(message)
{
    public int Status { get; } = status;

    public bool KeepsJoin { get; } = keepsJoin;
}

/// <summary>
/// The sentences of the join. The wording of the apps' invitation flow (<c>BlindInvitationWords</c> in the shared app library, which a server node does not reference) is copied here
/// where the same thing is said, so that a person who joined from a phone and one who joins from a terminal read the same words; "this app" becomes "this node".
/// </summary>
public static class BlindJoinWords
{
    public const string Malformed = "This is not an invitation. Copy the whole line that begins with bmb-invite: again.";
    public const string OtherCode = "This is a connection code of the old way, not an invitation. A server node shows its own pair code with: bmb blind pair-code";
    public const string UnknownVersion = "This invitation was made by a newer Bee Memory Bank than this node understands. Update this node, then use the invitation again.";
    public const string UnknownTrust = "This invitation asks for a way of checking the computer that this node does not know. Update this node, then use the invitation again.";
    public const string Expired = "This invitation has expired. Ask for a new one.";

    /// <summary>The full node is older than the invitations of a server node (it refuses the request as a whole, or has no invitations at all).</summary>
    public const string OlderHub =
        "The owner's Bee Memory Bank is too old to take this node's invitation: upgrade the full node first (2.7 or later), or pair with a code the old way.";

    public const string AlreadyAttached =
        "This node is already attached to a full node: remove it there first (the Blind nodes list of that full node), or wipe this node with: bmb blind wipe";

    public static string NotValid(string source) =>
        $"This invitation is not valid any more: it expired, was already used, or was withdrawn. Ask {source} for a new one.";

    public static string HubMismatch(string host) =>
        $"The computer at {host} did not answer with the identity the invitation names. Nothing was sent. Ask for a new invitation.";

    public static string CertificateRefused(string host) =>
        $"The certificate of {host} is not the one this invitation names. Nothing was sent. Ask for a new invitation.";

    public static string KnownUnderOtherKey(string host) =>
        $"{host} already knows a node with this name under another key. Nothing was changed. Ask for a new invitation.";

    public static string NotUnderstood(string host) =>
        $"{host} did not understand the request of this node. Update this node and try again.";

    public static string Unreachable(string host) => $"Could not reach {host}. Try again in a moment.";

    /// <summary>The request was sent and its answer did not arrive: {host} may hold it. The join is kept; the same invitation again, or the status, finds out.</summary>
    public static string AnswerLost(string host) =>
        $"The answer of {host} did not arrive. If it got this node's request it waits for its owner now. Look at where it stands with: bmb blind join --status (it asks {host}); run the same command again with the same invitation, which never loses the join; or give it up with: bmb blind join --cancel";

    /// <summary>Said after the refusal of a repeat: the first request of the join may be held by the full node, and only the status question or the owner's time ends it.</summary>
    public const string FirstRequestMayStand =
        "This does not end the join: the first request may still be held by the full node. Look at where it stands with: bmb blind join --status, or give it up with: bmb blind join --cancel";

    public static string Stopped => "This node is being wiped, so the join was stopped. Nothing of it is kept.";

    public static string Locked => "The owner's Bee Memory Bank is locked right now. Try again; the invitation is still valid until it runs out.";

    public static string AskedToWait(string host) => $"{host} asked this node to wait. Try again in a moment.";

    public static string ClockOff(TimeSpan hubMinusNode, string host)
    {
        var minutes = (int)Math.Round(Math.Abs(hubMinusNode.TotalMinutes));
        var direction = hubMinusNode > TimeSpan.Zero ? "behind" : "ahead of";
        return $"This node's clock is {minutes} minutes off ({direction} {host}'s). Correct the date and time on this computer; the question is asked again then.";
    }

    /// <summary>What the status says while the answer to the redemption is still missing.</summary>
    public static string AnswerPending(string host) =>
        $"This node sent its request to {host} and has not had the answer yet. Run the same command again with the same invitation, or give the join up with: bmb blind join --cancel";

    public static string WaitingFor(string name, string matchCode) => $"Waiting for {name} to confirm. Read this code out to them: {matchCode}";

    public static string Refused(string name) => $"{name} refused this node. Ask for a new invitation if this was a mistake.";

    public const string ExpiredCard = "The invitation ran out before the owner confirmed it. Ask for a new one.";

    public static string Confirmed(string name) => $"{name} confirmed this node and sent it the data. It is attached now.";

    public static string ValidFor(DateTimeOffset expiresAt, DateTimeOffset now)
    {
        var left = expiresAt - now;
        if (left <= TimeSpan.Zero) return "This invitation has expired.";
        if (left < TimeSpan.FromMinutes(1)) return "This invitation works for less than a minute more.";
        var minutes = (int)Math.Round(left.TotalMinutes);
        return minutes == 1 ? "This invitation works for about a minute more." : $"This invitation works for about {minutes} minutes more.";
    }
}

/// <summary>What <c>bmb blind join</c> shows before it does anything: read from the invitation text alone, no network. <paramref name="Limits"/> is what the node will start with.</summary>
public sealed record BlindJoinPreview(string SourceName, string Address, string Trust, DateTime ExpiresAt, string Validity, string Limits);

/// <summary>Where the join stands, for the CLI: <paramref name="State"/> is a <see cref="BlindJoinStates"/> word; <paramref name="Message"/> a sentence.</summary>
public sealed record BlindJoinView(string State, string? MatchCode, string? SourceName, string? Address, DateTime? ConfirmBy, string Message);

/// <summary>
/// A blind node joins a full node with an invitation (release 2.7, <c>bmb blind join</c>; docs/sync.md "Invitations"). The node is the one source of its keys: the CLI hands it the
/// invitation text over the local console API and this service does the rest - the identity probe of the hub the invitation names, the redemption with the node's own pair code
/// (the same code <c>bmb blind pair-code</c> shows, with a secret that stays valid until the owner's time runs out) instead of a backup key, signed with the node's identity key,
/// and the asking for the owner's decision with a signed status request. When the owner confirms, the full node adds and seeds this node exactly as if its pair code had been pasted
/// on the Blind nodes page; the seed arrives at the node's own seed route and spends the secret.
///
/// <para>The full node commits a redemption BEFORE it answers, and an answer can be lost (a dropped connection, a timeout) or the node can die right after: so the node writes its
/// record (<see cref="BlindJoinStates.Redeeming"/>) and holds the secret it gave the full node BEFORE the request leaves. Only a definite refusal of the full node, the owner's
/// time running out or the operator's cancel ends a join and forgets the secret; an answer that did not arrive leaves it as it is, and the same invitation again (which the full
/// node answers with the same answer), the status question or the seed itself carries it on. The invitation text is never stored.</para>
///
/// <para>A wipe takes the join service (<see cref="QuiesceAsync"/>): the join in flight is cancelled and awaited, a new one is refused until the wipe is over, so a join that
/// waited for the full node cannot write anything after the wipe.</para>
///
/// <para>One way of being attached at a time: a node that already holds a full node's seed refuses to join (<see cref="BlindJoinWords.AlreadyAttached"/>), and while a join waits for
/// the owner the pair code is not shown (<see cref="BlindJoinInProgressException"/>). Nothing here writes an invitation's secret anywhere: it is a parameter of the one call that
/// sends it. An older full node refuses the request as a whole (it does not know the member), and that is told as "upgrade the full node first".</para>
/// </summary>
public sealed class BlindJoinService(
    IServiceScopeFactory scopeFactory,
    IHttpMessageHandlerFactory handlers,
    BlindState state,
    BlindJoinStore store,
    BlindPairing pairing,
    IConfiguration config,
    TimeProvider time,
    ILogger<BlindJoinService> logger)
{
    /// <summary>The pairing secret of a join stays valid this long: the hub's confirmation window (72 hours) and an hour to spare.</summary>
    public static readonly TimeSpan PairingLifetime = TimeSpan.FromHours(73);

    /// <summary>The most often this node asks the hub where the owner's decision stands, however often the local route is asked.</summary>
    public static readonly TimeSpan MinAskInterval = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(5);

    /// <summary>The full node's confirmation window (<c>BlindInviteService.ConfirmWindow</c>, 72 hours), which the node assumes until the full node's own answer says when it ends.</summary>
    private static readonly TimeSpan HubConfirmWindow = TimeSpan.FromHours(72);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastAsked = DateTimeOffset.MinValue;
    private BlindJoinView? _lastView;

    // The wipe's barrier. _stop cancels what is admitted now; _generation tells an operation that queued before the wipe that the node it was asked about is gone.
    private readonly object _barrier = new();
    private CancellationTokenSource _stop = new();
    private long _generation;
    private bool _quiesced;

    /// <summary>
    /// Stops the join for a wipe: new calls are refused, the one in flight (a probe or a redemption waiting for the full node, a status question) is cancelled, and this returns when it
    /// has left, holding the service until the barrier is disposed. Nothing of a join is written between the return and the disposal. Throws <see cref="TimeoutException"/> if the
    /// join does not leave within <paramref name="timeout"/> (the service is open again then).
    /// </summary>
    public async Task<IDisposable> QuiesceAsync(TimeSpan timeout, CancellationToken ct)
    {
        CancellationTokenSource stopping;
        lock (_barrier)
        {
            _quiesced = true;
            stopping = _stop;
        }
        stopping.Cancel();
        bool entered;
        try
        {
            entered = await _gate.WaitAsync(timeout, ct);
        }
        catch
        {
            Reopen();
            throw;
        }
        if (!entered)
        {
            Reopen();
            throw new TimeoutException("A join did not stop in time.");
        }
        return new Closed(this);
    }

    private void Reopen()
    {
        lock (_barrier)
        {
            _quiesced = false;
            if (_stop.IsCancellationRequested) _stop = new CancellationTokenSource();
        }
    }

    private sealed class Closed(BlindJoinService service) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (service._barrier) service._generation++;
            service._lastView = null;
            service._lastAsked = DateTimeOffset.MinValue;
            service.Reopen();
            service._gate.Release();
        }
    }

    /// <summary>
    /// Takes the gate for one call: refused while a wipe holds the service, cancelled when a wipe starts, and refused if the node was wiped while the call queued. The caller disposes
    /// the returned scope; its token is the caller's linked with the wipe's.
    /// </summary>
    private async Task<Call> EnterAsync(CancellationToken ct)
    {
        CancellationTokenSource stop;
        long generation;
        lock (_barrier)
        {
            if (_quiesced) throw new BlindJoinException(BlindJoinWords.Stopped, 503);
            stop = _stop;
            generation = _generation;
        }
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        try
        {
            await _gate.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            linked.Dispose();
            throw new BlindJoinException(BlindJoinWords.Stopped, 503);
        }
        catch
        {
            linked.Dispose();
            throw;
        }
        bool refused;
        lock (_barrier) refused = _quiesced || generation != _generation;
        if (refused)
        {
            _gate.Release();
            linked.Dispose();
            throw new BlindJoinException(BlindJoinWords.Stopped, 503);
        }
        return new Call(this, linked, ct);
    }

    private sealed class Call(BlindJoinService service, CancellationTokenSource linked, CancellationToken caller) : IDisposable
    {
        public CancellationToken Token => linked.Token;

        /// <summary>True when the wipe, not the caller, cancelled the call.</summary>
        public bool Stopped => linked.IsCancellationRequested && !caller.IsCancellationRequested;

        public void Dispose()
        {
            linked.Dispose();
            service._gate.Release();
        }
    }

    // ── what the text says ────────────────────────────────────────────────

    /// <summary>The invitation text as the preview of the command shows it, or a refusal with the sentence for the person. No network, no write.</summary>
    public BlindJoinPreview Preview(string? text)
    {
        var invitation = Read(text);
        var now = time.GetUtcNow();
        return new BlindJoinPreview(invitation.SourceName, invitation.Address, invitation.Trust, invitation.ExpiresAt.UtcDateTime,
            BlindJoinWords.ValidFor(invitation.ExpiresAt, now), "None: a server node keeps what the full node sends it, with no limit of its own.");
    }

    /// <summary>Parses and checks the text: shape, version, trust, the hub's signature, expiry. The refusals use the apps' sentences.</summary>
    private BlindInvitation Read(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0) throw new BlindJoinException(BlindJoinWords.Malformed, 400);
        if (!BlindInvitation.TryParse(trimmed, out var invitation))
            throw new BlindJoinException(WhyNot(trimmed), 400);
        if (invitation.IsExpired(time.GetUtcNow())) throw new BlindJoinException(BlindJoinWords.Expired, 400);
        return invitation;
    }

    /// <summary>Why a text is not an invitation this node takes, as far as its own words can say (the same distinctions the apps make).</summary>
    private static string WhyNot(string text)
    {
        if (text.StartsWith("BMBBLIND1.", StringComparison.Ordinal) || text.StartsWith("bmb-blind-call:", StringComparison.Ordinal) || text.StartsWith("bmb-blind-phone:", StringComparison.Ordinal))
            return BlindJoinWords.OtherCode;
        const string prefix = "bmb-invite:?";
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) return BlindJoinWords.Malformed;
        string? version = null, trust = null;
        foreach (var part in text[prefix.Length..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq];
            var value = part[(eq + 1)..];
            if (key == "v") version = value;
            else if (key == "t") trust = value;
        }
        if (version is not null && version != "1") return BlindJoinWords.UnknownVersion;
        if (trust is not null && !BlindTrust.IsKnown(trust)) return BlindJoinWords.UnknownTrust;
        return BlindJoinWords.Malformed;
    }

    // ── start ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Makes the join: the probe, the redemption with this node's pair code, and the match code for the friend to read out. The same invitation again while its join waits (or while
    /// the answer to its redemption is missing) is the same join, with the same pair code; another invitation while one waits is refused.
    /// </summary>
    public async Task<BlindJoinView> StartAsync(string? text, CancellationToken ct)
    {
        var invitation = Read(text);
        using var call = await EnterAsync(ct);
        try
        {
            return await StartCoreAsync(invitation, call.Token);
        }
        catch (OperationCanceledException) when (call.Stopped)
        {
            throw new BlindJoinException(BlindJoinWords.Stopped, 503);
        }
    }

    private async Task<BlindJoinView> StartCoreAsync(BlindInvitation invitation, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var self = await sp.GetRequiredService<INodeIdentityRepository>().GetAsync()
            ?? throw new BlindJoinException("This node is not initialized yet.", 503);

        var current = await BlindJoinRecord.InProgressAsync(store, state, time);
        if (current is not null && current.InviteId != invitation.InviteIdText)
            throw new BlindJoinException(
                $"This node already waits for the answer to an invitation from {current.SourceName}. Wait for it, or give it up first with: bmb blind join --cancel", 409);
        if (current is null && await IsAttachedAsync(sp, self.NodeId))
            throw new BlindJoinException(BlindJoinWords.AlreadyAttached, 409);

        var host = new Uri(invitation.Address).Authority;
        using var client = Client(invitation.Trust == BlindTrust.Pin ? invitation.SpkiPin : null);

        // The computer at the address must be the hub the invitation names (its id AND its key) before anything is sent to it.
        await ProbeAsync(client, invitation, host, ct);

        // The full node dials this node to send it the data: the address it will use is in the pair code, so the node must know its own.
        if (string.IsNullOrWhiteSpace(config["BMB_PUBLIC_ADDRESS"]))
            throw new BlindJoinException(
                "BMB_PUBLIC_ADDRESS is not set on this node: the full node needs the public https address to reach it (https://host:port) to send it the data. Set it and restart the node.", 409);
        var code = await pairing.IssueJoinCodeAsync(PairingLifetime, reuseCurrent: current is not null);

        // The record first, then the request: the full node commits the redemption before it answers, and a node that cannot find its join afterwards cannot take the seed that follows
        // the owner's confirmation. An earlier answer of this very join (the owner has not decided yet) stays as it is until the new one replaces it.
        if (current is not { State: BlindJoinStates.Awaiting })
        {
            var started = time.GetUtcNow();
            await store.WriteAsync(current ?? new BlindJoinRecord(
                invitation.InviteIdText, invitation.Address, invitation.HubNodeId, invitation.Trust, invitation.SpkiPin, invitation.SourceName,
                BlindInviteMatchCode.Compute(invitation.InviteId, self.NodeId, self.Ed25519PublicKey),
                (started + HubConfirmWindow).ToUnixTimeSeconds(), BlindJoinStates.Redeeming, started.ToUnixTimeSeconds()), ct);
        }

        BlindInviteRedeemAnswer answer;
        try
        {
            answer = await RedeemAsync(sp, client, invitation, self, code.ToString(), host, ct);
            if (answer.MatchCode != BlindInviteMatchCode.Compute(invitation.InviteId, self.NodeId, self.Ed25519PublicKey))
                throw new BlindJoinException(BlindJoinWords.NotUnderstood(host), 502);
        }
        catch (BlindJoinException ex) when (!ex.KeepsJoin)
        {
            // A definite refusal of THIS request. With no earlier request (current is null) the full node holds nothing for this node, so the secret it would have used goes with
            // the record. A repeat of a join that already has a record is different: an earlier request may be held by the full node (its answer was lost), and what the full node
            // says to the repeat - the row is being confirmed (410), its session is locked (503), too many calls (429) - says nothing about that one. The record and the secret stay;
            // the status question, the owner's time running out or --cancel end the join.
            if (current is null)
            {
                store.Clear();
                await pairing.ForgetJoinSecretAsync();
                throw;
            }
            if (current.State == BlindJoinStates.Redeeming)
                throw new BlindJoinException($"{ex.Message} {BlindJoinWords.FirstRequestMayStand}", ex.Status, keepsJoin: true);
            throw;
        }
        // Any other way out (a lost answer, a timeout, the caller going away) leaves the record and the secret as they are: see the class summary.

        var record = new BlindJoinRecord(invitation.InviteIdText, invitation.Address, invitation.HubNodeId, invitation.Trust, invitation.SpkiPin, answer.SourceName,
            answer.MatchCode, answer.ConfirmByUnix, BlindJoinStates.Awaiting, current?.StartedAtUnix ?? time.GetUtcNow().ToUnixTimeSeconds());
        await store.WriteAsync(record, CancellationToken.None);
        logger.LogInformation("Joined a full node with an invitation ({Invite}); waiting for the owner", invitation.InviteIdText[..Math.Min(6, invitation.InviteIdText.Length)]);
        return _lastView = ViewOf(record);
    }

    // ── where it stands ───────────────────────────────────────────────────

    /// <summary>
    /// Where the join stands, asking the hub for the owner's decision when it is time (never more often than <see cref="MinAskInterval"/>). A join that ended says how, once; a node
    /// that was seeded in the meantime is confirmed whatever the hub says.
    /// </summary>
    public async Task<BlindJoinView> StatusAsync(CancellationToken ct)
    {
        using var call = await EnterAsync(ct);
        try
        {
            return await StatusCoreAsync(call.Token);
        }
        catch (OperationCanceledException) when (call.Stopped)
        {
            throw new BlindJoinException(BlindJoinWords.Stopped, 503);
        }
    }

    private async Task<BlindJoinView> StatusCoreAsync(CancellationToken ct)
    {
        if (store.Read() is not { } record)
            return new BlindJoinView(BlindJoinStates.None, null, null, null, null, "No join was made on this node.");

        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var self = await sp.GetRequiredService<INodeIdentityRepository>().GetAsync();
        var attached = self is not null && await IsAttachedAsync(sp, self.NodeId);
        if (record.State == BlindJoinStates.Confirmed && !attached)
        {
            // A node that was wiped after it joined is no longer attached: the old record is not its story any more.
            store.Clear();
            return new BlindJoinView(BlindJoinStates.None, null, null, null, null, "No join was made on this node.");
        }
        if (record.State is not (BlindJoinStates.Awaiting or BlindJoinStates.Redeeming)) return ViewOf(record);
        if (attached) return ViewOf(await EndAsync(record, BlindJoinStates.Confirmed));
        // Run out - or the secret it gave the full node is gone (the node was wiped, or the secret ran out): nothing can arrive any more.
        if (await BlindJoinRecord.InProgressAsync(store, state, time) is null)
            return ViewOf(await EndAsync(record, BlindJoinStates.Expired));
        if (self is null) return ViewOf(record);

        var now = time.GetUtcNow();
        if (_lastView is not null && now - _lastAsked < MinAskInterval) return _lastView;
        _lastAsked = now;
        return _lastView = await AskAsync(sp, record, self, ct);
    }

    private async Task<BlindJoinView> AskAsync(IServiceProvider sp, BlindJoinRecord record, NodeIdentity self, CancellationToken ct)
    {
        var host = new Uri(record.Address).Authority;
        var inviteId = Base64Url.DecodeFromChars(record.InviteId);
        var at = time.GetUtcNow().ToUnixTimeSeconds();
        var signature = sp.GetRequiredService<INodeAuthSigner>().SignChallenge(self, BlindInviteProofs.StatusMessage(inviteId, self.NodeId, at));
        var request = new BlindInviteStatusRequest(inviteId, self.NodeId, at, signature);

        using var client = Client(record.Trust == BlindTrust.Pin ? record.Pin : null);
        try
        {
            using var content = new ByteArrayContent(request.ToJson());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await client.PostAsync(Route(record.Address, "status"), content, ct);
            switch ((int)response.StatusCode)
            {
                case 200:
                    var body = await ReadBodyAsync(response, ct);
                    if (body is null || !BlindInviteStatusAnswer.TryParse(body, out var answer))
                        return Said(record, BlindJoinWords.NotUnderstood(host));
                    return answer.State switch
                    {
                        BlindInviteStates.Confirmed => ViewOf(await EndAsync(record, BlindJoinStates.Confirmed)),
                        BlindInviteStates.Refused => ViewOf(await EndAsync(record, BlindJoinStates.Refused)),
                        BlindInviteStates.Expired => ViewOf(await EndAsync(record, BlindJoinStates.Expired)),
                        // The full node knows the redemption and the owner has not decided: a redemption whose answer was lost is found.
                        _ when record.State == BlindJoinStates.Redeeming => ViewOf(await PromoteAsync(record)),
                        _ => ViewOf(record),
                    };
                case 410:
                    // One answer for everything the hub does not know. Past the point where it could be a clock problem (a signed question is only taken within minutes of the hub's
                    // clock) it is final. For a redemption whose answer never came it means the request did not reach the full node (or the invitation is gone): the join is over.
                    if (response.Headers.Date is { } hubNow && (hubNow - time.GetUtcNow()).Duration() > ClockTolerance)
                        return Said(record, BlindJoinWords.ClockOff(hubNow - time.GetUtcNow(), host));
                    var ended = await EndAsync(record, BlindJoinStates.Expired);
                    return record.State == BlindJoinStates.Redeeming
                        ? new BlindJoinView(ended.State, null, ended.SourceName, ended.Address, ended.ConfirmBy, BlindJoinWords.NotValid(ended.SourceName))
                        : ViewOf(ended);
                case 429:
                    return Said(record, BlindJoinWords.AskedToWait(host));
                case 503:
                    return Said(record, BlindJoinWords.Locked);
                case >= 500:
                    return Said(record, BlindJoinWords.Unreachable(host));
                default:
                    return Said(record, BlindJoinWords.NotUnderstood(host));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return Said(record, RefusedByTls(ex) ? BlindJoinWords.CertificateRefused(host) : BlindJoinWords.Unreachable(host));
        }
    }

    /// <summary>The operator gives the join up: the secret the full node was given is no longer valid, and the old pairing is allowed again. The full node's row stays until its owner refuses it or it runs out.</summary>
    public async Task<BlindJoinView> CancelAsync(CancellationToken ct)
    {
        using var call = await EnterAsync(ct);
        var record = store.Read();
        if (record is null) return new BlindJoinView(BlindJoinStates.None, null, null, null, null, "No join was made on this node.");
        if (record.State is BlindJoinStates.Awaiting or BlindJoinStates.Redeeming)
        {
            await pairing.ForgetJoinSecretAsync();
            store.Clear();
            _lastView = null;
            return new BlindJoinView(BlindJoinStates.None, null, record.SourceName, record.Address, null,
                $"The join was given up. If {record.SourceName}'s owner confirms it anyway, the full node cannot add this node; ask them to refuse it.");
        }
        store.Clear();
        return new BlindJoinView(BlindJoinStates.None, null, null, null, null, "The record of the last join was removed.");
    }

    // ── the calls ─────────────────────────────────────────────────────────

    private async Task ProbeAsync(HttpClient client, BlindInvitation invitation, string host, CancellationToken ct)
    {
        try
        {
            var identity = await SyncAddress.ReadIdentityAsync(client, invitation.Address, ct);
            if (identity.NodeId != invitation.HubNodeId || !PeerKey.Matches(invitation.HubPublicKey, identity.Ed25519PublicKeyB64))
                throw new BlindJoinException(BlindJoinWords.HubMismatch(host), 502);
        }
        catch (NoSyncServerException)
        {
            throw new BlindJoinException(BlindJoinWords.HubMismatch(host), 502);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new BlindJoinException(RefusedByTls(ex) ? BlindJoinWords.CertificateRefused(host) : BlindJoinWords.Unreachable(host), 502);
        }
    }

    private async Task<BlindInviteRedeemAnswer> RedeemAsync(IServiceProvider sp, HttpClient client, BlindInvitation invitation, NodeIdentity self, string pairCode, string host, CancellationToken ct)
    {
        var signature = sp.GetRequiredService<INodeAuthSigner>().SignChallenge(self,
            BlindInviteProofs.ServerRedeemMessage(invitation.InviteId, invitation.HubNodeId, self.NodeId, self.Ed25519PublicKey, pairCode));
        var displayName = BlindInviteText.IsPlain(self.DisplayName, BlindPhoneCode.MaxDisplayNameLength) ? self.DisplayName : "Blind node";
        var request = new BlindInviteRedeemRequest(
            invitation.InviteId, invitation.Secret, self.NodeId, self.Ed25519PublicKey, displayName,
            BlindInviteRedeemRequest.TokenOf(NodePlatform.Current, BlindInviteRedeemRequest.MaxPlatformLength),
            BlindInviteRedeemRequest.TokenOf(AppVersion.Current, BlindInviteRedeemRequest.MaxVersionLength), SyncProtocolVersion.Current, null, signature, pairCode);

        var body = request.ToJson();
        try
        {
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await client.PostAsync(Route(invitation.Address, "redeem"), content, ct);
            switch ((int)response.StatusCode)
            {
                case 202:
                    var answerBody = await ReadBodyAsync(response, ct);
                    // 202 is the full node's "committed": a body that cannot be read leaves the redemption held there and this node without its answer.
                    if (answerBody is null || !BlindInviteRedeemAnswer.TryParse(answerBody, out var answer))
                        throw new BlindJoinException(BlindJoinWords.AnswerLost(host), 502, keepsJoin: true);
                    return answer;
                case 410:
                    throw new BlindJoinException(BlindJoinWords.NotValid(invitation.SourceName), 410);
                case 409:
                    throw new BlindJoinException(BlindJoinWords.KnownUnderOtherKey(host), 409);
                case 429:
                    throw new BlindJoinException(BlindJoinWords.AskedToWait(host), 429);
                case 503:
                    throw new BlindJoinException(BlindJoinWords.Locked, 503);
                // 404/405: a full node older than 2.7 has no invitation routes. 400: a 2.7 full node that does not know a server node's redemption refuses the body as a whole.
                // Both mean the same thing for the operator.
                case 400 or 404 or 405:
                    throw new BlindJoinException(BlindJoinWords.OlderHub, 502);
                // A 5xx may be the full node failing after it committed, or a proxy in front of it giving up on a request that was served: the answer is missing, the redemption may not be.
                case >= 500:
                    throw new BlindJoinException(BlindJoinWords.AnswerLost(host), 502, keepsJoin: true);
                default:
                    throw new BlindJoinException(BlindJoinWords.NotUnderstood(host), 502);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // A refused certificate, or no connection at all, means nothing was sent: a definite "no". Anything else (a reset, a timeout, a body that stopped) may have come after the
            // full node read the request.
            if (RefusedByTls(ex)) throw new BlindJoinException(BlindJoinWords.CertificateRefused(host), 502);
            if (ex is HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError })
                throw new BlindJoinException(BlindJoinWords.Unreachable(host), 502);
            throw new BlindJoinException(BlindJoinWords.AnswerLost(host), 502, keepsJoin: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
        }
    }

    /// <summary>
    /// A client that reaches the hub by the trust the invitation names: the sync client of this node (its certificate check and its checked connect), with the invitation's pin
    /// stamped on every request when the trust is a pin. Nothing of a redirect, a proxy or an address a node may not call is followed.
    /// </summary>
    private HttpClient Client(string? pin) =>
        new(new PinStamp(pin) { InnerHandler = handlers.CreateHandler(SyncScheduler.HttpClientName) }, disposeHandler: false) { Timeout = CallTimeout };

    private sealed class PinStamp(string? pin) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (pin is not null) request.Options.Set(SpkiPinRegistry.ExplicitPin, pin);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private static string Route(string address, string name) => $"{address.TrimEnd('/')}/api/sync/invite/{name}";

    private static async Task<byte[]?> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        const int max = BlindInviteRedeemRequest.MaxBodyBytes;
        if (response.Content.Headers.ContentLength is > max) return null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(SyncAddress.BodyTimeout);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
            using var memory = new MemoryStream();
            var buffer = new byte[1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, limit.Token)) > 0)
            {
                if (memory.Length + read > max) return null;
                memory.Write(buffer, 0, read);
            }
            return memory.ToArray();
        }
        catch (Exception ex) when (ex is OperationCanceledException && !ct.IsCancellationRequested || ex is HttpRequestException or IOException)
        {
            return null;
        }
    }

    private static bool RefusedByTls(Exception ex)
    {
        for (var inner = ex; inner is not null; inner = inner.InnerException)
            if (inner is AuthenticationException) return true;
        return false;
    }

    // ── helpers ───────────────────────────────────────────────────────────

    /// <summary>True if this node holds a full node's seed: an active peer other than itself is in its whitelist (a node nobody has seeded has none).</summary>
    private static async Task<bool> IsAttachedAsync(IServiceProvider sp, Guid selfId) =>
        (await sp.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync()).Any(p => p.NodeId != selfId);

    /// <summary>The join ends (<paramref name="state"/>): written once, and the secret this node gave the full node is no longer valid unless the node was seeded with it.</summary>
    private async Task<BlindJoinRecord> EndAsync(BlindJoinRecord record, string newState)
    {
        var ended = record with { State = newState };
        await store.WriteAsync(ended);
        if (newState != BlindJoinStates.Confirmed) await pairing.ForgetJoinSecretAsync();
        _lastView = null;
        return ended;
    }

    /// <summary>The full node knows the redemption whose answer was lost: from here the join is the ordinary one that waits for the owner (the code to read out is the one this node computed itself).</summary>
    private async Task<BlindJoinRecord> PromoteAsync(BlindJoinRecord record)
    {
        var promoted = record with { State = BlindJoinStates.Awaiting };
        await store.WriteAsync(promoted);
        _lastView = null;
        return promoted;
    }

    private BlindJoinView Said(BlindJoinRecord record, string message) =>
        new(record.State, record.MatchCode, record.SourceName, record.Address, record.ConfirmBy, message);

    private BlindJoinView ViewOf(BlindJoinRecord record) => record.State switch
    {
        BlindJoinStates.Redeeming => new(record.State, record.MatchCode, record.SourceName, record.Address, record.ConfirmBy, BlindJoinWords.AnswerPending(new Uri(record.Address).Authority)),
        BlindJoinStates.Awaiting => new(record.State, record.MatchCode, record.SourceName, record.Address, record.ConfirmBy, BlindJoinWords.WaitingFor(record.SourceName, record.MatchCode)),
        BlindJoinStates.Confirmed => new(record.State, record.MatchCode, record.SourceName, record.Address, record.ConfirmBy, BlindJoinWords.Confirmed(record.SourceName)),
        BlindJoinStates.Refused => new(record.State, record.MatchCode, record.SourceName, record.Address, record.ConfirmBy, BlindJoinWords.Refused(record.SourceName)),
        _ => new(record.State, record.MatchCode, record.SourceName, record.Address, record.ConfirmBy, BlindJoinWords.ExpiredCard),
    };
}
