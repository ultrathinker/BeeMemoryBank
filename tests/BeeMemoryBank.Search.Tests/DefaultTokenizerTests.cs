using BeeMemoryBank.Search;

namespace BeeMemoryBank.Search.Tests;

public class DefaultTokenizerTests
{
    private readonly DefaultTokenizer _tokenizer = new();

    [Fact]
    public void Tokenize_MixedRuEnPunctuationSentence_SplitsIntoExpectedTokens()
    {
        var tokens = _tokenizer.Tokenize("Café servers—серверов! don't stop, 123 go.").ToList();

        tokens.Should().Equal("cafe", "servers", "серверов", "don", "t", "stop", "123", "go");
    }

    [Fact]
    public void Tokenize_NullText_ReturnsEmptySequence()
    {
        _tokenizer.Tokenize(null).Should().BeEmpty();
    }

    [Fact]
    public void Tokenize_EmptyText_ReturnsEmptySequence()
    {
        _tokenizer.Tokenize(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Tokenize_WhitespaceOnlyText_ReturnsEmptySequence()
    {
        _tokenizer.Tokenize("   \t\n  ").Should().BeEmpty();
    }

    [Fact]
    public void Tokenize_PurePunctuation_ReturnsEmptySequence()
    {
        _tokenizer.Tokenize("!!! --- ,,, ???").Should().BeEmpty();
    }

    [Fact]
    public void Tokenize_SingleWord_ReturnsOneLowercasedToken()
    {
        _tokenizer.Tokenize("HELLO").Should().Equal("hello");
    }

    [Fact]
    public void Tokenize_AccentedWord_StripsDiacritics()
    {
        _tokenizer.Tokenize("café").Should().Equal("cafe");
    }

    [Fact]
    public void Tokenize_NumbersAndWordsMixed_KeepsNumbersAsSeparateTokens()
    {
        _tokenizer.Tokenize("Room 42b has 3 doors").Should().Equal("room", "42b", "has", "3", "doors");
    }

    [Fact]
    public void Tokenize_EmojiAndSymbols_AreDroppedAsBoundaries()
    {
        _tokenizer.Tokenize("hello \U0001F600 world").Should().Equal("hello", "world");
    }

    [Fact]
    public void Tokenize_RussianSentence_SplitsOnPunctuationAndWhitespace()
    {
        _tokenizer.Tokenize("Привет, мир! Как дела?").Should().Equal("привет", "мир", "как", "дела");
    }
}
