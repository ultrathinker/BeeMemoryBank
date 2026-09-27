namespace BeeMemoryBank.Cli.Commands;

/// <summary>
/// Where `bmb blind` takes its secrets from — never the command line, which every account on the
/// host (and `docker inspect`, process monitors, shell history) can read. In order:
/// <c>--secrets-file PATH</c> (key=value lines; on Linux the file must not be readable by group or
/// others), else standard input when it is redirected (<c>docker exec -i … &lt; secrets.env</c>),
/// else a prompt on the terminal that does not echo.
/// </summary>
public static class BlindSecrets
{
    public const string ConsolePassword = "console_password";
    public const string ResticPassword = "restic_password";
    public const string S3SecretKey = "s3_secret_key";

    /// <summary>Reads key=value lines (blank lines and # comments skipped); unknown keys are refused.</summary>
    public static Dictionary<string, string> Parse(TextReader reader, IReadOnlyCollection<string> allowed)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            var t = line.Trim();
            if (t.Length == 0 || t[0] == '#') continue;
            var eq = t.IndexOf('=');
            if (eq <= 0)
                throw new FormatException("secrets: every line must be key=value");
            var key = t[..eq].Trim();
            if (!allowed.Contains(key))
                throw new FormatException($"secrets: unknown key '{key}' (expected: {string.Join(", ", allowed)})");
            result[key] = t[(eq + 1)..];
        }
        return result;
    }

    /// <summary>
    /// The secrets file, refused when others can read it: a world-readable file with the restic
    /// password is the argv leak moved to the disk.
    /// </summary>
    public static Dictionary<string, string> FromFile(string path, IReadOnlyCollection<string> allowed)
    {
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            const UnixFileMode others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((mode & others) != 0)
                throw new UnauthorizedAccessException(
                    $"secrets file {path} is readable by other accounts — chmod 600 it first");
        }
        using var reader = new StreamReader(path);
        return Parse(reader, allowed);
    }

    /// <summary>
    /// The secrets for one command: from the file when given, else stdin (redirected) or a prompt
    /// for each of <paramref name="prompts"/> (key → question) on a terminal.
    /// </summary>
    public static Dictionary<string, string> Resolve(string? file, IReadOnlyDictionary<string, string> prompts)
    {
        var allowed = prompts.Keys.ToArray();
        if (file is not null) return FromFile(file, allowed);
        if (Console.IsInputRedirected) return Parse(Console.In, allowed);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, question) in prompts)
        {
            Console.Write(question + ": ");
            var value = ReadHidden();
            if (value.Length > 0) result[key] = value;
        }
        return result;
    }

    private static string ReadHidden()
    {
        var chars = new List<char>();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); }
            else if (!char.IsControl(k.KeyChar)) chars.Add(k.KeyChar);
        }
        Console.WriteLine();
        return new string([.. chars]);
    }
}
