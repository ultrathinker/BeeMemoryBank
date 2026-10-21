using System.Runtime.CompilerServices;
using BeeMemoryBank.BlindDesktop.ViewModels;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>
/// What a diagnostic of the desktop head records about an exception: its type and its frames, and nothing it said. The messages here are hostile on purpose - a path, an address with a token, a key
/// alias, a line break - so that a log that kept any of them would show it.
/// </summary>
[Collection("ErrorLog")]
public sealed class DiagnosticTextTests
{
    private const string Canary = "CANARY-secret-token-42";

    private static string HostileMessage => $"Could not open C:\\Users\\someone\\{Canary}\\identity.key\nfor https://hub.example/x?token={Canary} (alias {Canary})";

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception ThrownFromAnInnerMethod(Exception ex)
    {
        try { throw ex; }
        catch (Exception caught) { return caught; }
    }

    private sealed class MessageThatMustNotBeRead : Exception
    {
        public override string Message => throw new InvalidOperationException("the message was read");
        public override string ToString() => throw new InvalidOperationException("the exception was turned into text");
    }

    [Fact]
    public void ItTellsTheTypeAndTheFrames_AndNotWhatTheExceptionSaid()
    {
        var ex = ThrownFromAnInnerMethod(new IOException(HostileMessage));

        var text = DiagnosticText.Describe(ex);

        text.Should().StartWith("IOException at ").And.Contain("DiagnosticTextTests.ThrownFromAnInnerMethod");
        text.Should().NotContain(Canary).And.NotContain("identity.key").And.NotContain("hub.example").And.NotContain("Could not open");
        text.Should().NotContain("\n", "one line, whatever the exception's message held");
    }

    [Fact]
    public void ItNeverReadsTheMessage_NotEvenToSkipIt()
    {
        var ex = ThrownFromAnInnerMethod(new MessageThatMustNotBeRead());

        DiagnosticText.Describe(ex).Should().StartWith("MessageThatMustNotBeRead at ");
    }

    [Fact]
    public void AFrameIsAMethodsName_NeverTheFileItWasBuiltFrom()
    {
        var text = DiagnosticText.Describe(ThrownFromAnInnerMethod(new InvalidOperationException("x")));

        text.Should().NotContain(".cs").And.NotContain(":line").And.NotContain("\\").And.NotContain(" in ");
    }

    [Fact]
    public void TheChainInsideAnExceptionIsToldByTypeAndFrames_NoInnerMessageEither()
    {
        var inner = ThrownFromAnInnerMethod(new UnauthorizedAccessException($"inner says {Canary}"));
        var ex = ThrownFromAnInnerMethod(new IOException($"outer says {Canary}", inner));

        var text = DiagnosticText.Describe(ex);

        text.Should().StartWith("IOException at ").And.Contain(" | inner UnauthorizedAccessException at ");
        text.Should().NotContain(Canary).And.NotContain("says");
    }

    [Fact]
    public void AnAggregate_IsToldByTheTypesOfItsFailures()
    {
        var ex = new AggregateException($"two failures {Canary}",
            ThrownFromAnInnerMethod(new IOException($"a {Canary}")),
            ThrownFromAnInnerMethod(new TimeoutException($"b {Canary}")));

        var text = DiagnosticText.Describe(ex);

        text.Should().StartWith("AggregateException").And.Contain("inner IOException at ").And.Contain("inner TimeoutException at ");
        text.Should().NotContain(Canary);
    }

    [Fact]
    public void ALongChainAndADeepStack_AreCut_ToOneShortLine()
    {
        Exception chain = new InvalidOperationException(Canary);
        for (var i = 0; i < 20; i++) chain = new IOException(Canary + i, chain);

        var text = DiagnosticText.Describe(chain);

        text.Should().EndWith(" | ...", "the rest of the chain is said to be cut");
        text.Length.Should().BeLessThanOrEqualTo(803);
        text.Should().NotContain(Canary);
    }

    [Fact]
    public void AnExceptionThatWasNeverThrown_HasNoFrames_AndStillHasItsType()
    {
        DiagnosticText.Describe(new TimeoutException(Canary)).Should().Be("TimeoutException");
    }

    [Fact]
    public void ErrorLog_WritesOneLine_WithTheTypeAndTheFrames_AndNoMessage()
    {
        var original = ErrorLog.PathOfLog;
        try
        {
            var path = Path.Combine(TestFolders.New("errorlog"), "error.log");
            ErrorLog.UsePath(path);

            ErrorLog.Write("Showing the window failed", ThrownFromAnInnerMethod(new IOException(HostileMessage, new UnauthorizedAccessException($"inner {Canary}"))));
            ErrorLog.Write("Reading the clipboard failed", new MessageThatMustNotBeRead());

            var lines = File.ReadAllLines(path);
            lines.Should().HaveCount(2, "a message with a line break does not break the file into lines");
            lines[0].Should().Contain("Showing the window failed").And.Contain("IOException at ").And.Contain("inner UnauthorizedAccessException");
            lines[1].Should().Contain("Reading the clipboard failed").And.Contain("MessageThatMustNotBeRead", "a log line is written even for an exception whose text cannot be read");
            File.ReadAllText(path).Should().NotContain(Canary).And.NotContain("identity.key").And.NotContain("hub.example");
        }
        finally
        {
            ErrorLog.UsePath(original);
        }
    }
}
