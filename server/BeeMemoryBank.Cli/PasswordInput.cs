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


/// <summary>
/// Where a command that creates or replaces a password gets its mandatory hint. A hint is not a secret, so
/// <c>--hint</c> is the ordinary way (a script supplies it); a person at a terminal is asked for it when the
/// option is left out. A redirected stdin is never read for it: the first line there is the password.
/// </summary>
public static class PasswordHintInput
{
    public static string? Resolve(
        string? hint, string prompt, TextReader? stdin = null, bool? stdinIsRedirected = null, TextWriter? error = null)
    {
        error ??= Console.Error;
        var given = hint?.Trim();
        if (!string.IsNullOrEmpty(given)) return given;

        if (stdinIsRedirected ?? Console.IsInputRedirected)
        {
            error.WriteLine("Error: --hint is required when stdin is not a terminal (the hint is shown on the sign-in page if the password is forgotten).");
            return null;
        }

        error.Write(prompt);
        var typed = (stdin ?? Console.In).ReadLine()?.Trim();
        if (string.IsNullOrEmpty(typed)) error.WriteLine("No password hint given.");
        return string.IsNullOrEmpty(typed) ? null : typed;
    }
}
