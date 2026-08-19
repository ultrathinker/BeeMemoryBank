using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Core.Models;
using Microsoft.AspNetCore.Http;

namespace BeeMemoryBank.Api.McpTools;

/// <summary>
/// Limits the size of MCP responses. Default: 10,000 tokens.
/// Truncated responses are saved to a temporary file for continued reading via bee_continue.
/// </summary>
/// <remarks>
/// This service is registered as a process-wide singleton (it owns the on-disk temp-file store
/// bee_continue reads from across separate requests), but the max-tokens LIMIT itself is kept
/// per-caller (keyed off HttpContext.Items["AuthAgent"], set by AgentAuthMiddleware for the
/// current request). A single shared int field here would mean one agent raising its limit via
/// bee_set_max_tokens silently changes what every other concurrently connected agent's calls
/// return too -- that was a real bug, not just a surprising default.
/// </remarks>
public class McpResponseManager(string dataPath, IHttpContextAccessor httpContextAccessor)
{
    private static readonly TimeSpan TempFileExpiry = TimeSpan.FromHours(24);

    public const int MinTokens = 1000;
    public const int MaxTokensCeiling = 100_000;
    private const int DefaultMaxTokens = 10_000;
    private const string UnauthenticatedCallerKey = "unauthenticated";

    private readonly string _tempPath = EnsureTempPath(dataPath);
    private readonly ConcurrentDictionary<string, int> _maxTokensByCaller = new();

    private static string EnsureTempPath(string dataPath)
    {
        var path = Path.Combine(dataPath, "temp");
        Directory.CreateDirectory(path);
        return path;
    }

    // Every MCP tool call arrives as its own HTTP request carrying its own bearer token, so
    // HttpContext.Items["AuthAgent"] (set fresh per-request by AgentAuthMiddleware) reliably
    // identifies which agent is calling right now. Requests that never resolved to an agent
    // (no token, or a bmbrt_ remote token, which doesn't reach the MCP tools at all) share one
    // fallback bucket -- there's no narrower identity to key on for those.
    private string CurrentCallerKey =>
        httpContextAccessor.HttpContext?.Items.TryGetValue("AuthAgent", out var obj) == true && obj is Agent agent
            ? $"agent:{agent.Id}"
            : UnauthenticatedCallerKey;

    public int MaxTokens => _maxTokensByCaller.GetValueOrDefault(CurrentCallerKey, DefaultMaxTokens);

    /// <summary>
    /// Sets the caller's own response limit. Returns false (with an error message, leaving the
    /// limit unchanged) for a value outside [MinTokens, MaxTokensCeiling] -- never silently
    /// clamps, so a caller can tell "you asked for too much" from "it worked".
    /// </summary>
    public bool TrySetMaxTokens(int maxTokens, out string? error)
    {
        if (maxTokens < MinTokens || maxTokens > MaxTokensCeiling)
        {
            error = $"maxTokens must be between {MinTokens} and {MaxTokensCeiling} (got {maxTokens}).";
            return false;
        }

        _maxTokensByCaller[CurrentCallerKey] = maxTokens;
        error = null;
        return true;
    }

    public string ProcessResponse(string response)
    {
        var limit = MaxTokens;
        var tokens = TokenEstimator.EstimateTokens(response);

        if (tokens <= limit)
            return response;

        var guid = Guid.NewGuid().ToString("N");
        SaveTempFile(guid, response);
        CleanupExpiredFiles();

        if (IsJsonResponse(response))
        {
            // JSON can't be truncated mid-structure and stay parseable, so this call only ever
            // returns a small preview, never a usable prefix -- offset MUST start at 0 (the true
            // beginning of the saved document). Setting it to an interior byte position while
            // only ever delivering a preview (the previous behavior) silently drops everything
            // between the preview and that position -- found in review, not a cosmetic issue.
            var preview = response.Length > 500 ? response[..500] : response;
            return JsonSerializer.Serialize(new
            {
                truncated = true,
                reason = $"Response exceeded max_tokens limit (~{tokens} tokens, limit {limit}).",
                preview,
                guid,
                offset = 0,
                totalChars = response.Length,
                hint = BuildContinueHint(guid, 0, tokens)
            });
        }

        // 90% of budget for content, 10% for warning
        var targetBytes = (int)(limit * 3.0 * 0.9);
        var charPos = TokenEstimator.FindCharPositionForByteLimit(response, targetBytes);
        var truncated = response[..charPos].TrimEnd() + "\n... [TRUNCATED]";
        var remainingTokens = TokenEstimator.EstimateTokens(response[charPos..]);

        var warning = $"\n⚠️ TRUNCATED: Response too large (~{tokens} tokens, limit {limit}). " +
                      $"Showed {charPos} of {response.Length} chars. " +
                      BuildContinueHint(guid, charPos, remainingTokens);
        return truncated + "\n" + warning;
    }

