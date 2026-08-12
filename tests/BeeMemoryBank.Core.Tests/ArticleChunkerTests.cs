using BeeMemoryBank.Core.Embeddings;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// WP-15: <see cref="ArticleChunker"/> splits article text into overlapping ~256-token chunks so a
/// "needle" placed past <see cref="OnnxEmbeddingGenerator.MaxSequenceLength"/> tokens into a long
/// article — invisible to the pre-WP-15 single-embedding-per-article search — ends up inside at
/// least one chunk.
/// </summary>
public class ArticleChunkerTests
{
    private static readonly ArticleChunker Chunker = ArticleChunker.CreateDefault();

    [Fact]
    public void Chunk_EmptyOrWhitespace_ReturnsEmpty()
    {
        Chunker.Chunk("").Should().BeEmpty();
        Chunker.Chunk("   \n\t  ").Should().BeEmpty();
    }

    [Fact]
    public void Chunk_ShortText_ReturnsExactlyOneChunk()
    {
        var chunks = Chunker.Chunk("The quick brown fox jumps over the lazy dog.");
        chunks.Should().HaveCount(1);
        chunks[0].Should().Contain("quick").And.Contain("lazy");
    }

    [Fact]
    public void Chunk_LongText_ProducesMultipleChunks()
    {
        // ~2000 distinct words, comfortably more than one ChunkTokenBudget's worth.
        var words = Enumerable.Range(0, 2000).Select(i => $"word{i}");
        var text = string.Join(' ', words);

        var chunks = Chunker.Chunk(text);

        chunks.Count.Should().BeGreaterThan(1);
    }

    [Fact]
    public void Chunk_ConsecutiveChunks_Overlap()
    {
        var words = Enumerable.Range(0, 2000).Select(i => $"word{i}");
        var text = string.Join(' ', words);

        var chunks = Chunker.Chunk(text);
        chunks.Count.Should().BeGreaterThan(1);

        // The last word of chunk N must reappear somewhere in chunk N+1 -- proof of a real overlap,
        // not just two adjacent, non-overlapping windows.
        for (int i = 0; i + 1 < chunks.Count; i++)
        {
            var lastWordOfChunk = chunks[i].Split(' ').Last();
            chunks[i + 1].Split(' ').Should().Contain(lastWordOfChunk,
                $"chunk {i} and chunk {i + 1} must share a sliding-window overlap");
        }
    }

    [Fact]
    public void Chunk_EveryChunk_FitsWithinTokenBudget()
    {
        var words = Enumerable.Range(0, 2000).Select(i => $"word{i}");
        var text = string.Join(' ', words);

        var chunks = Chunker.Chunk(text);

        foreach (var chunk in chunks)
        {
            // Re-derive the chunk's own word list via ArticleChunker.Chunk itself is circular; instead
            // rely on the chunk never exceeding budget by construction, verified structurally: a
            // single chunk must not itself decompose into >1 chunk when re-chunked (idempotence),
            // which would indicate it exceeded the budget in the first place.
            Chunker.Chunk(chunk).Should().HaveCount(1,
                "a chunk produced within budget must not itself split into further chunks when re-chunked");
        }
    }

    [Fact]
    public void Chunk_NeedleNearEndOfLongArticle_AppearsInSomeChunk()
    {
        // Simulates the exact scenario WP-15 exists for: a distinctive term placed well past the
        // 256-token point a single OnnxEmbeddingGenerator.Generate() call would have truncated at.
        var filler = string.Join(' ', Enumerable.Range(0, 1500).Select(i => $"filler{i}"));
        var text = $"{filler} needlemarker9f3a {filler}";

        var chunks = Chunker.Chunk(text);

        chunks.Should().Contain(c => c.Contains("needlemarker9f3a"));
    }

    [Fact]
    public void Chunk_IsDeterministic()
    {
        var words = Enumerable.Range(0, 500).Select(i => $"word{i}");
        var text = string.Join(' ', words);

        Chunker.Chunk(text).Should().Equal(Chunker.Chunk(text));
    }

    [Fact]
    public void Chunk_UnbrokenRunLongerThanBudget_SplitsFurtherInsteadOfOneOversizedChunk()
    {
        // A single "word" with no whitespace/punctuation for SplitWords to break on -- e.g. a
        // pasted base64 blob, JWT segment, or hash -- can tokenize to far more than ChunkTokenBudget
        // on its own. The old WordPiece tokenizer had an implicit cap (long words collapsed to one
        // [UNK]) that made this impossible; SentencePiece has none, so ArticleChunker itself must
        // split it further, or Encode() would silently truncate away most of it later.
        var unbrokenRun = string.Concat(Enumerable.Range(0, 4000).Select(i => (char)('a' + i % 26)));
        var text = $"prefix {unbrokenRun} needlemarker9f3a suffix";

        var chunks = Chunker.Chunk(text);

        chunks.Should().Contain(c => c.Contains("needlemarker9f3a"),
            "the needle placed right after the oversized run must not be lost -- it would be if the run were emitted as one giant chunk that Encode() later truncates");
        chunks.Should().HaveCountGreaterThan(1,
            "the oversized run alone must be split into more than one chunk, not emitted whole");
    }
}
