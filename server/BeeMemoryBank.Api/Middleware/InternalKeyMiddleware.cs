using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Api.Middleware;

public static class InternalKeyValidator
{
    public static bool Validate(HttpContext ctx)
    {
        var expectedKey = Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY");
        if (string.IsNullOrEmpty(expectedKey))
        {
            var remoteIp = ctx.Connection.RemoteIpAddress;
            return remoteIp != null && (
                remoteIp.Equals(System.Net.IPAddress.Loopback) ||
                remoteIp.Equals(System.Net.IPAddress.IPv6Loopback) ||
                remoteIp.ToString() == "::1");
        }
        var providedKey = ctx.Request.Headers["X-Internal-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(providedKey)) return false;
        // AUDIT NOTE: FixedTimeEquals returns false (not throws) when arrays have different lengths.
        // No timing leak on key length — both are UTF8 byte arrays, comparison is constant-time
        // regardless of length mismatch. This is safe as-is per .NET documentation.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(providedKey),
            Encoding.UTF8.GetBytes(expectedKey));
    }

    /// <summary>
    /// Combined gate: must hold both `BMB_INTERNAL_KEY` (or be loopback) AND advertise the
    /// expected role via `X-User-Role`. The role header is operator-asserted, so we must
    /// NEVER honor it without the prior internal-key check — otherwise any client could
    /// claim superadmin. By exposing the two checks as a single helper we make it
    /// structurally hard to do a "role check only" by accident.
    /// </summary>
    public static bool ValidateInternalAndRole(HttpContext ctx, string expectedRole = UserRoles.Superadmin)
    {
        if (!Validate(ctx)) return false;
        return ctx.Request.Headers["X-User-Role"].FirstOrDefault() == expectedRole;
    }
}
