using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Core.Interfaces;
using Dapper;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>One issued restore code, as the log shows it (never the code itself).</summary>
public sealed record RestoreCodeLogEntry(string IssuedAt, string ExpiresAt, string? IssuedBy, string? UsedAt, string? ClaimedNodeId, bool Revoked);

/// <summary>
/// One-time restore codes on a blind node (plan 6.7, 9): a code opens the recovery package and lets a new
/// device claim this blind node. 15 minutes, single use, 80 random bits; only its hash is stored. Wrong
/// codes count against every code still open: after <see cref="MaxFailures"/> of them all open codes are
/// revoked, so guessing cannot outrun the lifetime. Every issue is logged (audit log + the table itself).
/// </summary>
public class BlindRestoreCodeService(IDbConnectionFactory connFactory, IAuditLogRepository audit)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    public const int MaxFailures = 5;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O, 1/I

    public async Task<(string Code, DateTime ExpiresAt)> IssueAsync(string? issuedBy)
    {
        var chars = new char[16];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        var raw = new string(chars);
        var code = $"{raw[..4]}-{raw[4..8]}-{raw[8..12]}-{raw[12..]}";
        var now = DateTime.UtcNow;
        var expires = now + Lifetime;

        var hash = Hash(code);
        using (var conn = connFactory.CreateConnection())
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_blind_restore_code (code_hash, issued_at, expires_at, issued_by)
                  VALUES (@H, @I, @E, @B)",
                new { H = hash, I = now.ToString("O"), E = expires.ToString("O"), B = issuedBy });

        // The log names the code by a short prefix of its hash — enough to match log lines, useless
        // for redeeming it.
        await audit.LogAsync("blind_restore_code", hash[..12], "blind_restore_code_issued", "console",
            $"Restore code issued by {issuedBy ?? "console"}, valid until {expires:O}");
        return (code, expires);
    }

    /// <summary>True for an open code (issued, not expired, not used, not revoked). A wrong code is counted.</summary>
    public async Task<bool> ValidateAsync(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var now = DateTime.UtcNow.ToString("O");
        using var conn = connFactory.CreateConnection();
        var open = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM tbl_blind_restore_code
              WHERE code_hash = @H AND used_at IS NULL AND revoked = 0 AND expires_at > @Now",
            new { H = Hash(code), Now = now });
        if (open > 0) return true;

        await conn.ExecuteAsync(
            @"UPDATE tbl_blind_restore_code SET failed_attempts = failed_attempts + 1
              WHERE used_at IS NULL AND revoked = 0 AND expires_at > @Now;
              UPDATE tbl_blind_restore_code SET revoked = 1
              WHERE used_at IS NULL AND revoked = 0 AND failed_attempts >= @Max",
            new { Now = now, Max = MaxFailures });
        return false;
    }

    /// <summary>Uses the code up. False when it was not open (or someone used it a moment ago).</summary>
    public async Task<bool> ConsumeAsync(string code, Guid claimedBy)
    {
        using var conn = connFactory.CreateConnection();
        var changed = await conn.ExecuteAsync(
            @"UPDATE tbl_blind_restore_code SET used_at = @Now, claimed_node_id = @Node
              WHERE code_hash = @H AND used_at IS NULL AND revoked = 0 AND expires_at > @Now",
            new { H = Hash(code), Now = DateTime.UtcNow.ToString("O"), Node = claimedBy.ToString() });
        return changed == 1;
    }

    public async Task<List<RestoreCodeLogEntry>> LogAsync(int limit = 50)
    {
        using var conn = connFactory.CreateConnection();
        return (await conn.QueryAsync<(string, string, string?, string?, string?, long)>(
                @"SELECT issued_at, expires_at, issued_by, used_at, claimed_node_id, revoked
                  FROM tbl_blind_restore_code ORDER BY issued_at DESC LIMIT @L", new { L = limit }))
            .Select(r => new RestoreCodeLogEntry(r.Item1, r.Item2, r.Item3, r.Item4, r.Item5, r.Item6 != 0))
            .ToList();
    }

    // Case and dashes do not matter to the person typing it.
    private static string Hash(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            new string(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()))));
}
