using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Api.Middleware;

/// <summary>
/// Decides whether a request came from the node's own trusted inside — the Web layer, the desktop
/// tray, the CLI — by comparing its <c>X-Internal-Key</c> header against <c>BMB_INTERNAL_KEY</c>.
///
/// <para>Everything downstream keys off this answer: <c>CallerIdentity.Extract</c> only honours the
/// forwarded <c>X-User-Id</c>/<c>X-User-Role</c> headers for a caller that passes here, and
/// <c>PublicSurfaceMiddleware</c> now decides the node's ENTIRE reachable surface from it. So a
/// caller this method says yes to is a full superadmin for the asking: it just has to state the
/// role it wants in a header.</para>
///
/// <para><b>There used to be a second way to say yes.</b> When <c>BMB_INTERNAL_KEY</c> was unset,
/// this fell through to "trust any caller whose remote address is loopback" — a convenience from
/// when running the API and Web from two terminals meant no shared secret existed yet. It is gone
/// because it can no longer be reached and, since PublicSurface, would be far worse than it was:
/// with the fallback live, ANY process on the same host — a second container, another user's shell,
/// anything that gets a request onto 127.0.0.1 through a proxy hop — would be fully trusted.
/// Unreachable, because the API process guarantees the variable is set before the first request:
/// <c>Program.cs</c> throws at startup in Production when it is missing, and in every other
/// environment generates one into <c>{dataPath}/.internal-key</c> and sets it. Do not restore the
/// fallback; if two processes need to agree on a key, share the file, which is what Web already
/// does.</para>
/// </summary>
public static class InternalKeyValidator
{
    public static bool Validate(HttpContext ctx)
    {
        var expectedKey = Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY");
        // Fail closed. Reaching this would mean the startup guarantee above was broken, and the
        // safe reading of "the node has no internal key" is that nobody holds it, not everybody.
        if (string.IsNullOrEmpty(expectedKey)) return false;

        var providedKey = ctx.Request.Headers["X-Internal-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(providedKey)) return false;
        // AUDIT NOTE: FixedTimeEquals returns false (not throws) when arrays have different lengths.
        // No timing leak on key length — both are UTF8 byte arrays, comparison is constant-time
        // regardless of length mismatch. This is safe as-is per .NET documentation.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(providedKey),
            Encoding.UTF8.GetBytes(expectedKey));
    }
}
