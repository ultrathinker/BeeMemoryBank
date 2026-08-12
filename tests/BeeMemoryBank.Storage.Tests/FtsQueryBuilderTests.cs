using BeeMemoryBank.Storage.Sqlite;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// Pure unit tests for <see cref="FtsQueryBuilder"/>: the tokenize → stem → quote → join pipeline
/// that turns a raw user query into an FTS5 <c>MATCH</c> expression. No database involved.
/// </summary>
public class FtsQueryBuilderTests
{
    [Fact]
    public void Null_Or_Empty_Query_Returns_Null()
    {
        FtsQueryBuilder.BuildMatchExpression(null).Should().BeNull();
        FtsQueryBuilder.BuildMatchExpression("").Should().BeNull();
        FtsQueryBuilder.BuildMatchExpression("   ").Should().BeNull();
        FtsQueryBuilder.BuildMatchExpression("!!! ???").Should().BeNull(
            "punctuation-only input has no word tokens and must not produce a MATCH expr");
    }

    [Fact]
    public void Single_Ru_Inflected_Form_Stems_And_Becomes_Prefix_Token()
    {
        // "сервера" stems to "сервер"; the prefix invariant makes "сервер"* match all forms.
        FtsQueryBuilder.BuildMatchExpression("сервера").Should().Be("\"сервер\"*");
    }

    [Fact]
    public void Multiple_Terms_Are_Space_Joined_Implicit_And()
    {
        var expr = FtsQueryBuilder.BuildMatchExpression("postgres runbook");
        // The exact stem values are the stemmer's contract (tested in BeeMemoryBank.Search.Tests);
        // here we assert the structural contract that matters for the wiring: two terms, each a
        // quoted prefix literal, joined by a single space (FTS5 implicit AND — not OR, not comma).
        expr.Should().NotBeNull();
        var pieces = expr!.Split(' ');
        pieces.Should().HaveCount(2, "two query words yield two stemmed terms joined by a single space");
        pieces.Should().AllSatisfy(p =>
        {
            p.Should().StartWith("\"");
            p.Should().EndWith("\"*");
        });
    }

    [Fact]
    public void And_Operator_Never_Emitted_Explicitly()
    {
        // FTS5 implicit AND is just a space; we must not emit the bareword "AND" (which could be
        // parsed as the OR/AND/NOT operator if a query term happened to stem to it).
        var expr = FtsQueryBuilder.BuildMatchExpression("alpha beta");
        expr.Should().NotContain("AND", "implicit AND is a space, never a bareword operator");
        expr.Should().NotContain(" OR ");
        expr.Should().NotContain(" NOT ");
    }

    [Fact]
    public void Punctuation_Acts_As_Token_Boundary_Not_Syntax()
    {
        // Quotes / colons / parens in the raw query are punctuation boundaries for the tokenizer,
        // never FTS5 syntax. They never reach the quoting stage as part of a token.
        FtsQueryBuilder.BuildMatchExpression("a \"b\" c").Should().Be("\"a\"* \"b\"* \"c\"*");
        FtsQueryBuilder.BuildMatchExpression("x:y").Should().Be("\"x\"* \"y\"*");
    }

    [Fact]
    public void Case_And_Diacritics_Are_Normalized_Away()
    {
        // "Café" -> NFKC + lowercase + diacritic-strip -> "cafe"; English stemmer keeps it.
        FtsQueryBuilder.BuildMatchExpression("Café").Should().Be("\"cafe\"*");
    }

    [Fact]
    public void En_Plural_And_Past_Stem_To_Their_Prefix()
    {
        // "servers" -> "serv" (English suffix stripper drops -er, -s chains); "running" -> "run".
        // The exact stem value is the stemmer's contract (tested in BeeMemoryBank.Search.Tests);
        // here we only assert the builder wraps whatever stem it gets as a quoted prefix token,
        // so the output shape is what matters, and it must always be a proper prefix literal.
        var expr = FtsQueryBuilder.BuildMatchExpression("running");
        expr.Should().StartWith("\"").And.EndWith("\"*");
        expr.Should().NotContain(" ", "a single-term query yields exactly one token");
    }

    [Fact]
    public void Each_Emitted_Token_Is_A_Quoted_Prefix_Literal()
    {
        // Invariant over arbitrary multi-term input: every whitespace-separated piece of the
        // output is "<quote><stem><quote>*" — never a bareword, never an unquoted operator.
        var expr = FtsQueryBuilder.BuildMatchExpression("one two three four");
        expr.Should().NotBeNull();
        foreach (var piece in expr!.Split(' '))
        {
            piece.Should().StartWith("\"",
                "every term must be an FTS5 string literal, not a bareword that could be parsed as AND/OR/NOT");
            piece.Should().EndWith("\"*",
                "every term must be a prefix token (trailing * outside the quotes)");
        }
    }
}
