using System.Security.Cryptography;
using System.Text;

namespace BeeMemoryBank.Api.Helpers;

public static class RemoteTokenHelper
{
    // SHA-256 hex digest. Tokens themselves look like "bmbrt_<40-hex>".
    public static string Hash(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    public static string GenerateToken()
    {
        Span<byte> raw = stackalloc byte[20]; // 20 bytes → 40 hex chars
        RandomNumberGenerator.Fill(raw);
        return "bmbrt_" + Convert.ToHexStringLower(raw);
    }
}
