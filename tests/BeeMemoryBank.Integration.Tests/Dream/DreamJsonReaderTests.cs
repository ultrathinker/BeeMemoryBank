using System.Text.Json.Nodes;
using BeeMemoryBank.Api.Dream;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>The tolerant reading of a model reply and its schema check (plan 3.7): what is accepted, what is refused,
/// and that no refusal repeats the model's own words.</summary>
public class DreamJsonReaderTests
{
    private static JsonObject Read(string reply)
    {
        DreamJsonReader.TryReadObject(reply, out var obj, out var error).Should().BeTrue(error);
        return obj!;
    }

    private static string Fail(string? reply)
    {
        DreamJsonReader.TryReadObject(reply, out var obj, out var error).Should().BeFalse();
        obj.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
        return error!;
    }

    [Fact]
    public void APlainObject_IsRead()
    {
        Read("""{"verdict":"duplicate","score":90}""")["verdict"]!.GetValue<string>().Should().Be("duplicate");
    }

    [Theory]
    [InlineData("```json\n{\"a\":1}\n```")]
    [InlineData("```\n{\"a\":1}\n```")]
    [InlineData("Here is my answer:\n```JSON\n{\"a\":1}\n```\nHope that helps!")]
    [InlineData("Sure. {\"a\":1} Done.")]
    [InlineData("<think>I should reply {with} braces first</think>{\"a\":1}")]
    [InlineData("{\"a\":1,}")]
    [InlineData("{\"a\":1 /* why not */}")]
    public void ACodeFence_ProseAReasoningBlock_AreAllRight(string reply)
    {
        Read(reply)["a"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public void ABraceInProseBeforeAFencedObject_DoesNotMaskTheObject()
    {
        Read("The {first} thought.\n```json\n{\"a\":2}\n```").ToJsonString().Should().Be("""{"a":2}""");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    [InlineData("I cannot help with that.")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"a\":")]
    [InlineData("{\"a\":1} and {\"b\":2}")]
    [InlineData("{not json}")]
    public void AnythingThatIsNotOneJsonObject_IsRefused(string? reply) => Fail(reply);

    [Fact]
    public void AnOverDeepObject_IsRefused()
    {
        Fail(string.Concat(Enumerable.Repeat("{\"a\":", 40)) + "1" + new string('}', 40));
    }

    [Fact]
    public void AReplyBeyondTheCeiling_IsNotRead()
    {
        Fail("{\"a\":\"" + new string('x', DreamJsonReader.MaxReplyChars) + "\"}").Should().Contain("too long");
    }

    [Fact]
    public void RefusalMessages_NeverCarryTheModelsWords()
    {
        const string secret = "SECRET-WORDS-FROM-THE-NOTE";
        var errors = new[]
        {
            Fail($"{secret} and then {{\"a\": {secret} }}"),
            Fail($"{secret}"),
            Fail($"[\"{secret}\"]"),
        };
        errors.Should().OnlyContain(e => !e.Contains(secret));
    }

    // ── CleanText ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CleanText_TurnsControlCharsAndRunsOfWhiteSpaceIntoSingleSpaces()
    {
        DreamJsonReader.CleanText("  one\r\ntwo\t\tthree\u0000four  ", 100).Should().Be("one two three four");
    }

    [Fact]
    public void CleanText_DropsTheDirectionControlsThatMakeTextReadBackwards()
    {
        DreamJsonReader.CleanText("abc\u202edef\u2066ghi\u200f", 100).Should().Be("abcdefghi");
    }

    [Fact]
    public void CleanText_CutsAtTheLimit_WithAnEllipsis_AndNeverLonger()
    {
        var text = DreamJsonReader.CleanText(new string('a', 500), 400);
        text.Length.Should().Be(400);
        text.Should().EndWith("\u2026");
        DreamJsonReader.CleanText("short", 400).Should().Be("short");
    }

    [Fact]
    public void Cut_DoesNotSplitASurrogatePair()
    {
        var text = new string('a', 9) + "\U0001F600" + "bbbb";   // the emoji occupies indexes 9 and 10
        var cut = DreamJsonReader.Cut(text, 11);                 // keep 10 characters: would end between the two halves
        cut.Should().NotContain("\uD83D");
        cut.Length.Should().BeLessThanOrEqualTo(11);
        cut.Should().EndWith("\u2026");
    }

    // ── DreamJsonFields ─────────────────────────────────────────────────────────────────────────────────────────

    private static DreamJsonFields Fields(string json) => new(JsonNode.Parse(json)!.AsObject());

    [Fact]
    public void Text_RequiredAndMissing_RecordsTheFirstErrorOnly()
    {
        var f = Fields("""{"a":"x"}""");
        f.Text("missing", 10, required: true).Should().BeNull();
        f.Error.Should().Be("'missing' is required.");
        f.Integer("a", 0, 5, required: true);
        f.Error.Should().Be("'missing' is required.", "only the first problem is kept");
        f.Ok.Should().BeFalse();
    }

    [Fact]
    public void Text_OfTheWrongType_OrEmpty_IsRefused_AndALongOneIsCut()
    {
        var f = Fields("""{"n":5,"e":"  ","l":"aaaaaaaaaaaaaaaaaaaa"}""");
        f.Text("l", 10, required: true).Should().HaveLength(10).And.EndWith("\u2026");
        f.Ok.Should().BeTrue();
        f.Text("n", 10, required: true).Should().BeNull();
        f.Error.Should().Be("'n' must be a string.");

        var g = Fields("""{"e":"  "}""");
        g.Text("e", 10, required: true).Should().BeNull();
        g.Error.Should().Be("'e' must not be empty.");
    }

    [Fact]
    public void Text_NotRequired_IsNullWhenAbsentOrBlank_WithNoError()
    {
        var f = Fields("""{"e":" ", "z":null}""");
        f.Text("e", 10, required: false).Should().BeNull();
        f.Text("z", 10, required: false).Should().BeNull();
        f.Text("absent", 10, required: false).Should().BeNull();
        f.Ok.Should().BeTrue();
    }

    [Fact]
    public void OneOf_IgnoresCaseAndOuterWhiteSpace_AndReturnsTheAllowedSpelling()
    {
        var f = Fields("""{"v":"  Duplicate "}""");
        f.OneOf("v", ["duplicate", "overlap"], required: true).Should().Be("duplicate");
        f.Ok.Should().BeTrue();

        var g = Fields("""{"v":"rm -rf"}""");
        g.OneOf("v", ["duplicate", "overlap"], required: true).Should().BeNull();
        g.Error.Should().Be("'v' must be one of: duplicate, overlap.").And.NotContain("rm -rf");
    }

    [Theory]
    [InlineData("85", 85)]
    [InlineData("85.4", 85)]
    [InlineData("85.5", 86)]
    [InlineData("0", 0)]
    [InlineData("100", 100)]
    public void Integer_AcceptsAWholeNumberAndRoundsAFraction(string json, int expected)
    {
        var f = Fields($"{{\"s\":{json}}}");
        f.Integer("s", 0, 100, required: true).Should().Be(expected);
        f.Ok.Should().BeTrue();
    }

    [Theory]
    [InlineData("101")]
    [InlineData("-1")]
    [InlineData("\"85\"")]
    [InlineData("true")]
    [InlineData("1e400")]
    public void Integer_RefusesWhatIsOutOfRangeOrNotANumber(string json)
    {
        var f = Fields($"{{\"s\":{json}}}");
        f.Integer("s", 0, 100, required: true).Should().BeNull();
        f.Error.Should().Be("'s' must be a number from 0 to 100.");
    }

    [Fact]
    public void Array_IsRefusedWhenItIsSomethingElse()
    {
        var f = Fields("""{"q":"text","r":[1]}""");
        f.Array("r", required: true).Should().NotBeNull();
        f.Array("q", required: false).Should().BeNull();
        f.Error.Should().Be("'q' must be an array.");
    }
}
