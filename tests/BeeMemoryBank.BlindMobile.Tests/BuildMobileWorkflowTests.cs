using System.Text.RegularExpressions;

namespace BeeMemoryBank.BlindMobile.Tests;

/// <summary>
/// <c>OrdinaryAppHasNoBlindCodeTests</c> read the ordinary app's Release output, so they can only pass in a job that has
/// built that app. These tests read <c>.github/workflows/build-mobile.yml</c> and keep that wiring true: the job that never
/// builds the ordinary app must not run the guard, the job that does build it must run it after the build, and a change to
/// the guard's own tests must trigger the workflow (otherwise a weakened guard lands without CI ever having run it).
/// </summary>
public sealed class BuildMobileWorkflowTests
{
    private const string Guard = "OrdinaryAppHasNoBlindCodeTests";
    private const string OrdinaryPublish = "dotnet publish mobile/BeeMemoryBank.Mobile/BeeMemoryBank.Mobile.csproj";
    private const string BlindTestsProject = "tests/BeeMemoryBank.BlindMobile.Tests/BeeMemoryBank.BlindMobile.Tests.csproj";
    private const string GuardTestsPath = "tests/BeeMemoryBank.BlindMobile.Tests/**";

    [Fact]
    public void AJobThatDoesNotBuildTheOrdinaryApp_ExcludesTheGuardFromItsTestRun()
    {
        var offenders = new List<string>();
        var inspected = 0;

        foreach (var job in Jobs())
        {
            if (job.Commands.Any(c => c.Contains(OrdinaryPublish, StringComparison.Ordinal))) continue;

            foreach (var test in job.Commands.Where(IsBlindTestsRun))
            {
                inspected++;
                if (!test.Contains("!~" + Guard, StringComparison.Ordinal))
                    offenders.Add($"{job.Name}: {test}");
            }
        }

        inspected.Should().BeGreaterThan(0, "the build-blind job runs the blind app's tests");
        offenders.Should().BeEmpty(
            $"{Guard} fails when the ordinary app's Release output is missing, and such a job never builds it");
    }

    [Fact]
    public void TheJobThatBuildsTheOrdinaryApp_RunsTheGuardAfterTheBuild()
    {
        var jobs = Jobs().Where(j => j.Commands.Any(c => c.Contains(OrdinaryPublish, StringComparison.Ordinal))).ToList();
        jobs.Should().NotBeEmpty("the workflow builds the ordinary APK");

        foreach (var job in jobs)
        {
            var publish = job.Commands.FindIndex(c => c.Contains(OrdinaryPublish, StringComparison.Ordinal));
            var guard = job.Commands.FindIndex(c => IsBlindTestsRun(c) && c.Contains("~" + Guard, StringComparison.Ordinal)
                                                    && !c.Contains("!~" + Guard, StringComparison.Ordinal));

            guard.Should().BeGreaterThan(publish, $"job {job.Name} must run the guard on the APK it has just built");
        }
    }

    [Fact]
    public void BothTriggers_WatchTheGuardsTests()
    {
        var paths = TriggerPathFilters();

        paths.Should().HaveCount(2, "push and pull_request each carry a paths filter");
        foreach (var filter in paths)
            filter.Should().Contain(GuardTestsPath, "a commit that weakens the guard must start the workflow that runs it");
    }

    private static bool IsBlindTestsRun(string command) =>
        command.Contains("dotnet test", StringComparison.Ordinal) && command.Contains(BlindTestsProject, StringComparison.Ordinal);

    /// <summary>The <c>paths: [...]</c> lists of the <c>on:</c> block, each as its quoted entries.</summary>
    private static List<List<string>> TriggerPathFilters()
    {
        var lines = WorkflowLines();
        var start = lines.FindIndex(l => l.TrimEnd() == "on:");
        start.Should().BeGreaterThanOrEqualTo(0, "the workflow has an on: block");
        var end = lines.FindIndex(start + 1, l => l.Length > 0 && !char.IsWhiteSpace(l[0]) && !l.StartsWith('#'));

        return lines.Skip(start + 1).Take((end < 0 ? lines.Count : end) - start - 1)
            .Select(l => Regex.Match(l, @"^\s+paths:\s*\[(?<list>.*)\]\s*$"))
            .Where(m => m.Success)
            .Select(m => Regex.Matches(m.Groups["list"].Value, "'([^']*)'").Select(x => x.Groups[1].Value).ToList())
            .ToList();
    }

    private sealed record Job(string Name, List<string> Commands);

    /// <summary>Each job's shell commands, with backslash line continuations joined into one logical command.</summary>
    private static List<Job> Jobs()
    {
        var lines = WorkflowLines();
        var jobsAt = lines.FindIndex(l => l.TrimEnd() == "jobs:");
        jobsAt.Should().BeGreaterThanOrEqualTo(0, "the workflow has a jobs: block");

        var jobs = new List<Job>();
        Job? current = null;
        var pending = "";
        foreach (var line in lines.Skip(jobsAt + 1))
        {
            var header = Regex.Match(line, @"^  (?<name>[A-Za-z0-9_-]+):\s*$");
            if (header.Success)
            {
                current = new Job(header.Groups["name"].Value, []);
                jobs.Add(current);
                pending = "";
                continue;
            }
            if (current is null) continue;

            var text = line.Trim();
            if (pending.Length > 0 || text.StartsWith("dotnet ", StringComparison.Ordinal) || text.StartsWith("run: dotnet ", StringComparison.Ordinal))
            {
                pending += (pending.Length > 0 ? " " : "") + text.Replace("run: ", "", StringComparison.Ordinal).TrimEnd('\\').Trim();
                if (!text.EndsWith('\\'))
                {
                    current.Commands.Add(pending);
                    pending = "";
                }
            }
        }
        return jobs;
    }

    private static List<string> WorkflowLines()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "BeeMemoryBank.slnx"))) dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("the repository root holds BeeMemoryBank.slnx");
        var path = Path.Combine(dir!, ".github", "workflows", "build-mobile.yml");
        File.Exists(path).Should().BeTrue($"the mobile workflow must exist at {path}");
        return File.ReadAllLines(path).ToList();
    }
}
