using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services.BlindConsole;

/// <summary>
/// The blind console's own login (plan §9): a console password, stored as an Argon2id hash in
/// <c>{dataPath}/blind/console.json</c> (a file, not a vault table — the console must be able to
/// log in before and after a reseed, and the vault DB is replaced wholesale by one). Attempt
/// limiting lives in the same file: five bad passwords within 15 minutes set a fixed
/// <c>locked_until</c> 15 minutes ahead, persisted so a restart does not lift it, and requests
/// during the lock neither count nor extend it. The login log is what the console shows as
/// "Sign-in log".
/// </summary>
public sealed class BlindConsoleAuthService(
    string dataPath, ILogger<BlindConsoleAuthService> logger, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private const int MaxFailures = 5;
    private static readonly TimeSpan LockWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);
    private const int LogCap = 200;
    private const int SaltBytes = 32;

    private readonly string _dir = Path.Combine(dataPath, "blind");
    private string StateFile => Path.Combine(_dir, "console.json");
    private const string StateFileName = "blind/console.json";
    private readonly object _sync = new();

    public sealed class ConsoleState
    {
        [JsonPropertyName("hash_b64")] public string? HashB64 { get; set; }
        [JsonPropertyName("salt_b64")] public string? SaltB64 { get; set; }
        [JsonPropertyName("logins")] public List<LoginEntry> Logins { get; set; } = [];
        /// <summary>Set once, when the fifth failure lands; nothing moves it until it passes.</summary>
        [JsonPropertyName("locked_until")] public DateTime? LockedUntil { get; set; }
        /// <summary>Bumped on every password change, by whoever makes it: a console session is valid for one generation only.</summary>
        [JsonPropertyName("generation")] public long Generation { get; set; }
    }

    public sealed class LoginEntry
    {
        [JsonPropertyName("at")] public DateTime At { get; set; }
        [JsonPropertyName("outcome")] public string Outcome { get; set; } = "";
        [JsonPropertyName("remote")] public string? Remote { get; set; }
    }

    /// <summary>
    /// Reads the console state. Three cases, and only the first is "no password yet": an absent file is a first-time
    /// console (the first password may be set without a current one); a readable, well-formed file is the state as
    /// written; a file that is unreadable (locked, permissions) or damaged (empty, cut off, not a state) is neither. It
    /// is not "no password" (BMB-85): nothing may be set or changed on top of it, and nothing may be written over it.
    /// </summary>
    private bool TryLoad(out ConsoleState state, out string? unreadable)
    {
        state = new ConsoleState();
        unreadable = null;

        string text;
        try
        {
            text = File.ReadAllText(StateFile);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable = $"{StateFileName} cannot be read";
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ConsoleState>(text);
            if (parsed is not null && IsWellFormed(parsed))
            {
                state = parsed;
                return true;
            }
        }
        catch (JsonException)
        {
            // Not JSON at all (empty, or cut off by an interrupted write): damaged, as below.
        }

        unreadable = $"{StateFileName} is damaged (it does not hold a valid console state)";
        return false;
    }

    /// <summary>What the console writes. With a hash, its salt too, both base64 of the sizes it writes (a SHA-256 hash and a
    /// 32-byte salt). Without one there is no password yet, and the only state of that kind the console writes is a sign-in
    /// attempt made before any password was set: it is in the log, and the generation has not moved (only a password set
    /// moves it). Anything else without a hash is damaged. The log itself has no holes (BMB-85).</summary>
    private static bool IsWellFormed(ConsoleState s)
    {
        if (s.Logins is null || s.Logins.Any(l => l is null) || (s.HashB64 is null) != (s.SaltB64 is null)) return false;
        if (s.HashB64 is null) return s.Logins.Count > 0 && s.Generation == 0;
        try
        {
            return Convert.FromBase64String(s.HashB64).Length == SHA256.HashSizeInBytes
                && Convert.FromBase64String(s.SaltB64!).Length == SaltBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The state for read-only callers. An unreadable file reads as an empty state, which fails closed: no
    /// password, no sessions (the generation is 0), no log.</summary>
    private ConsoleState Load()
    {
        TryLoad(out var state, out _);
        return state;
    }

    private void Save(ConsoleState s)
    {
        Directory.CreateDirectory(_dir);
        var tmp = StateFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, StateFile, overwrite: true);
    }

    public bool HasPassword()
    {
        // An unreadable file may well hold a password, so it counts as set: fail closed.
        lock (_sync) return !TryLoad(out var s, out _) || s.HashB64 is not null;
    }

    /// <summary>Why the state file cannot be used (unreadable or damaged), or null when it can. An absent file can be used:
    /// it is the first-time console.</summary>
    public string? UnusableStateReason()
    {
        lock (_sync) return TryLoad(out _, out var unreadable) ? null : unreadable;
    }

    /// <summary>The password generation the console stamps on a session at sign-in and compares on every request.</summary>
    public long Generation()
    {
        lock (_sync) return Load().Generation;
    }

    /// <summary>
    /// Sets (or replaces) the console password. Replacing requires the current one — except the
    /// very first set, which is the pairing-time initialization done by <c>bmb blind init</c>.
    /// Refused, whatever is offered, while the state file is unreadable or damaged (<see cref="UnusableStateReason"/>).
    /// </summary>
    public bool TrySetPassword(string? currentPassword, string newPassword)
    {
        lock (_sync)
        {
            if (!TryLoad(out var s, out var unreadable))
            {
                logger.LogWarning("Blind console password not set: {Reason}", unreadable);
                return false;
            }
            if (s.HashB64 is not null)
            {
                if (currentPassword is null || !VerifyHash(s, currentPassword))
                    return false;
            }
            else if (!string.IsNullOrEmpty(currentPassword))
            {
                // No password set yet but one was offered: treat it as a wrong-state call, not as
                // a free replace of an existing hash.
                return false;
            }

            var salt = RandomNumberGenerator.GetBytes(SaltBytes);
            var hash = Hash(newPassword, salt);
            s.HashB64 = Convert.ToBase64String(hash);
            s.SaltB64 = Convert.ToBase64String(salt);
            s.Generation++;
            s.Logins.Insert(0, new LoginEntry { At = Now, Outcome = "password-set", Remote = null });
            Trim(s);
            Save(s);
            logger.LogInformation("Blind console password set");
            return true;
        }
    }

    /// <summary>
    /// Verifies a login attempt. Returns ok/locked/refused; every attempt — including the ones
    /// refused because locked — lands in the login log, because a locked console being probed is
    /// exactly what the operator wants to see in the sign-in log.
    /// </summary>
    public (bool Ok, bool Locked) Verify(string password, string? remote)
    {
        lock (_sync)
        {
            if (!TryLoad(out var s, out var unreadable))
            {
                // Refused without a journal entry: the journal would have to be written over the file that could not be read.
                logger.LogWarning("Blind console login refused: {Reason} (remote {Remote})", unreadable, remote);
                return (false, false);
            }

            // Lock check BEFORE any Argon2 work: the point of the lock is to stop an attacker
            // burning CPU on the verifier, so it must not itself pay for a derivation. A request
            // during the lock is journaled but neither counts nor extends it: the lock ends at
            // the time fixed when it began, so a prober cannot keep the owner out for ever.
            if (s.LockedUntil is { } until && Now < until)
            {
                Record(s, "locked", remote);
                logger.LogWarning("Blind console login refused: locked until {Until} (remote {Remote})", until, remote);
                return (false, true);
            }

            var ok = s.HashB64 is not null && VerifyHash(s, password);
            Record(s, ok ? "ok" : "bad-password", remote);
            if (ok) return (true, false);

            logger.LogWarning("Blind console login failed (remote {Remote})", remote);
            // Failures that count: bad passwords inside the window, after the last success and
            // after the last lock ended — a served lock starts the count again.
            var lastOk = s.Logins.FirstOrDefault(l => l.Outcome == "ok")?.At ?? DateTime.MinValue;
            var since = new[] { lastOk, s.LockedUntil ?? DateTime.MinValue, Now - LockWindow }.Max();
            if (s.Logins.Count(l => l.Outcome == "bad-password" && l.At > since) < MaxFailures)
                return (false, false);

            s.LockedUntil = Now + LockDuration;
            Save(s);
            logger.LogWarning("Blind console locked until {Until} after {Count} failures", s.LockedUntil, MaxFailures);
            return (false, true);
        }
    }

    public List<LoginEntry> RecentLogins()
    {
        lock (_sync) return [.. Load().Logins.Take(50)];
    }

    // ── hashing ─────────────────────────────────────────────────────────────

    // The console login hash uses the project's standard Argon2id parameters (64 MiB, t=3, p=4).
    // The derived bytes are hashed again before storage: the file holds a verifier, not a key.
    private static byte[] Hash(string password, byte[] salt)
    {
        var kek = KeyDerivation.DeriveKek(password, salt);
        return SHA256.HashData(kek);
    }

    private static bool VerifyHash(ConsoleState s, string password)
    {
        if (s.HashB64 is null || s.SaltB64 is null) return false;
        var expected = Convert.FromBase64String(s.HashB64);
        var actual = Hash(password, Convert.FromBase64String(s.SaltB64));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private void Record(ConsoleState s, string outcome, string? remote)
    {
        s.Logins.Insert(0, new LoginEntry { At = Now, Outcome = outcome, Remote = remote });
        Trim(s);
        Save(s);
    }

    private static void Trim(ConsoleState s)
    {
        if (s.Logins.Count > LogCap) s.Logins.RemoveRange(LogCap, s.Logins.Count - LogCap);
    }
}
