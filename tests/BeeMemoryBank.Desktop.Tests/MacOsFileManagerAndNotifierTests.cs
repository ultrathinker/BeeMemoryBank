using System;
using System.Collections.Generic;
using System.Linq;
using BeeMemoryBank.Desktop.MacOS;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>"Open the folder" / "show in Finder" for a Mac: <c>open</c> started with an argument list, never through a shell.</summary>
public sealed class MacOsFileManagerTests
{
    private static (MacOsFileManager Manager, FakeCommandRunner Runner, List<string> Created, List<string> Log) Make(
        Func<FakeCommandRunner.Call, CommandResult>? answer = null)
    {
        var runner = new FakeCommandRunner(answer);
        var created = new List<string>();
        var log = new List<string>();
        return (new MacOsFileManager(runner, created.Add, log.Add), runner, created, log);
    }

    [Fact]
    public void OpenFolder_RunsOpenWithTheFolderAsOneArgument_AfterCreatingIt()
    {
        var (manager, runner, created, _) = Make();

        manager.OpenFolder("/Users/someone/Library/Application Support/BeeMemoryBankData/My Vault");

        created.Should().Equal("/Users/someone/Library/Application Support/BeeMemoryBankData/My Vault");
        var call = runner.Calls.Should().ContainSingle().Subject;
        call.FileName.Should().Be("/usr/bin/open", "an absolute path, never looked up on PATH");
        call.Arguments.Should().Equal("/Users/someone/Library/Application Support/BeeMemoryBankData/My Vault");
    }

    [Fact]
    public void Reveal_RunsOpenDashR_WithTheItemAsOneArgument_AndCreatesNothing()
    {
        var (manager, runner, created, _) = Make();

        manager.Reveal("/Users/someone/Backups/phone.bmbbackup");

        created.Should().BeEmpty("revealing selects an item that exists; it does not make one");
        var call = runner.Calls.Should().ContainSingle().Subject;
        call.FileName.Should().Be("/usr/bin/open");
        call.Arguments.Should().Equal("-R", "/Users/someone/Backups/phone.bmbbackup");
    }

    [Theory]
    [InlineData("/Users/a b/it's here")]
    [InlineData("/Users/x/say \"hi\"")]
    [InlineData("/Users/x/back\\slash")]
    [InlineData("/Users/x/semi;colon && echo pwned")]
    [InlineData("/Users/x/$(touch injected)")]
    [InlineData("/Users/x/`id`")]
    [InlineData("/Users/x/-a Calculator")]
    [InlineData("/Users/x/line\nbreak")]
    public void APathWithSpacesQuotesOrShellSyntax_IsOneArgument_UnchangedByte_ForByte(string path)
    {
        var (manager, runner, _, _) = Make();

        manager.OpenFolder(path);
        manager.Reveal(path);

        runner.Calls.Should().HaveCount(2);
        runner.Calls[0].Arguments.Should().Equal(path);
        runner.Calls[1].Arguments.Should().Equal("-R", path);
    }

    [Fact]
    public void ARelativePath_IsMadeAbsolute_SoItCanNeverBeTakenForAnOption()
    {
        var (manager, runner, _, _) = Make();

        manager.Reveal("-R");   // not an absolute path: it must not reach `open` as an option of its own

        var argument = runner.Calls.Single().Arguments.Last();
        System.IO.Path.IsPathRooted(argument).Should().BeTrue();
        argument.Should().NotBe("-R");
        runner.Calls.Single().Arguments.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankPath_DoesNothing(string? path)
    {
        var (manager, runner, created, _) = Make();

        manager.OpenFolder(path!);
        manager.Reveal(path!);

        runner.Calls.Should().BeEmpty();
        created.Should().BeEmpty();
    }

    [Fact]
    public void AFailingOpen_IsLoggedWithItsReason_AndDoesNotThrow()
    {
        var (manager, _, _, log) = Make(_ => new CommandResult(1, "", "The file /nope does not exist.", TimedOut: false));

        var open = () => manager.OpenFolder("/nope");
        open.Should().NotThrow();

        log.Should().ContainSingle().Which.Should().Contain("/nope").And.Contain("does not exist");
    }

    [Fact]
    public void ARunnerThatThrows_AndAFolderThatCannotBeCreated_AreSwallowedAndLogged()
    {
        var runner = new FakeCommandRunner(_ => throw new InvalidOperationException("boom"));
        var log = new List<string>();
        var manager = new MacOsFileManager(runner, _ => throw new UnauthorizedAccessException("no"), log.Add);

        var open = () => manager.OpenFolder("/x/y");
        open.Should().NotThrow();

        log.Should().HaveCount(2);
        log[0].Should().Contain("Could not create").And.Contain("no");
        log[1].Should().Contain("boom");
    }
}

/// <summary>Banners through <c>osascript</c>: the text of a profile or file name is only ever text.</summary>
public sealed class MacOsNotifierTests
{
    [Fact]
    public void ThePlainCase_IsADisplayNotificationWithATitle()
    {
        AppleScriptText.DisplayNotification("Bee Memory Bank", "This Mac is about to sleep.")
            .Should().Be("display notification \"This Mac is about to sleep.\" with title \"Bee Memory Bank\"");
    }

