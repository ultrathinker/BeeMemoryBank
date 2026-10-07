using System.Text.RegularExpressions;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// BMB-88: what keeps <c>.github/workflows/docker-publish.yml</c> safe to have in a public repository: every action pinned by
/// commit, the least permissions, a fork guard on every job, nothing tagged before the smoke test passed, and no trigger that hands
/// a pull request a token that can push packages. It reads the file as text, like the mobile workflow's guard.
/// </summary>
public class DockerPublishWorkflowTests
{
    private static readonly string[] Lines = RepoFile(".github", "workflows", "docker-publish.yml")
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

    /// <summary>The jobs, each as its own lines (from "  name:" under "jobs:" to the next job).</summary>
    private static Dictionary<string, string[]> Jobs()
    {
        var start = Array.FindIndex(Lines, l => l == "jobs:");
        start.Should().BeGreaterThanOrEqualTo(0);
        var jobs = new Dictionary<string, string[]>();
        string? current = null;
        var body = new List<string>();
        foreach (var line in Lines.Skip(start + 1))
        {
            var header = Regex.Match(line, @"^  ([a-z][a-z0-9_-]*):\s*$");
            if (header.Success)
            {
                if (current != null) jobs[current] = body.ToArray();
                current = header.Groups[1].Value;
                body = new List<string>();
            }
            else if (current != null) body.Add(line);
        }
        if (current != null) jobs[current] = body.ToArray();
        return jobs;
    }

    /// <summary>The permissions a job grants, as "scope: level" lines.</summary>
    private static string[] PermissionsOf(string[] job)
    {
        var start = Array.FindIndex(job, l => l == "    permissions:");
        start.Should().BeGreaterThanOrEqualTo(0, "every job states its own permissions");
        return job.Skip(start + 1).TakeWhile(l => l.StartsWith("      ") && !l.StartsWith("       "))
            .Select(l => Regex.Replace(l.Trim(), @"\s*#.*$", "")).ToArray();
    }

    // The actions this workflow may use. Which commit each is pinned to is not fixed here: Dependabot (github-actions, weekly) moves
    // the pins, and a test that held the exact SHAs would turn the build red on every bump.
    private static readonly string[] AllowedActions =
    [
        "actions/checkout", "docker/setup-buildx-action", "docker/metadata-action", "docker/build-push-action",
        "docker/login-action", "actions/upload-artifact", "actions/download-artifact", "actions/attest-build-provenance",
    ];

    [Fact]
    public void EveryAction_IsPinnedByCommit_WithItsVersionAsAComment_AndOnlyTheseActionsAreUsed()
    {
        var uses = Active.Select(l => Regex.Match(l, @"^\s*(?:-\s+)?uses:\s*(\S+)(.*)$")).Where(m => m.Success).ToList();

        uses.Should().NotBeEmpty();
        foreach (var use in uses)
        {
            var reference = use.Groups[1].Value;
            var match = Regex.Match(reference, @"^([\w.-]+/[\w.-]+)@([0-9a-f]{40})$");
            match.Success.Should().BeTrue($"'{reference}' must be pinned by a full commit SHA, not a tag or a branch");
            AllowedActions.Should().Contain(match.Groups[1].Value, "only the actions this workflow was reviewed with are used");
            use.Groups[2].Value.Should().MatchRegex(@"^\s+# v\d+\.\d+\.\d+$", "the version the SHA stands for is written beside it");
        }
        uses.Select(u => u.Groups[1].Value.Split('@')[0]).Distinct().Should().BeEquivalentTo(AllowedActions);
    }

    [Fact]
    public void TheTriggers_AreAPublishedReleaseAndAManualRun_NeverAPullRequestOrAPush()
    {
        var on = Lines.SkipWhile(l => l != "on:").Skip(1).TakeWhile(l => l.StartsWith("  ") || l.Length == 0)
            .Where(l => !l.TrimStart().StartsWith('#') && l.Trim().Length > 0).ToArray();

        on.Should().Equal("  release:", "    types: [published]", "  workflow_dispatch:");
        Active.Should().NotContain(l => l.Contains("pull_request")).And.NotContain(l => l.Contains("secrets.") && !l.Contains("secrets.GITHUB_TOKEN"));
    }

    [Fact]
    public void TheWorkflow_ReadsOnly_AndEachJobAsksForWhatItNeeds()
    {
        var top = Array.FindIndex(Lines, l => l == "permissions:");
        Lines[top + 1].Trim().Should().Be("contents: read");
        Lines[top + 2].Should().NotStartWith("  ", "the workflow-wide permission is contents: read and nothing else");

        var jobs = Jobs();
        jobs.Keys.Should().BeEquivalentTo("build", "merge", "verify");
        PermissionsOf(jobs["build"]).Should().BeEquivalentTo("contents: read", "packages: write");
        PermissionsOf(jobs["merge"]).Should().BeEquivalentTo("contents: read", "packages: write", "id-token: write", "attestations: write");
        PermissionsOf(jobs["verify"]).Should().BeEquivalentTo("contents: read");
        Active.Should().NotContain(l => l.Contains("write-all") || l.Contains("contents: write"));
    }

