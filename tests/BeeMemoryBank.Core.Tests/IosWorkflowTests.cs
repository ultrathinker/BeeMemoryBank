using System.Text.RegularExpressions;

namespace BeeMemoryBank.Core.Tests;

/// <summary>Guard the public iPhone-app CI job: it must remain manual, read-only, fork-safe and run the actual simulator boundary scan.</summary>
public class IosWorkflowTests
{
    private static readonly string[] Lines = RepoFile(".github", "workflows", "build-ios.yml")
        .Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the tests run from inside the repository");
        return File.ReadAllText(Path.Combine([dir!.FullName, .. parts]));
    }

    private static IEnumerable<string> Active => Lines.Where(l => !l.TrimStart().StartsWith('#'));

    [Fact]
    public void TheWorkflow_IsManual_ReadOnly_AndForkSafe()
    {
        Lines.Should().Contain("on:", "the workflow has an explicit trigger");
        Lines.Should().Contain("  workflow_dispatch:");
        Active.Should().NotContain(l => l.TrimStart().StartsWith("push:") || l.TrimStart().StartsWith("pull_request:"));
        Active.Should().Contain("  contents: read").And.NotContain(l => l.Contains("write-all") || l.Contains("contents: write"));
        Active.Should().Contain("    if: github.repository == 'ultrathinker/BeeMemoryBank'");
        Active.Should().Contain("    runs-on: macos-latest");
    }

    [Fact]
    public void EveryAction_IsPinnedByCommit_WithItsVersionAsAComment_AndOnlyTheReviewedActionsAreUsed()
    {
        var uses = Active.Select(l => Regex.Match(l, @"^\s*(?:-\s+)?uses:\s*(\S+)(.*)$")).Where(m => m.Success).ToList();
        uses.Should().HaveCount(2);
        foreach (var use in uses)
        {
            var match = Regex.Match(use.Groups[1].Value, @"^([\w.-]+/[\w.-]+)@([0-9a-f]{40})$");
            match.Success.Should().BeTrue($"'{use.Groups[1].Value}' must be pinned by a full commit SHA");
            match.Groups[1].Value.Should().BeOneOf("actions/checkout", "actions/setup-dotnet");
            use.Groups[2].Value.Should().MatchRegex(@"^\s+# v\d+\.\d+\.\d+$", "the action version is recorded beside its SHA");
        }
    }

    [Fact]
    public void TheWorkflow_InstallsIosBuildsBothUnsignedSimulatorsAndScansBothBuiltApps()
    {
        var text = string.Join('\n', Active);
        text.Should().Contain("dotnet workload install ios maui-ios");
        text.Should().Contain("scripts/build-ios-blind.sh simulator Release");
        text.Should().Contain("scripts/build-ios-full.sh simulator Release");
        text.Should().Contain("dotnet build tests/BeeMemoryBank.BlindIos.Tests/BeeMemoryBank.BlindIos.Tests.csproj -c Release -m:2 -nr:false");
        text.Should().Contain("dotnet build tests/BeeMemoryBank.FullIos.Tests/BeeMemoryBank.FullIos.Tests.csproj -c Release -m:2 -nr:false");
        text.Should().Contain("BMB_REQUIRE_IOS_SCAN=1 dotnet test tests/BeeMemoryBank.BlindIos.Tests/BeeMemoryBank.BlindIos.Tests.csproj -c Release --no-build");
        text.Should().Contain("BMB_REQUIRE_IOS_SCAN=1 dotnet test tests/BeeMemoryBank.FullIos.Tests/BeeMemoryBank.FullIos.Tests.csproj -c Release --no-build");
    }
}