    [Theory]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("back\\slash", "\"back\\\\slash\"")]
    [InlineData("\\\"", "\"\\\\\\\"\"")]
    [InlineData("line1\nline2\rline3\ttab", "\"line1 line2 line3 tab\"")]
    [InlineData("", "\"\"")]
    public void Quote_EscapesBackslashAndQuote_AndFlattensControlCharacters(string text, string expected)
    {
        AppleScriptText.Quote(text).Should().Be(expected);
    }

    [Theory]
    [InlineData("\" & (do shell script \"touch /tmp/pwned\") & \"")]
    [InlineData("\\\" & (do shell script \"id\") & \\\"")]
    [InlineData("a\\")]
    [InlineData("\\\\\"")]
    public void AHostileName_StaysInsideItsStringLiteral(string hostile)
    {
        var literal = AppleScriptText.Quote(hostile);

        // Read the literal back the way AppleScript does: a backslash takes the next character literally, an unescaped quote ends the string.
        var (value, end) = ReadStringLiteral(literal);

        end.Should().Be(literal.Length, "the literal must end at its own last character - anything earlier would let the rest run as code");
        value.Should().Be(hostile, "the text comes back exactly, with nothing added or lost");
    }

    [Fact]
    public void ALongText_IsCut_AndTheLiteralStillCloses()
    {
        var literal = AppleScriptText.Quote(new string('x', 5000), 50);

        literal.Length.Should().BeLessThan(80);
        literal.Should().StartWith("\"").And.EndWith("...\"");
    }

    [Fact]
    public void ATextCutBetweenTheHalvesOfASurrogatePair_IsNotSplit()
    {
        var literal = AppleScriptText.Quote("ab" + "\U0001F600" + "cd", 3);

        // 3 characters = 'a', 'b' and the first half of the pair; the second half must come with it.
        literal.Should().Contain("\U0001F600");
    }

    [Fact]
    public void Notify_RunsOsascriptWithTheScriptAsTheValueOfDashE_NotThroughAShell()
    {
        var runner = new FakeCommandRunner();
        var notifier = new MacOsNotifier(runner, background: false);
        var name = "Prof \"A\" \\ B";

        notifier.Notify("Bee Memory Bank", $"Profile {name} was locked.");

        var call = runner.Calls.Should().ContainSingle().Subject;
        call.FileName.Should().Be("/usr/bin/osascript");
        call.Arguments.Should().HaveCount(2);
        call.Arguments[0].Should().Be("-e");
        call.Arguments[1].Should().Be("display notification \"Profile Prof \\\"A\\\" \\\\ B was locked.\" with title \"Bee Memory Bank\"");
    }

    [Fact]
    public void Notify_NeverThrows_WhateverTheToolDoes()
    {
        var notifier = new MacOsNotifier(new FakeCommandRunner(_ => throw new InvalidOperationException("no osascript")), background: false);

        var notify = () => notifier.Notify("t", "m");

        notify.Should().NotThrow();
    }

    [Fact]
    public void Notify_ByDefault_DoesNotWaitForTheTool()
    {
        using var release = new System.Threading.ManualResetEventSlim();
        var started = new System.Threading.ManualResetEventSlim();
        var runner = new FakeCommandRunner(_ =>
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return FakeCommandRunner.Ok;
        });
        var notifier = new MacOsNotifier(runner, background: true);

        var before = DateTime.UtcNow;
        notifier.Notify("t", "m");
        var returnedAfter = DateTime.UtcNow - before;

        returnedAfter.Should().BeLessThan(TimeSpan.FromSeconds(3), "a slow osascript must not hold the caller (the sleep handler has seconds)");
        started.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the banner is still run, in the background");
        release.Set();
    }

    /// <summary>Reads an AppleScript string literal from the start of the text: returns its value and the index just after the closing quote.</summary>
    private static (string Value, int End) ReadStringLiteral(string text)
    {
        text[0].Should().Be('"');
        var value = new System.Text.StringBuilder();
        var i = 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\')
            {
                // an escape: the next character is taken literally (the only escapes the quoter writes are \\ and \")
                value.Append(text[i + 1]);
                i += 2;
            }
            else if (c == '"')
            {
                return (value.ToString(), i + 1);
            }
            else
            {
                value.Append(c);
                i++;
            }
        }
        throw new InvalidOperationException("The literal is never closed.");
    }
}
