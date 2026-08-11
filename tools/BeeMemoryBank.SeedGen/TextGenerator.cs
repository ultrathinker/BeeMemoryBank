using System.Text;

namespace BeeMemoryBank.SeedGen;

/// <summary>
/// Generates plausible-ish prose by sampling words from a Zipf pool and punctuating them into
/// sentences and paragraphs. Output is plain markdown-ish text (occasional headings/bullets) so
/// it resembles real notes rather than a flat word dump. Length is measured in UTF-8 bytes to
/// honour the brief's byte-size targets (Russian text is 2 bytes/char, hence byte-based sizing).
///
/// Deterministic given the supplied <see cref="Random"/>; caches per-word byte lengths because
/// Zipf reuse makes popular words repeat many thousands of times.
/// </summary>
internal sealed class TextGenerator
{
    private readonly ZipfSampler<string> _words;
    private readonly Dictionary<string, int> _byteLenCache = new();

    private TextGenerator(ZipfSampler<string> words) => _words = words;

    public static TextGenerator ForLocale(WordPool pool, string locale) =>
        new(pool.ForLocale(locale));

    public string Generate(Random rng, int targetBytes)
    {
        var sb = new StringBuilder(targetBytes + 256);
        int bytes = 0;
        bool firstParagraph = true;

        while (bytes < targetBytes)
        {
            if (!firstParagraph)
            {
                sb.Append("\n\n");
                bytes += 2;
            }
            firstParagraph = false;

            double roll = rng.NextDouble();
            if (roll < 0.12)
            {
                AppendHeading(rng, sb, ref bytes);
                continue;
            }
            if (roll < 0.27)
            {
                AppendBulletList(rng, sb, ref bytes, targetBytes);
                continue;
            }

            int sentenceCount = 3 + rng.Next(5); // 3..7
            for (int s = 0; s < sentenceCount && bytes < targetBytes; s++)
            {
                if (s > 0) { sb.Append(' '); bytes++; }
                AppendSentence(rng, sb, ref bytes);
            }
        }

        return sb.ToString();
    }

    private void AppendHeading(Random rng, StringBuilder sb, ref int bytes)
    {
        sb.Append("# ");
        bytes += 2;
        int wordCount = 2 + rng.Next(4); // 2..5
        for (int w = 0; w < wordCount; w++)
        {
            if (w > 0) { sb.Append(' '); bytes++; }
            bytes += AppendWord(rng, sb, w == 0);
        }
    }

    private void AppendBulletList(Random rng, StringBuilder sb, ref int bytes, int targetBytes)
    {
        int items = 2 + rng.Next(4); // 2..5
        for (int i = 0; i < items && bytes < targetBytes; i++)
        {
            if (i > 0) { sb.Append('\n'); bytes++; }
            sb.Append("- ");
            bytes += 2;
            int wordCount = 3 + rng.Next(8); // 3..10
            for (int w = 0; w < wordCount; w++)
            {
                if (w > 0) { sb.Append(' '); bytes++; }
                bytes += AppendWord(rng, sb, w == 0);
            }
        }
    }

    private void AppendSentence(Random rng, StringBuilder sb, ref int bytes)
    {
        int wordCount = 6 + rng.Next(13); // 6..18
        for (int w = 0; w < wordCount; w++)
        {
            if (w > 0) { sb.Append(' '); bytes++; }
            bytes += AppendWord(rng, sb, w == 0);
            if (w > 0 && w < wordCount - 1 && rng.NextDouble() < 0.08)
            {
                sb.Append(',');
                bytes++;
            }
        }
        sb.Append('.');
        bytes++;
    }

    private int AppendWord(Random rng, StringBuilder sb, bool capitalize)
    {
        var word = _words.Sample(rng);
        if (capitalize) word = Capitalize(word);
        sb.Append(word);
        return ByteLength(word);
    }

    private int ByteLength(string word)
    {
        if (_byteLenCache.TryGetValue(word, out var len)) return len;
        len = Encoding.UTF8.GetByteCount(word);
        _byteLenCache[word] = len;
        return len;
    }

    private static string Capitalize(string word) =>
        string.IsNullOrEmpty(word) ? word : char.ToUpperInvariant(word[0]) + word[1..];
}
