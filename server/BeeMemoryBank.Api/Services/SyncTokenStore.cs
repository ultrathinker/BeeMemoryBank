using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// In-memory store for temporary challenge tokens and Bearer tokens for sync auth.
/// Singleton — data lives in the process memory.
/// </summary>
public class SyncTokenStore
{
    private sealed record ChallengeEntry(Guid ServerNodeId, DateTime ExpiresAt);
    private sealed record TokenEntry(Guid NodeId, DateTime ExpiresAt);

    private readonly ConcurrentDictionary<string, ChallengeEntry> _challenges = new();
    private readonly ConcurrentDictionary<string, TokenEntry> _tokens = new();

    /// <summary>Issues a new challenge (base64) with 60s TTL.</summary>
    public string IssueChallenge(Guid serverNodeId)
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        var challenge = Convert.ToBase64String(bytes);
        _challenges[challenge] = new ChallengeEntry(serverNodeId, DateTime.UtcNow.AddSeconds(60));
        return challenge;
    }

    /// <summary>Consumes the challenge (one-time use). Returns serverNodeId or null if invalid/expired.</summary>
    public bool ConsumeChallenge(string challenge, out Guid serverNodeId)
    {
        serverNodeId = default;
        if (!_challenges.TryRemove(challenge, out var entry)) return false;
        if (entry.ExpiresAt <= DateTime.UtcNow) return false;
        serverNodeId = entry.ServerNodeId;
        return true;
    }

    /// <summary>Issues a Bearer token for an authenticated node. TTL 1 hour.</summary>
    public string IssueToken(Guid nodeId)
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        var token = Convert.ToBase64String(bytes);
        _tokens[token] = new TokenEntry(nodeId, DateTime.UtcNow.AddHours(1));
        return token;
    }

    /// <summary>Validates a Bearer token. Returns true and nodeId if the token is valid.</summary>
    public bool TryValidateToken(string token, out Guid nodeId)
    {
        nodeId = default;
        if (!_tokens.TryGetValue(token, out var entry)) return false;
        if (entry.ExpiresAt <= DateTime.UtcNow)
        {
            _tokens.TryRemove(token, out _);
            return false;
        }
        nodeId = entry.NodeId;
        return true;
    }

    public void CleanupExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in _challenges)
        {
            if (entry.Value.ExpiresAt < now)
                _challenges.TryRemove(entry.Key, out _);
        }
        foreach (var entry in _tokens)
        {
            if (entry.Value.ExpiresAt < now)
                _tokens.TryRemove(entry.Key, out _);
        }
    }
}
