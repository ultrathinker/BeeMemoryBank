namespace BeeMemoryBank.Cli;

/// <summary>
/// Where a command that needs the master password gets it from. A password given as <c>--password</c> sits in the process list and
/// the shell history for anyone who can read them (week review F12), so <c>bmb init</c> and <c>bmb join</c> also take it from the
/// first line of stdin (<c>--password-stdin</c>, as <c>bmb rekey</c> does) or ask for it on the terminal without echo. The
/// documented <c>--password</c> keeps working; it prints a warning to stderr.
/// </summary>
public static class PasswordInput
{
    /// <summary>The warning printed when the password came on the command line.</summary>
    public const string CommandLineWarning =
        "Warning: --password puts the master password in the process list and the shell history. " +
        "Use --password-stdin (the password on the first line of stdin) or leave it out to be asked.";

    /// <summary>
    /// The password, or null (after saying why on <paramref name="error"/>) when there is none or the choice is ambiguous.
    /// </summary>
    /// <param name="onCommandLine"><c>--password</c>, when given.</param>
    /// <param name="fromStdin"><c>--password-stdin</c>.</param>
    /// <param name="prompt">What the terminal prompt says.</param>
    /// <param name="stdin">Standard input.</param>
    /// <param name="stdinIsRedirected">Stdin is a pipe or a file, not a terminal: it is read as a line, never asked for keys.</param>
    /// <param name="error">Standard error: the prompt, the warning and the reason for a refusal go there, never to stdout.</param>
    /// <param name="readKey">Reads one key without echo; the terminal prompt only.</param>
    public static string? Resolve(
        string? onCommandLine, bool fromStdin, string prompt, TextReader stdin, bool stdinIsRedirected, TextWriter error,
        Func<ConsoleKeyInfo>? readKey = null)
    {
        if (!string.IsNullOrEmpty(onCommandLine) && fromStdin)
        {
            error.WriteLine("Give the password once: either --password or --password-stdin.");
            return null;
        }

        if (!string.IsNullOrEmpty(onCommandLine))
        {
            error.WriteLine(CommandLineWarning);
            return onCommandLine;
        }

        string? password;
        if (fromStdin || stdinIsRedirected)
        {
            if (!fromStdin) error.Write(prompt);
            password = stdin.ReadLine()?.TrimEnd('\r');
        }
        else
        {
            error.Write(prompt);
            password = ReadWithoutEcho(readKey ?? (() => Console.ReadKey(intercept: true)));
            error.WriteLine();
        }

        if (string.IsNullOrEmpty(password))
        {
            error.WriteLine("No password given.");
            return null;
        }
        return password;
    }

    private static string ReadWithoutEcho(Func<ConsoleKeyInfo> readKey)
    {
        var chars = new List<char>();
        while (true)
        {
            var key = readKey();
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); continue; }
            if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar);
        }
        return new string(chars.ToArray());
    }
}
