using BeeMemoryBank.BlindDesktop.Windows;

namespace BeeMemoryBank.BlindDesktop.Tests.Windows;

public sealed class WindowsBlindPathsTests
{
    [Fact]
    public void TheDefaultRoot_IsTheBlindAppsOwnFolderUnderLocalAppData()
    {
        var root = WindowsBlindPaths.DefaultRoot();

        Path.GetFileName(root).Should().Be("BeeMemoryBankBlind");
        Path.GetDirectoryName(root).Should().Be(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        // The full app uses BeeMemoryBank (ProgramData / its profiles); the blind root is not that folder and not inside it.
        Path.GetFileName(root).Should().NotBe("BeeMemoryBank");
        root.Split(Path.DirectorySeparatorChar).Should().NotContain("BeeMemoryBank", "no folder of the path is the full app's");
    }

    [Fact]
    public void EveryPath_LiesInsideTheRoot()
    {
        var root = TestFolders.New("paths");
        var paths = new WindowsBlindPaths(root);

        paths.DataDirectory.Should().Be(root);
        paths.DatabasePath.Should().Be(Path.Combine(root, "beememorybank.db"));
        paths.SecretsDirectory.Should().Be(Path.Combine(root, "secrets"));
        paths.StatePath.Should().Be(Path.Combine(root, "state.json"));
    }
}
