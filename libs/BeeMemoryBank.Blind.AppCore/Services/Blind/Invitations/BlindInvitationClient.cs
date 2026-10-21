using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Metadata;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>What a call to the hub came to. A closed set: every caller decides what each one means for the account, and a new kind cannot slip through as "success".</summary>
public enum BlindInvitationOutcome
{
    Ok,

    /// <summary>410: unknown, expired, used by another device, withdrawn - the hub says one thing for all of them.</summary>
    NotValid,

    /// <summary>409: the hub knows this node id under another key.</summary>
    KnownUnderOtherKey,

    /// <summary>429: <see cref="BlindInvitationResult{T}.RetryAfter"/> says when to come back.</summary>
    RateLimited,

    /// <summary>503: the hub's vault is locked (or it is too busy); come back later.</summary>
    Locked,

    /// <summary>404 or 405: a full node older than 2.7 does not have the route.</summary>
    OlderHub,

    /// <summary>No answer: no connection, a timeout, a server in trouble. Nothing is known about what the hub did.</summary>
    Unreachable,

    /// <summary>The account's own limits or the device's say "not now" (nothing was sent): <see cref="BlindInvitationResult{T}.Pause"/>.</summary>
    Paused,

    /// <summary>The hub's TLS endpoint is not the one the invitation names (the pin or the chain refused it): nothing was sent.</summary>
    CertificateRefused,

    /// <summary>The computer at the address does not answer with the identity the invitation names (another node id or key, or not a node at all): nothing was sent.</summary>
    HubMismatch,

    /// <summary>An answer this app does not understand, or a 4xx it did not expect.</summary>
    NotUnderstood,

    /// <summary>The account's keys could not be read (a secret store that does not answer, or a key that is lost): nothing was sent.</summary>
    KeysUnavailable,
}

/// <summary>
/// The outcome of one call, with what the hub answered when it said Ok. <see cref="HubDate"/> is the <c>Date</c> header of a 410 (when the hub sent one): the hub's own clock, which the caller compares
/// with this device's before it believes that "not valid" means what it says (a status proof is only accepted within minutes of the hub's clock).
/// </summary>
public sealed record BlindInvitationResult<T>(
    BlindInvitationOutcome Outcome, T? Value = default, TimeSpan? RetryAfter = null, BlindPause? Pause = null, DateTimeOffset? HubDate = null)
{
    public bool Ok => Outcome == BlindInvitationOutcome.Ok;
}

