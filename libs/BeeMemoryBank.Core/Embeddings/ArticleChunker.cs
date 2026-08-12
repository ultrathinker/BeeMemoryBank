namespace BeeMemoryBank.Core.Embeddings;

/// <summary>
/// WP-15: splits article plaintext into overlapping chunks sized in real SentencePiece tokens (via
/// <see cref="XlmRobertaTokenizer.TokenizeWithCounts"/>), so each chunk fits within one
/// <see cref="OnnxEmbeddingGenerator.Generate"/> call without <see cref="XlmRobertaTokenizer.Encode"/>
/// silently re-truncating it.
///
/// <para>
/// <b>Why chunking exists.</b> <see cref="OnnxEmbeddingGenerator"/> truncates any input to
/// <see cref="OnnxEmbeddingGenerator.MaxSequenceLength"/> (256) tokens before embedding it — a
/// "needle" placed past that point in a long article is invisible to semantic search today,
/// because it was never part of what got embedded. Chunking embeds every ~256-token slice of the
/// article separately (with a sliding overlap so a needle straddling a chunk boundary isn't lost
/// either), and article-level semantic scoring becomes the max over its chunks.
/// </para>
/// </summary>
public sealed class ArticleChunker
{
    /// <summary>
    /// Content-token budget per chunk: <see cref="OnnxEmbeddingGenerator.MaxSequenceLength"/> minus
    /// the [BOS]/[EOS] tokens <see cref="XlmRobertaTokenizer.Encode"/> always adds, so a chunk
    /// built to this budget round-trips through <c>Generate</c> without truncation.
    /// </summary>
    public const int ChunkTokenBudget = OnnxEmbeddingGenerator.MaxSequenceLength - 2;

    /// <summary>Target token overlap between consecutive chunks, per the search-100k plan (WP-15).</summary>
    public const int ChunkOverlapTokens = 32;

    private readonly XlmRobertaTokenizer _tokenizer;

    internal ArticleChunker(XlmRobertaTokenizer tokenizer)
    {
        _tokenizer = tokenizer;
    }

    /// <summary>Constructs a chunker over the same embedded tokenizer <see cref="OnnxEmbeddingGenerator"/> uses.</summary>
    public static ArticleChunker CreateDefault() => new(XlmRobertaTokenizer.LoadDefault());

    /// <summary>
    /// Splits <paramref name="text"/> into chunk strings, each reconstructed by joining the
    /// contributing basic-tokenized words with a single space. Reconstructed text is not meant to
    /// be redisplayed — it is only ever fed back into <see cref="OnnxEmbeddingGenerator.Generate"/>,
    /// which re-tokenizes it, so losing original casing/punctuation/whitespace here is harmless to
    /// the resulting embedding.
    /// </summary>
    /// <remarks>
    /// Returns an empty list for blank input, and a single chunk for text that already fits within
    /// <see cref="ChunkTokenBudget"/> — chunking a short article still produces exactly one chunk,
    /// so callers don't need a separate "was this chunked at all" branch.
    /// </remarks>
    public List<string> Chunk(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<(string Word, int TokenCount)> words = _tokenizer.TokenizeWithCounts(text);
        if (words.Count == 0)
        {
            return [];
        }

        var chunks = new List<string>();
        int start = 0;

        while (start < words.Count)
        {
            int tokenSum = 0;
            int end = start;
            while (end < words.Count && tokenSum + words[end].TokenCount <= ChunkTokenBudget)
            {
                tokenSum += words[end].TokenCount;
                end++;
            }
            // Pathological case: a single word alone exceeds the budget (e.g. it collapsed to
            // [UNK]-length pieces beyond the budget cannot happen since WordPiece Count is at most
            // 1 for [UNK], but a legitimately long word could still exceed the budget on its own).
            // Always include at least one word so the outer loop makes forward progress.
            if (end == start)
            {
                end = start + 1;
            }

            chunks.Add(string.Join(' ', Enumerable.Range(start, end - start).Select(i => words[i].Word)));

            if (end >= words.Count)
            {
                break;
            }

            // Slide the window back from `end` by ~ChunkOverlapTokens worth of words, but never
            // back past `start + 1` -- guarantees forward progress every iteration even when a
            // chunk's own content is smaller than the overlap target (e.g. very long words).
            int newStart = end;
            int overlapTokens = 0;
            while (newStart > start + 1 && overlapTokens < ChunkOverlapTokens)
            {
                newStart--;
                overlapTokens += words[newStart].TokenCount;
            }
            start = newStart;
        }

        return chunks;
    }
}
