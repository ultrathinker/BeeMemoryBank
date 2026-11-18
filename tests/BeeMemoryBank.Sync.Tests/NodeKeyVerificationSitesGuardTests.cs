using System.Text.RegularExpressions;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Plan WS-D D3: every signature of a node is checked by <see cref="NodeEventVerifier"/>, which knows the key history. A raw
/// <c>Ed25519Signer.Verify</c> anywhere else in the product would accept or refuse a node's signature by a rule of its own; the few that remain
/// check something that is not a node's signature, and are pinned here with the reason.
/// </summary>
public class NodeKeyVerificationSitesGuardTests
{
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        // The helper itself and the transition statement it rests on.
        ["libs/BeeMemoryBank.Sync/NodeEventVerifier.cs"] = "the one helper",
        ["libs/BeeMemoryBank.Core/Models/NodeKeyTransition.cs"] = "the proofs of a key link, which the helper and the chain walk use",
        // Release manifests: signed by the release key, not by a node.
        ["server/BeeMemoryBank.Api/Services/UpdateService.cs"] = "release manifest",
        ["tools/BeeMemoryBank.Updater/Program.cs"] = "release manifest",
        ["tools/BeeMemoryBank.ReleaseTool/Program.cs"] = "release manifest",
        // A seed checked against its own public key (sign then verify a probe): a self-consistency check, not a peer's signature.
        ["libs/BeeMemoryBank.Sync/FileNodeKey.cs"] = "own seed probe",
        ["server/BeeMemoryBank.Api/Services/Recovery/RecoveryRestoreService.cs"] = "own seed probe",
        // An invitation carries the hub key it was signed with (self-certifying); the redeemer's identity probe then follows the transition
        // chain (plan D4).
        ["libs/BeeMemoryBank.Core/Models/BlindInvitation.cs"] = "invitation, self-certifying",
        ["libs/BeeMemoryBank.Core/Models/BlindInviteWire.cs"] = "invitation, self-certifying",
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }

    [Fact]
    public void No_product_code_checks_a_node_signature_except_through_the_helper()
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        foreach (var top in new[] { "libs", "server", "desktop", "mobile", "tools" })
        {
            var dir = Path.Combine(root, top);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/bin/") || relative.Contains("/obj/")) continue;
                if (!Regex.IsMatch(File.ReadAllText(file), @"Ed25519Signer\.Verify\(")) continue;
                if (!Allowed.ContainsKey(relative)) offenders.Add(relative);
            }
        }
        offenders.Should().BeEmpty("a node's signature is checked by NodeEventVerifier (plan WS-D D3), which knows its retired keys");
    }
}
