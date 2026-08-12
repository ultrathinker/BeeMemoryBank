using BeeMemoryBank.Search;

namespace BeeMemoryBank.Search.Tests;

/// <summary>
/// Golden-file tests for <see cref="RussianStemmer"/>: exact expected stems, pinned so future
/// rule changes show up as reviewable diffs. Covers noun case endings, the adjective ending
/// families (hard/soft, all genders and plural), and the three common verb conjugation shapes
/// (-ать, -ить, -ять infinitives with their present/past tense forms) called out in the WP-05
/// brief.
/// </summary>
public class RussianStemmerGoldenTests
{
    private readonly RussianStemmer _stemmer = new();

    [Theory]
    // "server" (a common loanword) across its full case paradigm — this is the exact example
    // from the WP-05 brief: "сервера"/"серверов"/"сервером" must all be prefix-compatible with
    // "сервер". This stemmer goes further and makes them all stem to the *identical* value.
    [InlineData("сервер", "сервер")]
    [InlineData("сервера", "сервер")]
    [InlineData("серверов", "сервер")]
    [InlineData("сервером", "сервер")]
    [InlineData("серверами", "сервер")]
    [InlineData("серверам", "сервер")]
    [InlineData("серверах", "сервер")]
    // "стол" (table) — a documented false-positive family: the bare "-л" rule (needed for
    // masculine past-tense verbs, e.g. читал -> чит) also fires on this noun's nominative
    // singular, so the whole paradigm converges on "сто" rather than "стол". Still a valid
    // prefix and still internally consistent (every form reduces the same way), which is what
    // the downstream prefix search actually needs — see RussianStemmer remarks for why this
    // tradeoff is accepted.
    [InlineData("стол", "сто")]
    [InlineData("стола", "сто")]
    [InlineData("столов", "сто")]
    [InlineData("столом", "сто")]
    // "дом" (house) — a paradigm the rules handle cleanly (no false-positive "-л" collision).
    [InlineData("дом", "дом")]
    [InlineData("дома", "дом")]
    [InlineData("домов", "дом")]
    [InlineData("дому", "дом")]
    // "кот" (cat).
    [InlineData("кот", "кот")]
    [InlineData("кота", "кот")]
    [InlineData("котов", "кот")]
    // "новый" (new) — hard-stem adjective across gender/plural/case.
    [InlineData("новый", "нов")]
    [InlineData("новая", "нов")]
    [InlineData("нового", "нов")]
    [InlineData("новыми", "нов")]
    // "синий" (blue) — soft-stem adjective variant.
    [InlineData("синий", "син")]
    [InlineData("синяя", "син")]
    [InlineData("синего", "син")]
    [InlineData("синими", "син")]
    // "большой" (big) — stressed-ending hard adjective.
    [InlineData("большой", "больш")]
    [InlineData("большая", "больш")]
    [InlineData("большого", "больш")]
    [InlineData("большими", "больш")]
    // "читать" (to read) — first-conjugation verb: infinitive, present tense (all persons), past.
    [InlineData("читать", "чит")]
    [InlineData("читаю", "чит")]
    [InlineData("читаешь", "чит")]
    [InlineData("читает", "чит")]
    [InlineData("читаем", "чит")]
    [InlineData("читают", "чит")]
    [InlineData("читал", "чит")]
    // "говорить" (to speak) — second-conjugation (-ить) verb.
    [InlineData("говорить", "говор")]
    [InlineData("говоришь", "говор")]
    [InlineData("говорит", "говор")]
    [InlineData("говорим", "говор")]
    [InlineData("говорил", "говор")]
    // "гулять" (to walk/stroll) — first-conjugation (-ять) verb.
    [InlineData("гулять", "гул")]
    [InlineData("гуляешь", "гул")]
    [InlineData("гуляет", "гул")]
    [InlineData("гуляют", "гул")]
    [InlineData("гуляла", "гул")]
    // "остров" (island) — another documented false positive: ends in "-ов", which is otherwise
    // the genitive plural noun ending, so it gets truncated even though it's not inflected here.
    [InlineData("остров", "остр")]
    // Short (3-letter) roots ending in a vowel that is itself a case ending.
    [InlineData("мама", "мам")]
    [InlineData("папа", "пап")]
    // Neuter noun, nominative singular vs. genitive singular (both reduce the same way).
    [InlineData("окно", "окн")]
    [InlineData("окна", "окн")]
    public void Stem_GoldenInflectedForm_MatchesExpectedStem(string word, string expectedStem)
    {
        _stemmer.Stem(word).Should().Be(expectedStem);
    }

    [Fact]
    public void Stem_GenitivePluralWithConsonantCluster_IsAKnownRecallGap()
    {
        // "окон" (genitive plural of "окно") loses its final vowel entirely in this declension
        // pattern, so it doesn't end in any suffix this deletion-only stemmer recognizes. It
        // therefore does NOT converge with "окно"/"окна" above. This is an accepted recall gap,
        // not a bug: the brief explicitly scopes this to "common declension patterns", not full
        // academic-grade morphology, and a deletion-only design cannot special-case every
        // irregular paradigm without inventing a substitution (which would break the prefix
        // invariant). Pinned here so the gap is documented and visible, not silently rediscovered.
        _stemmer.Stem("окон").Should().Be("окон");
    }
}
