using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>A request the invitation hub has received and not answered yet: the test says when it does (or the caller gives up first).</summary>
internal sealed class InviteHold
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the request has reached the hub.</summary>
    public Task Entered => _entered.Task;

    public Task Gate => _gate.Task;

    public void MarkEntered() => _entered.TrySetResult();

    public void Release() => _gate.TrySetResult();
}

/// <summary>
/// A full node as the invitation client sees it, in memory, for the head's tests: the identity page, the redemption and the status of one or more invitations (the same rules as the real hub: the
/// first redemption wins, everything not valid is a 410), and the sync routes of <see cref="DesktopFakeHub"/> for what comes after. A request to a route can be held until the test lets the hub answer;
/// a hub that <see cref="IgnoresCancellation"/> goes on waiting after the caller gave up, as a socket that already sent its bytes does. It records every request.
/// </summary>
internal sealed class DesktopInviteHub : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly DesktopFakeHub _sync;
    private readonly HttpMessageInvoker _syncInvoker;
    private readonly TimeProvider _time;
    private readonly List<Invite> _invites = [];
    private readonly Dictionary<string, Queue<HubFailure>> _failures = [];
    private readonly Dictionary<string, Queue<InviteHold>> _holds = [];
    private readonly List<string> _calls = [];

    private sealed class Invite(byte[] id, byte[] secret, DateTimeOffset expiresAt, string sourceName)
    {
        public byte[] Id { get; } = id;
        public byte[] Secret { get; } = secret;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public string SourceName { get; } = sourceName;
        public string State { get; set; } = "issued";
        public Guid? Node { get; set; }
        public byte[]? Key { get; set; }
        public int Redemptions { get; set; }
    }

    /// <summary>How the next call to a route goes wrong.</summary>
    internal enum HubFailure
    {
        /// <summary>No connection: the request never reaches the hub.</summary>
        Unreachable,

        /// <summary>The hub is locked: 503.</summary>
        Locked,
    }

    public DesktopInviteHub(string host, TimeProvider? time = null)
    {
        _sync = new DesktopFakeHub(host);
        _syncInvoker = new HttpMessageInvoker(_sync, disposeHandler: false);
        _time = time ?? TimeProvider.System;
    }

    public Guid NodeId => _sync.NodeId;
    public string Origin => _sync.Origin;
    public DesktopFakeHub Sync => _sync;

    /// <summary>A request that reached the hub waits for its hold's release even when the caller cancels it.</summary>
    public bool IgnoresCancellation { get; set; }

    public IReadOnlyList<string> Calls { get { lock (_gate) return [.. _calls]; } }

    public int Count(string route) => Calls.Count(c => c == route);

    /// <summary>How many redemptions the hub has taken (a request that was cancelled before the hub answered took none).</summary>
    public int Redemptions { get { lock (_gate) return _invites.Sum(i => i.Redemptions); } }

    /// <summary>The next request to <paramref name="route"/> (<c>identity</c>, <c>redeem</c>, <c>status</c>) reaches the hub and waits, unanswered, until the returned hold is released.</summary>
    public InviteHold HoldNext(string route)
    {
        var hold = new InviteHold();
        lock (_gate)
        {
            if (!_holds.TryGetValue(route, out var queue)) _holds[route] = queue = new Queue<InviteHold>();
            queue.Enqueue(hold);
        }
        return hold;
    }

    public void Fail(string route, HubFailure failure)
    {
        lock (_gate)
        {
            if (!_failures.TryGetValue(route, out var queue)) _failures[route] = queue = new Queue<HubFailure>();
            queue.Enqueue(failure);
        }
    }

    /// <summary>The text the owner would copy from the Blind nodes page: signed by this hub's key, as the real hub always signs it.</summary>
    public string IssueText(string sourceName = "Alex", TimeSpan? lifetime = null)
    {
        var id = RandomNumberGenerator.GetBytes(BlindInvitation.InviteIdSize);
        var secret = RandomNumberGenerator.GetBytes(BlindInvitation.SecretSize);
        var expires = _time.GetUtcNow() + (lifetime ?? TimeSpan.FromMinutes(30));
        lock (_gate) _invites.Add(new Invite(id, [.. secret], expires, sourceName));
        return BlindInvitation.CreateSigned(Origin, NodeId, BlindTrust.Pin, _sync.Pin, _sync.PublicKey, id, secret, expires, sourceName, _sync.Sign).ToWireText();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var route = request.RequestUri!.AbsolutePath switch
        {
            "/api/sync/identity" => "identity",
            "/api/sync/invite/redeem" => "redeem",
            "/api/sync/invite/status" => "status",
            _ => "other",
        };
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (_gate) _calls.Add(route);

        InviteHold? held = null;
        lock (_gate)
        {
            if (_holds.TryGetValue(route, out var holds) && holds.Count > 0) held = holds.Dequeue();
        }
        if (held is not null)
        {
            held.MarkEntered();
            if (IgnoresCancellation) await held.Gate;
            else await held.Gate.WaitAsync(ct);
        }

        HubFailure? failure = null;
        lock (_gate)
        {
            if (_failures.TryGetValue(route, out var queue) && queue.Count > 0) failure = queue.Dequeue();
        }
        switch (failure)
        {
            case HubFailure.Unreachable:
                throw new HttpRequestException("no route to host");
            case HubFailure.Locked:
                return Answer(503, new { error = "locked" });
        }

        switch (route)
        {
            case "identity":
                return Answer(200, new { nodeId = NodeId, displayName = "Hub", ed25519PublicKeyB64 = Convert.ToBase64String(_sync.PublicKey), protocolVersion = 3 });
            case "redeem":
                return Redeem(body);
            case "status":
                return Status(body);
            default:
                return await _syncInvoker.SendAsync(Clone(request, body), ct);
        }
    }

    private HttpResponseMessage Redeem(string body)
    {
        if (!BlindInviteRedeemRequest.TryParse(Encoding.UTF8.GetBytes(body), out var request) || !BlindInviteProofs.RedeemProofIsValid(request, NodeId))
            return Answer(400, new { error = "bad_request" });
        lock (_gate)
        {
            var row = _invites.SingleOrDefault(i => i.Id.AsSpan().SequenceEqual(request.InviteId));
            if (row is null || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(row.Secret), SHA256.HashData(request.Secret))) return Answer(410, new { error = "invite_not_valid" });
            var same = row.State == "redeemed" && row.Node == request.NodeId && row.Key!.AsSpan().SequenceEqual(request.PublicKey);
            if (!same)
            {
                if (row.State != "issued" || row.ExpiresAt <= _time.GetUtcNow()) return Answer(410, new { error = "invite_not_valid" });
                row.State = "redeemed";
                row.Node = request.NodeId;
                row.Key = [.. request.PublicKey];
                row.Redemptions++;
            }
            var answer = new BlindInviteRedeemAnswer(BlindInviteStates.Awaiting, BlindInviteMatchCode.Compute(row.Id, request.NodeId, request.PublicKey), row.SourceName,
                (_time.GetUtcNow() + TimeSpan.FromHours(72)).ToUnixTimeSeconds());
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Headers = { Date = _time.GetUtcNow() },
                Content = new ByteArrayContent(answer.ToJson()) { Headers = { ContentType = new("application/json") } },
            };
        }
    }

    private HttpResponseMessage Status(string body)
    {
        if (!BlindInviteStatusRequest.TryParse(Encoding.UTF8.GetBytes(body), out var request)) return Answer(400, new { error = "bad_request" });
        lock (_gate)
        {
            var row = _invites.SingleOrDefault(i => i.Id.AsSpan().SequenceEqual(request.InviteId));
            if (row is null || row.Key is null || row.Node != request.NodeId || !BlindInviteProofs.StatusProofIsValid(request, row.Key, _time.GetUtcNow()))
                return Answer(410, new { error = "invite_not_valid" });
            return Answer(200, new { state = row.State == "redeemed" ? "awaiting" : "expired" });
        }
    }

    /// <summary>The inner hub reads the request again (its body was read here already): a copy it can read.</summary>
    private static HttpRequestMessage Clone(HttpRequestMessage request, string body)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (request.Content is not null) copy.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return copy;
    }

    private HttpResponseMessage Answer(int code, object? json)
    {
        var response = new HttpResponseMessage((HttpStatusCode)code);
        response.Headers.Date = _time.GetUtcNow();
        if (json is not null) response.Content = new StringContent(JsonSerializer.Serialize(json, Web), Encoding.UTF8, "application/json");
        return response;
    }
}
