using BeeMemoryBank.BlindDesktop.ViewModels;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>
/// The parts of the shared host that the macOS head added a switch to, proved without any operating system: the command line, the
/// words of the window ("tray" or "menu bar"), and where the error log goes.
/// </summary>
[Collection("ErrorLog")]
public sealed class HostOptionsTests
{
    [Fact]
    public void TheCommandLine_IsReadAsBefore_AndTheNewSwitchesAreRecognised()
    {
        StartupOptions.Parse([]).Should().Be(new StartupOptions(false, null));
        StartupOptions.Parse(["--minimized"]).Minimized.Should().BeTrue();
        StartupOptions.Parse(["--MINIMIZED", "--data-dir", "/tmp/x"]).Should().Be(new StartupOptions(true, "/tmp/x"));
        StartupOptions.Parse(["--data-dir=/tmp/y"]).DataDirectory.Should().Be("/tmp/y");
        StartupOptions.Parse(["--data-dir="]).DataDirectory.Should().BeNull("an empty folder is no folder");
        StartupOptions.Parse(["--data-dir"]).DataDirectory.Should().BeNull("a flag without its value is ignored");
    }

    [Fact]
    public void SelfCheckOptions_AreParsed_AndTheWaitIsClamped()
    {
        var check = StartupOptions.Parse(["--self-check", "--data-dir", "/tmp/c"]);
        check.SelfCheck.Should().BeTrue();
        check.SelfCheckWaitSeconds.Should().Be(0);
        check.SelfCheckSecondStart.Should().BeFalse();

        var wait = StartupOptions.Parse(["--self-check-wait", "20", "--data-dir", "/tmp/c"]);
        wait.SelfCheck.Should().BeTrue("waiting is a form of checking");
        wait.SelfCheckWaitSeconds.Should().Be(20);

        StartupOptions.Parse(["--self-check-wait", "99999"]).SelfCheckWaitSeconds.Should().Be(300);
        StartupOptions.Parse(["--self-check-wait", "-5"]).SelfCheckWaitSeconds.Should().Be(0);
        StartupOptions.Parse(["--self-check-wait", "soon"]).SelfCheck.Should().BeFalse("not a number: not a switch");

        var second = StartupOptions.Parse(["--self-check-second-start", "--data-dir", "/tmp/c"]);
        second.SelfCheck.Should().BeTrue();
        second.SelfCheckSecondStart.Should().BeTrue();
    }

    [Fact]
    public void ACheck_WithoutAScratchFolder_IsRefused_ButNormalStartsAreNot()
    {
        StartupOptions.Parse(["--self-check"]).SelfCheckWithoutFolder.Should().BeTrue();
        StartupOptions.Parse(["--self-check-second-start"]).SelfCheckWithoutFolder.Should().BeTrue();
        StartupOptions.Parse(["--self-check-wait", "5"]).SelfCheckWithoutFolder.Should().BeTrue();
        StartupOptions.Parse(["--self-check", "--data-dir", "/tmp/c"]).SelfCheckWithoutFolder.Should().BeFalse();
        StartupOptions.Parse([]).SelfCheckWithoutFolder.Should().BeFalse("a normal start has no check");
        StartupOptions.Parse(["--minimized"]).SelfCheckWithoutFolder.Should().BeFalse();
    }

    [Fact]
    public void TheWindowSpeaksOfTheTray_ByDefault_AndOfTheMenuBar_OnAMac()
    {
        var windows = new MainViewModel(new FakeApp(), new FakeWorkRequests(), new FakeAutostart(), _ => []);
        var mac = new MainViewModel(new FakeApp(), new FakeWorkRequests(), new FakeAutostart(), _ => [], statusArea: "menu bar");

        windows.StatusArea.Should().Be("tray");
        windows.AutostartText.Should().Be("Start in the tray when I sign in", "the Windows words did not change");
        windows.CloseHintText.Should().Be("Closing this window keeps the app running in the tray.");
        mac.StatusArea.Should().Be("menu bar");
        mac.AutostartText.Should().Be("Start in the menu bar when I sign in");
        mac.CloseHintText.Should().Be("Closing this window keeps the app running in the menu bar.");
    }

    [Fact]
    public void TheErrorLog_GoesWhereThePlatformSays_MakesItsFolder_AndKeepsTheDefaultWithoutAnAnswer()
    {
        var original = ErrorLog.PathOfLog;
        try
        {
            ErrorLog.UsePath(null);
            ErrorLog.UsePath("  ");
            ErrorLog.PathOfLog.Should().Be(original, "no answer keeps the default");

            var path = Path.Combine(TestFolders.New("errorlog"), "Logs", "BeeMemoryBankBlind", "error.log");
            ErrorLog.UsePath(path);
            ErrorLog.Write("Showing the window failed", new InvalidOperationException("line one\nline two"));

            ErrorLog.PathOfLog.Should().Be(path);
            var lines = File.ReadAllLines(path);
            lines.Should().ContainSingle();
            lines[0].Should().Contain("Showing the window failed").And.Contain("InvalidOperationException: line one line two");
        }
        finally
        {
            ErrorLog.UsePath(original);
        }
    }
}
