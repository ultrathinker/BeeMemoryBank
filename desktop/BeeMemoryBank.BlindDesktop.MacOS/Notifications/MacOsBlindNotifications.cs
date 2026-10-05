using System.Globalization;
using System.Text;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.MacOS;

/// <summary>Builds the AppleScript the notifier runs. Pure text work.</summary>
public static class AppleScriptText
{
    /// <summary>
    /// An AppleScript string literal for any text: the text cannot end the literal or add code to the script. Backslash and double quote
    /// are escaped; line breaks and other control characters become a space (they have no place in a notification line); the length is
    /// capped.
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
/// The notifier behind <see cref="IBlindNotifications"/>: a banner through <c>osascript</c> (<c>display notification</c>) with the job's
/// title and its progress. It is presentation only: a refused permission, a missing tool or a failing run is swallowed (the work goes on
/// and the app's own status shows the same facts). Banners are rate limited so a job that reports every chunk does not flood the
/// notification centre. A later stage may move to UserNotifications for the signed app; the seam stays.
/// </summary>
public sealed class MacOsBlindNotifications : IBlindNotifications
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ICommandRunner _runner;
    private readonly TimeProvider _time;
    private readonly TimeSpan _minInterval;
    private readonly bool _background;
    private readonly object _gate = new();
    private string? _lastTitle;
    private DateTimeOffset _lastShownAt;

    public MacOsBlindNotifications() : this(new ProcessCommandRunner(), TimeProvider.System, TimeSpan.FromSeconds(30), background: true) { }

    internal MacOsBlindNotifications(ICommandRunner runner, TimeProvider time, TimeSpan minInterval, bool background)
    {
        _runner = runner;
        _time = time;
        _minInterval = minInterval;
        _background = background;
    }

    /// <summary>The banner text for a title and a progress between 0 and 1 (0 or less: no figure; 1: done).</summary>
    public static (string Title, string Body) Compose(string title, double progress)
    {
        var heading = string.IsNullOrWhiteSpace(title) ? "Bee Memory Bank" : title.Trim();
        var body = progress switch
        {
            >= 1 => "Done.",
            > 0 => (Math.Clamp(progress, 0, 1) * 100).ToString("0", CultureInfo.InvariantCulture) + " %",
            _ => "Working...",
        };
        return (heading, body);
    }

    public void Show(string title, double progress)
    {
        try
        {
            var done = progress >= 1;
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                var same = string.Equals(_lastTitle, title, StringComparison.Ordinal);
                if (same && !done && now - _lastShownAt < _minInterval) return;
                _lastTitle = title;
                _lastShownAt = now;
            }
            var (heading, body) = Compose(title, progress);
            var script = AppleScriptText.DisplayNotification(heading, body);
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
        catch (Exception) { /* no-op if it fails */ }
    }
}
