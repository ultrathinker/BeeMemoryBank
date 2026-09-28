using System.Text.RegularExpressions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// What the blind node's compose file publishes. Docker's port publishing bypasses the host
/// firewall, so the file itself is where "LAN only" and "console on loopback only" are decided.
/// (A test host checks the same with <c>docker compose config</c>; this keeps it from regressing.)
/// </summary>
public class BlindDockerPackagingTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the tests run from inside the repository");
        return File.ReadAllText(Path.Combine([dir!.FullName, .. parts]));
    }

    private static string ComposeFile() => RepoFile("docker", "blind", "compose.yaml");

    /// <summary>The environment block as compose will hand it to the container.</summary>
    private static Dictionary<string, string> ComposeEnvironment() =>
        Regex.Matches(ComposeFile(), @"^\s*-\s*""?([A-Z_]+)=(.*?)""?\s*$", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

    private static List<string> PortMappings() =>
        Regex.Matches(ComposeFile(), @"^\s*-\s*""([^""]*:56\d\d)""\s*$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();

    [Fact]
    public void SyncPort_IsBoundToTheNamedLanAddress_AndRequiresIt()
    {
        PortMappings().Should().ContainSingle(p => p.EndsWith(":5610:5610"))
            .Which.Should().StartWith("${BMB_LAN_ADDR:?",
                "no host address means every interface, a public one included; without the variable compose must refuse");
    }

    [Fact]
    public void Console_IsPublishedOnHostLoopbackOnly()
    {
        PortMappings().Should().ContainSingle(p => p.EndsWith(":5611"))
            .Which.Should().Be("127.0.0.1:5611:5611");
    }

    /// <summary>
    /// The published sync port must be the TLS one. Without BMB_BLIND_HTTPS_PORT the Api listens in
    /// plain HTTP there — which is what shipped — and the node cannot be paired in two separate
    /// ways: `bmb blind pair-code` refuses because BMB_PUBLIC_ADDRESS is not set, and a PC refuses
    /// any pair code whose address is not https:// (BlindPairCode.Parse).
    /// </summary>
    [Fact]
    public void SyncPort_SpeaksHttps_AndThePublicAddressIsHttpsOnThatPort()
    {
        var env = ComposeEnvironment();
        env.Should().ContainKey("BMB_BLIND_HTTPS_PORT").WhoseValue.Should().Be("5610",
            "the TLS listener belongs on the port the mesh dials");
        env.Should().ContainKey("BMB_PUBLIC_ADDRESS").WhoseValue
            .Should().Be("https://${BMB_LAN_ADDR:?set BMB_LAN_ADDR to this host's LAN address, e.g. BMB_LAN_ADDR=192.168.1.20}:5610",
                "the pair code must carry an https address on the published port, from the same variable the port is bound to")
            .And.StartWith("https://");
    }

    /// <summary>
    /// Local tools reach the Api on the plain loopback port. They used to be pointed at 5610, which
    /// is the TLS port once the fix above is in: the console's /login — and every `docker exec … bmb`
    /// call — would have answered 500 / connection errors on a node that pairs perfectly.
    /// </summary>
    [Fact]
    public void ConsoleAndCli_ReachTheApiOnTheLocalPlainPort()
    {
        var env = ComposeEnvironment();
        env.Should().ContainKey("BMB_BLIND_LOCAL_PORT").WhoseValue.Should().Be("5612");
        env.Should().ContainKey("BMB_API_URL").WhoseValue.Should().Be("http://127.0.0.1:5612",
            "the console and the CLI must not be pointed at the TLS listener");

        RepoFile("docker", "blind", "Dockerfile").Should().Contain("BMB_API_URL=http://127.0.0.1:5612",
            "`docker exec … bmb blind …` reads the image's environment, not compose's");
        RepoFile("docker", "blind", "entrypoint.sh").Should().NotContain("BMB_API_URL=http://127.0.0.1:5610",
            "the console's launch must not hard-wire the TLS port back in");
    }

    /// <summary>
    /// The healthcheck has to survive the switch to TLS — a container whose health probe can no
    /// longer talk to its own Api is reported unhealthy while the node is fine, and Docker restarts it.
    /// </summary>
    [Fact]
    public void Healthcheck_UsesTheTlsListenerAndTheLocalOne()
    {
        var dockerfile = RepoFile("docker", "blind", "Dockerfile");
        var healthcheck = Regex.Match(dockerfile, @"HEALTHCHECK[^\n]*\n(\s+CMD[^\n]*\n(?:\s+&&[^\n]*\n)?)")
            .Groups[1].Value;
        healthcheck.Should().NotBeNullOrWhiteSpace("the image must check its own Api");
        healthcheck.Should().Contain("curl -fsk https://localhost:5610/health",
            "the sync listener is the surface a peer or a pairing uses, and its certificate is "
            + "self-signed — curl needs -k to accept it")
            .And.Contain("http://localhost:5612/health", "and the loopback listener is what the console uses");
    }
}
