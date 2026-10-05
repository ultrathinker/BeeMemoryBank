namespace BeeMemoryBank.Platforms.Apple.Tests;

/// <summary>A test that needs a real Mac (Security.framework). Skipped, not failed, everywhere else.</summary>
public sealed class MacOnlyFactAttribute : FactAttribute
{
    public MacOnlyFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "needs macOS";
    }
}

/// <summary>A test of the "this is not a Mac" behavior. Skipped on a Mac.</summary>
public sealed class NotMacFactAttribute : FactAttribute
{
    public NotMacFactAttribute()
    {
        if (OperatingSystem.IsMacOS()) Skip = "needs an operating system other than macOS";
    }
}
