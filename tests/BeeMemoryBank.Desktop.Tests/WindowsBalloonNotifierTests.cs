using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using BeeMemoryBank.Desktop.Services;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Desktop.Tests;

/// <summary>
/// The Windows balloon notifier lifted out of the power-events service (BMB-77): what it hands the shell (title and message cut to the
/// balloon's 64/256 fields), that a second notice modifies the icon instead of adding a second one, that the temporary icon goes again,
/// and that it never throws. The shell calls are recorded here: no test shows a real balloon (the call itself is checked by hand).
/// </summary>
public sealed class WindowsBalloonNotifierTests
{
    private static readonly IntPtr Window = new(0x1234);

    private sealed class RecordingShell : WindowsBalloonNotifier.IBalloonShell
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public Exception? Fails { get; set; }
        public bool Shows { get; set; } = true;

        public bool Show(IntPtr window, int iconId, string title, string message, bool iconShown)
        {
            if (Fails != null) throw Fails;
            Calls.Enqueue($"{(iconShown ? "modify" : "add")} {window} {iconId} [{title}] [{message}]");
            return Shows;
        }

        public void Remove(IntPtr window, int iconId) => Calls.Enqueue($"remove {window} {iconId}");
    }

    [Fact]
    public void Fit_CutsTitleAndMessage_ToTheBalloonsFields_WithAnEllipsis()
    {
        var (title, message) = WindowsBalloonNotifier.Fit(new string('t', 100), new string('m', 400));

        title.Should().HaveLength(WindowsBalloonNotifier.TitleMax).And.EndWith("...");
        message.Should().HaveLength(WindowsBalloonNotifier.MessageMax).And.EndWith("...");
        WindowsBalloonNotifier.Fit("Short", "Fits").Should().Be(("Short", "Fits"));
    }

    [Fact]
    public void Fit_NeverCutsASurrogatePairInHalf_AndTurnsControlCharactersIntoSpaces()
    {
        var message = new string('m', WindowsBalloonNotifier.MessageMax - 4) + "\U0001F41D\U0001F41D\U0001F41D";

        var (title, fitted) = WindowsBalloonNotifier.Fit("Line\r\nbreak", message);

        title.Should().Be("Line  break");
        fitted.Length.Should().BeLessThanOrEqualTo(WindowsBalloonNotifier.MessageMax);
        char.IsHighSurrogate(fitted[^4]).Should().BeFalse("a lone half of a pair would show as a broken glyph");
    }

    [Fact]
    public async Task ANotice_AddsTheIcon_ASecondModifiesIt_AndTheIconGoesAfterTheLastOne()
    {
        var shell = new RecordingShell();
        using var notifier = new WindowsBalloonNotifier(() => Window, 7, shell, TimeSpan.FromMilliseconds(200));

        notifier.Notify("First", "one");
        notifier.Notify("Second", "two");
        // Wait for the removal itself, not a fixed time: on a busy machine a 200 ms timer can fire well after a second.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (shell.Calls.Count < 3 && DateTime.UtcNow < deadline) await Task.Delay(25);

        shell.Calls.Should().Equal(
            $"add {Window} 7 [First] [one]",
            $"modify {Window} 7 [Second] [two]",
            $"remove {Window} 7");
    }

    [Fact]
    public void WithNoWindowYet_NothingIsShown()
    {
        var shell = new RecordingShell();
        using var notifier = new WindowsBalloonNotifier(() => IntPtr.Zero, 7, shell, TimeSpan.FromSeconds(10));

        notifier.Notify("Title", "Message");

        shell.Calls.Should().BeEmpty();
    }

    [Fact]
    public void ItNeverThrows_NotWhenTheShellFails_NorWhenTheWindowCannotBeRead()
    {
        var failing = new RecordingShell { Fails = new InvalidOperationException("shell gone") };
        using var a = new WindowsBalloonNotifier(() => Window, 7, failing, TimeSpan.FromSeconds(10));
        using var b = new WindowsBalloonNotifier(() => throw new InvalidOperationException("no window"), 7, new RecordingShell(), TimeSpan.FromSeconds(10));
        using var c = new WindowsBalloonNotifier(() => Window, 7, new RecordingShell { Shows = false }, TimeSpan.FromSeconds(10));

        FluentActions.Invoking(() => a.Notify("t", "m")).Should().NotThrow();
        FluentActions.Invoking(() => b.Notify("t", "m")).Should().NotThrow();
        FluentActions.Invoking(() => c.Notify("t", "m")).Should().NotThrow();
    }

    [Fact]
    public void Dispose_RemovesAnIconThatIsStillThere_AndAfterThatNothingIsShown()
    {
        var shell = new RecordingShell();
        var notifier = new WindowsBalloonNotifier(() => Window, 7, shell, TimeSpan.FromMinutes(5));
        notifier.Notify("t", "m");

        notifier.Dispose();
        notifier.Notify("after", "dispose");

        shell.Calls.Should().Equal($"add {Window} 7 [t] [m]", $"remove {Window} 7");
    }

    [Fact]
    public void ThePowerEventsService_ShowsItsSleepNotice_ThroughTheNotifier_NotAShellCallOfItsOwn()
    {
        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "desktop", "BeeMemoryBank.Desktop", "Services", "PowerEventsService.cs"));

        source.Should().Contain("new WindowsBalloonNotifier(");
        source.Should().NotContain("Shell_NotifyIcon", "the balloon code lives in one place");
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "BeeMemoryBank.slnx"))) dir = System.IO.Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }
}
