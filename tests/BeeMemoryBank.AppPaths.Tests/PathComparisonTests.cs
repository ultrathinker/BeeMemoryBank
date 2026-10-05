using BeeMemoryBank.AppPaths;

namespace BeeMemoryBank.AppPaths.Tests;

public class PathComparisonTests
{
    [Theory]
    [InlineData(true, false, StringComparison.OrdinalIgnoreCase)]
    [InlineData(false, true, StringComparison.OrdinalIgnoreCase)]
    [InlineData(false, false, StringComparison.Ordinal)]
    public void ForPlatform_SelectsTheFilesystemComparison(bool isWindows, bool isMacOs, StringComparison expected)
    {
        PathComparison.ForPlatform(isWindows, isMacOs).Should().Be(expected);
    }
}
