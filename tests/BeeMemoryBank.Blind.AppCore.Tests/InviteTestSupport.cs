using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>One way a call to the fake hub can go wrong, queued by a test for the next call to a route.</summary>
internal abstract record HubFault
{
    /// <summary>The hub answers with this status (and, for 429 and 503, this Retry-After).</summary>
    public sealed record Status(int Code, int? RetryAfterSeconds = null, string? Error = null) : HubFault;

    /// <summary>No connection: the request never reaches the hub.</summary>
    public sealed record Unreachable : HubFault;

    /// <summary>The call's own time runs out before any answer: nothing is known about what the hub did.</summary>
    public sealed record TimeOut : HubFault;

    /// <summary>The hub does its work and the answer is lost on the way (a mobile network): the device sees a network error.</summary>
    public sealed record LoseAnswer : HubFault;
}

/// <summary>A request the fake hub has received and not answered yet: the test says when it does (or the caller gives up first).</summary>
internal sealed class HubHold
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
/// A full node as the invitation client sees it, in memory: the identity page, the redemption and the status of an invitation (the same rules as the real hub: the first redemption wins, the same
/// key may ask again and gets the same answer, everything else not valid is a 410, the status needs the signature of the key that redeemed), the sync routes an account calls after it was
/// confirmed (challenge, authenticate - only for a device that was confirmed and not revoked -, events, report-position) and <c>POST /api/sync/leave</c>. It records every request, which
/// is what the "nothing was sent" and "the secret is nowhere" tests read.
/// </summary>
internal sealed class FakeInviteHub : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private readonly FakeBlindHub _sync;
    private readonly HttpMessageInvoker _syncInvoker;
    private readonly TimeProvider _time;
    private readonly List<Invite> _invites = [];
    private readonly Dictionary<string, Queue<HubFault>> _faults = [];
    private readonly Dictionary<string, Queue<HubHold>> _holds = [];
    private readonly Dictionary<string, Guid> _tokens = [];
    private readonly HashSet<Guid> _accepted = [];
    private readonly HashSet<Guid> _revoked = [];
    private readonly List<(string Route, string Body)> _calls = [];

    private sealed class Invite(byte[] id, byte[] secret, DateTimeOffset expiresAt, string sourceName)
    {
        public byte[] Id { get; } = id;
        public byte[] Secret { get; } = secret;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public string SourceName { get; } = sourceName;
        public string State { get; set; } = "issued";
        public Guid? Node { get; set; }
        public byte[]? Key { get; set; }
        public string? DisplayName { get; set; }
        public string? Platform { get; set; }
        public string? AppVersion { get; set; }
        public byte[]? BackupKey { get; set; }
        public int Redemptions { get; set; }
    }

    public FakeInviteHub(string host, TimeProvider time)
    {
        _sync = new FakeBlindHub(host);
        _syncInvoker = new HttpMessageInvoker(_sync);
        _time = time;
        Host = host;
    }

    public string Host { get; }
    public Guid NodeId => _sync.NodeId;
    public byte[] PublicKey => _sync.PublicKey;
    public string Origin => _sync.Origin;
    public string Authority => new Uri(Origin).Authority;
    public string Pin => _sync.Pin;
    public string Canary => _sync.Canary;
    public FakeBlindHub Sync => _sync;

    /// <summary>The call code this hub gives an account whose pairing secret is <paramref name="pairingSecret"/> (the old way).</summary>
    public string CallCodeFor(byte[] pairingSecret) => _sync.CallCodeFor(pairingSecret);

    /// <summary>The hub's vault is locked: the redemption and the status answer 503 <c>locked</c>.</summary>
    public bool Locked { get; set; }

    /// <summary>The hub is older than 2.7: the invitation routes do not exist (404), the sync routes do.</summary>
    public bool Older { get; set; }

    /// <summary>What the identity page says, when a test wants it to say something else than the truth.</summary>
    public Guid? IdentityNodeId { get; set; }
    public byte[]? IdentityKey { get; set; }
    public int? IdentityStatus { get; set; }

    /// <summary>The confirm-by time (unix seconds) the hub puts in its 202, when a test wants it to say something it could not have said.</summary>
    public long? ConfirmByOverride { get; set; }

    /// <summary>The match code the hub puts in its 202, when a test wants it to say something else than the truth.</summary>
    public string? MatchCodeOverride { get; set; }

    /// <summary>When set, a call to the sync routes (authenticate, events, report-position, leave) waits for this before the hub answers.</summary>
    public TaskCompletionSource? HoldSyncCalls { get; set; }

    /// <summary>The next request to <paramref name="route"/> reaches the hub and then waits, unanswered, until the returned hold is released (or the caller cancels it).</summary>
    public HubHold HoldNext(string route)
    {
        var hold = new HubHold();
        lock (_gate)
        {
            if (!_holds.TryGetValue(route, out var queue)) _holds[route] = queue = new Queue<HubHold>();
            queue.Enqueue(hold);
        }
        return hold;
    }

    public void Enqueue(string route, params HubFault[] faults)
    {
        lock (_gate)
        {
            if (!_faults.TryGetValue(route, out var queue)) _faults[route] = queue = new Queue<HubFault>();
            foreach (var fault in faults) queue.Enqueue(fault);
        }
    }

    /// <summary>The invitation the owner would copy from the Blind nodes page: signed by this hub's key, as the real hub always signs it.</summary>
    public BlindInvitation Issue(string sourceName = "Alex", string trust = BlindTrust.Pin, TimeSpan? lifetime = null)
    {
        var id = RandomNumberGenerator.GetBytes(BlindInvitation.InviteIdSize);
        var secret = RandomNumberGenerator.GetBytes(BlindInvitation.SecretSize);
        var expires = HubNow + (lifetime ?? TimeSpan.FromMinutes(30));
        lock (_gate) _invites.Add(new Invite(id, [.. secret], expires, sourceName));
        return BlindInvitation.CreateSigned(Origin, NodeId, trust, trust == BlindTrust.Pin ? Pin : null, PublicKey, id, secret, expires, sourceName, _sync.Sign);
    }

    /// <summary>The same invitation as the string the owner copies (the one place the secret is written).</summary>
    public string IssueText(string sourceName = "Alex", string trust = BlindTrust.Pin, TimeSpan? lifetime = null) => Issue(sourceName, trust, lifetime).ToWireText();

    /// <summary>An invitation the hub knows, written WITHOUT its signature: a fixture the device must refuse.</summary>
    public BlindInvitation IssueUnsigned(string sourceName = "Alex", string trust = BlindTrust.Pin)
    {
        var signed = Issue(sourceName, trust);
        return signed with { Signature = null };
    }

    /// <summary>This hub's clock minus the test's clock (the device's): the hub answers, signs and expires by it, and says so in the <c>Date</c> header.</summary>
    public TimeSpan HubClockOffset { get; set; }

    /// <summary>The hub's answers carry no <c>Date</c> header (a proxy that strips it): the device cannot compare clocks.</summary>
    public bool OmitDate { get; set; }

    private DateTimeOffset HubNow => _time.GetUtcNow() + HubClockOffset;

    public IReadOnlyList<(string Route, string Body)> Calls { get { lock (_gate) return [.. _calls]; } }

    public IEnumerable<string> RoutesCalled => Calls.Select(c => c.Route);

    public string AllRequestText() => string.Join("\n----\n", Calls.Select(c => c.Route + "\n" + c.Body)) + "\n====\n" + _sync.AllRequestText();

    public int Count(string route) => Calls.Count(c => c.Route == route);

    // ---- what the owner does -----------------------------------------------------------------------------------------------------------------

    public string StateOf(BlindInvitation invitation) => Find(invitation.InviteId).State;

    public int RedemptionsOf(BlindInvitation invitation) => Find(invitation.InviteId).Redemptions;

    public Guid? RedeemerOf(BlindInvitation invitation) => Find(invitation.InviteId).Node;

    public byte[]? RedeemerKeyOf(BlindInvitation invitation) => Find(invitation.InviteId).Key;

    public byte[]? BackupKeyOf(BlindInvitation invitation) => Find(invitation.InviteId).BackupKey;

    public (string? Name, string? Platform, string? Version) DescribedBy(BlindInvitation invitation)
    {
        var row = Find(invitation.InviteId);
        return (row.DisplayName, row.Platform, row.AppVersion);
    }

    /// <summary>The match code the owner's page shows for the redemption.</summary>
    public string MatchCodeOf(BlindInvitation invitation)
    {
        var row = Find(invitation.InviteId);
        return BlindInviteMatchCode.Compute(row.Id, row.Node!.Value, row.Key!);
    }

    public void Confirm(BlindInvitation invitation)
    {
        var row = Find(invitation.InviteId);
        lock (_gate)
        {
            row.State = "confirmed";
            _accepted.Add(row.Node!.Value);
        }
    }

    public void Refuse(BlindInvitation invitation) => Find(invitation.InviteId).State = "refused";

    public void Expire(BlindInvitation invitation) => Find(invitation.InviteId).State = "expired";

    /// <summary>The hub's whitelist knows this device (an old-way pairing): it may authenticate.</summary>
    public void Accept(Guid node)
    {
        lock (_gate) _accepted.Add(node);
    }

    /// <summary>The owner revokes the device: it can authenticate no more.</summary>
    public void Revoke(Guid node)
    {
        lock (_gate) _revoked.Add(node);
    }

    public void Unrevoke(Guid node)
    {
        lock (_gate) _revoked.Remove(node);
    }

    private Invite Find(byte[] id)
    {
        lock (_gate) return _invites.Single(i => i.Id.AsSpan().SequenceEqual(id));
    }

    // ---- the routes --------------------------------------------------------------------------------------------------------------------------

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var route = path switch
        {
            "/api/sync/identity" => "identity",
            "/api/sync/invite/redeem" => "redeem",
            "/api/sync/invite/status" => "status",
            "/api/sync/leave" => "leave",
            "/api/sync/challenge" => "challenge",
            "/api/sync/authenticate" => "authenticate",
            _ => "other",
        };
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (_gate) _calls.Add((route, route is "redeem" or "status" or "authenticate" ? body : ""));

        HubHold? held = null;
        lock (_gate)
        {
            if (_holds.TryGetValue(route, out var holds) && holds.Count > 0) held = holds.Dequeue();
        }
        if (held is not null)
        {
            held.MarkEntered();
            await held.Gate.WaitAsync(ct);
        }

        HubFault? fault = null;
        lock (_gate)
        {
            if (_faults.TryGetValue(route, out var queue) && queue.Count > 0) fault = queue.Dequeue();
        }
        switch (fault)
        {
            case HubFault.Unreachable:
                throw new HttpRequestException("no route to host");
            case HubFault.TimeOut:
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException());
            case HubFault.Status status:
                return Answer(status.Code, status.Error is null ? null : (object)new { error = status.Error }, status.RetryAfterSeconds);
        }

        HttpResponseMessage answer;
        switch (route)
        {
            case "identity":
                answer = IdentityPage();
                break;
            case "redeem" when !Older:
                answer = Redeem(body);
                break;
            case "status" when !Older:
                answer = Status(body);
                break;
            case "redeem" or "status":
                answer = Answer(404, null);
                break;
            case "authenticate":
                answer = await Authenticate(request, body, ct);
                break;
            case "leave":
                answer = await LeaveAsync(request, ct);
                break;
            default:
                answer = await Forward(request, ct);
                break;
        }

        if (fault is HubFault.LoseAnswer)
        {
            answer.Dispose();
            throw new HttpRequestException("the connection was reset while the answer was on its way");
        }
        return answer;
    }

    private HttpResponseMessage IdentityPage()
    {
        if (IdentityStatus is { } code) return Answer(code, null);
        return Answer(200, new
        {
            nodeId = IdentityNodeId ?? NodeId,
            displayName = "Hub",
            ed25519PublicKeyB64 = Convert.ToBase64String(IdentityKey ?? PublicKey),
            protocolVersion = 3,
        });
    }

    private HttpResponseMessage Redeem(string body)
    {
        if (Locked) return Answer(503, new { error = "locked" });
        if (!BlindInviteRedeemRequest.TryParse(Encoding.UTF8.GetBytes(body), out var request)) return Answer(400, new { error = "bad_request" });
        if (!BlindInviteProofs.RedeemProofIsValid(request, NodeId)) return Answer(400, new { error = "bad_request" });

        lock (_gate)
        {
            var row = _invites.SingleOrDefault(i => i.Id.AsSpan().SequenceEqual(request.InviteId));
            if (row is null || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(row.Secret), SHA256.HashData(request.Secret))) return Answer(410, new { error = "invite_not_valid" });

            var same = row.State == "redeemed" && row.Node == request.NodeId && row.Key!.AsSpan().SequenceEqual(request.PublicKey);
            if (!same)
            {
                if (row.State != "issued" || row.ExpiresAt <= HubNow) return Answer(410, new { error = "invite_not_valid" });
                row.State = "redeemed";
                row.Node = request.NodeId;
                row.Key = [.. request.PublicKey];
                row.BackupKey = [.. request.BackupKey];
                row.DisplayName = request.DisplayName;
                row.Platform = request.Platform;
                row.AppVersion = request.AppVersion;
                row.Redemptions++;
            }
            var answer = new BlindInviteRedeemAnswer(BlindInviteStates.Awaiting, MatchCodeOverride ?? BlindInviteMatchCode.Compute(row.Id, request.NodeId, request.PublicKey), row.SourceName,
                ConfirmByOverride ?? (HubNow + TimeSpan.FromHours(72)).ToUnixTimeSeconds());
            return new HttpResponseMessage(HttpStatusCode.Accepted) { Headers = { Date = OmitDate ? null : HubNow }, Content = new ByteArrayContent(answer.ToJson()) { Headers = { ContentType = new("application/json") } } };
        }
    }

    private HttpResponseMessage Status(string body)
    {
        if (Locked) return Answer(503, new { error = "locked" });
        if (!BlindInviteStatusRequest.TryParse(Encoding.UTF8.GetBytes(body), out var request)) return Answer(400, new { error = "bad_request" });
        lock (_gate)
        {
            var row = _invites.SingleOrDefault(i => i.Id.AsSpan().SequenceEqual(request.InviteId));
            if (row is null || row.Key is null || row.Node != request.NodeId || !BlindInviteProofs.StatusProofIsValid(request, row.Key, HubNow))
                return Answer(410, new { error = "invite_not_valid" });
            var state = row.State switch { "confirmed" => "confirmed", "refused" => "refused", "redeemed" => "awaiting", _ => "expired" };
            return Answer(200, new { state });
        }
    }

    private async Task<HttpResponseMessage> Authenticate(HttpRequestMessage request, string body, CancellationToken ct)
    {
        if (HoldSyncCalls is { } hold) await hold.Task.WaitAsync(ct);
        var node = Guid.Parse(JsonDocument.Parse(body).RootElement.GetProperty("nodeId").GetString()!);
        lock (_gate)
        {
            // A device that was not confirmed, or was revoked, is not a peer: the hub does not know it.
            if (!_accepted.Contains(node) || _revoked.Contains(node)) return Answer(401, null);
            var token = "token-" + Guid.NewGuid().ToString("N");
            _tokens[token] = node;
            return Answer(200, new { token });
        }
    }

    private async Task<HttpResponseMessage> LeaveAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (HoldSyncCalls is { } hold) await hold.Task.WaitAsync(ct);
        var node = NodeOf(request);
        if (node is null) return Answer(401, null);
        lock (_gate) _calls.Add(("left:" + node.Value.ToString("D"), ""));
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private async Task<HttpResponseMessage> Forward(HttpRequestMessage request, CancellationToken ct)
    {
        if (HoldSyncCalls is { } hold) await hold.Task.WaitAsync(ct);
        if (request.RequestUri!.AbsolutePath != "/api/sync/challenge" && NodeOf(request) is null) return Answer(401, null);
        return await _syncInvoker.SendAsync(Clone(request), ct);
    }

    /// <summary>The device that holds the bearer token of the request, or null when the token is not one the hub issued to a device that is still allowed.</summary>
    private Guid? NodeOf(HttpRequestMessage request)
    {
        var token = request.Headers.Authorization?.Parameter;
        lock (_gate) return token is not null && _tokens.TryGetValue(token, out var node) && !_revoked.Contains(node) ? node : null;
    }

    /// <summary>The inner fake hub reads the request again (its body was read here already): a copy it can read.</summary>
    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var copy = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (request.Content is { } content)
        {
            var bytes = content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            copy.Content = new ByteArrayContent(bytes);
            foreach (var header in content.Headers) copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return copy;
    }

    private HttpResponseMessage Answer(int code, object? json, int? retryAfterSeconds = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)code);
        if (!OmitDate) response.Headers.Date = HubNow;
        if (json is not null) response.Content = new StringContent(JsonSerializer.Serialize(json, Web), Encoding.UTF8, "application/json");
        if (retryAfterSeconds is { } seconds) response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return response;
    }
}

