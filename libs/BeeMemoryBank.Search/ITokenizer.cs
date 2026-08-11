namespace BeeMemoryBank.Search;

/// <summary>
/// Splits raw text into a sequence of normalized tokens suitable for indexing or stemming.
/// </summary>
public interface ITokenizer
{
    /// <summary>
    /// Tokenizes <paramref name="text"/>: applies Unicode normalization, culture-invariant
    /// lowercasing, and diacritic stripping to the text as a whole, then splits the result on
    /// whitespace/punctuation boundaries. Never throws for any input, including <c>null</c>,
    /// empty, or whitespace-only text.
    /// </summary>
    /// <param name="text">The raw text to tokenize. May be <c>null</c> or empty.</param>
    /// <returns>The sequence of normalized tokens, in order of appearance. Never contains
    /// null or empty entries.</returns>
    IEnumerable<string> Tokenize(string? text);
}
