using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// The peer-authentication challenge is signed over a domain tag: V2 binds the audience node's id
/// into the signed bytes, V1 did not. V1 is gone from both ends — the client never produces it and
/// <c>/api/sync/authenticate</c> never accepts it — because "which version do you speak" was
/// decided by the RESPONDING peer, so an attacker could downgrade the handshake by omitting one
/// JSON field and then relay the unbound signature to the node the challenge really came from.
///
/// <para>This guard exists because removing V1 from <c>PeerAuthenticator</c> and
/// <c>SyncEndpoints</c> was NOT enough: three other components signed the same challenge with
/// their own hand-written copy of the domain tag — <c>SnapshotJoinClient</c> (mobile mesh join),
/// <c>InitEndpoints</c> (<c>/api/init/join</c>), and <c>RestoreInitiatorService</c> (fetching a
/// snapshot from a peer). Every one of them kept signing V1 and started failing with 401 against
/// upgraded nodes, and the whole test suite stayed green: the integration tests build their join
/// requests by hand rather than driving those code paths, so nothing exercised them.</para>
///
/// <para>A source scan is a blunt instrument, but it is the one that would have caught this. Real
/// end-to-end coverage of the three flows is the better fix and is still missing.</para>
/// </summary>
public class ChallengeSignatureVersionGuardTests
{
    [Fact]
    public void NoProductionCodeSignsTheUnboundV1Challenge()
    {
        var repoRoot = FindRepoRoot();
        var searchRoots = new[] { "libs", "server", "desktop", "mobile" }
            .Select(d => Path.Combine(repoRoot, d))
            .Where(Directory.Exists);

        // The literal only ever appears as a u8 domain tag being built for signing or verifying.
        // Comments explaining the history are fine and are stripped below.
        var v1Tag = new Regex("\"BMB-CHALLENGE-V1", RegexOptions.Compiled);

        var offenders = new List<string>();
        var filesScanned = 0;

        foreach (var root in searchRoots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;

                filesScanned++;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    // Skip comment lines: the V1 story is documented in several places on purpose,
                    // and that prose is exactly what stops someone reintroducing the fallback.
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("///") || trimmed.StartsWith("*"))
                        continue;

                    if (v1Tag.IsMatch(line))
                        offenders.Add($"{Path.GetRelativePath(repoRoot, file)}:{i + 1}");
                }
            }
        }

        filesScanned.Should().BeGreaterThan(100,
            "the scan must actually be walking the production source tree");

        offenders.Should().BeEmpty(
            "every peer handshake must sign the audience-bound BMB-CHALLENGE-V2 payload. " +
            "/api/sync/authenticate verifies V2 only, so a V1 signer does not fall back — it 401s, " +
            "and the flow it belongs to simply stops working");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            "Could not locate the repository root (no BeeMemoryBank.slnx above the test binary).");
    }
}
