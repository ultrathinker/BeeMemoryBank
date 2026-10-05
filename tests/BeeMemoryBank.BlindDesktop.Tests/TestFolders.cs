namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>
/// Scratch folders for the tests: one folder per test run (<c>run-&lt;time&gt;-&lt;process id&gt;</c>) under the temp folder, and the tests' folders
/// inside it. The tests do not remove them afterwards (nothing in the tests deletes files except the product code under test, such as the
/// wipe, in its own scratch folder); one run is one folder to look at, and they are listed in the owner's list of junk.
/// </summary>
internal static class TestFolders
{
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), "bmb-blind-desktop-tests");

    private static readonly string Run = Path.Combine(Root, $"run-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}");

    public static string New(string? name = null)
    {
        var folder = Path.Combine(Run, (name ?? "t") + "-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(folder);
        return folder;
    }
}
