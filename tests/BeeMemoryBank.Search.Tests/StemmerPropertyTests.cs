using System.Text;
using BeeMemoryBank.Search;

namespace BeeMemoryBank.Search.Tests;

/// <summary>
/// The load-bearing test for WP-05: a large-scale fuzz run asserting the prefix invariant and
/// idempotency hold for every generated case, not just the curated golden-file examples.
/// </summary>
/// <remarks>
/// <para>
/// <b>What "prefix" means here, precisely.</b> The invariant this project depends on is:
/// <c>Stem(token)</c> is either empty or a true prefix of <c>token</c> <i>as passed to
/// <see cref="IStemmer.Stem"/></i> — i.e. of the already-normalized token, not of whatever raw
/// text it may have come from. This test normalizes each generated string with
/// <see cref="TextNormalization.Normalize"/> first (exactly what a real <see cref="ITokenizer"/>
/// pass would produce) and then checks the prefix relationship against that normalized string,
/// which is the meaningful guarantee for the downstream FTS5 prefix-query design this WP exists
/// to support: the index stores normalized-but-unstemmed word forms, and the stem must prefix
/// those, not the original raw bytes (which may have had diacritics or different casing).
/// </para>
/// <para>
/// <b>Idempotency</b> is checked in the same pass: <c>Stem(Stem(x)) == Stem(x)</c> for every case.
/// </para>
/// </remarks>
public class StemmerPropertyTests
{
    // Large but not excessive: keeps `dotnet test` fast while comfortably exceeding "thousands of
    // cases" per generator category (several categories below, each run at this count or a
    // fraction of it for the combinatorial ones).
    private const int CasesPerCategory = 3000;

    // Written as a numeric cast rather than a literal combining-mark character in source so the
    // file doesn't contain an invisible/zero-width glyph that renders inconsistently across
    // editors. U+0301 is COMBINING ACUTE ACCENT.
    private static readonly char CombiningAcuteAccent = (char)0x0301;

    private readonly DefaultStemmer _stemmer = new();

    public static IEnumerable<object[]> RealDictionaryWords()
    {
        // A sample of real words already covered in depth by the golden-file tests, reused here
        // as fixed, non-random property-test inputs (as opposed to the golden tests' exact-value
        // assertions, these only check the two structural properties).
        string[] words =
        [
            "cats", "boxes", "played", "running", "quickly", "happiness", "agreement", "creation",
            "biggest", "server", "servers", "glass", "this", "bus", "closed", "friendliness",
            "unbelievable", "internationalization", "self-service", "co-operate",
            "сервер", "сервера", "серверов", "сервером", "стол", "стола", "дом", "дома",
            "новый", "новая", "синий", "большой", "читать", "читаю", "говорить", "гулять",
            "остров", "мама", "окно", "информация", "образование", "путешествие",
        ];
        foreach (string w in words)
        {
            yield return [w];
        }
    }

    [Theory]
    [MemberData(nameof(RealDictionaryWords))]
    public void Stem_RealDictionaryWord_IsPrefixAndIdempotent(string rawWord)
    {
        AssertPrefixAndIdempotent(rawWord);
    }

    [Fact]
    public void Stem_RandomEnglishLikeWords_ArePrefixAndIdempotent()
    {
        var random = new Random(20260811);
        const string alphabet = "abcdefghijklmnopqrstuvwxyz";
        RunFuzzBatch(CasesPerCategory, () => RandomWord(random, alphabet, 0, 25));
    }

    [Fact]
    public void Stem_RandomRussianLikeWords_ArePrefixAndIdempotent()
    {
        var random = new Random(20260812);
        const string alphabet = "абвгдежзийклмнопрстуфхцчшщъыьэюяё";
        RunFuzzBatch(CasesPerCategory, () => RandomWord(random, alphabet, 0, 25));
    }

    [Fact]
    public void Stem_RandomMixedScriptStrings_ArePrefixAndIdempotent()
    {
        var random = new Random(20260813);
        const string enAlphabet = "abcdefghijklmnopqrstuvwxyz";
        const string ruAlphabet = "абвгдежзийклмнопрстуфхцчшщъыьэюя";
        RunFuzzBatch(CasesPerCategory, () =>
        {
            int len = random.Next(0, 20);
            var builder = new StringBuilder(len);
            for (int i = 0; i < len; i++)
            {
                string alphabet = random.Next(2) == 0 ? enAlphabet : ruAlphabet;
                builder.Append(alphabet[random.Next(alphabet.Length)]);
            }

            return builder.ToString();
        });
    }

