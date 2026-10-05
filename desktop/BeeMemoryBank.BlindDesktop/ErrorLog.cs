namespace BeeMemoryBank.BlindDesktop;

/// <summary>
/// Where a problem of the app itself goes when the blind app's own log is not the place (a window command that failed, a start-up failure):
/// one line in a small text file in the temp folder, with the exception's type and a short message. The app puts no key and no code into an
/// exception message; the message of an I/O error may name a file, and the file never leaves this computer. A windowed app has no console.
/// Writing never throws.
/// </summary>
public static class ErrorLog
{
    public static string PathOfLog { get; private set; } = Path.Combine(Path.GetTempPath(), "BeeMemoryBankBlind-error.log");

    /// <summary>Moves the log to the place the platform prefers (null keeps the default). The folder is made when the first line is written.</summary>
    public static void UsePath(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path)) PathOfLog = path;
    }

    public static void Write(string what, Exception ex)
    {
        try
        {
            var message = (ex.Message ?? "").ReplaceLineEndings(" ").Trim();
            if (message.Length > 200) message = message[..200] + "...";
            var folder = Path.GetDirectoryName(PathOfLog);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            File.AppendAllText(PathOfLog, $"{DateTimeOffset.Now:O} {what} {ex.GetType().Name}: {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nothing else can be done.
        }
    }
}
