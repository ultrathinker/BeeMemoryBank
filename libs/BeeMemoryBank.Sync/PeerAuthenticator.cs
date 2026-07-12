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

    /// <summary>
    /// Authenticates to a remote sync peer and returns a Bearer token.
    ///
    /// Flow (byte-for-byte identical to what <see cref="SyncClient"/> used to inline):
    ///   POST <paramref name="baseUrl"/>/api/sync/challenge
    ///   → sign the returned challenge (tagged with the literal bytes "BMB-CHALLENGE-V1\0"
    ///     prepended) via <paramref name="authSigner"/>
    ///   → POST <paramref name="baseUrl"/>/api/sync/authenticate with
    ///     { NodeId, ChallengeB64, SignatureB64 }
    ///   → Bearer token.
    /// </summary>
    /// <param name="authSigner">Signs the challenge with this node's Ed25519 key.</param>
    /// <param name="http">HttpClient used for the two round-trips.</param>
    /// <param name="baseUrl">Remote node base URL (no trailing slash required).</param>
    /// <param name="identity">This node's identity (NodeId + keys).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The Bearer token to attach to subsequent peer requests.</returns>
    public static async Task<string> AuthenticateAsync(
        INodeAuthSigner authSigner,
        HttpClient http,
        string baseUrl,
        NodeIdentity identity,
        CancellationToken ct = default)
    {
        // Get challenge
        var challengeResp = await http.PostAsync($"{baseUrl}/api/sync/challenge", null, ct);
        challengeResp.EnsureSuccessStatusCode();
        var challengeData = await challengeResp.Content.ReadFromJsonAsync<ChallengeDto>(JsonOpts, ct)
            ?? throw new InvalidDataException("Invalid challenge response.");

        // Sign challenge with domain tag (server-side verifier requires tagged form).
        var challengeBytes = Convert.FromBase64String(challengeData.Challenge);
        var domainTag = "BMB-CHALLENGE-V1\0"u8.ToArray();
        var challengePayload = domainTag.Concat(challengeBytes).ToArray();
        // Signing is delegated to INodeAuthSigner: the default derives the key via the master
        // DEK (server/CLI/unlocked foreground), while the mobile background ingest path signs
        // via a hardware-backed Keystore key with no DEK — enabling unattended backup-sync.
        var signature = authSigner.SignChallenge(identity, challengePayload);

        // Authenticate
        var authResp = await http.PostAsJsonAsync($"{baseUrl}/api/sync/authenticate", new
        {
            NodeId = identity.NodeId,
            ChallengeB64 = challengeData.Challenge,
            SignatureB64 = Convert.ToBase64String(signature)
        }, ct);
        authResp.EnsureSuccessStatusCode();
        var authData = await authResp.Content.ReadFromJsonAsync<AuthTokenDto>(JsonOpts, ct)
            ?? throw new InvalidDataException("Invalid authenticate response.");

        return authData.Token;
    }

    private sealed record ChallengeDto(string Challenge, Guid ServerNodeId);
    private sealed record AuthTokenDto(string Token);
}
