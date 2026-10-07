using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Core.Interfaces;
using Dapper;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>What a claim did with its code (<see cref="BlindRestoreCodeService.ConsumeAsync"/>).</summary>
public enum RestoreCodeUse
{
    /// <summary>The code was open and is now spent by this device.</summary>
    First,

    /// <summary>
    /// This same device spent it a moment ago, still within the code's lifetime: its claim is being repeated, because the
    /// first one failed after the code was spent (a crash, a database error) and the device retried.
    /// </summary>
    Repeat,

    /// <summary>Not open, and not spent by this device: refused.</summary>
    Refused
}

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

    /// <summary>
    /// True for an open code (issued, not expired, not used, not revoked) - or, when <paramref name="claimant"/> is given,
    /// for one that device itself spent within the code's lifetime (a repeated claim, <see cref="RestoreCodeUse.Repeat"/>).
    /// A wrong code is counted.
    /// </summary>
    public async Task<bool> ValidateAsync(string? code, Guid? claimant = null)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var now = DateTime.UtcNow.ToString("O");
        using var conn = connFactory.CreateConnection();
        var open = await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM tbl_blind_restore_code
              WHERE code_hash = @H AND used_at IS NULL AND revoked = 0 AND expires_at > @Now",
            new { H = Hash(code), Now = now });
        if (open > 0) return true;
        if (claimant is { } node && await SpentByAsync(conn, code, node, now)) return true;

        await conn.ExecuteAsync(
            @"UPDATE tbl_blind_restore_code SET failed_attempts = failed_attempts + 1
              WHERE used_at IS NULL AND revoked = 0 AND expires_at > @Now;
              UPDATE tbl_blind_restore_code SET revoked = 1
              WHERE used_at IS NULL AND revoked = 0 AND failed_attempts >= @Max",
            new { Now = now, Max = MaxFailures });
        return false;
    }

    /// <summary>
    /// Uses the code up for <paramref name="claimedBy"/>. A code this same device already spent, within its lifetime, is
    /// answered <see cref="RestoreCodeUse.Repeat"/> instead of refused: the claim after it writes to other tables on other
    /// connections, and a failure there (or a crash) used to burn the only code while the device was not trusted - its
    /// retry got 401, and the person had to issue a new code at the console. Possession of the code stays the only
    /// authority: a second device (another node id) is refused, and what a repeat may write is the caller's to limit.
    /// </summary>
    public async Task<RestoreCodeUse> ConsumeAsync(string code, Guid claimedBy)
    {
        var now = DateTime.UtcNow.ToString("O");
        using var conn = connFactory.CreateConnection();
        var changed = await conn.ExecuteAsync(
            @"UPDATE tbl_blind_restore_code SET used_at = @Now, claimed_node_id = @Node
              WHERE code_hash = @H AND used_at IS NULL AND revoked = 0 AND expires_at > @Now",
            new { H = Hash(code), Now = now, Node = claimedBy.ToString() });
        if (changed == 1) return RestoreCodeUse.First;
        return await SpentByAsync(conn, code, claimedBy, now) ? RestoreCodeUse.Repeat : RestoreCodeUse.Refused;
    }

    private static async Task<bool> SpentByAsync(System.Data.IDbConnection conn, string code, Guid node, string now) =>
        await conn.ExecuteScalarAsync<long>(
            @"SELECT COUNT(*) FROM tbl_blind_restore_code
              WHERE code_hash = @H AND used_at IS NOT NULL AND revoked = 0 AND expires_at > @Now
                AND claimed_node_id = @Node COLLATE NOCASE",
            new { H = Hash(code), Now = now, Node = node.ToString() }) > 0;

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
