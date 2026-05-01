using System.Text;

namespace BeeMemoryBank.Api.McpTools;

public static class TokenEstimator
{
    /// <summary>
    /// Conservative token estimate: ceil(UTF8ByteCount / 3).
    /// Overestimates slightly — safe for enforcing limits.
    /// English (~1 byte/char): ~3 chars/token. Russian (~2 bytes/char): ~1.5 chars/token.
    /// </summary>
    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var byteCount = Encoding.UTF8.GetByteCount(text);
        return (int)Math.Ceiling(byteCount / 3.0);
    }

    /// <summary>
    /// Find the character index where UTF-8 byte count reaches targetBytes.
    /// Handles surrogate pairs correctly.
    /// </summary>
    public static int FindCharPositionForByteLimit(string text, int targetBytes)
    {
        if (string.IsNullOrEmpty(text) || targetBytes <= 0) return 0;

        var totalBytes = 0;
        for (var i = 0; i < text.Length; i++)
        {
            int charBytes;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                charBytes = Encoding.UTF8.GetByteCount(text, i, 2);
                if (totalBytes + charBytes > targetBytes) return i;
                totalBytes += charBytes;
                i++;
            }
            else
            {
                charBytes = Encoding.UTF8.GetByteCount(text, i, 1);
                if (totalBytes + charBytes > targetBytes) return i;
                totalBytes += charBytes;
            }
        }

        return text.Length;
    }
}
