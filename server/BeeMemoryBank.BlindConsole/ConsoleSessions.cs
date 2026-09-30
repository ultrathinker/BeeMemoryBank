using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace BeeMemoryBank.BlindConsole;

/// <summary>
/// Console login sessions: random bearer tokens in a cookie, kept in process memory. No
/// persistence on purpose — restarting the console logs everyone out, which for a management
/// page on a headless node is the safe default, not an inconvenience. Sliding expiry.
/// </summary>
public sealed class ConsoleSessions(TimeSpan? ttl = null)
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(12);
    private readonly TimeSpan _ttl = ttl ?? DefaultTtl;
    private readonly ConcurrentDictionary<string, (DateTime Expires, long Generation)> _sessions = new();

    /// <summary>A session belongs to one password generation (the Api bumps it on every password change).</summary>
    public string Issue(long generation)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = (DateTime.UtcNow + _ttl, generation);
        // Opportunistic sweep; a dictionary of idle tokens costs bytes, not correctness.
        var now = DateTime.UtcNow;
        foreach (var (t, (expires, _)) in _sessions)
            if (expires < now)
                _sessions.TryRemove(t, out _);
        return token;
    }

    /// <summary>
    /// Valid, and issued under <paramref name="generation"/>, the password generation now. A generation
    /// that could not be read (null) refuses the request but keeps the session for when the Api answers.
    /// </summary>
    public bool IsValid(string? token, long? generation)
    {
        if (token is null || !_sessions.TryGetValue(token, out var session)) return false;
        if (session.Expires < DateTime.UtcNow || (generation is not null && session.Generation != generation))
        {
            _sessions.TryRemove(token, out _);
            return false;
        }
        if (generation is null) return false;
        _sessions[token] = (DateTime.UtcNow + _ttl, session.Generation);
        return true;
    }

    /// <summary>The session that changed the password moves to the new generation; every other one stays behind.</summary>
    public void SetGeneration(string? token, long generation)
    {
        if (token is not null && _sessions.TryGetValue(token, out var session))
            _sessions[token] = (session.Expires, generation);
    }

    public void Drop(string? token)
    {
        if (token is not null) _sessions.TryRemove(token, out _);
    }
}