/// <summary>The bottom of every account's pipeline in these tests: it hands a request to the hub whose address it is for, and an address no hub has is "no route to host".</summary>
internal sealed class HubRouter : HttpMessageHandler
{
    private readonly Dictionary<string, FakeInviteHub> _hubs = new(StringComparer.OrdinalIgnoreCase);

    public void Add(FakeInviteHub hub) => _hubs[hub.Authority] = hub;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (!_hubs.TryGetValue(request.RequestUri!.Authority, out var hub)) throw new HttpRequestException("no route to host");
        var invoker = new HttpMessageInvoker(hub, disposeHandler: false);
        return invoker.SendAsync(request, ct);
    }
}

/// <summary>
/// Everything a test of the invitation flow needs: a clock the test moves, the folder and the stores of the accounts, a router of fake hubs, and hosts built over them. Hosts made here run on
/// the fake clock and over <see cref="HubRouter"/>, as a head's host runs on its own.
/// </summary>
internal sealed class InviteRig : IDisposable
{
    public AccountRig Accounts { get; } = new();
    public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
    public HubRouter Router { get; } = new();
    public List<FakeInviteHub> Hubs { get; } = [];

    public FakeInviteHub Hub(string host = "hub-a.test")
    {
        var hub = new FakeInviteHub(host, Clock);
        Hubs.Add(hub);
        Router.Add(hub);
        return hub;
    }

