using System.Diagnostics;
using BeeMemoryBank.Cli;

namespace BeeMemoryBank.Cli.Tests;

/// <summary>
/// <c>bmb init</c> and <c>bmb join</c> can take the master password without it being an argument (week review F12): from the first
/// line of stdin or from a prompt without echo; the documented <c>--password</c> still works and warns.
/// </summary>
public class PasswordInputTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "bmb_cli_pw_" + Guid.NewGuid().ToString("N"));

    // A test deletes nothing outside its own temp folder, and that one is left to the OS.
    public void Dispose() { }

    private static (string? Password, string Error) Resolve(
        string? onCommandLine, bool fromStdin, string stdin, bool redirected, Func<ConsoleKeyInfo>? readKey = null)
    {
        var error = new StringWriter();
        var password = PasswordInput.Resolve(onCommandLine, fromStdin, "Master password: ", new StringReader(stdin), redirected, error, readKey);
        return (password, error.ToString());
    }

    [Fact]
    public void TheCommandLinePassword_StillWorks_AndWarnsOnStderr()
    {
        var (password, error) = Resolve("from-argv", fromStdin: false, stdin: "", redirected: false);

        password.Should().Be("from-argv");
        error.Should().Contain("--password-stdin").And.NotContain("from-argv", "the warning never repeats the secret");
    }

    [Theory]
    [InlineData("secret one\n", "secret one")]
    [InlineData("secret one\r\nsecond line\r\n", "secret one")]
    [InlineData("  padded  \n", "  padded  ")]
    public void WithPasswordStdin_ItIsTheFirstLine(string stdin, string expected)
    {
        var (password, error) = Resolve(null, fromStdin: true, stdin, redirected: true);

        password.Should().Be(expected);
        error.Should().BeEmpty("nothing to warn about and no prompt text when stdin was asked for");
    }

    [Fact]
    public void BothWays_AreRefused_NotGuessed()
    {
        var (password, error) = Resolve("a", fromStdin: true, stdin: "b\n", redirected: true);

        password.Should().BeNull();
        error.Should().Contain("once");
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    public void AnEmptyStdin_IsNoPassword(string stdin)
    {
        var (password, error) = Resolve(null, fromStdin: true, stdin, redirected: true);

        password.Should().BeNull();
        error.Should().Contain("No password");
    }

    [Fact]
    public void WithNothingGiven_AndAPipedStdin_ItAsksAndReadsALine()
    {
        var (password, error) = Resolve(null, fromStdin: false, "from the pipe\n", redirected: true);

        password.Should().Be("from the pipe");
        error.Should().StartWith("Master password: ");
    }

    [Fact]
    public void WithNothingGiven_AtATerminal_ItIsTypedWithoutEchoAndBackspaceWorks()
    {
        var keys = new Queue<ConsoleKeyInfo>(
        [
            Key('a', ConsoleKey.A), Key('x', ConsoleKey.X), Key('\b', ConsoleKey.Backspace), Key('b', ConsoleKey.B),
            Key('\r', ConsoleKey.Enter)
        ]);

        var (password, error) = Resolve(null, fromStdin: false, stdin: "", redirected: false, readKey: keys.Dequeue);

        password.Should().Be("ab");
        error.Should().Be("Master password: " + Environment.NewLine, "the typed characters are never written");
    }

    private static ConsoleKeyInfo Key(char c, ConsoleKey key) => new(c, key, false, false, false);

    // ------------------------------------------------------------------ through the real program

    private static (int Exit, string Output, string Error) RunBmb(string stdin, params string[] args)
    {
        var bmb = Path.Combine(AppContext.BaseDirectory, "bmb.dll");
        File.Exists(bmb).Should().BeTrue("the Cli project's output sits next to the tests");
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        psi.ArgumentList.Add(bmb);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
            throw new TimeoutException("bmb did not end in two minutes");
        }
        return (process.ExitCode, output.Result, error.Result);
    }

    [Fact]
    public void BmbInit_TakesThePasswordFromStdin_AndTheNodeOpensWithIt()
    {
        var init = RunBmb("Stdin-Password-1\n", "init", "--data", _tempDir, "--name", "stdinnode", "--password-stdin");

        init.Exit.Should().Be(0, init.Error + init.Output);
        init.Output.Should().Contain("successfully initialized");
        init.Error.Should().NotContain("Warning", "no password was on the command line");

        var unlock = RunBmb("", "unlock", "--data", _tempDir, "--password", "Stdin-Password-1");
        unlock.Exit.Should().Be(0, "the password the node was made with is exactly the first line of stdin: " + unlock.Error + unlock.Output);
    }

    [Fact]
    public void BmbInit_WithThePasswordOnTheCommandLine_StillWorks_AndSaysHowToDoBetter()
    {
        var dir = _tempDir + "_argv";

        var init = RunBmb("", "init", "--data", dir, "--name", "argvnode", "--password", "Argv-Password-1");

        init.Exit.Should().Be(0, init.Error + init.Output);
        init.Error.Should().Contain("--password-stdin").And.NotContain("Argv-Password-1");
        RunBmb("", "unlock", "--data", dir, "--password", "Argv-Password-1").Exit.Should().Be(0);
    }

    [Fact]
    public void BmbInit_WithNoPassword_AtAnEmptyPipe_RefusesAndCreatesNothing()
    {
        var dir = _tempDir + "_none";

        var init = RunBmb("", "init", "--data", dir, "--name", "nopw");

        init.Exit.Should().Be(1);
        init.Error.Should().Contain("No password given");
        File.Exists(Path.Combine(dir, "beememorybank.db")).Should().BeFalse();
    }

    [Fact]
    public void BmbJoin_WithoutAPassword_AsksForIt_BeforeItTouchesTheNetwork()
    {
        var join = RunBmb("", "join", "--data", _tempDir + "_join", "--remote", "https://127.0.0.1:1", "--name", "j");

        join.Exit.Should().Be(1);
        join.Error.Should().Contain("No password given");
    }
}
