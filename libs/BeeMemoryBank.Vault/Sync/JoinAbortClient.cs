using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Sync;

/// <summary>What a joiner learned from asking the host to take its row back.</summary>
public enum JoinAbortOutcome
{
    /// <summary>The host revoked the row the join had made.</summary>
    Removed,

    /// <summary>The host holds no active row for this join (never written, or already revoked): nothing is left there.</summary>
    NothingToRemove,

    /// <summary>The host or a proxy in front of it does not know the route (404 or 405): a host older than 2.5.1, or a proxy that does not forward it.</summary>
    NotSupported,

    /// <summary>Anything else: refused, unreachable, too slow. Whatever the host holds is unknown.</summary>
    Failed
}

/// <summary>
/// The joiner's side of <c>POST /api/join/abort</c>: after a join that the host answered but that then failed on this side, the
/// host is asked to revoke the row it wrote (an active, possibly superadmin, never-synced peer nobody holds the key of, which
/// holds back compaction). Best effort by design: it never throws, never takes longer than <see cref="Timeout"/>, and says only
/// what the host said, so a caller can keep its own error and fall back to telling the person which row to revoke.
/// </summary>
public static class JoinAbortClient
{
    /// <summary>How long the host may take. The abort answers after one password check; it never waits for a snapshot.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>True when the host no longer lists the joiner as a member (<see cref="JoinAbortOutcome.Removed"/> or <see cref="JoinAbortOutcome.NothingToRemove"/>).</summary>
    public static bool HostForgot(this JoinAbortOutcome outcome) => outcome is JoinAbortOutcome.Removed or JoinAbortOutcome.NothingToRemove;

    /// <summary>
    /// Whether a join request that failed with <paramref name="ex"/> may still have been processed by the host: it does not
    /// when the connection was never made (refused, no such name, TLS refused, proxy tunnel), and does when the request went out and
    /// the answer did not come back (a timeout, a reset, a broken answer).
    /// </summary>
    public static bool MayHaveReachedHost(HttpRequestException ex) =>
        ex.HttpRequestError is not (HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
            or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError);

    /// <summary>
    /// Asks the host at <paramref name="baseUrl"/> to take back the row of this join. <paramref name="http"/> must be the client
    /// the join itself used (no redirects, the join code's key pin if there was a code), and <paramref name="joinToken"/> the code's
    /// token if there was one: the abort carries the master password and goes only where the join went.
    /// </summary>
    public static async Task<JoinAbortOutcome> TryAbortAsync(
        HttpClient http,
        string baseUrl,
        string masterPassword,
        Guid nodeId,
        byte[] publicKey,
        string? joinToken = null,
        TimeSpan? limit = null)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/join/abort")
            {
                Content = JsonContent.Create(new
                {
                    masterPassword,
                    nodeId,
                    ed25519PublicKeyB64 = Convert.ToBase64String(publicKey)
                }, options: new JsonSerializerOptions(JsonSerializerDefaults.Web))
            };
            if (joinToken != null) request.Headers.Add(JoinCode.TokenHeader, joinToken);

            using var response = await JoinHttp.BoundAsync(t => http.SendAsync(request, t), limit: limit ?? Timeout);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) return JoinAbortOutcome.NotSupported;
            if (!response.IsSuccessStatusCode) return JoinAbortOutcome.Failed;

            try
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("removed", out var removed)
                    && removed.ValueKind == JsonValueKind.False)
                    return JoinAbortOutcome.NothingToRemove;
            }
            catch (JsonException) { /* a 2xx whose body is not ours: the host did not refuse */ }
            return JoinAbortOutcome.Removed;
        }
        catch
        {
            return JoinAbortOutcome.Failed;
        }
    }
}
