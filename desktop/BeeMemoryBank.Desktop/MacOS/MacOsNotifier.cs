using System;
using System.Text;
using System.Threading.Tasks;
using BeeMemoryBank.Desktop.Services;

namespace BeeMemoryBank.Desktop.MacOS;

/// <summary>Builds the AppleScript the notifier runs. Pure text work, tested on any OS.</summary>
public static class AppleScriptText
{
    /// <summary>
    /// An AppleScript string literal for any text: the text cannot end the literal or add code to the script. Backslash and double quote
    /// are escaped; line breaks and other control characters become a space (they have no place in a notification line); the length is
    /// capped. A profile or file name in a message is therefore only ever text.
    /// </summary>
    public static string Quote(string? text, int maxLength = 200)
    {
        var builder = new StringBuilder("\"");
        var count = 0;
        foreach (var c in text ?? "")
        {
            if (count >= maxLength && !char.IsLowSurrogate(c)) { builder.Append("..."); break; }
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\u2028' or '\u2029': builder.Append(' '); break;
                default: builder.Append(char.IsControl(c) ? ' ' : c); break;
            }
            count++;
        }
        return builder.Append('"').ToString();
    }

    public static string DisplayNotification(string title, string body) =>
        $"display notification {Quote(body)} with title {Quote(title, 80)}";
}

/// <summary>
/// A banner through <c>osascript</c> (<c>display notification</c>). Presentation only: a refused permission, a missing tool or a failing
/// run is swallowed (the shell's own status shows the same facts) and nothing depends on the banner being seen. It runs in the
/// background, so a slow <c>osascript</c> never holds the caller - least of all the sleep handler, which has seconds, not minutes.
/// A later stage may move to UserNotifications for the signed app; the seam stays.
/// </summary>
public sealed class MacOsNotifier : IUserNotifier
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ICommandRunner _runner;
    private readonly bool _background;

    public MacOsNotifier() : this(new ProcessCommandRunner(), background: true) { }

    internal MacOsNotifier(ICommandRunner runner, bool background)
    {
        _runner = runner;
        _background = background;
    }

    public void Notify(string title, string message)
    {
        try
        {
            var script = AppleScriptText.DisplayNotification(title, message);
            if (_background) _ = Task.Run(() => Run(script));
            else Run(script);
        }
        catch (Exception)
        {
            // presentation only
        }
    }

    private void Run(string script)
    {
        try { _runner.Run(MacTools.Osascript, ["-e", script], Timeout); }
        catch (Exception) { /* a banner that did not show is not an error */ }
    }
}
