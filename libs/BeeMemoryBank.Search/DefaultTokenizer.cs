namespace BeeMemoryBank.Search;

/// <summary>
/// Default <see cref="ITokenizer"/> implementation: normalizes the whole input via
/// <see cref="TextNormalization.Normalize"/>, then splits it into runs of letters/digits,
/// treating everything else (whitespace, punctuation, symbols, emoji) as a boundary. This is a
/// single linear pass over the text — no backtracking, no per-token re-scans.
/// </summary>
public sealed class DefaultTokenizer : ITokenizer
{
    /// <inheritdoc />
    public IEnumerable<string> Tokenize(string? text)
    {
        string normalized = TextNormalization.Normalize(text);
        return TokenizeNormalized(normalized);
    }

    private static IEnumerable<string> TokenizeNormalized(string normalized)
    {
        int start = -1;
        for (int i = 0; i < normalized.Length; i++)
        {
            if (char.IsLetterOrDigit(normalized[i]))
            {
                if (start < 0)
                {
                    start = i;
                }
            }
            else if (start >= 0)
            {
                yield return normalized.Substring(start, i - start);
                start = -1;
            }
        }

        if (start >= 0)
        {
            yield return normalized.Substring(start, normalized.Length - start);
        }
    }
}
