using System.Globalization;
using System.Text;

namespace BeeMemoryBank.Core.Embeddings;

/// <summary>
/// Minimal BERT WordPiece tokenizer (uncased). Handles ASCII and Unicode text including Russian.
/// Implements the same algorithm as the original Google BERT tokenizer.
/// </summary>
internal sealed class BertWordPieceTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly int _clsId;
    private readonly int _sepId;
    private readonly int _unkId;
    private const int MaxWordChars = 100;

    public BertWordPieceTokenizer(Dictionary<string, int> vocab)
    {
        _vocab = vocab;
        _clsId = vocab["[CLS]"];
        _sepId = vocab["[SEP]"];
        _unkId = vocab["[UNK]"];
    }

    /// <summary>Returns (inputIds, attentionMask, tokenTypeIds) as int64 arrays.</summary>
    public (long[] InputIds, long[] AttentionMask, long[] TokenTypeIds) Encode(string text, int maxLength = 512)
    {
        var ids = new List<int>(maxLength) { _clsId };

        foreach (var word in BasicTokenize(text))
        {
            var pieces = WordPiece(word);
            if (ids.Count + pieces.Count + 1 > maxLength) break; // reserve slot for [SEP]
            ids.AddRange(pieces);
        }

        ids.Add(_sepId);

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

    private static IEnumerable<string> BasicTokenize(string text)
    {
        // Normalize to NFD and strip non-spacing marks (accent stripping for uncased model)
        var nfd = text.Normalize(NormalizationForm.FormD);
        var cleaned = new StringBuilder(nfd.Length);
        foreach (var ch in nfd)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                cleaned.Append(ch);
        text = cleaned.ToString().ToLowerInvariant();

        var words = new List<string>();
        var current = new StringBuilder();

        foreach (var ch in text)
        {
            if (IsChineseCjk(ch))
            {
                // CJK characters are treated as individual tokens
                if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
                words.Add(ch.ToString());
            }
            else if (char.IsControl(ch) && !char.IsWhiteSpace(ch))
            {
                // Strip control characters (BERT spec)
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
            }
            else if (IsBertPunctuation(ch))
            {
                if (current.Length > 0) { words.Add(current.ToString()); current.Clear(); }
                words.Add(ch.ToString());
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
            words.Add(current.ToString());

        return words;
    }

    private List<int> WordPiece(string word)
    {
        if (word.Length > MaxWordChars)
            return [_unkId];

        var subTokens = new List<int>();
        int start = 0;

        while (start < word.Length)
        {
            int end = word.Length;
            int foundId = -1;

            while (start < end)
            {
                var substr = start == 0 ? word[start..end] : "##" + word[start..end];
                if (_vocab.TryGetValue(substr, out int id))
                {
                    foundId = id;
                    break;
                }
                end--;
            }

            if (foundId == -1)
                return [_unkId];

            subTokens.Add(foundId);
            start = end;
        }

        return subTokens;
    }

    // BERT punctuation: ASCII ranges + Unicode punctuation/symbols (em-dash, smart quotes, etc.)
    private static bool IsBertPunctuation(char ch) =>
        (ch >= '!' && ch <= '/') ||
        (ch >= ':' && ch <= '@') ||
        (ch >= '[' && ch <= '`') ||
        (ch >= '{' && ch <= '~') ||
        char.IsPunctuation(ch) ||
        char.IsSymbol(ch);

    // CJK Unified Ideographs and extensions
    private static bool IsChineseCjk(char ch) =>
        (ch >= '\u4E00' && ch <= '\u9FFF') ||
        (ch >= '\u3400' && ch <= '\u4DBF') ||
        (ch >= '\uF900' && ch <= '\uFAFF') ||
        (ch >= '\u2E80' && ch <= '\u2EFF');

    internal static Dictionary<string, int> LoadVocab(Stream stream)
    {
        var vocab = new Dictionary<string, int>(32000);
        using var reader = new StreamReader(stream);
        int idx = 0;
        while (reader.ReadLine() is { } line)
            vocab[line] = idx++;
        return vocab;
    }
}