    [Fact]
    public void EveryJob_RunsOnlyInThisRepository()
    {
        foreach (var (name, job) in Jobs())
            job.Should().Contain(l => l.StartsWith("    if: github.repository == 'ultrathinker/BeeMemoryBank'"),
                $"job {name} must do nothing in a fork: the image names are this repository's packages");
    }

    [Fact]
    public void NothingIsPushed_BeforeTheSmokeTest_AndOnlyATagRunPushesOrTags()
    {
        var build = Jobs()["build"];
        var smoke = Array.FindIndex(build, l => l.Contains("scripts/smoke-docker.sh"));
        var login = Array.FindIndex(build, l => l.Contains("docker/login-action@"));
        var push = Array.FindIndex(build, l => l.Contains("push-by-digest=true"));

        smoke.Should().BeGreaterThan(0);
        login.Should().BeGreaterThan(smoke, "the registry is not even logged in to before the image passed");
        push.Should().BeGreaterThan(smoke);
        build.Count(l => l.Trim() == "if: github.ref_type == 'tag'").Should().BeGreaterThanOrEqualTo(5,
            "the tag check, the login, the push, the digest export and its upload run for a tag only");
        Jobs()["merge"].Should().Contain(l => l.Contains("github.ref_type == 'tag'"));
        Jobs()["verify"].Should().Contain(l => l.Contains("github.ref_type == 'tag'"));
    }

    [Fact]
    public void EachImagesDownloadPattern_MatchesOnlyItsOwnDigestArtifacts()
    {
        // `beememorybank` is a prefix of `beememorybank-blind`: with hyphens between the parts, the full image's pattern
        // `digests-beememorybank-*` also matches `digests-beememorybank-blind-amd64`, and the blind image's digests would be
        // merged into the full image's manifest list. Both templates are read from the file and played out for every image and arch.
        var upload = string.Join('\n', Jobs()["build"]);
        var uploadName = Regex.Match(upload, @"^\s*name: (digests.*?)\s*$", RegexOptions.Multiline).Groups[1].Value;
        var merge = string.Join('\n', Jobs()["merge"]);
        var pattern = Regex.Match(merge, @"^\s*pattern: (digests.*?)\s*$", RegexOptions.Multiline).Groups[1].Value;
        uploadName.Should().NotBeEmpty();
        pattern.Should().NotBeEmpty();

        var images = Regex.Match(merge, @"image: \[([^\]]+)\]").Groups[1].Value.Split(',').Select(s => s.Trim()).ToArray();
        images.Should().HaveCountGreaterThan(1);
        string[] arches = ["amd64", "arm64"];
        var all = images.SelectMany(i => arches.Select(a =>
            (Image: i, Name: uploadName.Replace("${{ matrix.image }}", i).Replace("${{ matrix.arch }}", a)))).ToArray();

        foreach (var image in images)
        {
            // upload-artifact / download-artifact globs: `*` is any run of characters.
            var glob = new Regex("^" + Regex.Escape(pattern.Replace("${{ matrix.image }}", image)).Replace("\\*", ".*") + "$");
            all.Where(a => glob.IsMatch(a.Name)).Select(a => a.Image).Should().AllBeEquivalentTo(image,
                $"the pattern for {image} must not pick up another image's digests")
                .And.HaveCount(arches.Length, "and it must pick up both architectures of its own");
        }
    }

    [Fact]
    public void OneImagesFailure_DoesNotCancelTheOtherImagesTagging()
    {
        string.Join('\n', Jobs()["merge"]).Should().MatchRegex(@"fail-fast: false");
    }

    [Fact]
    public void ThePublishedTags_AreTheVersion_TheLine_AndLatestOnlyForAFinalRelease()
    {
        var merge = string.Join('\n', Jobs()["merge"]);

        merge.Should().Contain("type=semver,pattern={{version}}").And.Contain("type=semver,pattern={{major}}.{{minor}}")
            .And.Contain("latest=auto", "metadata-action writes latest only for a semver tag that is not a pre-release");
        merge.Should().NotContain("type=raw").And.NotContain("{{major}},", "no floating major tag");
        string.Join('\n', Active).Should().NotContain("actions/cache", "a tag run's cache is not readable by the next tag, and it would evict the ONNX and MAUI caches of the other workflows");
    }
}
