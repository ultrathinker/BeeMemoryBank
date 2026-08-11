using BeeMemoryBank.Search;

namespace BeeMemoryBank.Search.Tests;

public class LanguageDetectorTests
{
    [Theory]
    [InlineData("hello")]
    [InlineData("server")]
    [InlineData("café")] // accented Latin, before diacritic stripping.
    [InlineData("42b7")] // digits don't count toward either side; the single Latin letter still wins.
    public void Detect_LatinDominantToken_ReturnsEnglish(string token)
    {
        LanguageDetector.Detect(token).Should().Be(DetectedLanguage.English);
    }

    [Theory]
    [InlineData("сервер")]
    [InlineData("привет")]
    [InlineData("ёж")]
    public void Detect_CyrillicDominantToken_ReturnsRussian(string token)
    {
        LanguageDetector.Detect(token).Should().Be(DetectedLanguage.Russian);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("😀")]
    public void Detect_NoOrTiedAlphabetSignal_ReturnsUnknown(string token)
    {
        LanguageDetector.Detect(token).Should().Be(DetectedLanguage.Unknown);
    }

    [Fact]
    public void Detect_NullToken_ReturnsUnknown()
    {
        LanguageDetector.Detect(null).Should().Be(DetectedLanguage.Unknown);
    }

    [Fact]
    public void Detect_EvenlyMixedScriptToken_ReturnsUnknown()
    {
        // 3 Cyrillic letters, 3 Latin letters: an exact tie resolves to Unknown, not a guess.
        LanguageDetector.Detect("abcавс").Should().Be(DetectedLanguage.Unknown);
    }

    [Fact]
    public void Detect_MixedScriptWithCyrillicMajority_ReturnsRussian()
    {
        LanguageDetector.Detect("привetт").Should().Be(DetectedLanguage.Russian);
    }
}