    /// <summary>A host over this rig's root and stores, on the fake clock and over the fake hubs; the options are built here so that a test can say what a head says (a phone, the device's conditions).</summary>
    public BlindAccountHost Host(IBlindCrashPoints? crash = null, IBlindAccountLeaveHook? leave = null, int maxAccounts = BlindAccountHost.DefaultMaxAccounts,
        TimeSpan? leaveTimeout = null, IBlindDeviceConditions? conditions = null, bool isPhone = false, bool noLeaveHook = false,
        Action<IServiceCollection, BlindAccountEntry>? overrides = null, IBlindAccountLifecycle? lifecycle = null)
    {
        return new BlindAccountHost(new BlindAccountHostOptions
        {
            RootDirectory = Accounts.Root,
            LegacyDatabasePath = Accounts.LegacyDb,
            Secrets = Accounts.Secrets,
            States = Accounts.States,
            DisplayNameFactory = () => "Test device",
            CrashPoints = crash,
            MaxAccounts = maxAccounts,
            LeaveHook = noLeaveHook ? null : leave ?? BlindSourceLeaveHook.Instance,
            PrimaryHandlerFactory = _ => () => Router,
            ConfigureOverrides = overrides,
            DrainTimeout = TimeSpan.FromSeconds(10),
            LeaveTimeout = leaveTimeout ?? TimeSpan.FromSeconds(2),
            Lifecycle = lifecycle,
            Conditions = conditions,
            Time = Clock,
            IsPhone = isPhone,
            RootLockWait = TimeSpan.FromMilliseconds(150),
        });
    }

    public void Dispose() => Accounts.Dispose();
}

/// <summary>What a test asks of a started host, in one place.</summary>
internal static class InviteTestExtensions
{
    public static BlindAccountSnapshot CardOf(this BlindAccountHost host, string accountId) => host.GetSnapshot().Accounts.Single(a => a.Entry.Id == accountId);

    public static string StateLineOf(this BlindAccountHost host, string accountId, TimeProvider time) =>
        BlindAccountCard.Build(host.CardOf(accountId), time.LocalTimeZone, time.GetUtcNow()).State;

    public static string[] PhasesOf(this BlindAccountHost host) => host.GetSnapshot().Accounts.Select(a => a.Entry.Phase).ToArray();
}
