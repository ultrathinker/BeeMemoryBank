using System.Text.RegularExpressions;
using BeeMemoryBank.Embeddings;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// BMB-88: the full-node image bakes the embedding model in, verified at build time against the hash the node itself checks
/// (<see cref="EmbeddingModelWiring.BundledModelSha256"/>). A model bump that forgets the Dockerfile would ship an image whose
/// node rejects its own model and silently runs without search by meaning; this keeps the two in one step. The rest pins what
/// makes the image multi-arch and small, the same way docker/blind/Dockerfile already is.
/// </summary>
public class DockerImageModelPinTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the tests run from inside the repository");
        return File.ReadAllText(Path.Combine([dir!.FullName, .. parts]));
    }

    private static readonly string Dockerfile = RepoFile("Dockerfile");

    [Fact]
    public void TheImage_DownloadsTheModel_VerifiedByTheHashTheNodeChecks()
    {
        // The instruction as Docker reads it: continuation lines joined.
        var add = Regex.Match(Dockerfile.Replace("\\\n", " "), @"^ADD [^\n]*", RegexOptions.Multiline).Value;

        add.Should().Contain($"--checksum=sha256:{EmbeddingModelWiring.BundledModelSha256}",
            "the image's model and the node's expected hash must be the same file");
        // A commit of the model repository, never `main`: a moving branch could change or break the build.
        add.Should().MatchRegex(@"https://huggingface\.co/Xenova/multilingual-e5-small/resolve/[0-9a-f]{40}/onnx/model_quantized\.onnx")
            .And.NotContain("/resolve/main/")
            .And.Contain("/app/api/model.onnx").And.Contain("--chmod=0644");
    }

    [Fact]
    public void TheVerifiedModel_IsAddedAfterTheApi_SoNothingFromABuildTreeReplacesIt_AndTheCliHasNoCopyOfItsOwn()
    {
        Dockerfile.IndexOf("ADD --chmod=0644 --checksum=", StringComparison.Ordinal).Should()
            .BeGreaterThan(Dockerfile.IndexOf("COPY --from=build /app/api ./api/", StringComparison.Ordinal));
        Regex.Match(Dockerfile, @"BeeMemoryBank\.Cli\.csproj[^\n]*\n[^\n]*").Value.Should().Contain("-p:BmbBundleModel=false",
            "the CLI uses the Api's model (CliServiceProvider): a second 113 MB copy is dead weight");
    }

    [Fact]
    public void TheBuild_RunsOnTheBuildPlatform_AndPublishesForTheTargetOnly()
    {
        Dockerfile.Should().Contain("FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build");
        Regex.Matches(Dockerfile, @"^ARG TARGETARCH\s*=", RegexOptions.Multiline).Should().BeEmpty(
            "a default value overrides the platform BuildKit passes in");
        Regex.Matches(Dockerfile, @"^ARG TARGETARCH\s*$", RegexOptions.Multiline).Should().HaveCount(1);
        Regex.Matches(Dockerfile, @"-r ""\$\{rid\}"" --no-self-contained -p:UseAppHost=false").Should().HaveCount(3,
            "the Api, the Web front and the CLI each carry only their platform's natives");
        Dockerfile.Should().NotContain("COPY tests/").And.NotContain("COPY tools/");
    }

    [Fact]
    public void OnlyTheWebPort_IsExposed()
    {
        Regex.Matches(Dockerfile, @"^EXPOSE\s+(.*)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim())
            .Should().Equal(["5301"], "5300 (the Api: /mcp, sync, a master-password endpoint) must not be offered by `docker run -P`");
    }

    [Fact]
    public void DockerStop_ReachesTheApi_NotOnlyTheWebFront()
    {
        var entrypoint = RepoFile("docker-entrypoint.sh");

        entrypoint.Should().StartWith("#!/bin/bash", "wait -n is a bash builtin");
        entrypoint.Should().Contain("trap ").And.Contain("TERM").And.Contain("wait -n \"$api\" \"$web\"");
        entrypoint.Should().NotContain("cd /app/web && exec", "the Web front is a child of the script, no longer PID 1 in its place");
    }
}
