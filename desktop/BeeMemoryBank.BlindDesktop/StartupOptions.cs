namespace BeeMemoryBank.BlindDesktop;

/// <summary>
/// The command line: <c>--minimized</c> (start in the tray) and, for tests and checks, <c>--data-dir &lt;folder&gt;</c> (a private root other
/// than the default) and <c>--self-check</c> (a headless check of the composition that shows nothing and exits 0 or 1; see
/// <see cref="HeadlessSelfCheck"/>). <c>--self-check-wait &lt;seconds&gt;</c> makes the check keep the one-copy lock for that long so that a second
/// process can prove it is refused, and <c>--self-check-second-start</c> is that second process.
/// </summary>
public sealed record StartupOptions(bool Minimized, string? DataDirectory, bool SelfCheck = false, int SelfCheckWaitSeconds = 0,
    bool SelfCheckSecondStart = false)
{
    /// <summary>A check on the real data folder is refused (it would make the real app's folder and, on a Mac, name its Keychain items).</summary>
    public bool SelfCheckWithoutFolder => SelfCheck && DataDirectory is null;

    public const string MinimizedFlag = "--minimized";
    public const string DataDirectoryFlag = "--data-dir";
    public const string SelfCheckFlag = "--self-check";
    public const string SelfCheckWaitFlag = "--self-check-wait";
    public const string SelfCheckSecondStartFlag = "--self-check-second-start";

    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        var minimized = false;
        var selfCheck = false;
        var secondStart = false;
        var wait = 0;
        string? dataDirectory = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.Equals(MinimizedFlag, StringComparison.OrdinalIgnoreCase)) minimized = true;
            else if (arg.Equals(SelfCheckFlag, StringComparison.OrdinalIgnoreCase)) selfCheck = true;
            else if (arg.Equals(SelfCheckSecondStartFlag, StringComparison.OrdinalIgnoreCase)) { selfCheck = true; secondStart = true; }
            else if (arg.Equals(SelfCheckWaitFlag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count && int.TryParse(args[i + 1], out var seconds))
            {
                selfCheck = true;
                wait = Math.Clamp(seconds, 0, 300);
                i++;
            }
            else if (arg.Equals(DataDirectoryFlag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count) dataDirectory = args[++i];
            else if (arg.StartsWith(DataDirectoryFlag + "=", StringComparison.OrdinalIgnoreCase)) dataDirectory = arg[(DataDirectoryFlag.Length + 1)..];
        }
        return new StartupOptions(minimized, string.IsNullOrWhiteSpace(dataDirectory) ? null : dataDirectory, selfCheck, wait, secondStart);
    }
}
