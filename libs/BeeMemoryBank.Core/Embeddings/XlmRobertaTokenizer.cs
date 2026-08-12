using System.Text;
using Microsoft.ML.Tokenizers;

namespace BeeMemoryBank.Core.Embeddings;

/// <summary>
/// SentencePiece tokenizer for multilingual-e5-small (and other XLM-RoBERTa-based models). Wraps
/// <see cref="SentencePieceTokenizer"/> (stable since Microsoft.ML.Tokenizers v1.0.1 -- it parses a
/// raw <c>sentencepiece_model.proto</c> stream directly, which is exactly the format
/// <c>sentencepiece.bpe.model</c> ships) and applies the fairseq id-offset scheme HuggingFace's
/// <c>XLMRobertaTokenizer</c> uses on top of it: raw SentencePiece piece ids are NOT the model's
/// vocabulary ids. Four fairseq special tokens are inserted ahead of the SentencePiece vocabulary
/// (&lt;s&gt;=0, &lt;pad&gt;=1, &lt;/s&gt;=2, &lt;unk&gt;=3), every other piece id is shifted by +1, and the
/// SentencePiece model's own id 0 (its own &lt;unk&gt;) is remapped to fairseq's id 3 instead of 1.
/// Confirmed against intfloat/multilingual-e5-small's tokenizer_config.json
/// (<c>"tokenizer_class": "XLMRobertaTokenizer"</c>) and HuggingFace's XLMRobertaTokenizer source.
/// Getting this offset wrong wouldn't crash anything -- it would just silently feed the model token
/// ids one off from what it was trained on.
/// </summary>
internal sealed class XlmRobertaTokenizer
{
    private const int BeginningOfSentenceId = 0;
    private const int EndOfSentenceId = 2;
    private const int UnknownId = 3;
    private const int FairseqOffset = 1;

    private readonly SentencePieceTokenizer _sp;

    private XlmRobertaTokenizer(SentencePieceTokenizer sp) => _sp = sp;

    /// <summary>
    /// Returns each basic-tokenized word alongside its real SentencePiece token count, without the
    /// [BOS]/[EOS]/max-length truncation <see cref="Encode"/> applies. Used by
    /// <see cref="ArticleChunker"/> to size chunks in the same units <see cref="Encode"/> counts
    /// against <c>OnnxEmbeddingGenerator.MaxSequenceLength</c>.
    /// </summary>
    public List<(string Word, int TokenCount)> TokenizeWithCounts(string text)
    {
        var result = new List<(string, int)>();
        foreach (var word in SplitWords(text))
        {
            result.Add((word, CountPieces(word)));
        }
        return result;
    }

    /// <summary>Returns (inputIds, attentionMask, tokenTypeIds) as int64 arrays.</summary>
    public (long[] InputIds, long[] AttentionMask, long[] TokenTypeIds) Encode(string text, int maxLength = 512)
    {
        int budget = maxLength - 2; // reserve slots for [BOS]/[EOS]
        var ids = new List<int>(maxLength) { BeginningOfSentenceId };

        int added = 0;
        foreach (var rawId in _sp.EncodeToIds(text, addBeginningOfSentence: false, addEndOfSentence: false))
        {
            if (added >= budget) break;
            ids.Add(ToVocabId(rawId));
            added++;
        }
        ids.Add(EndOfSentenceId);

        int count = ids.Count;
        var inputIds = new long[count];
        var attentionMask = new long[count];
        var tokenTypeIds = new long[count]; // all zeros for single-sequence input
        for (int i = 0; i < count; i++)
        {
            inputIds[i] = ids[i];
            attentionMask[i] = 1L;
        }

        return (inputIds, attentionMask, tokenTypeIds);
    }

    private int CountPieces(string word) =>
        _sp.EncodeToIds(word, addBeginningOfSentence: false, addEndOfSentence: false).Count;

    /// <summary>
    /// Real SentencePiece token count for an arbitrary substring. Used by
    /// <see cref="ArticleChunker"/> to binary-search a safe split point inside a single
    /// "word" too large to fit a whole chunk on its own (e.g. an unbroken run of base64/JWT/hash
    /// text with no whitespace or punctuation) -- the old WordPiece tokenizer had an implicit cap
    /// (words over 100 chars collapsed to a single [UNK]) that made this impossible; SentencePiece
    /// has no equivalent cap, so a run long enough can otherwise tokenize past the whole chunk
    /// budget by itself and get silently truncated by <see cref="Encode"/>.
    /// </summary>
    internal int CountTokens(string text) => CountPieces(text);

    private static int ToVocabId(int rawSentencePieceId) =>
        rawSentencePieceId == 0 ? UnknownId : rawSentencePieceId + FairseqOffset;

    // Splits text into whitespace-separated words, punctuation, and CJK characters as their own
    // units -- the same segmentation ArticleChunker needs to rejoin words into chunk text. Unlike
    // the old BERT tokenizer's basic-tokenize step, this does NOT lowercase or strip accents:
    // SentencePiece's own normalizer (baked into the .model file) handles that, and this split
    // exists only to give ArticleChunker word-sized units, not to preprocess for tokenization.
    private static IEnumerable<string> SplitWords(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
        }

        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                Flush();
                words.Add(ch.ToString());
            }
            else if (char.IsControl(ch) && !char.IsWhiteSpace(ch))
            {
                // Strip control characters.
            }
            else if (char.IsWhiteSpace(ch))
            {
                Flush();
            }
            else if (IsPunctuation(ch))
            {
                Flush();
                words.Add(ch.ToString());
            }
            else
            {
                current.Append(ch);
            }
        }

        Flush();
        return words;
    }

    private static bool IsPunctuation(char ch) =>
        (ch >= '!' && ch <= '/') ||
        (ch >= ':' && ch <= '@') ||
        (ch >= '[' && ch <= '`') ||
        (ch >= '{' && ch <= '~') ||
        char.IsPunctuation(ch) ||
        char.IsSymbol(ch);

    // CJK Unified Ideographs and extensions
    private static bool IsCjk(char ch) =>
        (ch >= '一' && ch <= '鿿') ||
        (ch >= '㐀' && ch <= '䶿') ||
        (ch >= '豈' && ch <= '﫿') ||
        (ch >= '⺀' && ch <= '⻿');

    /// <summary>
    /// Loads the tokenizer embedded in this assembly (the same sentencepiece.bpe.model
    /// <see cref="OnnxEmbeddingGenerator"/> uses). Shared by <see cref="OnnxEmbeddingGenerator"/>
    /// and <see cref="ArticleChunker"/> so both count tokens identically -- a chunk
    /// <see cref="ArticleChunker"/> sizes against this exact tokenizer is guaranteed to round-trip
    /// through <see cref="OnnxEmbeddingGenerator.Generate"/> without truncation.
    /// </summary>
    internal static XlmRobertaTokenizer LoadDefault()
    {
        var modelStream = typeof(XlmRobertaTokenizer).Assembly
            .GetManifestResourceStream("BeeMemoryBank.Core.Embeddings.Models.sentencepiece.bpe.model")
            ?? throw new InvalidOperationException("Embedded sentencepiece.bpe.model not found in BeeMemoryBank.Core assembly.");

        using (modelStream)
        {
            var sp = SentencePieceTokenizer.Create(
                modelStream,
                addBeginningOfSentence: false,
                addEndOfSentence: false,
                specialTokens: null);
            return new XlmRobertaTokenizer(sp);
        }
    }
}
