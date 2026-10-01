using System.Text.Json;

namespace BeeMemoryBank.BlindMobile.Tests;

public class ForbiddenReferencesTests
{
    private static readonly string[] ForbiddenNames =
    [
        "BeeMemoryBank.Embeddings",
        "BeeMemoryBank.Media",
        "Microsoft.ML.OnnxRuntime",
        "Microsoft.ML.Tokenizers",
        "Markdig",
        "Indiko.Maui.Controls.Markdown",
        "SixLabors.ImageSharp",
        "BeeMemoryBank.Sync"
    ];

    private static string FindRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "BeeMemoryBank.slnx")) ||
                File.Exists(Path.Combine(current, "BeeMemoryBank.sln")))
            {
                return current;
            }
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException("Could not locate repository root from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void BlindMobile_ProjectAssetsJson_ContainsNoForbiddenReferences()
    {
        var repoRoot = FindRepoRoot();
        var assetsJsonPath = Path.Combine(repoRoot, "mobile", "BeeMemoryBank.BlindMobile", "obj", "project.assets.json");

        File.Exists(assetsJsonPath).Should().BeTrue($"project.assets.json must exist at {assetsJsonPath} (run dotnet restore first)");

        var jsonContent = File.ReadAllText(assetsJsonPath);
        using var doc = JsonDocument.Parse(jsonContent);
        var root = doc.RootElement;

        var violations = new List<string>();

        // Check targets (resolved packages and project references per framework)
        if (root.TryGetProperty("targets", out var targets))
        {
            foreach (var target in targets.EnumerateObject())
            {
                foreach (var lib in target.Value.EnumerateObject())
                {
                    var libName = lib.Name;
                    foreach (var forbidden in ForbiddenNames)
                    {
                        if (libName.StartsWith(forbidden + "/", StringComparison.OrdinalIgnoreCase) ||
                            libName.Equals(forbidden, StringComparison.OrdinalIgnoreCase))
                        {
                            violations.Add($"Target '{target.Name}' contains forbidden reference '{libName}'");
                        }
                    }
                }
            }
        }

        // Check libraries (all resolved package/project libraries in the graph)
        if (root.TryGetProperty("libraries", out var libraries))
        {
            foreach (var lib in libraries.EnumerateObject())
            {
                var libName = lib.Name;
                foreach (var forbidden in ForbiddenNames)
                {
                    if (libName.StartsWith(forbidden + "/", StringComparison.OrdinalIgnoreCase) ||
                        libName.Equals(forbidden, StringComparison.OrdinalIgnoreCase))
                    {
                        violations.Add($"Libraries contains forbidden reference '{libName}'");
                    }
                }
            }
        }

        violations.Should().BeEmpty(
            "BeeMemoryBank.BlindMobile resolved graph must not contain any forbidden references: " +
            string.Join("; ", violations));
    }

    [Fact]
    public void BlindMobile_Csproj_ContainsNoForbiddenProjectOrPackageReferences()
    {
        var repoRoot = FindRepoRoot();
        var csprojPath = Path.Combine(repoRoot, "mobile", "BeeMemoryBank.BlindMobile", "BeeMemoryBank.BlindMobile.csproj");

        File.Exists(csprojPath).Should().BeTrue($"csproj must exist at {csprojPath}");

        var content = File.ReadAllText(csprojPath);

        foreach (var forbidden in ForbiddenNames)
        {
            content.Should().NotContain(forbidden,
                $"BeeMemoryBank.BlindMobile.csproj must not directly reference {forbidden}");
        }
    }
}