    private static bool IsJsonResponse(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsWhiteSpace(s[i])) continue;
            return s[i] == '{' || s[i] == '[';
        }
        return false;
    }

    public string Continue(string guid, int offset, bool ignoreLimit = false)
    {
        if (!Guid.TryParse(guid, out _))
            return JsonSerializer.Serialize(new { error = "Invalid continuation guid." });

        var filePath = Path.Combine(_tempPath, $"{guid}.json");
        if (!File.Exists(filePath))
            return JsonSerializer.Serialize(new
            {
                error = "Continuation file not found or expired (24h). Re-run the original tool call."
            });

        var fullContent = File.ReadAllText(filePath, Encoding.UTF8);
        if (offset < 0)
            return JsonSerializer.Serialize(new { error = $"Invalid offset {offset}: must be >= 0." });
        if (offset >= fullContent.Length)
            return JsonSerializer.Serialize(new
            {
                status = "complete",
                message = "All content has been delivered."
            });

        var remaining = fullContent[offset..];
        var tokens = TokenEstimator.EstimateTokens(remaining);
        // ignoreLimit always means "use the hard ceiling for this one call", never a number
        // above it -- it exists so a caller never HAS to touch the shared session-wide limit
        // (via bee_set_max_tokens) just to read one large document in a single call.
        var effectiveLimit = ignoreLimit ? MaxTokensCeiling : MaxTokens;

        if (tokens <= effectiveLimit)
            return remaining;

        var targetBytes = (int)(effectiveLimit * 3.0 * 0.9);
        var charPos = TokenEstimator.FindCharPositionForByteLimit(remaining, targetBytes);
        var truncated = remaining[..charPos].TrimEnd() + "\n... [TRUNCATED]";
        var newOffset = offset + charPos;
        var remainingTokens = TokenEstimator.EstimateTokens(remaining[charPos..]);

        var warning = $"\n⚠️ TRUNCATED: Response too large (~{tokens} tokens, limit {effectiveLimit}). " +
                      $"Showed {newOffset} of {fullContent.Length} chars. " +
                      BuildContinueHint(guid, newOffset, remainingTokens);
        return truncated + "\n" + warning;
    }

    /// <summary>
    /// Always states the exact remaining token cost instead of just listing both APIs -- a hint
    /// that hands the caller a number lets it decide in one step; a hint that just lists "call
    /// bee_continue, or try ignoreLimit" makes it guess and often re-ask.
    /// </summary>
    private static string BuildContinueHint(string guid, int offset, int remainingTokens)
    {
        var basic = $"Call bee_continue(guid: \"{guid}\", offset: {offset}) for the next chunk.";

        if (remainingTokens <= MaxTokensCeiling)
        {
            return basic +
                   $" To get all remaining ~{remainingTokens} tokens in a single call instead: " +
                   $"bee_continue(guid: \"{guid}\", offset: {offset}, ignoreLimit: true).";
        }

        return basic +
               $" Too large for a single call even with ignoreLimit (~{remainingTokens} tokens, " +
               $"hard ceiling {MaxTokensCeiling}) — must be read incrementally.";
    }

    private void SaveTempFile(string guid, string content)
    {
        Directory.CreateDirectory(_tempPath);
        File.WriteAllText(Path.Combine(_tempPath, $"{guid}.json"), content, Encoding.UTF8);
    }

    private void CleanupExpiredFiles()
    {
        try
        {
            var cutoff = DateTime.UtcNow - TempFileExpiry;
            foreach (var file in Directory.GetFiles(_tempPath, "*.json"))
            {
                // Use LastWriteTimeUtc instead of CreationTimeUtc — on Linux (ext4/btrfs),
                // creation time may not be supported or may be unreliable.
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    try { File.Delete(file); }
                    catch { /* best effort */ }
                }
            }
        }
        catch { /* best effort */ }
    }
}
