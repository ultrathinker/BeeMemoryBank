namespace BeeMemoryBank.FullIos.Tests;

/// <summary>
/// A new folder per test under the system's temp folder (bmb-fullios-tests). Left in place: nothing in these tests deletes files (the
/// owner's machines are cleaned by the owner); every folder is small and named by the test that made it.
/// </summary>
internal static class TestFolders
{
    public static string New(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "bmb-fullios-tests", $"{name}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
