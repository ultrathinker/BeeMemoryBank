namespace BeeMemoryBank.AppPaths;

public static class PathComparison
{
    public static StringComparison ForCurrentPlatform() =>
        ForPlatform(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS());

    public static StringComparison ForPlatform(bool isWindows, bool isMacOs) =>
        isWindows || isMacOs ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
