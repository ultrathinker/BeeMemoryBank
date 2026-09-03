using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync;

/// <summary>
/// Performs the peer-to-peer challenge / sign / authenticate handshake against a remote
/// sync node and returns a Bearer token. Extracted from <see cref="SyncClient"/> so the
/// reachability self-test flow (and any other peer-calling code path) reuses the exact
/// same authentication flow rather than duplicating challenge-sign-authenticate logic into
/// a second place.
/// </summary>
/// <remarks>
/// Stateless by design — this mirrors the codebase convention for crypto/auth helpers
/// (<see cref="BeeMemoryBank.Crypto.NodeIdentityCrypto"/>, Ed25519Signer, KeyDerivation),
/// which are static classes taking their dependencies as parameters. The only collaborator
/// (<see cref="INodeAuthSigner"/>) is passed in explicitly so the caller's DI wiring decides
/// how the signing key is derived (master DEK vs. hardware-backed Keystore).
/// </remarks>
public static class PeerAuthenticator
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // V2 binds the signed payload to the specific server we intend to authenticate to (M6): a
    // malicious/compromised peer, or a plain-HTTP LAN MITM (realistic given mDNS discovery), that
    // we're authenticating TO could otherwise fetch a fresh challenge from some unrelated third
    // node C and hand it to us as its own — nothing in the old "BMB-CHALLENGE-V1\0" + challenge
    // payload said WHO the signature was for, so it verified just as well at C as at the peer we
    // thought we were talking to, handing whoever relayed it a Bearer token AS US on a node we
    // never intended to contact. V1 is kept as a same-cycle fallback purely for interop with a
    // peer that hasn't upgraded past this fix yet (its /api/sync/authenticate only knows how to
    // verify the old tag) — see AuthenticateAsync below and the matching server-side check in
    // SyncEndpoints.cs. Retire the V1 branch (both here and server-side) once the whole mesh has
    // upgraded; until then a not-yet-upgraded peer gets exactly the protection it had before M6
    // (none), which is no worse than today.
    private static readonly byte[] DomainTagV2 = "BMB-CHALLENGE-V2\0"u8.ToArray();
    private static readonly byte[] DomainTagV1 = "BMB-CHALLENGE-V1\0"u8.ToArray();

    /// <summary>
    /// Authenticates to a remote sync peer and returns a Bearer token.
    ///
    /// Flow:
    ///   POST <paramref name="baseUrl"/>/api/sync/challenge
    ///   → verify the response's ServerNodeId matches <paramref name="expectedServerNodeId"/>
    ///     (M6 — refuses to sign a challenge issued for a different node than the one we intended
    ///     to dial; see the domain-tag comment above for what this closes)
    ///   → sign the challenge (tagged with the audience-bound "BMB-CHALLENGE-V2\0" + server NodeId
    ///     prefix) via <paramref name="authSigner"/>
    ///   → POST <paramref name="baseUrl"/>/api/sync/authenticate with
    ///     { NodeId, ChallengeB64, SignatureB64 }
    ///   → on a 401 (peer hasn't upgraded past M6 yet), retry once with the legacy unbound
    ///     "BMB-CHALLENGE-V1\0" tag
    ///   → Bearer token.
    /// </summary>
    /// <param name="authSigner">Signs the challenge with this node's Ed25519 key.</param>
    /// <param name="http">HttpClient used for the round-trips.</param>
    /// <param name="baseUrl">Remote node base URL (no trailing slash required).</param>
    /// <param name="identity">This node's identity (NodeId + keys).</param>
    /// <param name="expectedServerNodeId">
    /// The NodeId we independently believe <paramref name="baseUrl"/> belongs to — e.g. the
    /// whitelist entry's NodeId for the peer being dialed, or the NodeId a prior
    /// <c>/api/sync/identity</c> call on this same connection returned. This is the audience
    /// anchor: we refuse to sign a challenge whose claimed ServerNodeId doesn't match it.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The Bearer token to attach to subsequent peer requests.</returns>
    public static async Task<string> AuthenticateAsync(
        INodeAuthSigner authSigner,
        HttpClient http,
        string baseUrl,
        NodeIdentity identity,
        Guid expectedServerNodeId,
        CancellationToken ct = default)
    {
        var (token, unauthorized) = await AuthenticateOnceAsync(
            authSigner, http, baseUrl, identity, expectedServerNodeId, bindAudience: true, throwOnFailure: false, ct);
        if (!unauthorized) return token!;

        // Fall back to the legacy unbound format once. A genuine auth failure (revoked node,
        // wrong key, expired challenge) 401s again here and throws normally via
        // EnsureSuccessStatusCode below, same as it always has.
        (token, _) = await AuthenticateOnceAsync(
            authSigner, http, baseUrl, identity, expectedServerNodeId, bindAudience: false, throwOnFailure: true, ct);
        return token!;
    }

    private static async Task<(string? Token, bool Unauthorized)> AuthenticateOnceAsync(
        INodeAuthSigner authSigner,
        HttpClient http,
        string baseUrl,
        NodeIdentity identity,
        Guid expectedServerNodeId,
        bool bindAudience,
        bool throwOnFailure,
        CancellationToken ct)
    {
        // Get challenge
        var challengeResp = await http.PostAsync($"{baseUrl}/api/sync/challenge", null, ct);
        challengeResp.EnsureSuccessStatusCode();
        var challengeData = await challengeResp.Content.ReadFromJsonAsync<ChallengeDto>(JsonOpts, ct)
            ?? throw new InvalidDataException("Invalid challenge response.");

        // Audience check (M6): refuse to sign a challenge issued for a DIFFERENT node than the one
        // we intended to dial. This is what actually stops the relay attack described above — the
        // server-side signature verification alone isn't enough, because a peer that honestly
        // relays a foreign ServerNodeId (rather than lying about it) would otherwise get us to
        // embed that foreign node's id ourselves, producing a signature that's perfectly valid
        // there. Not a substitute for the server-side check (SyncEndpoints.cs verifies against ITS
        // OWN recorded identity, not anything the caller claims) — the two are complementary: this
        // one stops us from signing for the wrong audience in the first place; that one stops a
        // signature from being redeemable anywhere but the node it was actually bound to.
        if (challengeData.ServerNodeId != expectedServerNodeId)
            throw new InvalidOperationException(
                $"Challenge audience mismatch from {baseUrl}: expected node {expectedServerNodeId}, got " +
                $"{challengeData.ServerNodeId}. Refusing to sign — this looks like a relayed/foreign " +
                "challenge (possible MITM).");

        var challengeBytes = Convert.FromBase64String(challengeData.Challenge);
        // Signing is delegated to INodeAuthSigner: the default derives the key via the master
        // DEK (server/CLI/unlocked foreground), while the mobile background ingest path signs
        // via a hardware-backed Keystore key with no DEK — enabling unattended backup-sync.
        var domainTag = bindAudience ? DomainTagV2 : DomainTagV1;
        var challengePayload = bindAudience
            ? domainTag.Concat(expectedServerNodeId.ToByteArray()).Concat(challengeBytes).ToArray()
            : domainTag.Concat(challengeBytes).ToArray();
        var signature = authSigner.SignChallenge(identity, challengePayload);

        // Authenticate
        var authResp = await http.PostAsJsonAsync($"{baseUrl}/api/sync/authenticate", new
        {
            NodeId = identity.NodeId,
            ChallengeB64 = challengeData.Challenge,
            SignatureB64 = Convert.ToBase64String(signature)
        }, ct);

        if (!throwOnFailure && authResp.StatusCode == HttpStatusCode.Unauthorized)
            return (null, true);

        authResp.EnsureSuccessStatusCode();
        var authData = await authResp.Content.ReadFromJsonAsync<AuthTokenDto>(JsonOpts, ct)
            ?? throw new InvalidDataException("Invalid authenticate response.");

        return (authData.Token, false);
    }

    private sealed record ChallengeDto(string Challenge, Guid ServerNodeId);
    private sealed record AuthTokenDto(string Token);
}
