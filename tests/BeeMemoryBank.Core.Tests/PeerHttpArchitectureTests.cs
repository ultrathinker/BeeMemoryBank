using System.Text.RegularExpressions;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// BMB-307 fix round 3 (G5): an HTTP client that dials a peer, or the other computer of a join, opens its sockets through <c>PeerSocketConnector</c> (the name is resolved
/// there, every answer must be an address a node may call, and only those are connected to) and uses no proxy. The connector guarded only the scheduler and the hub probe,
/// while the join clients (the one for a typed address, the one pinned by a code, the named no-redirect client) were built with ordinary handlers. This test reads the
/// sources and fails for any handler that is built without the connector unless the place is listed here WITH the reason it never dials a peer: a new client gets the
/// connector or a reason, never silence.
/// </summary>
public class PeerHttpArchitectureTests
{
    private static readonly string[] Roots = ["libs", "server", "desktop", "mobile"];

    private static readonly Regex Construction = new(@"\bnew\s+(?:System\.Net\.Http\.)?(?:SocketsHttpHandler|HttpClientHandler)\b", RegexOptions.Compiled);

    private sealed record Allowed(string File, string Reason);

    /// <summary>Handlers that are built without the connector, and why that is safe: the client dials this node's own front, or a fixed address no peer can name.</summary>
    private static readonly Allowed[] NotAPeer =
    [
        new("server/BeeMemoryBank.Api/Endpoints/ChatEndpoints.Attachments.cs",
            "the chat's image fetch: it has its own guard against internal addresses (the ImageFetchClient of the chat) and is no peer"),
        new("server/BeeMemoryBank.Api/Services/BlindBackup/S3RecoverySetWriter.cs",
            "an S3 endpoint the owner configured for the backup of this node; it is no peer and no join"),
        new("server/BeeMemoryBank.Api/Startup/ApiServices.cs",
            "the OpenRouter client: pinned to https://openrouter.ai and refusing redirects; it is no peer"),
        new("server/BeeMemoryBank.BlindConsole/ApiProxy.cs",
            "the blind console talks to its own node, on loopback; the address is the node's own front"),
        new("server/BeeMemoryBank.Cli/Commands/BlindApi.cs",
            "the command line talks to a blind node's own console API that the person named; it is no peer of the mesh and carries no join"),
        new("desktop/BeeMemoryBank.Node/Front/NodeAlarmsControl.cs",
            "the node's own front, on loopback (the alarms the tray shows)"),
        new("desktop/BeeMemoryBank.Node/Front/NodeLockControl.cs",
            "the node's own front, on loopback (lock and unlock from the tray)"),
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) dir = dir.Parent;
        dir.Should().NotBeNull("the tests run from inside the repository");
        return dir!.FullName;
    }

    private static IEnumerable<string> Sources(string root)
    {
        foreach (var top in Roots)
        {
            var folder = Path.Combine(root, top);
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/obj/") || relative.Contains("/bin/")) continue;
                yield return relative;
            }
        }
    }

    private static string Code(string root, string relative) =>
        string.Join('\n', File.ReadAllText(Path.Combine(root, relative)).Split('\n').Select(l => { var i = l.IndexOf("//", StringComparison.Ordinal); return i >= 0 && !l.Contains("http", StringComparison.Ordinal) ? l[..i] : l; }));

    [Fact]
    public void EveryHandlerThatMayDialAPeerOrTheOtherComputerOfAJoinHasTheCheckedConnect()
    {
        var root = RepoRoot();
        var building = Sources(root).Where(r => Construction.IsMatch(Code(root, r))).ToList();

        var withoutConnector = building
            .Where(r => !Code(root, r).Contains("PeerSocketConnector.ConnectAsync", StringComparison.Ordinal))
            .Where(r => !NotAPeer.Any(a => a.File == r))
            .ToList();
        withoutConnector.Should().BeEmpty(
            "a handler that dials a peer opens its sockets through PeerSocketConnector (PeerHttp.NoRedirectHandler, SpkiPin.CreatePinnedHandler, JoinHttp.CreateClient); "
            + "a client that cannot dial a peer is listed in NotAPeer with the reason");

        var stale = NotAPeer.Where(a => !building.Contains(a.File)).Select(a => a.File).ToList();
        stale.Should().BeEmpty("an allowed place that builds no handler any more is removed from the list");
        NotAPeer.Should().OnlyContain(a => a.Reason.Length > 30, "every allowed place says why");
    }

    [Fact]
    public void TheJoinClientsAndTheNamedNoRedirectClientAreMadeByTheCheckedFactories()
    {
        var root = RepoRoot();
        string Read(string relative) => File.ReadAllText(Path.Combine(root, relative));

        Code(root, "libs/BeeMemoryBank.Crypto/JoinHttp.cs").Should().NotContain("new SocketsHttpHandler", "the join client takes its handler from PeerHttp / SpkiPin, which have the connect");
        Read("libs/BeeMemoryBank.Crypto/JoinHttp.cs").Should().Contain("PeerHttp.NoRedirectHandler(network)").And.Contain("SpkiPin.CreatePinnedHandler(spkiPin, network)");
        var pinned = Read("libs/BeeMemoryBank.Crypto/SpkiPin.cs");
        pinned.Should().Contain("PeerSocketConnector.ConnectAsync").And.Contain("UseProxy = false");
        var peerHttp = Read("libs/BeeMemoryBank.Crypto/PeerNetwork.cs");
        peerHttp.Should().Contain("PeerSocketConnector.ConnectAsync").And.Contain("UseProxy = false").And.Contain("AllowAutoRedirect = false");

        foreach (var host in new[] { "server/BeeMemoryBank.Api/Startup/ApiServices.cs", "server/BeeMemoryBank.BlindNode/Startup/BlindNodeServices.cs" })
        {
            var text = Read(host);
            var registration = text[text.IndexOf("NoRedirectClientName", StringComparison.Ordinal)..];
            registration[..Math.Min(registration.Length, 260)].Should().Contain("PeerHttp.NoRedirectHandler()", host + ": the named no-redirect client has the checked connect");
        }
    }
}
