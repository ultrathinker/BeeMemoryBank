using BeeMemoryBank.BlindDesktop.Windows;
using Microsoft.Win32;

namespace BeeMemoryBank.BlindDesktop.Tests.Windows;

/// <summary>
/// The Run-key adapter, tested with a value name that belongs to the test (never the real one) and, for the main cases, in a scratch key of
/// the tests. One test uses the real Run key with a test value name, and checks that no other value of that key was touched.
/// </summary>
public sealed class RegistryAutostartTests
{
    private const string FullAppValue = "BeeMemoryBank";
    // One scratch key for all tests (the values in it are unique and removed again); the key itself stays and is listed as junk.
    private static string TestKey() => @"Software\BeeMemoryBankBlind.Tests\Run";
    private static string TestValue() => "BeeMemoryBankBlind.Test." + Guid.NewGuid().ToString("N")[..8];
    private const string FakeExe = @"C:\Program Files\BeeMemoryBankBlind.Test\BeeMemoryBank.BlindDesktop.exe";

    private static object? Raw(string keyPath, string value)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(value);
    }

    [Fact]
    public void TheRealValueName_IsTheBlindAppsOwn_AndNotTheFullAppsOne()
    {
        RegistryAutostart.ValueName.Should().Be("BeeMemoryBankBlind");
        RegistryAutostart.ValueName.Should().NotBe(FullAppValue);
        RegistryAutostart.RunKeyPath.Should().Be(@"Software\Microsoft\Windows\CurrentVersion\Run");
    }

    [Fact]
    public void Enabling_WritesTheQuotedPathWithMinimized_AndDisablingRemovesIt()
    {
        var keyPath = TestKey();
        var value = TestValue();
        var autostart = new RegistryAutostart(value, keyPath, () => FakeExe);

        autostart.IsEnabled.Should().BeFalse();

        autostart.SetEnabled(true);
        Raw(keyPath, value).Should().Be("\"" + FakeExe + "\" --minimized");
        autostart.IsEnabled.Should().BeTrue();
        using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            key!.GetValueKind(value).Should().Be(RegistryValueKind.String);

        autostart.SetEnabled(true); // twice is fine
        autostart.IsEnabled.Should().BeTrue();

        autostart.SetEnabled(false);
        Raw(keyPath, value).Should().BeNull();
        autostart.IsEnabled.Should().BeFalse();

        autostart.SetEnabled(false); // removing what is not there is fine
    }

    [Fact]
    public void AValuePointingAtAnotherExe_IsNotEnabled_UntilTheUserEnablesItAgain()
    {
        var keyPath = TestKey();
        var value = TestValue();
        new RegistryAutostart(value, keyPath, () => @"C:\old\place\BeeMemoryBank.BlindDesktop.exe").SetEnabled(true);

        var moved = new RegistryAutostart(value, keyPath, () => FakeExe);
        moved.IsEnabled.Should().BeFalse("the entry would start an exe that is no longer this one");
        moved.SetEnabled(true);
        moved.IsEnabled.Should().BeTrue();
        Raw(keyPath, value).Should().Be(RegistryAutostart.CommandFor(FakeExe));
    }

    [Fact]
    public void TheCommand_QuotesAPathWithSpaces()
    {
        RegistryAutostart.CommandFor(@"C:\My Apps\Blind App\x.exe").Should().Be("\"C:\\My Apps\\Blind App\\x.exe\" --minimized");
    }

    [Fact]
    public void InTheRealRunKey_OnlyTheTestValueIsWritten_AndRemoved_AndNothingElseChanges()
    {
        var value = TestValue();
        var before = Snapshot();
        var autostart = new RegistryAutostart(value, RegistryAutostart.RunKeyPath, () => FakeExe);
        try
        {
            autostart.SetEnabled(true);
            Raw(RegistryAutostart.RunKeyPath, value).Should().Be("\"" + FakeExe + "\" --minimized");
            Snapshot().Where(p => p.Key != value).Should().BeEquivalentTo(before, "no other value of the Run key is touched, the full app's included");
        }
        finally
        {
            autostart.SetEnabled(false);
        }

        Raw(RegistryAutostart.RunKeyPath, value).Should().BeNull();
        Snapshot().Should().BeEquivalentTo(before, "the key is as it was");

        Dictionary<string, string> Snapshot()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryAutostart.RunKeyPath);
            return key is null
                ? []
                : key.GetValueNames().ToDictionary(n => n, n => key.GetValue(n)?.ToString() ?? "");
        }
    }

    [Fact]
    public void TheDefaultExecutable_IsNeverDotnet()
    {
        // The default resolver is private; what matters is its result: the value written when no path is given never names dotnet.exe.
        var keyPath = TestKey();
        var value = TestValue();
        var autostart = new RegistryAutostart(value, keyPath);
        try
        {
            autostart.SetEnabled(true);
            Raw(keyPath, value)!.ToString().Should().NotContainEquivalentOf("dotnet.exe");
        }
        catch (InvalidOperationException)
        {
            // Started by a host that has no apphost of its own: refusing is the right answer.
        }
        finally
        {
            autostart.SetEnabled(false);
        }
    }
}
