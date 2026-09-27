using System.Text.RegularExpressions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// What the blind node's compose file publishes. Docker's port publishing bypasses the host
/// firewall, so the file itself is where "LAN only" and "console on loopback only" are decided.
/// (A test host checks the same with <c>docker compose config</c>; this keeps it from regressing.)
/// </summary>
public class BlindDockerPackagingTests
{
    private static string ComposeFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the tests run from inside the repository");
        return File.ReadAllText(Path.Combine(dir!.FullName, "docker", "blind", "compose.yaml"));
    }

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
}
