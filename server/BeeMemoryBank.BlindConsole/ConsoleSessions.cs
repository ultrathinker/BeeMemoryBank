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
    private readonly ConcurrentDictionary<string, DateTime> _sessions = new();

    public string Issue()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = DateTime.UtcNow + _ttl;
        // Opportunistic sweep; a dictionary of idle tokens costs bytes, not correctness.
        var now = DateTime.UtcNow;
        foreach (var (t, expires) in _sessions)
            if (expires < now)
                _sessions.TryRemove(t, out _);
        return token;
    }

    public bool IsValid(string? token)
    {
        if (token is null || !_sessions.TryGetValue(token, out var expires)) return false;
        if (expires < DateTime.UtcNow)
        {
            _sessions.TryRemove(token, out _);
            return false;
        }
        _sessions[token] = DateTime.UtcNow + _ttl;
        return true;
    }

    public void Drop(string? token)
    {
        if (token is not null) _sessions.TryRemove(token, out _);
    }

    /// <summary>Ends every session except <paramref name="keep"/>: what a password change does to the other browsers.</summary>
    public void DropAllExcept(string? keep)
    {
        foreach (var token in _sessions.Keys)
            if (token != keep)
                _sessions.TryRemove(token, out _);
    }
}
