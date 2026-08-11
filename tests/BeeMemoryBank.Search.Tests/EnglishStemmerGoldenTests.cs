using BeeMemoryBank.Search;

namespace BeeMemoryBank.Search.Tests;

/// <summary>
/// Golden-file tests for <see cref="EnglishStemmer"/>: exact expected stems, pinned so future
/// rule changes show up as reviewable diffs rather than silent behavior drift. Covers every
/// inflection family called out in the WP-05 brief: plurals, -ed, -ing, -ly, -ness, -ment, the
/// -tion/-sion family (handled here via the generic "-ion" rule, see <see cref="EnglishStemmer"/>
/// remarks), and comparative/superlative -er/-est.
/// </summary>
public class EnglishStemmerGoldenTests
{
    private readonly EnglishStemmer _stemmer = new();

    [Theory]
    // Plurals (-s / -es).
    [InlineData("cats", "cat")]
    [InlineData("dogs", "dog")]
    [InlineData("boxes", "box")]
    [InlineData("buses", "bus")]
    [InlineData("watches", "watch")]
    [InlineData("codes", "code")] // silent-e root: "-es" guard defers to plain "-s" here.
    [InlineData("bikes", "bike")]
    [InlineData("glasses", "glass")]
    // -ed.
    [InlineData("played", "play")]
    [InlineData("walked", "walk")]
    [InlineData("wanted", "want")]
    [InlineData("needed", "need")]
    [InlineData("jumped", "jump")]
    [InlineData("opened", "open")]
    [InlineData("closed", "clo")] // known cascade: "-ed" strips to "clos", which then matches the plain "-s" rule too.
    [InlineData("painted", "paint")]
    // -ing.
    [InlineData("playing", "play")]
    [InlineData("working", "work")]
    [InlineData("jumping", "jump")]
    [InlineData("opening", "open")]
    [InlineData("talking", "talk")]
    [InlineData("running", "runn")] // no consonant-doubling undo, see EnglishStemmer remarks.
    [InlineData("reading", "read")]
    [InlineData("hopping", "hopp")]
    // -ly.
    [InlineData("quickly", "quick")]
    [InlineData("slowly", "slow")]
    [InlineData("badly", "bad")]
    [InlineData("likely", "like")]
    [InlineData("friendly", "friend")]
    [InlineData("calmly", "calm")]
    // -ness.
    [InlineData("happiness", "happi")]
    [InlineData("kindness", "kind")]
    [InlineData("darkness", "dark")]
    [InlineData("sadness", "sad")]
    [InlineData("illness", "ill")]
    [InlineData("weakness", "weak")]
    // -ment.
    [InlineData("agreement", "agree")]
    [InlineData("government", "govern")]
    [InlineData("development", "develop")]
    [InlineData("movement", "move")]
    [InlineData("payment", "pay")]
    [InlineData("treatment", "treat")]
    // -tion/-sion family, via generic "-ion".
    [InlineData("creation", "creat")]
    [InlineData("education", "educat")]
    [InlineData("information", "informat")]
    [InlineData("decision", "decis")]
    [InlineData("vision", "vis")]
    [InlineData("action", "act")]
    [InlineData("nation", "nat")]
    [InlineData("discussion", "discuss")]
    // Comparative / superlative -er / -est.
    [InlineData("faster", "fast")]
    [InlineData("bigger", "bigg")]
    [InlineData("smaller", "small")]
    [InlineData("quicker", "quick")]
    [InlineData("biggest", "bigg")]
    [InlineData("smallest", "small")]
    [InlineData("fastest", "fast")]
    [InlineData("older", "old")]
    public void Stem_GoldenInflectedForm_MatchesExpectedStem(string word, string expectedStem)
    {
        _stemmer.Stem(word).Should().Be(expectedStem);
    }

    [Theory]
    // Base forms with no suffix to strip.
    [InlineData("cat", "cat")]
    [InlineData("dog", "dog")]
    [InlineData("box", "box")]
    // The "-ss"/"-us"/"-is" guard on the plain "-s" rule: these must not be truncated further.
    [InlineData("this", "this")]
    [InlineData("bus", "bus")]
    [InlineData("glass", "glass")]
    // "server"/"servers" both converge on the same stem via -er then plain -s respectively.
    [InlineData("server", "serv")]
    [InlineData("servers", "serv")]
    public void Stem_StableOrGuardedWord_MatchesExpectedStem(string word, string expectedStem)
    {
        _stemmer.Stem(word).Should().Be(expectedStem);
    }
}
