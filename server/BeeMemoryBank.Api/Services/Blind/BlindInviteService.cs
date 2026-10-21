using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Hosting.AspNetCore;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Recovery;

namespace BeeMemoryBank.Api.Services;

/// <summary>An invitation request this node refuses; the message is written for the owner and carries no secret.</summary>
public sealed class BlindInviteException(string message, int status = 400, bool stillOpen = false) : InvalidOperationException(message)
{
    /// <summary>The HTTP status the management route answers with.</summary>
    public int Status { get; } = status;

    /// <summary>The confirmation is not decided and not undone: the row stays confirming and is tried again (a blind node that is paused, one that cannot be reached yet).</summary>
    public bool StillOpen { get; } = stillOpen;
}

/// <param name="Trust"><c>auto</c> (default: a certificate the system's chain vouches for, else a pin), <c>pin</c> or <c>public-ca</c>.</param>
public sealed record IssueBlindInviteRequest(string? Label, string? SourceName, int? LifetimeMinutes, string? Address, string? Trust);

/// <summary>
/// The invitation as the owner copies it (<paramref name="Text"/>); the secret in it is shown once and kept nowhere. A record would print every member in its
/// <c>ToString()</c>, and a log line or exception dump of this value would then write the bearer capability: the text is left out of it.
/// </summary>
public sealed record IssuedBlindInvite(string InviteId, string Text, DateTime ExpiresAt, string Address, string Trust, string? Pin, string? Notice)
{
    public override string ToString() => $"IssuedBlindInvite {{ InviteId = {InviteId}, Text = ***, ExpiresAt = {ExpiresAt:O}, Trust = {Trust} }}";
}

/// <summary>One row of the owner's list (<c>GET /api/blind-nodes/invites</c>): never the secret, never a key.</summary>
public sealed record BlindInviteListItem(
    string InviteId, string State, string Label, string SourceName, string Address, string Trust, string CreatedBy, DateTime CreatedAt, DateTime ExpiresAt,
    string? DeviceName, string? Platform, string? AppVersion, string? SeenFrom, DateTime? RedeemedAt, string? MatchCode, DateTime? ConfirmDeadline,
    DateTime? DecidedAt, string? DecidedBy, string? DeviceId, string Kind = BlindInviteKinds.Device);

public sealed record ConfirmedBlindInvite(Guid NodeId, string DisplayName);

public sealed record BlindInviteDefaults(string? Address, string SourceName, int[] Lifetimes, int DefaultLifetime);

public enum BlindInviteOutcome { Ok, BadRequest, Locked, NotValid, DeviceKnownUnderOtherKey }

public sealed record BlindInviteReply<T>(BlindInviteOutcome Outcome, T? Body = default) where T : class;

/// <summary>
/// The per-process budgets of the invitation routes, one place a test can reset: the public ones count per caller address (a redemption is cheap for the
/// sender and an invitation is a bearer secret, so guessing is capped hard), one counts the audit lines a stranger can cause, one the wrong master
/// passwords at a confirmation. Registered as a singleton of the node, not a static, as <c>SyncChallengeRateLimiter</c>.
/// </summary>
public sealed class BlindInviteRateLimits(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The invitations that are open now: the ids that are served from a reserved budget of their own (<see cref="Admit"/>).</summary>
    public OpenInviteIds OpenInvites { get; } = new();

    /// <summary>The most callers (addresses, IPv6 by /64) the per-caller budgets remember at once; see <see cref="SlidingWindowRateLimiter"/> for what happens at the cap.</summary>
    public const int MaxCallers = 10_000;

    public SlidingWindowRateLimiter Redeem { get; internal set; } = NewRedeemLimiter();
    public SlidingWindowRateLimiter Status { get; internal set; } = NewStatusLimiter();

    private static SlidingWindowRateLimiter NewRedeemLimiter() => new(10, TimeSpan.FromMinutes(10), MaxCallers);
    private static SlidingWindowRateLimiter NewStatusLimiter() => new(60, TimeSpan.FromMinutes(10), MaxCallers);

    // The process-wide ceilings of the three public routes, in front of the body, the JSON and the signature: a burst, a steady rate, a number at once. The owner
    // holds at most 20 open invitations, so a legitimate redemption is a rare event and a status poll a few a second at most.
    public PublicRouteBudget RedeemBudget { get; internal set; } = NewRedeemBudget();
    public PublicRouteBudget StatusBudget { get; internal set; } = NewStatusBudget();
    public PublicRouteBudget LeaveBudget { get; internal set; } = NewLeaveBudget();

    /// <summary>
    /// The gate in front of a public route, before the body is parsed or anything is verified. Two lanes.
    /// <para>The RESERVED lane: the request names (<paramref name="inviteId"/>, read from the first bytes of its body) an invitation that is open now. It is served from a
    /// small budget of its own for that id - a burst, a steady rate and a number at once - and from nothing else: not the per-caller table (which a flood of rotating
    /// addresses could have filled, and which would then refuse the friend's address) and not the general budget below. Naming an open id is not a privilege: the id alone
    /// opens nothing (the secret or the device's signature is still needed), and a flood naming one id is bounded by its own small budget.</para>
    /// <para>The GENERAL lane: everything else (an unknown id, no id, a body that is not a request): the caller's own budget first (so a banned address costs the shared
    /// one nothing), then the process-wide one - and when the process-wide one turns the request away, the attempt it just recorded for the caller is taken back
    /// (<see cref="SlidingWindowRateLimiter.Refund"/>): a request that was refused must not use up the caller's budget, nor fill the table of callers.</para>
    /// The caller is the connection's address, an IPv6 one by its /64 (<see cref="BlindInviteCaller.KeyOf"/>).
    /// </summary>
    public BlindInviteAdmission Admit(BlindInviteRoute route, System.Net.IPAddress? remote, string? inviteId = null)
    {
        if (inviteId is not null && OpenInvites.IsOpen(inviteId, _time.GetUtcNow().UtcDateTime))
        {
            var reserved = OpenInvites.Lane(route, inviteId, () => route == BlindInviteRoute.Redeem ? NewReservedRedeemBudget() : NewReservedStatusBudget()).TryEnter();
            return reserved.Allowed
                ? BlindInviteAdmission.Admitted(reserved)
                : BlindInviteAdmission.Refused(reserved.Status, reserved.RetryAfterSeconds, reserved.Status == 503 ? BusyMessage : TooManyMessage);
        }

        var perCaller = route == BlindInviteRoute.Redeem ? Redeem : Status;
        var budget = route == BlindInviteRoute.Redeem ? RedeemBudget : StatusBudget;
        var caller = BlindInviteCaller.KeyOf(remote);
        if (!perCaller.TryAcquire(caller))
            return BlindInviteAdmission.Refused(429, (int)Math.Ceiling((perCaller.RetryAfter(caller) ?? TimeSpan.FromMinutes(1)).TotalSeconds), TooManyMessage);

        var entry = budget.TryEnter();
        if (entry.Allowed) return BlindInviteAdmission.Admitted(entry);
        perCaller.Refund(caller);
        return BlindInviteAdmission.Refused(entry.Status, entry.RetryAfterSeconds, entry.Status == 503 ? BusyMessage : TooManyMessage);
    }

    public const string TooManyMessage = "Too many attempts. Try again later.";
    public const string BusyMessage = "This node is busy. Try again in a moment.";

    // The reserved lane, per open invitation: a friend redeems once (and retries a few times on a bad network) and polls the status every few seconds for the time the
    // owner takes to answer.
    private static PublicRouteBudget NewReservedRedeemBudget() => new(burst: 10, refillPerSecond: 0.5, maxConcurrent: 4);
    private static PublicRouteBudget NewReservedStatusBudget() => new(burst: 30, refillPerSecond: 2, maxConcurrent: 4);

    private static PublicRouteBudget NewRedeemBudget() => new(burst: 30, refillPerSecond: 3, maxConcurrent: 16);
    private static PublicRouteBudget NewStatusBudget() => new(burst: 100, refillPerSecond: 20, maxConcurrent: 32);
    private static PublicRouteBudget NewLeaveBudget() => new(burst: 30, refillPerSecond: 3, maxConcurrent: 16);

    /// <summary>Audit lines for refused redemptions: a stranger cannot grow the audit log without bound by guessing.</summary>
    public SlidingWindowRateLimiter RefusedAudit { get; } = new(30, TimeSpan.FromMinutes(10));

    public SlidingWindowRateLimiter ConfirmPassword { get; } = new(5, TimeSpan.FromMinutes(5));

    public void ResetAll()
    {
        Redeem = NewRedeemLimiter();
        Status = NewStatusLimiter();
        RefusedAudit.ResetAll();
        ConfirmPassword.ResetAll();
        OpenInvites.Clear();
        RedeemBudget = NewRedeemBudget();
        StatusBudget = NewStatusBudget();
        LeaveBudget = NewLeaveBudget();
    }
}

