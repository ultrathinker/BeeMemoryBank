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

    // V2 binds the signed payload to the specific server we intend to authenticate to: otherwise a
    // malicious/compromised peer, or a plain-HTTP LAN MITM (realistic given mDNS discovery), that
    // we're authenticating TO could fetch a fresh challenge from some unrelated third node C and
    // hand it to us as its own. The unbound "BMB-CHALLENGE-V1\0" + challenge payload says nothing
    // about WHO the signature is for, so it verifies just as well at C as at the peer we think we
    // are talking to, handing whoever relayed it a Bearer token AS US on a node we never intended
    // to contact.
    //
    // THERE IS NO V1 FALLBACK, and adding one would silently undo all of the above. Whether a
    // challenge response carries ServerNodeId is the responding peer's choice, not a property of
    // genuinely old peers: a fallback on its absence lets any attacker omit it, get us to produce
    // an unbound V1 signature over a challenge relayed from node C, and redeem it at C — the exact
    // relay attack V2 exists to stop, reachable by deleting one line of JSON. A peer that does
    // not declare an audience fails authentication outright.
    //
    // The matching server-side verifier in SyncEndpoints.cs accepts V2 only, for the same reason,
    // so every node in the mesh must speak V2 to sync.
    private static readonly byte[] DomainTagV2 = "BMB-CHALLENGE-V2\0"u8.ToArray();

    /// <summary>
    /// Authenticates to a remote sync peer and returns a Bearer token.
    ///
    /// Flow:
    ///   POST <paramref name="baseUrl"/>/api/sync/challenge
    ///   → verify the response's ServerNodeId matches <paramref name="expectedServerNodeId"/>
    ///     (refuses to sign a challenge issued for a different node than the one we intended
    ///     to dial; see the domain-tag comment above for what this closes)
    ///   → sign the challenge (tagged with the audience-bound "BMB-CHALLENGE-V2\0" + server NodeId
    ///     prefix) via <paramref name="authSigner"/>
    ///   → POST <paramref name="baseUrl"/>/api/sync/authenticate with
    ///     { NodeId, ChallengeB64, SignatureB64, ProtocolVersion }
    ///   → Bearer token.
    ///
    /// A 401 is a plain failure with no retry. There is deliberately no unbound-payload fallback
    /// to downgrade into — see the domain-tag comment above.
    /// </summary>
    /// <param name="authSigner">Signs the challenge with this node's Ed25519 key.</param>
    /// <param name="http">HttpClient used for the round-trips.</param>
    /// <param name="baseUrl">Remote node base URL (no trailing slash required).</param>
    /// <param name="identity">This node's identity (NodeId + keys).</param>
    /// <param name="expectedServerNodeId">
    /// The NodeId we independently believe <paramref name="baseUrl"/> belongs to. This MUST come
    /// from a source the peer at <paramref name="baseUrl"/> doesn't control — the whitelist
    /// entry's NodeId for the peer being dialed (tbl_whitelist, pinned out-of-band when the peer
    /// was added), never a value read from this same connection (e.g. a prior
    /// <c>/api/sync/identity</c> call), which a malicious/compromised peer can set to whatever it
    /// likes. This is the audience anchor: we refuse to sign a challenge whose claimed
    /// ServerNodeId doesn't match it. See <see cref="SyncClient"/>'s caller for how it resolves
    /// this from the whitelist rather than trusting the peer's own self-report.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="declaredProtocol">
    /// The protocol this node declares (<see cref="Metadata.ISyncProtocolPolicy.Declared"/>): 3 until it has applied the
    /// metadata activation, 4 after. A caller without the policy declares 3 (<see cref="SyncProtocolVersion.Current"/>).
    /// </param>
    /// <param name="expectedServerPublicKey">
    /// The key this node knows the peer by (its whitelist row). With it, a 401 that carries the peer's signed "this device was revoked" refusal
    /// (<see cref="DeviceRevokedProof"/>) becomes a <see cref="ThisDeviceRevokedException"/>; without it, or when the proof does not verify under
    /// it, the 401 stays a plain refusal.
    /// </param>
    /// <returns>The Bearer token to attach to subsequent peer requests.</returns>
    public static async Task<string> AuthenticateAsync(
        INodeAuthSigner authSigner,
        HttpClient http,
        string baseUrl,
        NodeIdentity identity,
        Guid expectedServerNodeId,
        CancellationToken ct = default,
        int? declaredProtocol = null,
        IReadOnlyList<NodeKeyLinkDto>? keyChain = null,
        byte[]? expectedServerPublicKey = null)
    {
        return await AuthenticateOnceAsync(
            authSigner, http, baseUrl, identity, expectedServerNodeId, ct, declaredProtocol, keyChain, expectedServerPublicKey);
    }

    private static async Task<string> AuthenticateOnceAsync(
        INodeAuthSigner authSigner,
        HttpClient http,
        string baseUrl,
        NodeIdentity identity,
        Guid expectedServerNodeId,
        CancellationToken ct,
        int? declaredProtocol,
        IReadOnlyList<NodeKeyLinkDto>? keyChain,
        byte[]? expectedServerPublicKey)
    {
        // TrimEnd('/'): the two requests below are string-concatenated onto baseUrl, so a base
        // ending in a slash yields "//api/sync/challenge" and a 404. Callers that come through
        // SyncClient are already normalized; this covers the ones that call in directly.
        baseUrl = baseUrl.TrimEnd('/');

        // Get challenge
        var challengeResp = await http.PostAsync($"{baseUrl}/api/sync/challenge", null, ct);
        challengeResp.EnsureSuccessStatusCode();
        var challengeData = await challengeResp.Content.ReadFromJsonAsync<ChallengeDto>(JsonOpts, ct)
            ?? throw new InvalidDataException("Invalid challenge response.");

        // Audience check: refuse to sign a challenge issued for a DIFFERENT node than the one
        // we intended to dial. This is what actually stops the relay attack described above — the
        // server-side signature verification alone isn't enough, because a peer that honestly
        // relays a foreign ServerNodeId (rather than lying about it) would otherwise get us to
        // embed that foreign node's id ourselves, producing a signature that's perfectly valid
        // there. Not a substitute for the server-side check (SyncEndpoints.cs verifies against ITS
        // OWN recorded identity, not anything the caller claims) — the two are complementary: this
        // one stops us from signing for the wrong audience in the first place; that one stops a
        // signature from being redeemable anywhere but the node it was actually bound to.
        //
        // A missing ServerNodeId is a hard failure, not a compatibility case. The responding peer
        // decides whether to send the field, so "absent" tells us nothing about how old that peer
        // is — treating it as "old peer, sign the unbound V1 payload instead" would let anyone
        // downgrade us out of the audience binding by omitting one JSON property, and then relay
        // the resulting signature to the node it was really fetched from.
        if (challengeData.ServerNodeId is not { } actualServerNodeId)
            throw new InvalidOperationException(
                $"Peer at {baseUrl} returned a challenge with no ServerNodeId. Refusing to sign an " +
                "unbound challenge — every node on this build declares its own id, so this is " +
                "either a peer older than the mesh-wide V2 upgrade or an attempted downgrade.");

        if (actualServerNodeId != expectedServerNodeId)
            throw new InvalidOperationException(
                $"Challenge audience mismatch from {baseUrl}: expected node {expectedServerNodeId}, " +
                $"got {actualServerNodeId}. Refusing to sign — this looks like a relayed/foreign " +
                "challenge (possible MITM).");

        var challengeBytes = Convert.FromBase64String(challengeData.Challenge);
        // Signing is delegated to INodeAuthSigner: the default derives the key via the master
        // DEK (server/CLI/unlocked foreground), while the mobile background ingest path signs
        // via a hardware-backed Keystore key with no DEK — enabling unattended backup-sync.
        var challengePayload = DomainTagV2
            .Concat(expectedServerNodeId.ToByteArray())
            .Concat(challengeBytes)
            .ToArray();
        var signature = authSigner.SignChallenge(identity, challengePayload);

        // Authenticate. Every 2.6 build advertises the envelope capability whatever it declares (docs/design/step-a-spec.md
        // 5.1, 5.6): an activated peer still lets in a node that declares 3 with it (one that has not applied the activation
        // yet), and its readiness report counts this node as ready.
        var authResp = await http.PostAsJsonAsync($"{baseUrl}/api/sync/authenticate", new
        {
            NodeId = identity.NodeId,
            ChallengeB64 = challengeData.Challenge,
            SignatureB64 = Convert.ToBase64String(signature),
            ProtocolVersion = declaredProtocol ?? SyncProtocolVersion.Current,
            Capabilities = Metadata.MetadataCapabilities.Advertised,
            AppVersion = Metadata.BuildVersion.Current,
            // What this build runs on, for the network map of the node that is called (a peer that does not know the fields ignores them).
            Platform = Metadata.NodePlatform.Current,
            InContainer = Metadata.NodePlatform.InContainer,
            // This node's key transition chain (release 2.12, plan WS-D D4): a peer that has not learned this node's re-key yet lets it in when
            // the chain leads from the key it has on record to the key that signed. Null (not sent) for a node that never re-keyed.
            KeyChain = keyChain is { Count: > 0 } ? keyChain : null,
        }, ct);

        if (authResp.StatusCode == HttpStatusCode.Unauthorized && expectedServerPublicKey is not null
            && await ReadRevokedProofAsync(authResp, ct) is { } proof
            && DeviceRevokedProof.Verify(expectedServerPublicKey, expectedServerNodeId, identity.NodeId, challengeBytes, proof))
            throw new ThisDeviceRevokedException(expectedServerNodeId);
        authResp.EnsureSuccessStatusCode();
        var authData = await authResp.Content.ReadFromJsonAsync<AuthTokenDto>(JsonOpts, ct)
            ?? throw new InvalidDataException("Invalid authenticate response.");

        return authData.Token;
    }

    /// <summary>The signature of a "this device was revoked" refusal, or null for any other 401 (no body, another body, a body that cannot be read).</summary>
    private static async Task<byte[]?> ReadRevokedProofAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<RevokedRefusalDto>(JsonOpts, ct);
            if (body is null || body.Refusal != DeviceRevokedProof.Refusal || string.IsNullOrEmpty(body.ProofB64)) return null;
            return Convert.FromBase64String(body.ProofB64);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private sealed record RevokedRefusalDto(string? Refusal, string? ProofB64);

    // ServerNodeId stays nullable so an omitted field is distinguishable from a declared
    // Guid.Empty — a non-nullable Guid would deserialize both to the same value, and the two get
    // different error messages above.
    private sealed record ChallengeDto(string Challenge, Guid? ServerNodeId);
    private sealed record AuthTokenDto(string Token);
}
