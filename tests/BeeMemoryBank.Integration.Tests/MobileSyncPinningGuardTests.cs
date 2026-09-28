using System.Text.RegularExpressions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Review L-stage1 #6: the phone cannot be started in this test run, so its composition is checked
/// at the source. The default client — what every mobile SyncWithAsync caller takes from the
/// factory — is composed with the pinned handler, and no mobile sync caller builds a client of its
/// own that would bypass it. (The composition itself is covered by PinnedSyncClientCompositionTests.)
/// </summary>
public class MobileSyncPinningGuardTests
{
    private static readonly string Mobile = Path.Combine(FindRepoRoot(), "mobile", "BeeMemoryBank.Mobile");

    [Fact]
    public void TheDefaultClient_IsComposedWithThePinnedHandler()
    {
        var program = File.ReadAllText(Path.Combine(Mobile, "MauiProgram.cs"));

        program.Should().MatchRegex(@"AddHttpClient\(string\.Empty\)\s*\.UsePinnedSyncHandler\(\)");
    }

    [Fact]
    public void MobileSyncCallers_UseTheFactorysDefaultClient()
    {
        var callers = Directory.GetFiles(Mobile, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && File.ReadAllText(f).Contains("SyncWithAsync("))
            .ToList();

        callers.Should().NotBeEmpty();
        foreach (var file in callers)
        {
            var source = File.ReadAllText(file);
            source.Should().NotContain("new HttpClient(", $"{Path.GetFileName(file)} must sync through the pinned client");
            Regex.IsMatch(source, @"CreateClient\(\)").Should().BeTrue($"{Path.GetFileName(file)} takes the default client");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }
}