/// <summary>
/// The device's side of the three keyless routes of an invitation, for ONE account, through that account's own metered HTTP client (the one the invitation's trust selects:
/// <see cref="BlindHttpClientProvider.GetClient(BlindCallCode)"/>): the identity probe (<c>GET /api/sync/identity</c>), the redemption (<c>POST /api/sync/invite/redeem</c>) and the
/// question where the owner's decision stands (<c>POST /api/sync/invite/status</c>). Every call counts against the account's limits and is refused, with nothing sent, when they
/// say "not now". Nothing here keeps the invitation's secret: it is a parameter of the one call that sends it, and the body that carries it is wiped after the call.
///
/// <para>A call never throws for what the network or the hub did (that is an outcome); it throws only for cancellation. No text of an answer, of an exception or of a header is
/// taken over: the outcomes are words of this app.</para>
/// </summary>
public sealed class BlindInvitationClient(
    BlindHttpClientProvider http, IBlindSecretStore keys, BlindPhoneState state, TimeProvider time, ISyncProtocolPolicy? protocol = null)
{
    /// <summary>One call may take this long; a hub that does not answer in this time is "unreachable" and tried again later.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    private const int MaxAnswerBytes = BlindInviteRedeemRequest.MaxBodyBytes;

    /// <summary>
    /// Asks the address for its identity and compares it with the invitation's: the NodeId AND the key must be the same, or this refuses and the caller sends nothing. Through the
    /// client the invitation's trust selects, so a pinned hub is reached only with its pinned key and a hub on a public certificate only with a valid chain for its name.
    /// </summary>
    public async Task<BlindInvitationResult<object>> ProbeAsync(BlindInvitationTarget target, CancellationToken ct)
    {
        try
        {
            using var client = Client(target);
            var identity = await SyncAddress.ReadIdentityAsync(client, target.Address, ct).ConfigureAwait(false);
            // The node id alone is a claim; the key the hub states about itself must be the one the invitation names, in constant time.
            return identity.NodeId == target.HubNodeId && PeerKey.Matches(target.HubPublicKey, identity.Ed25519PublicKeyB64)
                ? new(BlindInvitationOutcome.Ok)
                : new(BlindInvitationOutcome.HubMismatch);
        }
        catch (NoSyncServerException)
        {
            return new(BlindInvitationOutcome.HubMismatch);
        }
        catch (Exception ex) when (Classify(ex, ct) is { } result)
        {
            return new(result.Outcome, Pause: result.Pause);
        }
    }

    /// <summary>
    /// Redeems the invitation with this account's identity: its node id and key, a name, platform and version, the backup key, and a proof signed with the key (so nobody can enrol a key
    /// they do not hold, and a redemption cannot be replayed at another hub). <paramref name="secret"/> is the invitation's one-time secret; it goes in the body and nowhere else.
    /// Retrying with the same account and secret is safe, but the hub answers the same pending 202 again only while the invitation is still <c>redeemed</c> and its confirmation window is open;
    /// after the owner has moved it on, the redemption is refused and the device asks for the signed status instead (<see cref="StatusAsync"/>).
    /// </summary>
    public async Task<BlindInvitationResult<BlindInviteRedeemAnswer>> RedeemAsync(BlindInvitationTarget target, byte[] secret, CancellationToken ct)
    {
        byte[]? body = null;
        try
        {
            body = BuildRedeemBody(target, secret);
            if (body is null) return new(BlindInvitationOutcome.KeysUnavailable);

            using var client = Client(target);
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await client.PostAsync(Route(target, "redeem"), content, ct).ConfigureAwait(false);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Accepted:
                    return await ReadAsync<BlindInviteRedeemAnswer>(response, BlindInviteRedeemAnswer.TryParse, ct).ConfigureAwait(false) is { } answer
                        ? new(BlindInvitationOutcome.Ok, answer)
                        : new(BlindInvitationOutcome.NotUnderstood);
                default:
                    return Refusal<BlindInviteRedeemAnswer>(response);
            }
        }
        catch (Exception ex) when (Classify(ex, ct) is { } result)
        {
            return new(result.Outcome, Pause: result.Pause);
        }
        finally
        {
            if (body is not null) CryptographicOperations.ZeroMemory(body);
        }
    }

    /// <summary>Where the owner's decision stands, asked with a proof signed by the key this account redeemed with. The answer is one of <see cref="BlindInviteStates"/>.</summary>
    public async Task<BlindInvitationResult<string>> StatusAsync(BlindInvitationTarget target, CancellationToken ct)
    {
        try
        {
            if (state.NodeId is not { } nodeId) return new(BlindInvitationOutcome.KeysUnavailable);
            byte[] signature;
            var at = time.GetUtcNow().ToUnixTimeSeconds();
            byte[]? seed = null;
            try
            {
                seed = keys.LoadIdentitySeed();
                if (seed is null) return new(BlindInvitationOutcome.KeysUnavailable);
                signature = Ed25519Signer.Sign(seed, BlindInviteProofs.StatusMessage(target.InviteId, nodeId, at));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                return new(BlindInvitationOutcome.KeysUnavailable);
            }
            finally
            {
                if (seed is not null) CryptographicOperations.ZeroMemory(seed);
            }

            var request = new BlindInviteStatusRequest(target.InviteId, nodeId, at, signature);
            using var client = Client(target);
            using var content = new ByteArrayContent(request.ToJson());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await client.PostAsync(Route(target, "status"), content, ct).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) return Refusal<string>(response);
            return await ReadAsync<BlindInviteStatusAnswer>(response, BlindInviteStatusAnswer.TryParse, ct).ConfigureAwait(false) is { } answer
                ? new(BlindInvitationOutcome.Ok, answer.State)
                : new(BlindInvitationOutcome.NotUnderstood);
        }
        catch (Exception ex) when (Classify(ex, ct) is { } result)
        {
            return new(result.Outcome, Pause: result.Pause);
        }
    }

    // ---- building and reading ---------------------------------------------------------------------------------------------------------------

    /// <summary>The redemption's body, or null when the account's keys cannot be read. The seed and the backup key are wiped here; the body is the caller's to wipe.</summary>
    private byte[]? BuildRedeemBody(BlindInvitationTarget target, byte[] secret)
    {
        byte[]? seed = null;
        byte[]? backupKey = null;
        try
        {
            if (state.NodeId is not { } nodeId || state.PublicKey is not { } publicKey) return null;
            seed = keys.LoadIdentitySeed();
            backupKey = keys.LoadBackupKey();
            if (seed is null || backupKey is null) return null;
            var proof = Ed25519Signer.Sign(seed, BlindInviteProofs.RedeemMessage(target.InviteId, target.HubNodeId, nodeId, publicKey, backupKey));
            var request = new BlindInviteRedeemRequest(
                target.InviteId, secret, nodeId, publicKey, DisplayName(), Token(NodePlatform.Current, BlindInviteRedeemRequest.MaxPlatformLength),
                Token(BuildVersion.Current, BlindInviteRedeemRequest.MaxVersionLength), protocol?.Declared ?? SyncProtocolVersion.Current, backupKey, proof);
            return request.ToJson();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A secret store that does not answer (locked, denied, a blob that does not open) is no reason to send anything.
            return null;
        }
        finally
        {
            if (seed is not null) CryptographicOperations.ZeroMemory(seed);
            if (backupKey is not null) CryptographicOperations.ZeroMemory(backupKey);
        }
    }

    /// <summary>The name this device gave itself when its identity was made, if the hub will take it as it is; otherwise the neutral one.</summary>
    private string DisplayName() =>
        state.DisplayName is { } name && BlindInviteText.IsPlain(name, BlindPhoneCode.MaxDisplayNameLength) ? name : "Blind copy";

    /// <summary>A platform or a version in the form the hub takes (letters, digits and . + - _ only, at most <paramref name="max"/>): what does not fit is left out, never sent as it was.</summary>
    internal static string Token(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var clean = new string(value.Where(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '+' or '-' or '_').ToArray());
        return clean.Length <= max ? clean : clean[..max];
    }

    private HttpClient Client(BlindInvitationTarget target)
    {
        // A call code under a secret nobody holds: it only names the hub and its trust, so that the provider hands out the client that trust selects.
        var scratch = RandomNumberGenerator.GetBytes(BlindPairingSecret.Size);
        try
        {
            var client = http.GetClient(target.CallCode(scratch));
            client.Timeout = CallTimeout;
            return client;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(scratch);
        }
    }

    private static string Route(BlindInvitationTarget target, string name) => $"{target.Address.TrimEnd('/')}/api/sync/invite/{name}";

    /// <summary>What a status code other than the expected one means. The body is not read: nothing the hub wrote in it is taken over.</summary>
    private static BlindInvitationResult<T> Refusal<T>(HttpResponseMessage response) => (int)response.StatusCode switch
    {
        410 => new(BlindInvitationOutcome.NotValid, HubDate: response.Headers.Date),
        409 => new(BlindInvitationOutcome.KnownUnderOtherKey),
        429 => new(BlindInvitationOutcome.RateLimited, RetryAfter: RetryAfterOf(response)),
        503 => new(BlindInvitationOutcome.Locked, RetryAfter: RetryAfterOf(response)),
        404 or 405 => new(BlindInvitationOutcome.OlderHub),
        >= 500 => new(BlindInvitationOutcome.Unreachable),
        _ => new(BlindInvitationOutcome.NotUnderstood),
    };

    private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }

    /// <summary>The answer's body, read bounded and parsed with <paramref name="parse"/>; null when it is too long, not what the route answers, or cannot be read in time.</summary>
    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, ParseBody<T> parse, CancellationToken ct) where T : class
    {
        if (response.Content.Headers.ContentLength is > MaxAnswerBytes) return null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(SyncAddress.BodyTimeout);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, limit.Token).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + read > MaxAnswerBytes) return null;
                memory.Write(buffer, 0, read);
            }
            return parse(memory.ToArray(), out var value) ? value : null;
        }
        catch (Exception ex) when (ex is OperationCanceledException && !ct.IsCancellationRequested || ex is HttpRequestException or IOException)
        {
            return null;
        }
    }

    private delegate bool ParseBody<T>(ReadOnlySpan<byte> body, out T? value) where T : class;

    /// <summary>
    /// What an exception means: an outcome for the ones the network and the limits make, null for the ones that must not be turned into one (cancellation by the caller, and anything
    /// that is a bug). A refused certificate or pin is told apart from a hub that is simply not there.
    /// </summary>
    private static (BlindInvitationOutcome Outcome, BlindPause? Pause)? Classify(Exception ex, CancellationToken ct)
    {
        switch (ex)
        {
            case OperationCanceledException when ct.IsCancellationRequested:
                return null;
            case BlindPausedException paused:
                return (BlindInvitationOutcome.Paused, paused.Pause);
            case OperationCanceledException:                       // the call's own time ran out
                return (BlindInvitationOutcome.Unreachable, null);
            case HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException:
                return (RefusedByTls(ex) ? BlindInvitationOutcome.CertificateRefused : BlindInvitationOutcome.Unreachable, null);
            default:
                return null;
        }
    }

    private static bool RefusedByTls(Exception ex)
    {
        for (var inner = ex; inner is not null; inner = inner.InnerException)
            if (inner is AuthenticationException) return true;
        return false;
    }
}
