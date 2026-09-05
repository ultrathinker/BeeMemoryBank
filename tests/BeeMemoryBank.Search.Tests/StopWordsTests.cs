using BeeMemoryBank.Search;

namespace BeeMemoryBank.Search.Tests;

/// <summary>
/// Unit coverage for <see cref="StopWords"/>: the curated English + Russian query-time stop-word
/// set is matched on the SURFACE token (post-normalization, pre-stemming), so these assertions use
/// the exact normalized forms the tokenizer emits. Content words a user would actually search for
/// must never be treated as stop words.
/// </summary>
public class StopWordsTests
{
    [Theory]
    [InlineData("the")]
    [InlineData("a")]
    [InlineData("and")]
    [InlineData("of")]
    [InlineData("is")]
    [InlineData("not")]
    public void IsStopWord_EnglishFunctionWords_AreStopWords(string token) =>
        StopWords.IsStopWord(token).Should().BeTrue();

    [Theory]
    [InlineData("и")]
    [InlineData("в")]
    [InlineData("не")]
    [InlineData("что")]
    [InlineData("для")]
    [InlineData("этом")]
    public void IsStopWord_RussianFunctionWords_AreStopWords(string token) =>
        StopWords.IsStopWord(token).Should().BeTrue();

    [Theory]
    [InlineData("system")]
    [InlineData("server")]
    [InlineData("database")]
    [InlineData("поиск")]
    [InlineData("сервер")]
    [InlineData("zzneedle")]
    public void IsStopWord_ContentWords_AreNotStopWords(string token) =>
        StopWords.IsStopWord(token).Should().BeFalse();

    [Fact]
    public void IsStopWord_FoldsYoAndCase_TheSameWayTheTokenizerDoes()
    {
        // "ещё" normalizes (lowercase + ё->е) to "еще", which IS in the list; the raw form must
        // match once normalized exactly as a real query token would be.
        StopWords.IsStopWord(TextNormalization.Normalize("ЕЩЁ")).Should().BeTrue();
        StopWords.IsStopWord(TextNormalization.Normalize("Её")).Should().BeTrue();
        StopWords.IsStopWord(TextNormalization.Normalize("The")).Should().BeTrue();
    }

    [Fact]
    public void IsStopWord_EmptyOrNull_IsNotStopWord()
    {
        StopWords.IsStopWord(string.Empty).Should().BeFalse();
        StopWords.IsStopWord(null!).Should().BeFalse();
    }
}