    [Fact]
    public void Stem_RandomDigitStrings_ArePrefixAndIdempotent()
    {
        var random = new Random(20260814);
        RunFuzzBatch(CasesPerCategory / 2, () =>
        {
            int len = random.Next(0, 20);
            var builder = new StringBuilder(len);
            for (int i = 0; i < len; i++)
            {
                builder.Append((char)('0' + random.Next(10)));
            }

            return builder.ToString();
        });
    }

    [Fact]
    public void Stem_RandomPerturbedRealWords_ArePrefixAndIdempotent()
    {
        // Takes real words and randomly inserts/deletes/duplicates characters, simulating typos,
        // truncated OCR/import text, and other "almost a word" input the stemmer must not choke
        // on. Also covers strings with combining diacritics injected at random positions.
        string[] seeds =
        [
            "server", "computer", "development", "information", "beautiful", "running",
            "сервер", "информация", "путешествие", "красивый", "разработка", "образование",
        ];
        var random = new Random(20260815);
        RunFuzzBatch(CasesPerCategory, () => PerturbWord(random, seeds[random.Next(seeds.Length)]));
    }

    [Fact]
    public void Stem_KnownDegenerateInputs_ArePrefixAndIdempotent()
    {
        string[] degenerate =
        [
            "",
            " ",
            "   ",
            "\t\n",
            "a",
            "я",
            "1",
            "😀",
            "!!!",
            "###@@@",
            new string('a', 10_000),
            new string('и', 10_000),
            new string('s', 500), // pathological: every "-s" guard should keep this stable quickly.
            "café", // raw combining-mark form (e + U+0301), not precomposed.
            "\U0001F600hello\U0001F600",
            "hello-world_123",
            "Съешь" + CombiningAcuteAccent, // Cyrillic word with a combining accent mark injected.
        ];

        foreach (string input in degenerate)
        {
            AssertPrefixAndIdempotent(input);
        }
    }

    private void RunFuzzBatch(int count, Func<string> generator)
    {
        for (int i = 0; i < count; i++)
        {
            AssertPrefixAndIdempotent(generator());
        }
    }

    private void AssertPrefixAndIdempotent(string raw)
    {
        // This is what a real ITokenizer pass would hand to IStemmer.Stem — the prefix guarantee
        // is stated relative to this normalized form, not the raw input (see remarks above).
        string normalized = TextNormalization.Normalize(raw);

        string stem = _stemmer.Stem(normalized);

        bool isEmptyOrPrefix = stem.Length == 0
            || (stem.Length <= normalized.Length && normalized.StartsWith(stem, StringComparison.Ordinal));

        isEmptyOrPrefix.Should().BeTrue(
            $"Stem('{normalized}') = '{stem}' must be empty or a true prefix of the normalized token");

        string stemOfStem = _stemmer.Stem(stem);
        stemOfStem.Should().Be(stem, $"stemming '{stem}' again must be a no-op (idempotency)");
    }

    private static string RandomWord(Random random, string alphabet, int minLength, int maxLength)
    {
        int len = random.Next(minLength, maxLength + 1);
        var builder = new StringBuilder(len);
        for (int i = 0; i < len; i++)
        {
            builder.Append(alphabet[random.Next(alphabet.Length)]);
        }

        return builder.ToString();
    }

    private static string PerturbWord(Random random, string word)
    {
        var chars = new List<char>(word);
        int operations = random.Next(1, 4);
        for (int op = 0; op < operations; op++)
        {
            if (chars.Count == 0)
            {
                break;
            }

            switch (random.Next(4))
            {
                case 0: // Delete a random character.
                    chars.RemoveAt(random.Next(chars.Count));
                    break;
                case 1: // Duplicate a random character.
                    int dupIndex = random.Next(chars.Count);
                    chars.Insert(dupIndex, chars[dupIndex]);
                    break;
                case 2: // Insert a random letter.
                    chars.Insert(random.Next(chars.Count + 1), (char)('a' + random.Next(26)));
                    break;
                case 3: // Inject a combining diacritic (Unicode combining acute accent, U+0301).
                    chars.Insert(random.Next(chars.Count + 1), CombiningAcuteAccent);
                    break;
            }
        }

        return new string(chars.ToArray());
    }
}
