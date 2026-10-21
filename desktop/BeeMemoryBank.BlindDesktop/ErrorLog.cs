namespace BeeMemoryBank.BlindDesktop;

/// <summary>
/// Where a problem of the app itself goes when the blind app's own log is not the place (a window command that failed, a start-up failure):
/// one line in a small text file in the temp folder, with the exception's TYPE and the frames of its stack - never what the exception said
/// (<see cref="ViewModels.DiagnosticText"/>): the message of an I/O error names a file, one of an HTTP error an address with a token, and a log is what a support
/// request attaches. A windowed app has no console. Writing never throws.
/// </summary>
public static class ErrorLog
{
    public static string PathOfLog { get; private set; } = Path.Combine(Path.GetTempPath(), "BeeMemoryBankBlind-error.log");

    /// <summary>Moves the log to the place the platform prefers (null keeps the default). The folder is made when the first line is written.</summary>
    public static void UsePath(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path)) PathOfLog = path;
    }

    /// <summary>One line without an exception: a problem the device loop describes in its own words (never a key or a code).</summary>
    public static void Note(string what)
    {
        try
        {
            var folder = Path.GetDirectoryName(PathOfLog);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            File.AppendAllText(PathOfLog, $"{DateTimeOffset.Now:O} {what.ReplaceLineEndings(" ").Trim()}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nothing else can be done.
        }
    }

    public static void Write(string what, Exception ex)
    {
        try
        {
            var folder = Path.GetDirectoryName(PathOfLog);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            File.AppendAllText(PathOfLog, $"{DateTimeOffset.Now:O} {what} {ViewModels.DiagnosticText.Describe(ex)}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nothing else can be done.
        }
    }
}