/// <summary>
/// Invitations to a blind copy (release 2.7; docs/sync.md, "Invitations"). The owner makes one on the full node the copy will call (the hub, ADR 0007);
/// the friend's device redeems it at <c>POST /api/sync/invite/redeem</c>; the owner confirms against the match code with the master password, and only
/// then does the device become a blind peer exactly as a phone paired with codes does (<see cref="BlindPhonePairingService"/>): <c>whitelist_add</c>
/// of a plain peer and the backup key sealed as <c>android-backup:&lt;id&gt;</c>.
///
/// <para>What protects it: the invitation expires (15, 30 or 60 minutes), the first redemption burns it in ONE conditional statement, it names this
/// node's identity key and how to trust its TLS endpoint, it carries no secret of the mesh, and until the confirmation no vault content and no usable key of the
/// memory bank is sent to the device. It is a one-time capability, not an encrypted message. Full node only: a blind node holds the (empty) table and none of this code.</para>
///
/// <para>What flows the other way is not nothing: the redemption already sends this node the device's id, public key, name, platform, version, protocol, a proof signed with its key
/// and the device's own backup key (the owner may need it to restore from the device's backup). The node keeps that backup key encrypted under the master DEK until the owner decides,
/// so a redemption needs an unlocked node; the database clears it in every final state.</para>
///
/// <para>A SERVER blind node (<c>bmb blind join</c>) redeems the same way but sends its pair code instead of a backup key. The owner's confirmation then does what pasting that code on the
/// Blind nodes page does (<see cref="BlindNodeManager.AddAsync"/>: pre-flight, <c>whitelist_add</c>, seed, push position) in the same state machine: the row is <c>confirming</c> before the first write,
/// and an interrupted confirmation is finished or undone by the same reconciler. The code is kept under the master DEK like the backup key and cleared in every final state.</para>
/// </summary>
public sealed class BlindInviteService(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    SessionService session,
    HubTrustProbe probe,
    BlindInviteRateLimits limits,
    IConfiguration config,
    TimeProvider time,
    ILogger<BlindInviteService> logger)
{
    public static readonly int[] AllowedLifetimesMinutes = [15, 30, 60];
    public const int DefaultLifetimeMinutes = 30;

    /// <summary>How long the owner has to confirm a redemption.</summary>
    public static readonly TimeSpan ConfirmWindow = TimeSpan.FromHours(72);

    /// <summary>Rows in a final state are deleted after this.</summary>
    public static readonly TimeSpan FinalRetention = TimeSpan.FromDays(30);

    /// <summary>A node does not hold more open invitations than this: each is a bearer secret in somebody's clipboard.</summary>
    public const int MaxOpen = 20;

    private static readonly byte[] BackupAadPrefix = "bmb-invite-backup-key-v1:"u8.ToArray();

    // One decision at a time (confirm, refuse, withdraw, expire, reconcile): a confirmation writes to the whole mesh between its two row changes, and nothing
    // may refuse, expire or finish the same row in between. The redemption does not take it: it only moves issued -> redeemed, in one statement.
    private readonly SemaphoreSlim _decide = new(1, 1);

    /// <summary>
    /// A test seam, null in a running node: called with the name of a point of the confirmation (<c>begun</c> after the row says confirming, <c>whitelisted</c> after
    /// the whitelist entry, <c>sealed</c> after the seal, <c>enrolled</c> before the row says confirmed). A test throws from it to stand for a crash there, or moves
    /// the clock; the node must then be able to finish or undo the confirmation from the row alone.
    /// </summary>
    internal Func<string, Task>? Checkpoint { get; set; }

    private Task HitAsync(string point) => Checkpoint?.Invoke(point) ?? Task.CompletedTask;

    // How much cryptographic work the public routes did (a secret compared, a signature verified). A refusal of an unknown invitation does the same work as a
    // refusal of a known one, which is what keeps the answer time from telling them apart; the tests compare these counts.
    private static long _secretComparisons;
    private static long _signatureVerifications;
    internal static long SecretComparisons => Interlocked.Read(ref _secretComparisons);
    internal static long SignatureVerifications => Interlocked.Read(ref _signatureVerifications);

    // What an unknown invitation is compared and verified against: a fixed hash and a real key that no request was signed with.
    private static readonly string DummySecretHash = Base64Url.EncodeToString(SHA256.HashData("bmb-invite-unknown-v1"u8));
    private static readonly byte[] DummyDeviceKey = NewDummyKey();

    private static byte[] NewDummyKey()
    {
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        return publicKey;
    }

    private DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    // ── making an invitation ──────────────────────────────────────────────

    /// <summary>What the form of "Invite a blind copy" starts from: the address the operator declared (<c>BMB_PUBLIC_ADDRESS</c>; the page falls back to its own origin), this node's name, the lifetimes.</summary>
    public async Task<BlindInviteDefaults> DefaultsAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var self = await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync();
        var declared = config["BMB_PUBLIC_ADDRESS"]?.Trim();
        return new BlindInviteDefaults(string.IsNullOrEmpty(declared) ? null : declared, self?.DisplayName.Trim() ?? "", AllowedLifetimesMinutes, DefaultLifetimeMinutes);
    }

    public async Task<IssuedBlindInvite> IssueAsync(IssueBlindInviteRequest request, string actor, CancellationToken ct)
    {
        if (!session.IsUnlocked)
            throw new BlindInviteException("Unlock this node first: the invitation is signed with its identity key.", 403);

        var lifetime = request.LifetimeMinutes ?? DefaultLifetimeMinutes;
        if (!AllowedLifetimesMinutes.Contains(lifetime))
            throw new BlindInviteException("Choose 15, 30 or 60 minutes.");
        var label = (request.Label ?? "").Trim();
        if (label.Length > 0 && !BlindInviteText.IsPlain(label, BlindInvitation.MaxSourceNameLength))
            throw new BlindInviteException("The label must be at most 64 characters without control characters.");
        var mode = (request.Trust ?? "auto").Trim().ToLowerInvariant();
        if (mode is not ("auto" or BlindTrust.Pin or BlindTrust.PublicCa))
            throw new BlindInviteException("Choose \"automatic\", \"pinned certificate\" or \"normal certificate\".");

        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var invites = sp.GetRequiredService<IBlindInviteRepository>();
        var self = await sp.GetRequiredService<INodeIdentityRepository>().GetAsync()
            ?? throw new BlindInviteException("This node is not initialized.", 503);

        var sourceName = string.IsNullOrWhiteSpace(request.SourceName) ? self.DisplayName.Trim() : request.SourceName.Trim();
        if (!BlindInviteText.IsPlain(sourceName, BlindInvitation.MaxSourceNameLength))
            throw new BlindInviteException("The name shown to your friend must be 1 to 64 characters without control characters.");

        var now = UtcNow;
        // A first look before this node dials its own address; the cap itself is enforced by the insert below, atomically.
        if (await invites.CountOpenAsync(now) >= MaxOpen) throw TooManyOpen();

        var origin = OriginOf(string.IsNullOrWhiteSpace(request.Address) ? config["BMB_PUBLIC_ADDRESS"] : request.Address);
        var (trust, pin, notice) = await ProbeSelfAsync(origin, mode, self, ct);

        var inviteId = RandomNumberGenerator.GetBytes(BlindInvitation.InviteIdSize);
        var secret = RandomNumberGenerator.GetBytes(BlindInvitation.SecretSize);
        // Always signed: a device refuses an invitation without the hub's signature. A node that cannot sign here does not issue one (the key is not available in this mode,
        // or it signs with a key that is not the identity key the invitation names), and says so, rather than hand out a string no device would take.
        var signer = sp.GetRequiredService<INodeAuthSigner>();
        BlindInvitation invitation;
        try
        {
            invitation = BlindInvitation.CreateSigned(origin, self.NodeId, trust, pin, self.Ed25519PublicKey, inviteId, secret,
                new DateTimeOffset(now, TimeSpan.Zero).AddMinutes(lifetime), sourceName, payload => signer.SignChallenge(self, payload));
        }
        catch (ArgumentException ex)
        {
            CryptographicOperations.ZeroMemory(secret);
            throw new BlindInviteException(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CryptographicOperations.ZeroMemory(secret);
            throw new BlindInviteException("This node cannot sign an invitation with its identity key now (" + ex.GetType().Name + "). No invitation was made.", 503);
        }

        var idText = invitation.InviteIdText;
        if (!await invites.TryInsertAsync(new BlindInviteRecord
        {
            InviteId = idText,
            SecretHash = Base64Url.EncodeToString(SHA256.HashData(secret)),
            Label = label,
            SourceName = sourceName,
            Address = origin,
            Trust = trust,
            Pin = pin,
            CreatedBy = actor,
            CreatedAt = now,
            ExpiresAt = invitation.ExpiresAt.UtcDateTime,
            State = BlindInviteRowState.Issued,
        }, now, MaxOpen))
        {
            CryptographicOperations.ZeroMemory(secret);
            throw TooManyOpen();
        }
        await RefreshOpenInvitesAsync(sp);
        await AuditAsync(sp, idText, "invite_issued", "web",
            $"Invitation {Short(idText)} issued by {actor} for \"{label}\" ({lifetime} minutes, {trust}, address {PeerAddressPolicy.Fingerprint(origin)})");

        var text = invitation.ToWireText();
        CryptographicOperations.ZeroMemory(secret);
        return new IssuedBlindInvite(idText, text, invitation.ExpiresAt.UtcDateTime, origin, trust, pin, notice);
    }

    private static BlindInviteException TooManyOpen() =>
        new($"There are already {MaxOpen} open invitations. Withdraw some or wait for them to run out.", 409);

    private static string OriginOf(string? address)
    {
        var typed = address?.Trim();
        if (string.IsNullOrEmpty(typed))
            throw new BlindInviteException("Give the public https address of this node: the address your friend's device will call.");
        if (!typed.Contains("://", StringComparison.Ordinal)) typed = "https://" + typed;
        if (BlindListener.Origin(typed) is not { } origin)
            throw new BlindInviteException("Blind copies call https only: give the address as https://host or https://host:port, without a path.");
        var canonical = PeerAddressPolicy.Canonicalize(origin);
        if (!canonical.Ok) throw new BlindInviteException(canonical.Refusal!);
        // The rule the probe applies to every address a name resolves to, applied to a literal at once (this machine, link-local, the cloud metadata services).
        if (IPAddress.TryParse(new Uri(canonical.Address!).IdnHost.Trim('[', ']'), out var literal) && !HostAddressPolicy.IsPossibleHub(literal))
            throw new BlindInviteException("That address can never be a hub (this machine itself, a link-local or cloud-metadata address, an unspecified or a multicast address). " +
                "Give the address a blind copy will use to reach it: a public one, or one on your own network.");
        return canonical.Address!;
    }

    /// <summary>
    /// This node calls its own public address the way a blind copy will, and the address must answer as THIS node (its id and its key): a wrong address, or a
    /// proxy that does not forward <c>/api/sync/</c>, is found now and not by a friend. Automatic: the system's certificate chain first, else the key the node
    /// presents (a pin, trust on first use: it is this node's own certificate, and with <c>BMB_PUBLIC_TLS_SPKI</c> set it must match).
    /// </summary>
    private async Task<(string Trust, string? Pin, string? Notice)> ProbeSelfAsync(string origin, string mode, NodeIdentity self, CancellationToken ct)
    {
        try
        {
            if (mode is "auto" or BlindTrust.PublicCa)
            {
                try
                {
                    var found = await probe.ProbeAsync(origin, BlindTrust.PublicCa, null, ct);
                    EnsureThisNode(found, origin, self);
                    return (BlindTrust.PublicCa, null, null);
                }
                catch (HubProbeException) when (mode == "auto")
                {
                    // Not a certificate the system vouches for (or not reachable at all): the pin attempt below says which.
                }
            }

            var expected = config["BMB_PUBLIC_TLS_SPKI"]?.Trim();
            if (string.IsNullOrEmpty(expected) || !BeeMemoryBank.Api.Services.Recovery.RestoreTlsPin.IsWellFormed(expected)) expected = null;
            var pinned = await probe.ProbeAsync(origin, BlindTrust.Pin, expected, ct);
            EnsureThisNode(pinned, origin, self);
            return (BlindTrust.Pin, pinned.PresentedPin,
                pinned.PubliclyTrusted
                    ? "This address also has a certificate the system trusts. Choose \"normal certificate\": a pin breaks when the certificate is renewed with a new key."
                    : "This node's own certificate is pinned: a copy invited now stops working if that certificate's key ever changes, and needs a new invitation.");
        }
        catch (HubProbeException ex)
        {
            throw new BlindInviteException(ex.Message);
        }
    }

    private static void EnsureThisNode(HubProbeResult found, string origin, NodeIdentity self)
    {
        var authority = new Uri(origin).Authority;
        if (found.NodeId != self.NodeId)
            throw new HubProbeException($"The node at {authority} is not this node: it answers as another one. Give the address of this very node.");
        if (found.Ed25519PublicKeyB64 != Convert.ToBase64String(self.Ed25519PublicKey))
            throw new HubProbeException($"The node at {authority} has this node's id but another key. Not trusting it.");
    }

    /// <summary>
    /// Tells the public routes which invitations are open now (issued and not run out, redeemed or confirming and the owner's time not run out): the reserved lane of
    /// <see cref="BlindInviteRateLimits.Admit"/> serves a request that names one of them. Called after every change of a row, and at start by the first sweep.
    /// </summary>
    private async Task RefreshOpenInvitesAsync(IServiceProvider sp)
    {
        var now = UtcNow;
        var rows = await sp.GetRequiredService<IBlindInviteRepository>().ListAsync(MaxOpen * 4);
        limits.OpenInvites.Replace(rows
            .Where(r => (r.State == BlindInviteRowState.Issued && r.ExpiresAt > now)
                        || (r.State is BlindInviteRowState.Redeemed or BlindInviteRowState.Confirming && r.ConfirmDeadline > now))
            .Select(r => (r.InviteId, r.State == BlindInviteRowState.Issued ? r.ExpiresAt : r.ConfirmDeadline!.Value)));
    }

    /// <summary>
    /// True if a server blind node's invitation for the node this pair code names is waiting for the owner. The old pairing (the code pasted on the Blind nodes page) refuses such a
    /// node: one node, one way of being added at a time. A text that is not a pair code is not "pending"; the pairing itself says what is wrong with it.
    /// </summary>
    public async Task<bool> ServerInvitationIsPendingAsync(string? pairCodeText)
    {
        if (string.IsNullOrWhiteSpace(pairCodeText)) return false;
        BlindPairCode code;
        try { code = BlindPairCode.Parse(pairCodeText); }
        catch (FormatException) { return false; }
        using var scope = scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IBlindInviteRepository>().HasOpenServerRedemptionAsync(code.NodeId);
    }

    // ── the owner's list ──────────────────────────────────────────────────

    public async Task<IReadOnlyList<BlindInviteListItem>> ListAsync()
    {
        await ExpireAsync();
        using var scope = scopeFactory.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<IBlindInviteRepository>().ListAsync();
        return rows.Select(r => new BlindInviteListItem(
            r.InviteId, r.State, r.Label, r.SourceName, r.Address, r.Trust, r.CreatedBy, r.CreatedAt, r.ExpiresAt,
            r.RedeemerName, r.RedeemerPlatform, r.RedeemerVersion, r.RedeemerSeenFrom, r.RedeemedAt, MatchCodeOf(r), r.ConfirmDeadline,
            r.DecidedAt, r.DecidedBy, r.RedeemerNodeId, r.Kind)).ToList();
    }

    private static string? MatchCodeOf(BlindInviteRecord r) =>
        r.RedeemerNodeId is not null && r.RedeemerKey is not null
        && Guid.TryParse(r.RedeemerNodeId, out var nodeId)
        && CodeTextDecode(r.InviteId, BlindInvitation.InviteIdSize) is { } id
        && CodeTextDecode(r.RedeemerKey, CryptoConstants.Ed25519PublicKeySize) is { } key
            ? BlindInviteMatchCode.Compute(id, nodeId, key)
            : null;

    private static byte[]? CodeTextDecode(string value, int length)
    {
        try
        {
            var bytes = Base64Url.DecodeFromChars(value);
            return bytes.Length == length ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // ── the device's side: redeem and ask ─────────────────────────────────

    /// <summary>
    /// <c>POST /api/sync/invite/redeem</c>. One answer for everything that is not a valid invitation (unknown, expired, used by another device, a wrong
    /// secret); the proof is checked before any row is read, so it says nothing about which invitations exist.
    /// </summary>
    public async Task<BlindInviteReply<BlindInviteRedeemAnswer>> RedeemAsync(byte[] body, string seenFrom, CancellationToken ct)
    {
        if (!session.IsUnlocked) return new(BlindInviteOutcome.Locked);
        if (!BlindInviteRedeemRequest.TryParse(body, out var request)) return new(BlindInviteOutcome.BadRequest);

        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var self = await sp.GetRequiredService<INodeIdentityRepository>().GetAsync();
        if (self is null) return new(BlindInviteOutcome.Locked);
        if (!BlindInviteProofs.RedeemProofIsValid(request, self.NodeId)) return new(BlindInviteOutcome.BadRequest);
        // A server blind node's pair code must name the very node that signed the redemption: the owner's confirmation seeds whatever the code points at.
        if (request.IsServerJoin && !PairCodeNamesTheRedeemer(request)) return new(BlindInviteOutcome.BadRequest);

        var invites = sp.GetRequiredService<IBlindInviteRepository>();
        var idText = Base64Url.EncodeToString(request.InviteId);
        var now = UtcNow;

        var row = await invites.GetAsync(idText);
        // The hash and the constant-time comparison are done for an unknown invitation too (against a fixed hash), so that the refusal of an id that does not
        // exist takes as long as the refusal of one that does.
        var secretMatches = SecretMatches(request.Secret, row?.SecretHash ?? DummySecretHash);
        if (row is null || !secretMatches)
            return await NotValidAsync(sp, row is null ? "unknown" : "wrong secret", seenFrom);

        var keyText = Base64Url.EncodeToString(request.PublicKey);
        if (row.State == BlindInviteRowState.Redeemed && row.ConfirmDeadline > now
            && Guid.TryParse(row.RedeemerNodeId, out var redeemer) && redeemer == request.NodeId && row.RedeemerKey == keyText)
        {
            // The same device asking again (its answer was lost on the way): the same answer. It must be the same KIND of node; a server node that begins again
            // holds a fresh pair code, and the one kept is replaced (the owner's confirmation must use the code the node still has).
            if (request.IsServerJoin != (row.Kind == BlindInviteKinds.Server)) return await NotValidAsync(sp, "kind changed", seenFrom);
            if (request.IsServerJoin && !await ReplacePairCodeAsync(invites, row, request, idText))
                return new(BlindInviteOutcome.Locked);
            return new(BlindInviteOutcome.Ok, AnswerFor(row, request));
        }
        if (row.State != BlindInviteRowState.Issued || row.ExpiresAt <= now)
            return await NotValidAsync(sp, "not open", seenFrom);

        var existing = await sp.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(request.NodeId, includeDeleted: true);
        if (existing is { Status: "A" } && !existing.Ed25519PublicKey.AsSpan().SequenceEqual(request.PublicKey))
            return new(BlindInviteOutcome.DeviceKnownUnderOtherKey);

        byte[] dek;
        try { dek = session.GetMasterDek(); }
        catch (SessionLockedException) { return new(BlindInviteOutcome.Locked); }
        byte[]? sealedKey = null;
        byte[]? sealedPairCode = null;
        try
        {
            if (request.IsServerJoin) sealedPairCode = EncryptPairCode(request.PairCode!, dek, idText);
            else sealedKey = EncryptBackupKey(request.BackupKey!, dek, idText);
        }
        finally { CryptographicOperations.ZeroMemory(dek); }

        var kind = request.IsServerJoin ? BlindInviteKinds.Server : BlindInviteKinds.Device;
        var won = await invites.TryRedeemAsync(idText, row.SecretHash, now, new BlindInviteRedemption(
            request.NodeId, keyText, request.DisplayName, NodePlatformName(request.Platform), request.AppVersion, seenFrom, sealedKey, now + ConfirmWindow, kind, sealedPairCode));
        if (!won) return await NotValidAsync(sp, "lost the race", seenFrom);
        await RefreshOpenInvitesAsync(sp);

        await AuditAsync(sp, idText, "invite_redeemed", "peer",
            $"Invitation {Short(idText)} redeemed by {(request.IsServerJoin ? "server blind node" : "device")} {request.NodeId} ({NodePlatformName(request.Platform)}) from {seenFrom}; waiting for the owner");
        var fresh = (await invites.GetAsync(idText))!;
        return new(BlindInviteOutcome.Ok, AnswerFor(fresh, request));
    }

    /// <summary><c>POST /api/sync/invite/status</c>: where the owner's decision stands, for the device that redeemed (proved by its signature).</summary>
    public async Task<BlindInviteReply<BlindInviteStatusAnswer>> StatusAsync(byte[] body, CancellationToken ct)
    {
        if (!session.IsUnlocked) return new(BlindInviteOutcome.Locked);
        if (!BlindInviteStatusRequest.TryParse(body, out var request)) return new(BlindInviteOutcome.BadRequest);

        using var scope = scopeFactory.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<IBlindInviteRepository>().GetAsync(Base64Url.EncodeToString(request.InviteId));

        // One signature verification whatever the row: against the device's key when the invitation was redeemed by this device, else against a fixed key that
        // verifies nothing - so the refusal of an unknown invitation costs what the refusal of a known one does.
        var key = row is { RedeemerKey: not null } && Guid.TryParse(row.RedeemerNodeId, out var redeemer) && redeemer == request.NodeId
            ? CodeTextDecode(row.RedeemerKey, CryptoConstants.Ed25519PublicKeySize)
            : null;
        Interlocked.Increment(ref _signatureVerifications);
        var proofIsValid = BlindInviteProofs.StatusProofIsValid(request, key ?? DummyDeviceKey, time.GetUtcNow());
        if (row is null || key is null || !proofIsValid) return new(BlindInviteOutcome.NotValid);

        var now = UtcNow;
        var state = row.State switch
        {
            BlindInviteRowState.Confirmed => BlindInviteStates.Confirmed,
            BlindInviteRowState.Refused => BlindInviteStates.Refused,
            BlindInviteRowState.Redeemed when row.ConfirmDeadline > now => BlindInviteStates.Awaiting,
            // A confirming row is awaiting until the reconciler has decided it, whatever the clock says: past the owner's time it is undone (expired) or finished (confirmed)
            // by the reconciler, and until then the device may already be a peer. While the node is locked the route answers the generic locked outcome and nobody
            // is authenticated, so this is the only moment the answer could be seen; it must not say "expired" for a device that is still active.
            BlindInviteRowState.Confirming => BlindInviteStates.Awaiting,
            _ => BlindInviteStates.Expired,
        };
        return new(BlindInviteOutcome.Ok, new BlindInviteStatusAnswer(state));
    }

    private BlindInviteRedeemAnswer AnswerFor(BlindInviteRecord row, BlindInviteRedeemRequest request) =>
        new(BlindInviteStates.Awaiting, BlindInviteMatchCode.Compute(request.InviteId, request.NodeId, request.PublicKey), row.SourceName,
            new DateTimeOffset(DateTime.SpecifyKind(row.ConfirmDeadline!.Value, DateTimeKind.Utc)).ToUnixTimeSeconds());

    /// <summary>True if the request's pair code is a well-formed blind pair code of the node that signed the redemption (same node id, same key).</summary>
    private static bool PairCodeNamesTheRedeemer(BlindInviteRedeemRequest request)
    {
        try
        {
            var code = BlindPairCode.Parse(request.PairCode!);
            return code.NodeId == request.NodeId && PeerKey.Matches(request.PublicKey, code.PublicKeyB64);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task<bool> ReplacePairCodeAsync(IBlindInviteRepository invites, BlindInviteRecord row, BlindInviteRedeemRequest request, string idText)
    {
        byte[] dek;
        try { dek = session.GetMasterDek(); }
        catch (SessionLockedException) { return false; }
        try
        {
            await invites.TryReplacePairCodeAsync(idText, request.NodeId, EncryptPairCode(request.PairCode!, dek, idText));
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    private async Task<BlindInviteReply<BlindInviteRedeemAnswer>> NotValidAsync(IServiceProvider sp, string why, string seenFrom)
    {
        // A stranger guessing must not be able to grow the audit log without bound: past the budget the refusal is only counted in the process log.
        if (limits.RefusedAudit.TryAcquire("refused"))
            await AuditAsync(sp, "none", "invite_redeem_refused", "peer", $"A redemption of an invitation was refused ({why}) from {seenFrom}");
        else
            logger.LogWarning("Invitation redemptions are being refused faster than they are audited ({Why})", why);
        return new(BlindInviteOutcome.NotValid);
    }

    private static bool SecretMatches(byte[] secret, string storedHash)
    {
        Interlocked.Increment(ref _secretComparisons);
        var actual = Encoding.ASCII.GetBytes(Base64Url.EncodeToString(SHA256.HashData(secret)));
        var stored = Encoding.ASCII.GetBytes(storedHash);
        return CryptographicOperations.FixedTimeEquals(actual, stored);
    }

    // The platform a device states is stored as the closed set the rest of the product draws, never as it came.
    private static string NodePlatformName(string declared) => BeeMemoryBank.Sync.Metadata.NodePlatform.Normalize(declared) ?? "";

    // ── the owner's decision ──────────────────────────────────────────────

    /// <summary>
    /// Confirm a redemption: the master password again, then a state machine that stays correct when the process dies in the middle of it.
    /// <list type="number">
    /// <item>Everything that can be refused without a side effect: the stored backup key opens, the device may be enrolled, the pre-flight passes. A failure here
    /// leaves the row <c>redeemed</c> and changes nothing.</item>
    /// <item>The row becomes <c>confirming</c> (the clock read just now), BEFORE the first write that makes the device a peer. From here on an active peer for
    /// this device is allowed, because the row says so.</item>
    /// <item>The enrolment of <see cref="BlindPhonePairingService.PairAsync"/>, idempotent: <c>whitelist_add</c> of a plain peer, then the seal
    /// <c>android-backup:&lt;id&gt;</c> signed by this node.</item>
    /// <item>The row becomes <c>confirmed</c> (the clock read again): only if the owner's time has not run out meanwhile.</item>
    /// </list>
    /// A crash or an error between 2 and 4 leaves <c>confirming</c>; <see cref="ReconcileAsync"/> (at start and every minute) or the owner's next press of
    /// Confirm finishes it by running the same idempotent steps, or - when it cannot (the time ran out, the pre-flight now fails, the key no longer opens) -
    /// undoes it: revokes the entry this confirmation made and ends the row refused or expired, with the staging key cleared. A confirmation that is repeated
    /// after it succeeded (a lost answer, a double click) is the same success.
    /// </summary>
    public async Task<ConfirmedBlindInvite> ConfirmAsync(string inviteId, string? password, string actor, CancellationToken ct)
    {
        if (!session.IsUnlocked) throw new BlindInviteException("Unlock this node first: the device's backup key is sealed under the master key.", 403);
        if (string.IsNullOrEmpty(password) || !limits.ConfirmPassword.TryAcquire("confirm"))
            throw new BlindInviteException(string.IsNullOrEmpty(password) ? "Enter the master password to confirm." : "Too many wrong passwords. Try again in a few minutes.",
                string.IsNullOrEmpty(password) ? 403 : 429);
        if (!await session.VerifyMasterPasswordAsync(password))
            throw new BlindInviteException("Invalid master password", 403);
        limits.ConfirmPassword.ResetAll();

        await _decide.WaitAsync(ct);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            var invites = sp.GetRequiredService<IBlindInviteRepository>();
            var row = await invites.GetAsync(inviteId) ?? throw new BlindInviteException("That invitation does not exist (any more).", 404);

            if (row.State == BlindInviteRowState.Confirmed) return ConfirmedOf(row);
            if (row.State == BlindInviteRowState.Redeemed)
            {
                // The pre-flight ran inside Begin, before the row changed: nothing more to ask of the network.
                row = await BeginConfirmAsync(sp, invites, row, actor, ct);
                return await FinishConfirmAsync(sp, row, actor, ct);
            }
            if (row.State != BlindInviteRowState.Confirming)
                throw new BlindInviteException(row.State == BlindInviteRowState.Issued
                    ? "Nobody has redeemed this invitation yet."
                    : "This invitation can no longer be confirmed: it was decided or ran out. Make a new one.", 409);

            // Left behind by an interruption: this press does exactly what the reconciler would - the pre-flight again, then finish or undo.
            return await PreflightAndFinishAsync(sp, row, actor, ct);
        }
        finally
        {
            _decide.Release();
        }
    }

    private async Task<BlindInviteRecord> BeginConfirmAsync(IServiceProvider sp, IBlindInviteRepository invites, BlindInviteRecord row, string actor, CancellationToken ct)
    {
        if (row.ConfirmDeadline <= UtcNow)
            throw new BlindInviteException("This invitation can no longer be confirmed: it was decided or ran out. Make a new one.", 409);
        var nodeId = Guid.Parse(row.RedeemerNodeId!);
        var deviceKey = CodeTextDecode(row.RedeemerKey!, CryptoConstants.Ed25519PublicKeySize)
            ?? throw new BlindInviteException("The stored device key is damaged. Refuse this invitation.", 409);

        // Without a side effect first, so that a refusal leaves the row redeemed and the owner free to try again or to refuse.
        if (row.Kind == BlindInviteKinds.Server)
        {
            _ = OpenPairCode(row) ?? throw new BlindInviteException(PairCodeLost, 409);
            // A node that is an active peer already is attached to this network (the old pairing, or a confirmation that was undone only in part): this invitation did not add it and must
            // not be the one to undo it. The owner removes it first, or refuses the invitation.
            if (await ConflictFreeEntryAsync(sp, nodeId, deviceKey) is { Status: "A" })
                throw new BlindInviteException("This blind node is already attached to this network. Remove it from the Blind nodes list first, or refuse this invitation.", 409);
        }
        else
        {
            var backupKey = OpenBackupKey(row) ?? throw new BlindInviteException(BackupKeyLost, 409);
            CryptographicOperations.ZeroMemory(backupKey);
            await ConflictFreeEntryAsync(sp, nodeId, deviceKey);
        }
        await PreflightAsync(sp, ct);

        // The decision, and the identity of the event the enrolment will log, are durable before the device can become a peer. The clock is read here, after the
        // pre-flight's network calls, not before them.
        if (!await invites.TryBeginConfirmAsync(row.InviteId, UtcNow, EnrollEventIdFor(row.InviteId).ToString()))
            throw new BlindInviteException("This invitation was decided or ran out while it was being confirmed. Look at the list again.", 409);
        await RefreshOpenInvitesAsync(sp);
        await HitAsync("begun");
        return (await invites.GetAsync(row.InviteId))!;
    }

    /// <summary>
    /// The id the <c>whitelist_add</c> of this invitation's enrolment is logged under: derived from the invitation's id, so it is the same on every attempt and needs
    /// no state to be remembered, and recorded on the row when it becomes confirming. After a crash the event log answers "did the add get written?" by this id.
    /// </summary>
    internal static Guid EnrollEventIdFor(string inviteId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("bmb-invite-enroll-v1:" + inviteId))[..16]);

    private const string BackupKeyLost =
        "The backup key stored with this invitation can no longer be opened (the master key changed after the device redeemed it). Refuse it and make a new invitation.";

    private byte[]? OpenBackupKey(BlindInviteRecord row)
    {
        var dek = session.GetMasterDek();
        try { return TryDecryptBackupKey(row.BackupKeyEnc, dek, row.InviteId); }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    /// <summary>The device's whitelist entry as it is now; refuses (409) when the id is already an active peer under another key.</summary>
    private static async Task<WhitelistEntry?> ConflictFreeEntryAsync(IServiceProvider sp, Guid nodeId, byte[] deviceKey)
    {
        var existing = await sp.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(nodeId, includeDeleted: true);
        if (existing is { Status: "A" } && !existing.Ed25519PublicKey.AsSpan().SequenceEqual(deviceKey))
            throw new BlindInviteException("A device with this id is already in the network under another key. Disconnect it first.", 409);
        return existing;
    }

    private async Task PreflightAsync(IServiceProvider sp, CancellationToken ct)
    {
        using var http = httpClientFactory.CreateClient(SyncScheduler.HttpClientName);
        await sp.GetRequiredService<BlindPreflight>().RunAsync(http, ct);
    }

    /// <summary>
    /// The one way an interrupted confirmation is taken up again - by the reconciler and by the owner pressing Confirm on a row that is already confirming - so that
    /// the two cannot disagree: the owner's time first (nothing is asked of the network for a row that ran out), then the pre-flight (a failure undoes the enrolment
    /// and refuses the row, and is re-thrown for the caller to explain), then <see cref="FinishConfirmAsync"/>.
    /// </summary>
    private async Task<ConfirmedBlindInvite> PreflightAndFinishAsync(IServiceProvider sp, BlindInviteRecord row, string actor, CancellationToken ct)
    {
        if (row.ConfirmDeadline > UtcNow)
        {
            try
            {
                await PreflightAsync(sp, ct);
            }
            catch (BlindPreflightFailedException)
            {
                await UndoAsync(sp, row, BlindInviteRowState.Refused, "the pre-flight no longer passes");
                throw;
            }
        }
        return await FinishConfirmAsync(sp, row, actor, ct);
    }

    /// <summary>
    /// The steps after the row says confirming, for a row that does: enrol (idempotent), then the row says confirmed. Anything that makes it impossible to
    /// finish undoes the enrolment and ends the row, then says so with a 409.
    /// </summary>
    private async Task<ConfirmedBlindInvite> FinishConfirmAsync(IServiceProvider sp, BlindInviteRecord row, string actor, CancellationToken ct)
    {
        var invites = sp.GetRequiredService<IBlindInviteRepository>();
        var inviteId = row.InviteId;
        var nodeId = Guid.Parse(row.RedeemerNodeId!);

        if (row.ConfirmDeadline <= UtcNow)
        {
            await UndoAsync(sp, row, BlindInviteRowState.Expired, "the owner's time ran out before the enrolment was completed");
            throw RanOut();
        }
        var deviceKey = CodeTextDecode(row.RedeemerKey!, CryptoConstants.Ed25519PublicKeySize);
        if (deviceKey is null)
        {
            await UndoAsync(sp, row, BlindInviteRowState.Refused, "the stored device key is damaged");
            throw new BlindInviteException("The stored device key is damaged. The device was not added.", 409);
        }
        try
        {
            await ConflictFreeEntryAsync(sp, nodeId, deviceKey);
        }
        catch (BlindInviteException)
        {
            await UndoAsync(sp, row, BlindInviteRowState.Refused, "the device id belongs to another key");
            throw;
        }

        var displayName = row.Kind == BlindInviteKinds.Server
            ? await SeedServerNodeAsync(sp, row, nodeId, ct)
            : await EnrolDeviceAsync(sp, row, nodeId, deviceKey);

        await HitAsync("enrolled");
        if (!await invites.TryConfirmAsync(inviteId, UtcNow, actor))
        {
            // Not confirmed: the owner's time ran out while the device was being enrolled. A peer without a confirmed row is what must never stay.
            var current = await invites.GetAsync(inviteId);
            if (current is { State: BlindInviteRowState.Confirmed }) return ConfirmedOf(current);
            if (current is { State: BlindInviteRowState.Confirming })
                await UndoAsync(sp, current, BlindInviteRowState.Expired, "the owner's time ran out while the device was being enrolled");
            throw RanOut();
        }
        await RefreshOpenInvitesAsync(sp);
        await AuditAsync(sp, inviteId, "invite_confirmed", "web",
            $"Blind copy {nodeId} ({row.RedeemerName}) confirmed by {actor} from invitation {Short(inviteId)}");
        logger.LogInformation("Invited blind copy {NodeId} confirmed on invitation {Invite}", nodeId, Short(inviteId));
        return new ConfirmedBlindInvite(nodeId, displayName);
    }

    /// <summary>The enrolment of an app copy: the plain peer and the seal of its backup key (idempotent, <see cref="EnrolAsync"/>). Undoes the confirmation and says why when it cannot.</summary>
    private async Task<string> EnrolDeviceAsync(IServiceProvider sp, BlindInviteRecord row, Guid nodeId, byte[] deviceKey)
    {
        var backupKey = OpenBackupKey(row);
        if (backupKey is null)
        {
            await UndoAsync(sp, row, BlindInviteRowState.Refused, "the stored backup key can no longer be opened");
            throw new BlindInviteException(BackupKeyLost, 409);
        }
        try
        {
            var self = await sp.GetRequiredService<INodeIdentityRepository>().GetAsync() ?? throw new BlindInviteException("This node is not initialized.", 503);
            var enrollment = BlindPhoneEnrollment.PrepareInvited(nodeId, deviceKey, DisplayNameFor(row.Label, row.RedeemerName), backupKey, row.Address, self.NodeId,
                row.Pin ?? "", self.Ed25519PublicKey, row.Trust, UtcNow, out var error);
            if (enrollment is null)
            {
                await UndoAsync(sp, row, BlindInviteRowState.Refused, "the device cannot be enrolled");
                throw new BlindInviteException(error!, 409);
            }
            try
            {
                await EnrolAsync(sp, row, enrollment, self);
            }
            catch (EnrolmentSupersededException)
            {
                await UndoAsync(sp, row, BlindInviteRowState.Refused, "the device was removed from the network while it was being added");
                throw new BlindInviteException("The device was removed from the network while it was being added. It was not added again; the invitation is refused.", 409);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(enrollment.Seal.BackupKey);
            }
            return enrollment.Entry.DisplayName;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(backupKey);
        }
    }

    /// <summary>
    /// The confirmation of a SERVER blind node: what pasting its pair code on the Blind nodes page does (<see cref="BlindNodeManager.AddAsync"/>: identity check, pre-flight, the
    /// <c>whitelist_add</c> with the node's pin, the seed, the push position), run from the row that already says confirming. A node this computer already added and seeded under
    /// this confirmation (the push position was written after the row became confirming) is not seeded twice: the code is spent by its first seed. A node that got as far as
    /// the whitelist and may be seeded, but whose code is spent, is reached through the trust the seed gave this computer (a reseed). A failure leaves the row confirming and says
    /// why: the reconciler and the owner's next press try again until the owner's time runs out, then the whole thing is undone.
    /// </summary>
    private async Task<string> SeedServerNodeAsync(IServiceProvider sp, BlindInviteRecord row, Guid nodeId, CancellationToken ct)
    {
        var codeText = OpenPairCodeText(row);
        if (codeText is null)
        {
            await UndoAsync(sp, row, BlindInviteRowState.Refused, "the stored pair code can no longer be opened");
            throw new BlindInviteException(PairCodeLost, 409);
        }
        var manager = sp.GetRequiredService<BlindNodeManager>();
        var whitelist = sp.GetRequiredService<IWhitelistRepository>();
        var existing = await whitelist.GetByNodeIdAsync(nodeId, includeDeleted: false);
        if (existing is { Status: "A" }
            && await sp.GetRequiredService<ISyncPushPositionRepository>().GetAsync(nodeId) is { } position && position.PushedAt >= row.ConfirmingAt)
            return existing.DisplayName;

        try
        {
            try
            {
                return (await manager.AddAsync(codeText, ct)).DisplayName;
            }
            catch (BlindNodeUnreachableException) when (existing is { Status: "A" })
            {
                await manager.ReseedAsync(nodeId, ct);
                return existing.DisplayName;
            }
        }
        catch (BlindPreflightFailedException)
        {
            await UndoAsync(sp, row, BlindInviteRowState.Refused, "the pre-flight no longer passes");
            throw;
        }
        catch (FormatException)
        {
            await UndoAsync(sp, row, BlindInviteRowState.Refused, "the stored pair code is not a pair code");
            throw new BlindInviteException(PairCodeLost, 409);
        }
        catch (PeerPausedException ex)
        {
            // The node took the call and said its owner's limit stops the seed: not decided, and not a failure. The row stays confirming. The reconciler does not rebuild and
            // upload the package again before the time the node named (an hour for a storage limit); the owner pressing Confirm asks it at once.
            _heldUntil[row.InviteId] = UtcNow + Clamp(ex.RetryAfter, MinHold, MaxHold);
            throw new BlindInviteException($"{BlindNodeManager.PausedSeedSentence(ex)} The invitation stays open; it is tried again after the time the node named, or press Confirm again.", 409, stillOpen: true);
        }
        catch (Exception ex) when (ex is BlindNodeUnreachableException or BaselineNotReadyException or HttpRequestException or IOException or TimeoutException)
        {
            // Not decided: the row stays confirming and is tried again. Only the kind of failure is told (never the node's own words).
            throw new BlindInviteException($"The blind node could not be added yet: {FailureText.Say(ex)} The invitation stays open; it is tried again every minute, or press Confirm again.", 409, stillOpen: true);
        }
    }

    // The confirmations a blind node held back (a paused seed): not tried again by the reconciler before the time the node named, however often it passes (every minute).
    // In memory ON PURPOSE: a restart of this node ends the wait. Remembering it would take a column on the invitation row (a migration, and the release already has two) or a file of
    // its own to keep honest, to save what a retry costs now that the seed is judged at its first part: one package build and one first part the blind node refuses before it
    // stages a byte (503, X-BMB-Paused, Retry-After), which puts the hold back. At most one such attempt per open invitation (there are at most 20) per restart; documented.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _heldUntil = new();
    private static readonly TimeSpan MinHold = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MaxHold = TimeSpan.FromHours(1);

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) => value < min ? min : value > max ? max : value;

    private const string PairCodeLost =
        "The pair code stored with this invitation can no longer be opened or read (the master key changed after the node redeemed it). Refuse it; the friend runs bmb blind join again with a new invitation.";

    /// <summary>The server node's pair code as text, or null when the stored blob no longer opens.</summary>
    private string? OpenPairCodeText(BlindInviteRecord row)
    {
        var dek = session.GetMasterDek();
        try { return TryDecryptPairCode(row.PairCodeEnc, dek, row.InviteId); }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    /// <summary>The server node's pair code, read; null when it cannot be opened or is not a pair code.</summary>
    private BlindPairCode? OpenPairCode(BlindInviteRecord row)
    {
        try { return OpenPairCodeText(row) is { } text ? BlindPairCode.Parse(text) : null; }
        catch (FormatException) { return null; }
    }

    private static BlindInviteException RanOut() =>
        new("This invitation ran out while it was being confirmed. The device was not added. Make a new one.", 409);

    private static ConfirmedBlindInvite ConfirmedOf(BlindInviteRecord row) =>
        new(Guid.Parse(row.RedeemerNodeId!), row.Kind == BlindInviteKinds.Server ? ServerNodeName(Guid.Parse(row.RedeemerNodeId!)) : DisplayNameFor(row.Label, row.RedeemerName));

    /// <summary>The name <see cref="BlindNodeManager.AddAsync"/> gives a blind node it adds.</summary>
    internal static string ServerNodeName(Guid nodeId) => $"Blind {nodeId.ToString()[..8]}";

    /// <summary>
    /// The name the peer gets: the owner's label, else the device's own name with " (invited)" - cut so that the whole stays within the limit of a peer's name
    /// (<see cref="BlindPhoneCode.MaxDisplayNameLength"/>), which the device is allowed to fill by itself.
    /// </summary>
    internal static string DisplayNameFor(string label, string? deviceName)
    {
        const string suffix = " (invited)";
        if (label.Length > 0) return label.Length <= BlindPhoneCode.MaxDisplayNameLength ? label : label[..BlindPhoneCode.MaxDisplayNameLength];
        var name = (deviceName ?? "").Trim();
        var room = BlindPhoneCode.MaxDisplayNameLength - suffix.Length;
        if (name.Length > room)
        {
            name = name[..room];
            if (char.IsHighSurrogate(name[^1])) name = name[..^1];
            name = name.TrimEnd();
        }
        return name.Length == 0 ? "Invited device" : name + suffix;
    }

    /// <summary>The device's entry was written by somebody else (a revoke) after this invitation's add: it is not this enrolment's to put back.</summary>
    private sealed class EnrolmentSupersededException : Exception;

    // The enrolment of BlindPhonePairingService.PairAsync after its pre-flight, for a device that is not paired by a code: whitelist_add of a plain peer (logged
    // first, as every whitelist write) and the seal signed by this node. Idempotent BY IDENTITY: the add is logged under the id recorded on the row
    // (EnrollEventIdFor), so a retry after a crash between the log and the local write finds its own event in the log - and takes its version for the local row -
    // instead of logging a second add. An entry that is already an active peer is left as it is, and the seal is last-writer-wins per name.
    private async Task EnrolAsync(IServiceProvider sp, BlindInviteRecord row, BlindInvitedEnrollment enrollment, NodeIdentity self)
    {
        var whitelist = sp.GetRequiredService<IWhitelistRepository>();
        var device = enrollment.Entry;
        var existing = await whitelist.GetByNodeIdAsync(device.NodeId, includeDeleted: true);

        if (existing is not { Status: "A" })
        {
            var eventId = Guid.Parse(row.EnrollEventId ?? EnrollEventIdFor(row.InviteId).ToString());
            var own = await sp.GetRequiredService<IEventLogRepository>().GetByIdAsync(eventId.ToString());
            RowVersion version;
            if (own is null)
            {
                version = await sp.GetRequiredService<IEventLogger>().LogWhitelistAddAsync(device, eventId);
            }
            else
            {
                // Our add is in the log already. If the local row carries a LATER write, somebody revoked the device after we added it: that is theirs to decide.
                version = new RowVersion(own.LamportTs, own.NodeId);
                if (existing is not null && !ConflictResolver.IncomingWins(existing.Version, version)) throw new EnrolmentSupersededException();
            }
            await HitAsync("logged");

            device.LamportTs = version.LamportTs;
            device.SourceNodeId = version.SourceNodeId;
            if (existing is null) await whitelist.CreateAsync(device);
            else await whitelist.UpdateAsync(device);
        }
        await HitAsync("whitelisted");

        var signed = enrollment.Seal with
        {
            PairedBy = self.NodeId,
            PairingSignature = sp.GetRequiredService<INodeAuthSigner>().SignChallenge(self, enrollment.Seal.PairingStatement(device.NodeId)),
        };
        var sealedValue = signed.Encode();
        try
        {
            await sp.GetRequiredService<SealedSecretService>().PublishAsync(enrollment.SealedSecretName, sealedValue);
        }
        finally
        {
            Array.Clear(sealedValue);
        }
        await HitAsync("sealed");
    }

    /// <summary>
    /// Ends a confirmation that cannot be completed. What it revokes is decided by the identity recorded on the row, not by a bit or by what the local table shows:
    /// if the <c>whitelist_add</c> logged under <see cref="BlindInviteRecord.EnrollEventId"/> exists in the log, a matching <c>whitelist_revoke</c> is logged (first, like
    /// every whitelist write) - even when the local row was never written, because the event may already have reached other nodes; and the local row is revoked when it
    /// still carries that add. If the log has no such event nothing was published and nothing is revoked. An entry the invitation did not add is never touched: one that was
    /// already a peer (the enrolment skipped it), or one a later write of somebody else put there - a local row version NEWER than the add means another flow decided
    /// after it (it revoked the device, or revoked and added it again). Then the row ends <paramref name="finalState"/> and the table's trigger clears the staging key.
    /// The seal <c>android-backup:&lt;id&gt;</c> stays: the protocol has no event that withdraws a sealed secret (it is last-writer-wins per name), and a disconnected paired
    /// phone leaves its seal in the same way; it holds the device's own backup key under the master key and opens for nobody else. A crash between the revoke and the end of
    /// the row repeats this with the local row already revoked (a newer version): no second revoke.
    /// </summary>
    private async Task UndoAsync(IServiceProvider sp, BlindInviteRecord row, string finalState, string why)
    {
        await RevokeOwnEnrolmentAsync(sp, row);
        if (await sp.GetRequiredService<IBlindInviteRepository>().TryAbortConfirmingAsync(row.InviteId, finalState, UtcNow, "system"))
        {
            await RefreshOpenInvitesAsync(sp);
            await AuditAsync(sp, row.InviteId, "invite_confirm_undone", "system",
                $"Confirmation of invitation {Short(row.InviteId)} undone ({finalState}): {why}");
        }
    }

    private async Task RevokeOwnEnrolmentAsync(IServiceProvider sp, BlindInviteRecord row)
    {
        if (row.Kind == BlindInviteKinds.Server)
        {
            await RevokeServerNodeAsync(sp, row);
            return;
        }
        if (!Guid.TryParse(row.EnrollEventId, out var eventId) || !Guid.TryParse(row.RedeemerNodeId, out var nodeId)) return;
        var own = await sp.GetRequiredService<IEventLogRepository>().GetByIdAsync(eventId.ToString());
        if (own is not { EventType: EventTypes.WhitelistAdd }) return;   // the add was never logged: nothing was published, nothing to take back

        var ownVersion = new RowVersion(own.LamportTs, own.NodeId);
        var whitelist = sp.GetRequiredService<IWhitelistRepository>();
        var local = await whitelist.GetByNodeIdAsync(nodeId, includeDeleted: true);
        if (local is not null && ConflictResolver.IncomingWins(ownVersion, local.Version)) return;   // somebody else wrote the entry after our add

        var revoke = await sp.GetRequiredService<IEventLogger>().LogWhitelistRevokeAsync(nodeId);
        if (local is { Status: "A" }) await whitelist.RevokeAsync(nodeId, revoke);
        sp.GetService<IPeerStateResetter>()?.PeerChanged(nodeId);
    }

    /// <summary>
    /// The undo of a server node's confirmation: the node was added by this confirmation only (the confirmation began on a node that was not an active peer, and the old pairing refuses
    /// a node whose invitation is being confirmed), so an active entry now is this confirmation's: it is revoked by an event like any other disconnection. Nothing active, nothing to do.
    /// </summary>
    private static async Task RevokeServerNodeAsync(IServiceProvider sp, BlindInviteRecord row)
    {
        if (!Guid.TryParse(row.RedeemerNodeId, out var nodeId)) return;
        var whitelist = sp.GetRequiredService<IWhitelistRepository>();
        if (await whitelist.GetByNodeIdAsync(nodeId, includeDeleted: true) is not { Status: "A" }) return;
        var revoke = await sp.GetRequiredService<IEventLogger>().LogWhitelistRevokeAsync(nodeId);
        await whitelist.RevokeAsync(nodeId, revoke);
        sp.GetService<IPeerStateResetter>()?.PeerChanged(nodeId);
    }

    /// <summary>
    /// Finishes or undoes the confirmations a crash or an error left in <c>confirming</c> - at start, and every minute after (<see cref="SweepAsync"/>). It needs the
    /// unlocked node (the backup key and the signing key are under the master key) and does nothing while it is locked: the row says what is half done, and an
    /// active peer for it is allowed. That is safe because a locked node authenticates nobody and serves nothing (redeem and status answer the generic locked
    /// outcome, the sync routes are closed), so the 72-hour limit of a confirming row is enforced at the first moment the node is unlocked and usable - the sweeper's
    /// first pass - and until then <see cref="StatusAsync"/> says "awaiting" for it, never "expired" for a device that may still be a peer. A row whose time ran out, or whose pre-flight now fails, is undone; any other failure is tried again at the next pass, until the
    /// row's time runs out.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        if (!session.IsUnlocked) return;
        await _decide.WaitAsync(ct);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            var confirming = (await sp.GetRequiredService<IBlindInviteRepository>().ListConfirmingAsync()).ToList();
            foreach (var gone in _heldUntil.Keys.Where(id => confirming.All(r => r.InviteId != id)).ToList()) _heldUntil.TryRemove(gone, out _);
            foreach (var row in confirming)
            {
                ct.ThrowIfCancellationRequested();
                // A blind node that said "not before this time" (its owner's limit) is not rebuilt for and uploaded to again until then; the row's own time still runs out as ever.
                if (_heldUntil.TryGetValue(row.InviteId, out var until) && until > UtcNow && row.ConfirmDeadline > until) continue;
                try
                {
                    await PreflightAndFinishAsync(sp, row, "system", ct);
                    _heldUntil.TryRemove(row.InviteId, out _);
                    logger.LogInformation("Finished the interrupted confirmation of invitation {Invite}", Short(row.InviteId));
                }
                catch (BlindInviteException ex) when (ex.StillOpen)
                {
                    // Not undone and not decided: the row is still open and is tried again.
                    logger.LogInformation("The confirmation of invitation {Invite} is still open; it is tried again", Short(row.InviteId));
                }
                catch (Exception ex) when (ex is BlindInviteException or BlindPreflightFailedException)
                {
                    // It undid what it could not finish and wrote the reason into the audit log (invite_confirm_undone).
                    logger.LogInformation("The interrupted confirmation of invitation {Invite} was undone", Short(row.InviteId));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Finishing the confirmation of invitation {Invite} did not work; it is tried again", Short(row.InviteId));
                }
            }
        }
        finally
        {
            _decide.Release();
        }
    }

    public async Task RefuseAsync(string inviteId, string actor)
    {
        await _decide.WaitAsync();
        try
        {
            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            if (!await sp.GetRequiredService<IBlindInviteRepository>().TryRefuseAsync(inviteId, UtcNow, actor))
                throw new BlindInviteException("That redemption is not waiting for an answer (any more).", 409);
            await RefreshOpenInvitesAsync(sp);
            await AuditAsync(sp, inviteId, "invite_refused", "web", $"Redemption of invitation {Short(inviteId)} refused by {actor}");
        }
        finally
        {
            _decide.Release();
        }
    }

    public async Task WithdrawAsync(string inviteId, string actor)
    {
        await _decide.WaitAsync();
        try
        {
            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            if (!await sp.GetRequiredService<IBlindInviteRepository>().TryWithdrawAsync(inviteId, UtcNow, actor))
                throw new BlindInviteException("That invitation is not open (any more), or a device already redeemed it: refuse the redemption instead.", 409);
            await RefreshOpenInvitesAsync(sp);
            await AuditAsync(sp, inviteId, "invite_withdrawn", "web", $"Invitation {Short(inviteId)} withdrawn by {actor}");
        }
        finally
        {
            _decide.Release();
        }
    }

    // ── the device's goodbye ──────────────────────────────────────────────

    /// <summary>
    /// A blind copy's owner removed it from the device (<c>POST /api/sync/leave</c>): noted in memory for the Blind nodes page and the network map, and one audit line.
    /// The hub does not revoke the device - only an event of a superadmin can - so the copy stays in the network until its owner here does.
    /// </summary>
    public async Task NoteLeftAsync(Guid nodeId)
    {
        using var scope = scopeFactory.CreateScope();
        scope.ServiceProvider.GetService<BeeMemoryBank.Sync.PeerContactLog>()?.NoteLeft(nodeId, UtcNow);
        await AuditAsync(scope.ServiceProvider, nodeId.ToString(), "blind_copy_left", "peer",
            $"The owner of blind copy {nodeId} removed it from the device. It stays in the network until it is revoked here.");
    }

    // ── housekeeping ──────────────────────────────────────────────────────

    /// <summary>
    /// The background pass (at start and every minute): finishes or undoes the confirmations a crash or a failure left half done (<see cref="ReconcileAsync"/>),
    /// then expires what ran out and deletes the old final rows (<see cref="ExpireAsync"/>).
    /// </summary>
    public async Task SweepAsync(CancellationToken ct = default)
    {
        await ReconcileAsync(ct);
        await ExpireAsync();
    }

    /// <summary>Expires what ran out (clearing the stored backup keys through the table's trigger) and deletes final rows older than <see cref="FinalRetention"/>.</summary>
    public async Task ExpireAsync()
    {
        await _decide.WaitAsync();
        try
        {
            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            var invites = sp.GetRequiredService<IBlindInviteRepository>();
            var now = UtcNow;
            foreach (var id in await invites.ListDueForExpiryAsync(now))
                if (await invites.TryExpireAsync(id, now))
                    await AuditAsync(sp, id, "invite_expired", "system", $"Invitation {Short(id)} ran out");
            await invites.DeleteFinalBeforeAsync(now - FinalRetention);
            await RefreshOpenInvitesAsync(sp);
        }
        finally
        {
            _decide.Release();
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static string Short(string inviteId) => inviteId.Length <= 6 ? inviteId : inviteId[..6];

    private static Task AuditAsync(IServiceProvider sp, string entityId, string action, string actorType, string details) =>
        sp.GetRequiredService<IAuditLogRepository>().LogAsync("blind_invite", entityId, action, actorType, details);

    private static byte[] BackupAad(string inviteId) => [.. BackupAadPrefix, .. Encoding.UTF8.GetBytes(inviteId)];

    /// <summary><c>iv || wrapped</c>, the framing of <see cref="DekManager.WrapDek"/>, with an AAD bound to the invitation: a blob cannot be moved to another row.</summary>
    internal static byte[] EncryptBackupKey(byte[] backupKey, byte[] dek, string inviteId)
    {
        var (wrapped, iv) = DekManager.WrapDek(backupKey, dek, BackupAad(inviteId));
        return [.. iv, .. wrapped];
    }

    private static readonly byte[] PairCodeAadPrefix = "bmb-invite-pair-code-v1:"u8.ToArray();

    private static byte[] PairCodeAad(string inviteId) => [.. PairCodeAadPrefix, .. Encoding.UTF8.GetBytes(inviteId)];

    /// <summary>A server node's pair code under the master DEK, <c>iv || wrapped</c> as the backup key is, with an AAD of its own bound to the invitation.</summary>
    internal static byte[] EncryptPairCode(string pairCode, byte[] dek, string inviteId)
    {
        var plain = Encoding.UTF8.GetBytes(pairCode);
        try
        {
            var (wrapped, iv) = DekManager.WrapDek(plain, dek, PairCodeAad(inviteId));
            return [.. iv, .. wrapped];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    internal static string? TryDecryptPairCode(byte[]? blob, byte[] dek, string inviteId)
    {
        if (blob is not { Length: > CryptoConstants.IvSize }) return null;
        byte[]? plain = null;
        try
        {
            plain = DekManager.UnwrapVersioned(blob[CryptoConstants.IvSize..], blob[..CryptoConstants.IvSize], dek, PairCodeAad(inviteId));
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or DecoderFallbackException)
        {
            return null;
        }
        finally
        {
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
        }
    }

    internal static byte[]? TryDecryptBackupKey(byte[]? blob, byte[] dek, string inviteId)
    {
        if (blob is not { Length: > CryptoConstants.IvSize }) return null;
        try
        {
            return DekManager.UnwrapVersioned(blob[CryptoConstants.IvSize..], blob[..CryptoConstants.IvSize], dek, BackupAad(inviteId));
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Expires and deletes invitations in the background (a minute is fine: every route also checks the clock itself).</summary>
public sealed class BlindInviteSweeper(BlindInviteService service, IServiceScopeFactory scopeFactory, ILogger<BlindInviteSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            // A pass at start (a confirmation a crash left half done is finished or undone as soon as the node is unlocked and usable), then one every minute.
            do
            {
                // A sealed node with a closed database has no table to sweep (docs/design/step-b-spec.md 5.5).
                await scopeFactory.WaitUsableAsync(stoppingToken);
                try
                {
                    await service.SweepAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "The invitation sweep did not run");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }
}
