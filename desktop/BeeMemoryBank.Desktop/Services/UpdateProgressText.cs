namespace BeeMemoryBank.Desktop.Services;

/// <summary>
/// What the update window says while a new version comes in (BMB-32). The percent the update library reports stops moving while it unpacks and applies
/// what it downloaded (about a minute and a half on a slow virtual machine): a bar that stands still at 70 % with "close this window to stop the
/// download" looks like a frozen program. When the percent has not moved for <see cref="StalledAfter"/>, the window says that it is still working and
/// shows a bar that moves by itself.
/// </summary>
public static class UpdateProgressText
{
    /// <summary>How long the percent may stand still, mid-way, before the window says it is unpacking.</summary>
    public static readonly TimeSpan StalledAfter = TimeSpan.FromSeconds(8);

    /// <param name="Progress">The percent for a bar that fills, or -1 for one that moves by itself.</param>
    public sealed record View(string Title, string Body, int Progress);

    public static View For(string version, int percent, TimeSpan sinceLastChange) =>
        sinceLastChange >= StalledAfter && percent is > 0 and < 100
            ? new View($"Installing version {version}",
                "Still working: the downloaded update is being unpacked. On a slow computer this takes a minute or two. You can keep working.",
                -1)
            : new View($"Downloading version {version}",
                $"{percent}%. You can keep working; close this window to stop the download.",
                percent);
}
