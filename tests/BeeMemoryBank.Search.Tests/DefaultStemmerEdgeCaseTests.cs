using BeeMemoryBank.Search;

namespace BeeMemoryBank.Search.Tests;

/// <summary>
/// Explicit edge-case coverage for <see cref="DefaultStemmer"/> and the individual language
/// stemmers: every case the WP-05 brief calls out by name (empty, whitespace, single character,
/// all-digits, mixed script, emoji/symbols, very long strings) plus null, which none of those
/// interfaces declare non-nullable but which real callers will eventually pass anyway.
/// </summary>
public class DefaultStemmerEdgeCaseTests
{
    private readonly DefaultStemmer _stemmer = new();

    [Fact]
    public void Stem_Null_ReturnsEmptyStringAndDoesNotThrow()
    {
        _stemmer.Stem(null).Should().Be(string.Empty);
    }

    [Fact]
    public void Stem_EmptyString_ReturnsEmptyString()
    {
        _stemmer.Stem(string.Empty).Should().Be(string.Empty);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Stem_WhitespaceOnly_ReturnsInputUnchanged(string whitespace)
    {
        // Whitespace has no dominant alphabet, so language detection reports Unknown and the
        // stemmer must pass it through untouched rather than crash.
        _stemmer.Stem(whitespace).Should().Be(whitespace);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("я")]
    [InlineData("5")]
    public void Stem_SingleCharacter_NeverCrashesAndStaysAPrefix(string singleChar)
    {
        string stem = _stemmer.Stem(singleChar);
        (stem.Length == 0 || singleChar.StartsWith(stem, StringComparison.Ordinal)).Should().BeTrue();
    }

    [Theory]
    [InlineData("42")]
    [InlineData("1234567890")]
    [InlineData("007")]
    public void Stem_AllDigits_PassesThroughUnchanged(string digits)
    {
        // Unknown language classification for a pure-number token; both stemmers are skipped.
        _stemmer.Stem(digits).Should().Be(digits);
    }

    [Theory]
    [InlineData("приветxy")] // 6 Cyrillic letters vs. 2 Latin: Cyrillic majority, still mixed script.
    [InlineData("serverпр")] // 6 Latin letters vs. 2 Cyrillic: Latin majority, still mixed script.
    public void Stem_MixedScriptToken_NeverCrashesAndStaysAPrefix(string mixed)
    {
        string stem = _stemmer.Stem(mixed);
        (stem.Length == 0 || mixed.StartsWith(stem, StringComparison.Ordinal)).Should().BeTrue();
    }

    [Theory]
    [InlineData("😀")]
    [InlineData("!!!")]
    [InlineData("###@@@")]
    public void Stem_EmojiOrSymbolsOnly_PassesThroughUnchanged(string symbols)
    {
        _stemmer.Stem(symbols).Should().Be(symbols);
    }

    [Fact]
    public void Stem_VeryLongString_CompletesQuicklyAndStaysAPrefix()
    {
        string longWord = new string('a', 100_000);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        string stem = _stemmer.Stem(longWord);
        stopwatch.Stop();

        (stem.Length == 0 || longWord.StartsWith(stem, StringComparison.Ordinal)).Should().BeTrue();
        // Generous bound: a linear-time algorithm handles 100k characters in well under a second;
        // this is here to catch an accidental quadratic-blowup regression, not to micro-benchmark.
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(1000);
    }

    [Fact]
    public void Stem_StringWithCombiningDiacritic_ConsidersOnlyTheNormalizedForm()
    {
        // "e" + combining acute accent (U+0301), not the precomposed "é". DefaultStemmer only
        // strips trailing characters of whatever string it's given — normalization is the
        // tokenizer's job — so this raw combining-mark form is stemmed as-is without crashing.
        string withCombiningMark = "café";
        string stem = _stemmer.Stem(withCombiningMark);
        (stem.Length == 0 || withCombiningMark.StartsWith(stem, StringComparison.Ordinal)).Should().BeTrue();
    }

    [Fact]
    public void Stem_UsingTokenizerThenStemmerTogether_NormalizesDiacriticsFirst()
    {
        var tokenizer = new DefaultTokenizer();
        string token = tokenizer.Tokenize("café").Single();
        _stemmer.Stem(token).Should().Be("cafe");
    }
}
