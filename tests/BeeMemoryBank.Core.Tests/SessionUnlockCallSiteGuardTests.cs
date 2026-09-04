using System.Text.RegularExpressions;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Pins the set of files allowed to call <c>SessionService.UnlockAsync</c> / <c>UnlockWithDek</c>.
///
/// <para>
/// Both are process-wide: <c>SessionService.IsUnlocked</c> is ONE flag shared by the web UI and
/// every MCP agent on the node, so calling either opens the vault for everybody, not for the
/// caller. Reaching for <c>UnlockAsync</c> as a convenient "is this password correct?" check is
/// therefore not a stylistic slip — it is how a destructive endpoint once became a master-password
/// oracle that unlocked the whole vault as a side effect of merely being asked, even when the
/// operation it guarded then failed. <c>VerifyMasterPasswordAsync</c> exists for the checking case
/// and the re-authentication call sites were converted to it; nothing but this test stops the next
/// person from reaching for the unlock again.
/// </para>
///
/// <para>
/// Source scanning rather than reflection over IL, deliberately. Enumerating call sites through
/// reflection would mean loading every assembly that could hold one — including
/// BeeMemoryBank.Mobile, which targets net10.0-android and cannot be loaded by a desktop test host
/// at all — and would then need an IL reader to find the callee behind a <c>call</c> opcode. A
/// file:line list is also what the person who just went red actually needs to read.
/// </para>
///
/// <para>
/// Scope is the four shipped source roots. <c>tests/</c> is excluded because a test unlocking its
/// own throwaway vault is the point. <c>tools/</c> is excluded because those are one-shot developer
/// utilities that own their whole process (bmb-migrator and bmb-seedgen each unlock a vault nobody
/// else is sharing), and BeeMemoryBank.SearchBench has an unrelated <c>UnlockAsync</c> of its own on
/// an HTTP client, with no compile-time dependency on Core at all.
/// </para>
/// </summary>
public class SessionUnlockCallSiteGuardTests
{
    private static readonly string[] SearchRoots = ["libs", "server", "desktop", "mobile"];

