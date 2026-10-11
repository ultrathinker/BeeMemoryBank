using BeeMemoryBank.Api.Services.BlindConsole;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The console state file has three cases, not two (BMB-85). An absent file is a first-time console, where the first
/// password may be set without a current one. A file that is unreadable or damaged is not "no password": nothing may be
/// set or changed on top of it, and nothing may be written over it.
/// </summary>
public class BlindConsoleStateFileTests : IDisposable
{
    private const string Password = "console-pw-123";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bmb_blind_statefile_" + Guid.NewGuid().ToString("N"));
    private string StateFile => Path.Combine(_dir, "blind", "console.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private BlindConsoleAuthService NewService() => new(_dir, NullLogger<BlindConsoleAuthService>.Instance);

    [Fact]
    public void AbsentFile_IsAFirstTimeConsole_AndItsFirstPasswordNeedsNoCurrentOne()
    {
        var auth = NewService();

        auth.HasPassword().Should().BeFalse();
        auth.TrySetPassword(null, Password).Should().BeTrue();
        auth.Verify(Password, null).Should().Be((true, false));
    }

    [Fact]
    public void SignInBeforeAnyPassword_LeavesAFirstTimeConsole_ThatCanStillSetItsPassword()
    {
        // The state without a hash that the console does write: a sign-in attempt before any password is set. It is logged,
        // so the file is a first-time console, not a damaged one.
        NewService().Verify("guess-before-setup", null).Should().Be((false, false));
        File.Exists(StateFile).Should().BeTrue();

        NewService().HasPassword().Should().BeFalse();
        NewService().TrySetPassword(null, Password).Should().BeTrue();
        NewService().Verify(Password, null).Should().Be((true, false));
    }

    [Theory]
    [InlineData("")]                        // emptied, e.g. a crash right after the file was created
    [InlineData("{ \"hash_b64\": \"AAAA")]  // cut off by an interrupted write
    [InlineData("null")]                    // parses, but holds no state
    [InlineData("{\"hash_b64\":\"AAAA\"}")] // a hash without its salt
    [InlineData("{\"hash_b64\":\"\",\"salt_b64\":\"\"}")]         // an empty hash and salt: nothing to check a password against
    [InlineData("{\"hash_b64\":\"AAAA\",\"salt_b64\":\"AAAA\"}")] // a hash and a salt of 3 bytes, not SHA-256 and 32 bytes
    [InlineData("{\"logins\":[null]}")]                           // a hole in the sign-in log
    [InlineData("{}")]                                            // no state at all: the console never writes that
    [InlineData("{\"generation\":1}")]                            // a password generation, but no hash and no log
    [InlineData("{\"logins\":[],\"generation\":1}")]              // a password generation without a hash
    public void DamagedFile_IsNotNoPassword_SoNothingIsSetOrWrittenOverIt(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, content);
        var auth = NewService();

        auth.HasPassword().Should().BeTrue("a damaged file may hold a password, so it counts as set");
        auth.TrySetPassword(null, Password).Should().BeFalse("only an absent file allows the first set");
        auth.Verify(Password, null).Should().Be((false, false));
        File.ReadAllText(StateFile).Should().Be(content, "nothing is written over a file that could not be read");
    }

    [Fact]
    public void UnreadableFile_RefusesToSetOrChangeThePassword_AndWritesNothing()
    {
        var auth = NewService();
        auth.TrySetPassword(null, Password).Should().BeTrue();
        var before = File.ReadAllText(StateFile);

        // Another process holds the file open exclusively, so the console cannot read it.
        using (new FileStream(StateFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            auth.HasPassword().Should().BeTrue("an unreadable file may hold a password, so it counts as set");
            auth.TrySetPassword(Password, "another-console-pw").Should().BeFalse();
            auth.Verify(Password, null).Should().Be((false, false));
        }

        File.ReadAllText(StateFile).Should().Be(before, "the file is what it was");
        NewService().Verify(Password, null).Should().Be((true, false), "the password still works once the file is readable");
    }
}
