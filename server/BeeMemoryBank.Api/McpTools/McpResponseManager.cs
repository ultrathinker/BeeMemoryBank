using System.Text;
using System.Text.Json;

namespace BeeMemoryBank.Api.McpTools;

/// <summary>
/// Limits the size of MCP responses. Default: 10,000 tokens.
/// Truncated responses are saved to a temporary file for continued reading via bee_continue.
/// </summary>
public class McpResponseManager
{
    private static readonly TimeSpan TempFileExpiry = TimeSpan.FromHours(24);

    private readonly string _tempPath;
    private int _maxTokens = 10_000;

    public McpResponseManager(string dataPath)
    {
        _tempPath = Path.Combine(dataPath, "temp");
        Directory.CreateDirectory(_tempPath);
    }

    public int MaxTokens => _maxTokens;

    public void SetMaxTokens(int maxTokens)
    {
        _maxTokens = Math.Clamp(maxTokens, 1000, 20000);
    }

    public string ProcessResponse(string response)
    {
        var tokens = TokenEstimator.EstimateTokens(response);

        if (tokens <= _maxTokens)
            return response;

        var guid = Guid.NewGuid().ToString("N");
        SaveTempFile(guid, response);
        CleanupExpiredFiles();

        // 90% of budget for content, 10% for warning
        var targetBytes = (int)(_maxTokens * 3.0 * 0.9);
        var charPos = TokenEstimator.FindCharPositionForByteLimit(response, targetBytes);
        var truncated = response[..charPos];

        if (IsJsonResponse(response))
        {
            // Always return a JSON wrapper for JSON responses — appending a plain-text
            // warning would corrupt the response for any strict JSON parser.
            var preview = truncated.Length > 500 ? truncated[..500] : truncated;
            return JsonSerializer.Serialize(new
            {
                truncated = true,
                reason = $"Response exceeded max_tokens limit (~{tokens} tokens, limit {_maxTokens})",
                preview,
                guid,
                offset = charPos,
                totalChars = response.Length,
                hint = $"Call bee_continue(guid: \"{guid}\", offset: {charPos}) for the next chunk."
            });
        }

        truncated = truncated.TrimEnd() + "\n... [TRUNCATED]";
        var warning = FormatTruncationWarning(guid, charPos, response.Length, tokens, _maxTokens);
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

    public string Continue(string guid, int offset)
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
        if (offset >= fullContent.Length)
            return JsonSerializer.Serialize(new
            {
                status = "complete",
                message = "All content has been delivered."
            });

        var remaining = fullContent[offset..];
        var tokens = TokenEstimator.EstimateTokens(remaining);

        if (tokens <= _maxTokens)
            return remaining;

        var targetBytes = (int)(_maxTokens * 3.0 * 0.9);
        var charPos = TokenEstimator.FindCharPositionForByteLimit(remaining, targetBytes);
        var truncated = remaining[..charPos];
        var newOffset = offset + charPos;

        var totalTokens = TokenEstimator.EstimateTokens(fullContent);
        var warning = FormatTruncationWarning(guid, newOffset, fullContent.Length, totalTokens, _maxTokens);
        return truncated + "\n" + warning;
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

    private static string FormatTruncationWarning(string guid, int offset, int totalChars, int totalTokens, int maxTokens)
    {
        return $"\n⚠️ TRUNCATED: Response too large (~{totalTokens} tokens, limit {maxTokens}). " +
               $"Showed {offset} of {totalChars} chars. " +
               $"Call bee_continue(guid: \"{guid}\", offset: {offset}) to get the next chunk. " +
               $"Use bee_set_max_tokens to increase the limit.";
    }
}
