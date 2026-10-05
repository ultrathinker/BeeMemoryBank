using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.Services;

/// <summary>
/// Progress of a long job for the tray: the text of the tray icon's tooltip. A quiet app does not pop up notices, and a tray icon
/// has no real balloon on every platform. The shell calls <see cref="Show"/> and <see cref="Clear"/> when the controller says the job
/// state changed and puts <see cref="Line"/> into the tooltip; a failure to present it can never affect a job.
/// </summary>
public sealed class TrayNotifications : IBlindNotifications
{
    private volatile string? _line;

    /// <summary>"First load: 42 %" while a long job runs, otherwise null.</summary>
    public string? Line => _line;

    public void Show(string title, double progress) =>
        _line = progress is > 0 and < 1 ? $"{title}: {progress:P0}" : title;

    /// <summary>The job ended: the tooltip goes back to the status line.</summary>
    public void Clear() => _line = null;
}