    /// <summary>
    /// A dotted invocation of either method. The leading <c>\.</c> is what keeps the declarations in
    /// SessionService.cs, <c>&lt;see cref="UnlockAsync"/&gt;</c> references, and the similarly-named
    /// <c>TryAutoUnlockAsync</c> / <c>EnableAutoUnlockAsync</c> out of the results — in every one of
    /// those the character before <c>Unlock</c> is not a dot.
    /// </summary>
    private static readonly Regex UnlockCall = new(
        @"\.\s*Unlock(?:Async|WithDek)\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Files that are genuine entry points into an unlocked vault. Keyed by file, not by line: line
    /// numbers rot on every edit above the call site, and the question this list answers ("is this
    /// component allowed to unlock at all?") is a per-file one anyway.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedCallSites = new(StringComparer.OrdinalIgnoreCase)
    {
        ["libs/BeeMemoryBank.Core/Services/OsAutoUnlockService.cs"] =
            "OS auto-unlock: unwraps the master DEK from the DPAPI-protected os_auto_unlock slot and " +
            "verifies it against the sentinel before installing it. Unlocking IS the feature.",

        ["server/BeeMemoryBank.Api/Endpoints/SessionEndpoints.cs"] =
            "POST /api/session/unlock and /login — the human unlock. This is the endpoint the flag exists for.",

        ["server/BeeMemoryBank.Api/Endpoints/SnapshotEndpoints.cs"] =
            "POST /api/snapshots/restore — applying an encrypted snapshot needs the master DEK, and the " +
            "restore path locks again when it is done. Already unlocked? It re-authenticates with " +
            "VerifyMasterPasswordAsync instead.",

        ["server/BeeMemoryBank.Api/Middleware/AgentAuthMiddleware.cs"] =
            "Agent-token unlock via UnlockWithDek: an agent owned by a superadmin carries the master DEK " +
            "wrapped with its own API key (Agent.CanAutoUnlock). By design — the owner can already unlock " +
            "through the web UI. An ordinary user's agent has no key material and never reaches this.",

        ["server/BeeMemoryBank.Api/Services/RestoreInitiatorService.cs"] =
            "Network-wide restore, continue-without-backup path — same shape as SnapshotEndpoints: unlock " +
            "only when locked (the apply needs the DEK), VerifyMasterPasswordAsync when already open.",

        ["server/BeeMemoryBank.Cli/Commands/UnlockCommand.cs"] =
            "`bmb unlock` — unlocking IS the command.",

        ["server/BeeMemoryBank.Cli/Commands/AgentCommand.cs"] =
            "`bmb agent create` — wrapping the new agent's DEK requires the master DEK. The CLI runs as its " +
            "own short-lived process, so 'process-wide' means this one command.",

        ["server/BeeMemoryBank.Cli/Commands/ArticleCommand.cs"] =
            "`bmb article create/get/delete` — reading or writing article content, and signing the event, " +
            "require the master DEK. Same one-shot process as AgentCommand.",

        ["mobile/BeeMemoryBank.Mobile/Pages/UnlockPage.xaml.cs"] =
            "The Android app's unlock screen (typed password and fingerprint paths).",

        ["mobile/BeeMemoryBank.Mobile/Pages/SetupPage.xaml.cs"] =
            "First-run setup on Android: unlocks the vault it has just created.",

        ["mobile/BeeMemoryBank.Mobile/Platforms/Android/MainActivity.cs"] =
            "Android auto-unlock on resume, from the password held in the platform keystore.",
    };

    [Fact]
    public void OnlyKnownEntryPointsMayUnlockTheSharedSession()
    {
        var repoRoot = FindRepoRoot();
        var found = new SortedDictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in EnumerateSourceFiles(repoRoot))
        {
            var text = File.ReadAllText(file);
            var matches = UnlockCall.Matches(text);
            if (matches.Count == 0) continue;

            var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            found[relative] = matches.Select(m => LineNumberAt(text, m.Index)).ToList();
        }

        var unexpected = found.Keys.Where(f => !AllowedCallSites.ContainsKey(f)).ToList();
        var stale = AllowedCallSites.Keys.Where(f => !found.ContainsKey(f)).OrderBy(f => f).ToList();

        var failure = new List<string>();

        if (unexpected.Count > 0)
        {
            failure.Add(
                "These files call SessionService.UnlockAsync / UnlockWithDek and are not on the allow-list:\n" +
                string.Join("\n", unexpected.Select(f => $"  - {f} (line{(found[f].Count > 1 ? "s" : "")} {string.Join(", ", found[f])})")) +
                "\n\nBoth methods unlock the vault for the ENTIRE PROCESS — the web UI and every MCP agent " +
                "share one SessionService.IsUnlocked flag. If what you actually need is to check that a " +
                "password is correct (re-authentication before a destructive operation, for example), call " +
                "SessionService.VerifyMasterPasswordAsync instead: same key-slot policy, same " +
                "wrong-password-and-unauthorised-slot-are-indistinguishable property, no side effect.\n" +
                "If unlocking really is the intent, add the file to AllowedCallSites in " +
                $"{nameof(SessionUnlockCallSiteGuardTests)} with a one-line justification saying why this " +
                "component is allowed to open the vault for everyone.");
        }

        if (stale.Count > 0)
        {
            failure.Add(
                "These files are on the allow-list but no longer call either method — delete the entries so " +
                "the list keeps meaning something:\n" +
                string.Join("\n", stale.Select(f => $"  - {f}")));
        }

        Assert.True(failure.Count == 0, string.Join("\n\n", failure));
    }

    private static IEnumerable<string> EnumerateSourceFiles(string repoRoot)
    {
        foreach (var root in SearchRoots)
        {
            var path = Path.Combine(repoRoot, root);
            if (!Directory.Exists(path)) continue;

            foreach (var file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                var sep = Path.DirectorySeparatorChar;
                if (file.Contains($"{sep}bin{sep}") || file.Contains($"{sep}obj{sep}")) continue;
                yield return file;
            }
        }
    }

    private static int LineNumberAt(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException($"Could not locate the repo root from {AppContext.BaseDirectory}");
    }
}
