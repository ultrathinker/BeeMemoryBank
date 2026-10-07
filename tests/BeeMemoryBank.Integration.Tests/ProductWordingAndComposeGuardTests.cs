using System.Text.RegularExpressions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Guards for text and configuration that must stay true: what the Docker compose files publish, and the few
/// sentences in installers and docs that once promised more than the product does. They read repository files,
/// like <see cref="WebCspComplianceGuardTests"/>.
/// </summary>
public class ProductWordingAndComposeGuardTests
{
    private static readonly string Root = FindRepoRoot();

    // A published port: - "<host side>:<container port 5300 or 5301>".
    private static readonly Regex PortMapping = new(@"^\s*-\s*""[^""]*:(5300|5301)""\s*$", RegexOptions.Compiled);

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));

    private static string[] ActiveLines(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !l.TrimStart().StartsWith('#')).ToArray();

    [Fact]
    public void TheDefaultComposeFile_PublishesTheWebPortOnLoopback_AndNotTheApiPort()
    {
        var active = ActiveLines(Read("docker-compose.yml"));

        var published = active.Where(l => PortMapping.IsMatch(l)).ToArray();
        published.Should().ContainSingle("only the Web port is published by default").Which
            .Should().Contain("${BMB_WEB_BIND:-127.0.0.1}:${BMB_WEB_PORT:-5301}:5301",
                "the login page is 'local only' unless the owner sets BMB_WEB_BIND on purpose");
        active.Should().NotContain(l => l.Contains(":5300\""), "the API port stays unpublished unless the owner uncomments the optional line");
    }

    [Fact]
    public void TheDefaultComposeFile_HasTheOptionalApiPortLines_ButOnlyAsComments()
    {
        var text = Read("docker-compose.yml");

        text.Should().Contain("# - \"127.0.0.1:${BMB_API_PORT:-5300}:5300\"", "the optional line is loopback only, never 0.0.0.0");
        text.Should().Contain("# - BMB_API_PUBLISHED_PORT=${BMB_API_PORT:-5300}");
        text.Should().NotContain("0.0.0.0:${BMB_API_PORT");
    }

    [Fact]
    public void EveryPortOfTheReverseProxyComposeFile_IsBoundToLoopback()
    {
        var mappings = ActiveLines(Read("docker-compose.reverse-proxy.yml"))
            .Where(l => PortMapping.IsMatch(l)).ToArray();

        mappings.Should().HaveCount(4);
        mappings.Should().OnlyContain(l => l.Contains("\"127.0.0.1:") || l.Contains("\"::1:"));
        ActiveLines(Read("docker-compose.reverse-proxy.yml")).Should().Contain(l => l.Contains("BMB_API_PUBLISHED_PORT=${BMB_API_PORT:-5300}"));
    }

    [Fact]
    public void TheEnvExample_DocumentsTheNewVariables()
    {
        var text = Read(".env.example");

        text.Should().Contain("BMB_WEB_BIND").And.Contain("BMB_MCP_URL").And.Contain("BMB_API_PORT");
    }

    [Fact]
    public void TheMacLocalNetworkText_SaysWhatTheMacAppNowDoes_AndNothingItDoesNot()
    {
        var script = Read("scripts", "pack-macos-full.sh");
        var text = Regex.Match(script, @"NSLocalNetworkUsageDescription</key>\s*<string>([^<]*)</string>").Groups[1].Value;

        text.Should().NotBeEmpty();
        // It browses for other nodes, announces itself only when the setting is switched on, and can be joined through the
        // Connect a device door (a phone or another computer): all three are true on the Mac since task P7.
        text.Should().Contain("other nodes").And.Contain("local network")
            .And.Contain("Devices on my network").And.Contain("join this Mac");
    }

    [Fact]
    public void TheMsiFirewallFeature_DoesNotPromiseAccessTheServiceDoesNotGive()
    {
        var wxs = Read("installers", "windows", "msi", "Package.wxs");
        var description = Regex.Match(wxs, @"<Feature Id=""FirewallFeature""[^>]*Description=""([^""]*)""", RegexOptions.Singleline).Groups[1].Value;

        description.Should().NotBeEmpty();
        description.Length.Should().BeLessThanOrEqualTo(255, "an MSI feature description holds 255 characters");
        description.Should().NotContain("allow other devices to connect");
        description.Should().Contain("only this computer by default").And.Contain("reverse proxy")
            .And.Contain("Devices on my network").And.Contain("Connect a device");
    }

    [Fact]
    public void TheInternetAccessGuide_DescribesTheSettingTheDoorAndTheOverride_WithNoMarkerLeft()
    {
        var text = Read("docs", "internet-access.md");

        text.Should().Contain("**off by default**").And.Contain("BMB_HTTPS_ENABLED=1");
        text.Should().Contain("Devices on my network").And.Contain("Connect a device").And.Contain("bmb join --code");
        text.Should().NotContain("TODO(P7)", "task P7 built what the marker announced");
        text.Replace("\r\n", "\n").Should().NotContain("already\ncovers the home network, phones and blind-node pairing");
    }

    [Fact]
    public void TheReadme_RestartsTheNssmServicesOneAtATime()
    {
        var readme = Read("README.md");

        readme.Should().NotContain("nssm restart BeeMemoryBankApi BeeMemoryBankWeb", "nssm restart takes one service name");
        readme.Should().Contain("nssm restart BeeMemoryBankApi");
    }

    private static readonly string[] GuestRoutes =
        ["/api/auth/remote-token", "/api/folders/accessible", "/api/folders/by-path/snapshot"];

    // The sentence RemoteAccountErrors.RoutesNotForwarded shows; the doc quotes it so a guest can search for it.
    private const string RoutesNotForwardedText =
        "The other node does not let guest sign-ins through. Its reverse proxy must forward /api/auth/remote-token, /api/folders/accessible and /api/folders/by-path/snapshot: see docs/internet-access.md";

    [Fact]
    public void EveryProxyRecipe_CarriesTheOptionalGuestRoutesBlock_WithExactlyTheThreeRoutes()
    {
        var guide = Read("docs", "internet-access.md").Replace("\r\n", "\n");

        // Caddy, nginx and Cloudflare Tunnel each have a block, marked optional, in front of the guest routes.
        guide.Should().Contain("**Optional: guest accounts for other people.**").And.Contain("**Optional: guest accounts for other people**");
        guide.Should().Contain("@guests path /api/auth/remote-token /api/folders/accessible /api/folders/by-path/snapshot");
        guide.Should().Contain("location ~ ^/api/(auth/remote-token|folders/accessible|folders/by-path/snapshot)$ {");
        guide.Should().Contain("path: '^/api/(auth/remote-token|folders/accessible|folders/by-path/snapshot)$'");
        Read("docs", "deployment.md").Should().Contain("ProxyPassMatch \"^/api/(auth/remote-token|folders/accessible|folders/by-path/snapshot)$\"");

        // The section says it is off unless added, why that matters for security, and what the guest sees.
        var section = guide[guide.IndexOf("## 4. Optional: guest accounts for other people", StringComparison.Ordinal)..];
        section.Should().Contain("off unless you add the block").And.Contain("rate-limits").And.Contain("user name and password");
        section.Should().Contain(RoutesNotForwardedText);
        foreach (var route in GuestRoutes) section.Should().Contain(route);
    }

    [Fact]
    public void TheReverseProxyComposeFile_ListsTheOptionalGuestRoutes_AsComments()
    {
        var text = Read("docker-compose.reverse-proxy.yml");

        foreach (var route in GuestRoutes) text.Should().Contain("#   " + route);
        text.Should().Contain("OPTIONAL, off unless you add it to the proxy");
        ActiveLines(text).Should().NotContain(l => GuestRoutes.Any(l.Contains), "the compose file forwards nothing itself; the proxy does");
    }

    [Fact]
    public void ThePublicSurfaceComment_SaysTheRecipesCarryTheGuestRoutesAsAnOptionalBlock()
    {
        var text = Read("libs", "BeeMemoryBank.Hosting.AspNetCore", "PublicSurface.cs").Replace("\r\n", "\n");

        text.Should().NotContain("and the shipped\n        // configurations do not forward these");
        text.Should().Contain("OPTIONAL \"guest accounts for other people\" block");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate repo root from " + AppContext.BaseDirectory);
    }
}
