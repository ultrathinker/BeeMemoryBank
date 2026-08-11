using BeeMemoryBank.Search;

namespace BeeMemoryBank.Search.Tests;

public class TextNormalizationTests
{
    [Theory]
    [InlineData("café", "cafe")]
    [InlineData("Café", "cafe")]
    [InlineData("naïve", "naive")]
    [InlineData("RÉSUMÉ", "resume")]
    [InlineData("HELLO", "hello")]
    [InlineData("МОСКВА", "москва")]
    [InlineData("Ёж", "еж")] // "ё" decomposes and folds to "е" once its combining diaeresis is stripped.
    public void Normalize_StripsDiacriticsAndLowercases(string input, string expected)
    {
        TextNormalization.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Normalize_NullOrEmpty_ReturnsEmptyString()
    {
        TextNormalization.Normalize(null).Should().Be(string.Empty);
        TextNormalization.Normalize(string.Empty).Should().Be(string.Empty);
    }

    [Fact]
    public void Normalize_PlainAsciiWord_IsUnchangedExceptCasing()
    {
        TextNormalization.Normalize("Hello").Should().Be("hello");
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        string once = TextNormalization.Normalize("Café NAÏVE москва");
        string twice = TextNormalization.Normalize(once);
        twice.Should().Be(once);
    }
}
