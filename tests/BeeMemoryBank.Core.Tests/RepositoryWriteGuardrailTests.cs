using System.Reflection;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Fails when a type outside <c>BeeMemoryBank.Core</c> calls a repository write method that is not
/// on <see cref="AllowList"/>.
///
/// <para>
/// MCP write tools used to inject repositories directly and write through them, bypassing the
/// services that log sync events — that produced a folder that deleted only locally and never on
/// any peer, and tags that never propagated. Both call sites are fixed, but nothing stopped it
/// happening again: "the event log sees every write" was a paragraph in AGENTS.md, a convention
/// someone has to remember. This test makes it a build failure instead.
/// </para>
///
/// <para>
/// Deliberately NOT <c>internal</c> + <c>InternalsVisibleTo</c>: there are ~80-plus existing direct
/// call sites across server/desktop/mobile, and deciding which of those legitimately predate any
/// service (bootstrap, node-local tables that are never synced, the sync wire protocol itself) from
/// which are the next version of the folder/tag bug is judgement work, not something a mechanical
/// compiler visibility sweep can decide unattended. <see cref="AllowList"/> is that judgement,
/// recorded once, in a place a reviewer can see change with every PR.
/// </para>
///
/// <para>
/// <b>Source scan, not reflection, for the call sites.</b> Reflection only sees IL in a method body;
/// telling "calls <c>_articleRepo.CreateAsync(...)</c>" from "calls
/// <c>_articleService.CreateAsync(...)</c>" from IL means resolving every <c>callvirt</c> target
/// against the interfaces below anyway, which is just a source-level answer wearing an IL-level
/// costume — and unlike a source scan, it can't point a failure message at a file and a line.
/// Reflection is still used for the OTHER half: which methods on those interfaces actually count as
/// a write. Hardcoding that list would silently go stale the moment a repository gained or renamed a
/// method (a sibling task is doing exactly that to ArticleRepository/FolderRepository as this test is
/// being written) — so instead this test reads the CURRENT shape of every <c>I*Repository</c>
/// interface off the compiled <c>BeeMemoryBank.Core</c> assembly and classifies each method by name:
/// <see cref="ReadPrefixes"/> is a short, deliberately conservative list of prefixes that are never a
/// write (<c>Get</c>, <c>List</c>, <c>Search</c>, <c>Count</c>, <c>Exists</c>, <c>Is</c>); everything
/// else defaults to "write". Defaulting to write means the occasional false positive — a check like
/// <c>ThrowIfWriteDenied</c> or a cache invalidation like <c>InvalidateVectorCache</c> would need an
/// AllowList entry if ever called from outside Core — but it never lets a real write through
/// unnoticed by starting from a wrong guess about a new method's name.
/// </para>
///
/// <para>
/// <b>What this test does NOT catch.</b> The allow-list is granted at (file, interface, method)
/// granularity, not per call site. Two entries below (see the comments on
/// <c>WhitelistEndpoints.cs</c> and <c>JoinEndpoints.cs</c>) are call sites that follow the same
/// "mutate, but publish the mesh event yourself first" discipline as their siblings in the same file
/// — except that these two specific ones do NOT publish the event, which is precisely the shape of
/// bug this test exists to prevent. They are allow-listed anyway (this test asserts access is
/// legitimate, not that every call site is bug-free — verifying "was the event actually published
/// three lines up" is a dataflow question no source-line scan answers honestly) and flagged instead
/// in the task report that added this test. A human still has to read that.
/// </para>
/// </summary>
public class RepositoryWriteGuardrailTests
{
    private static readonly string[] ScanRoots = ["server", "desktop", "mobile"];

    /// <summary>Method-name prefixes that are never a write. Everything else defaults to "write" — see the class doc comment for why that default is the safe direction.</summary>
    private static readonly string[] ReadPrefixes = ["Get", "List", "Search", "Count", "Exists", "Is"];

    private sealed record AllowedCall(string File, string Interface, string Method, string Reason);

    // Shared reasons for entries whose justification is identical across many call sites, so the
    // list below reads as "what" without repeating "why" fifty times.
    private const string AuditLogReason =
        "IAuditLogRepository is the local admin-activity audit trail (no LamportTs/SourceNodeId on " +
        "the row, DeleteOlderThanAsync is its only other write) — it is not the sync event log this " +
        "guardrail protects, and there is no AuditLogService to route it through.";

    private const string BootstrapReason =
        "Node bootstrap (standalone init or join): runs before any user, session or service exists, " +
        "so there is nothing yet to route the write through. Mirrors the equivalent server endpoint.";

    private const string NodeLocalSettingReason =
        "Node-local admin setting on tbl_node_identity — the interface's own doc comment says it is " +
        "never synced, so there is no sync event for a service to log.";

    /// <summary>
    /// Every (file, interface, method) triple that is known to call a repository write method
    /// directly from outside <c>BeeMemoryBank.Core</c>, with the one-line reason it is legitimate.
    /// A new triple that shows up here without a reason is exactly the failure this test is for:
    /// route the write through the service that logs the event, or add it here with a reason a
    /// reviewer can push back on.
    /// </summary>
    private static readonly AllowedCall[] AllowList =
    [
        // ── Node bootstrap: server/BeeMemoryBank.Api/Endpoints/InitEndpoints.cs (standalone) and
        // JoinEndpoints.cs (join an existing network), plus mobile's own copy of the same two flows
        // for a phone acting as its own node. All of it runs before InitializationService says the
        // node is initialized — there is no session, no unlocked vault, nothing to log an event
        // against yet.
        new("server/BeeMemoryBank.Api/Endpoints/InitEndpoints.cs", "INodeIdentityRepository", "CreateAsync", BootstrapReason),
        new("server/BeeMemoryBank.Api/Endpoints/InitEndpoints.cs", "IKeySlotRepository", "CreateAsync", BootstrapReason),
        new("server/BeeMemoryBank.Api/Endpoints/InitEndpoints.cs", "IUserRepository", "CreateAsync", BootstrapReason),
        new("server/BeeMemoryBank.Api/Endpoints/InitEndpoints.cs", "INodeIdentityRepository", "StoreSentinelAsync", BootstrapReason),
        new("server/BeeMemoryBank.Api/Endpoints/InitEndpoints.cs", "IWhitelistRepository", "CreateAsync", BootstrapReason),
        new("server/BeeMemoryBank.Api/Endpoints/InitEndpoints.cs", "ISyncPositionRepository", "UpsertAsync", BootstrapReason),
        new("server/BeeMemoryBank.Api/Endpoints/InitEndpoints.cs", "INodeIdentityRepository", "MarkInitialSyncCompletedAsync", BootstrapReason),

        new("mobile/BeeMemoryBank.Mobile/Services/NodeSetupService.cs", "INodeIdentityRepository", "CreateAsync", BootstrapReason),
        new("mobile/BeeMemoryBank.Mobile/Services/NodeSetupService.cs", "IKeySlotRepository", "CreateAsync", BootstrapReason),
        new("mobile/BeeMemoryBank.Mobile/Services/NodeSetupService.cs", "IUserRepository", "CreateAsync", BootstrapReason),
        new("mobile/BeeMemoryBank.Mobile/Services/NodeSetupService.cs", "INodeIdentityRepository", "StoreSentinelAsync", BootstrapReason),
        new("mobile/BeeMemoryBank.Mobile/Services/NodeSetupService.cs", "IWhitelistRepository", "CreateAsync", BootstrapReason),
        new("mobile/BeeMemoryBank.Mobile/Services/NodeSetupService.cs", "ISyncPositionRepository", "UpsertAsync", BootstrapReason),
        new("mobile/BeeMemoryBank.Mobile/Services/NodeSetupService.cs", "INodeIdentityRepository", "MarkInitialSyncCompletedAsync", BootstrapReason),

        // /api/join is reached AFTER the joining node already exists, by the node it is joining —
        // that side is past bootstrap, so it earns its allow-list entries individually below rather
        // than sharing BootstrapReason.
        new("server/BeeMemoryBank.Api/Endpoints/JoinEndpoints.cs", "IWhitelistRepository", "CreateAsync",
            "New peer: logs eventLogger.LogWhitelistAddAsync FIRST and stamps the returned version onto the row before creating it, exactly the discipline this guardrail wants."),
        new("server/BeeMemoryBank.Api/Endpoints/JoinEndpoints.cs", "IWhitelistRepository", "UpdateAsync",
            "Re-join with the same key: updates DisplayName/ApiAddress WITHOUT publishing a whitelist_update event first, unlike the sibling create-branch three lines below and unlike WhitelistEndpoints' own /address handler. " +
            "SUSPICIOUS — flagged in the night-13b task report, not fixed here: a re-join that changes display name or address looks like it never reaches other peers."),

        // ── Node-local settings: tbl_node_identity holds this node's own configuration, and every
        // one of these is documented on INodeIdentityRepository as never synced (each node brands,
        // times out and toggles itself independently) — there is no sync event to log.
        new("server/BeeMemoryBank.Api/Endpoints/BrandingEndpoints.cs", "INodeIdentityRepository", "SetBrandNameAsync", NodeLocalSettingReason),
        new("server/BeeMemoryBank.Api/Endpoints/KeyEndpoints.cs", "INodeIdentityRepository", "ClearMasterPasswordNoticeAsync", NodeLocalSettingReason),
        new("server/BeeMemoryBank.Api/Endpoints/KeyEndpoints.cs", "INodeIdentityRepository", "SetMasterPasswordChangedLocallyAtAsync", NodeLocalSettingReason),
        new("server/BeeMemoryBank.Api/Endpoints/SearchMetricsEndpoints.cs", "INodeIdentityRepository", "SetCanGenerateEmbeddingsAsync", NodeLocalSettingReason),
        new("server/BeeMemoryBank.Api/Endpoints/SessionEndpoints.cs", "INodeIdentityRepository", "SetSessionSettingsAsync", NodeLocalSettingReason),

        // ── Agents: node-local identity, "created, authenticated, and revoked per-node. They are
        // never synchronized to other nodes." (file header of AgentEndpoints.cs). No AgentService
        // exists — the endpoint IS the only writer, by design.
        new("server/BeeMemoryBank.Api/Endpoints/AgentEndpoints.cs", "IAgentRepository", "CreateAsync",
            "Agents are a node-local identity, never synchronized (file header) — no AgentService exists."),
        new("server/BeeMemoryBank.Api/Endpoints/AgentEndpoints.cs", "IAgentRepository", "DeleteAsync",
            "Agents are a node-local identity, never synchronized (file header) — no AgentService exists."),
        new("server/BeeMemoryBank.Api/Middleware/AgentAuthMiddleware.cs", "IAgentRepository", "UpdateAccessAsync",
            "Bumps last-accessed/request-count on every authenticated request; explicitly non-critical (wrapped in a try/catch that swallows failure) local telemetry, not vault content."),

        // ── Remote API tokens: a node-local credential record for cross-instance polling
        // (RemoteAuthEndpoints.cs doc comment). The token itself never leaves this node's database;
        // what leaves is the opaque bearer value handed to the caller once.
        new("server/BeeMemoryBank.Api/Endpoints/RemoteAuthEndpoints.cs", "IRemoteApiTokenRepository", "CreateAsync",
            "Issues a node-local credential record for cross-instance polling; not synced vault content."),
        new("server/BeeMemoryBank.Api/Middleware/AgentAuthMiddleware.cs", "IRemoteApiTokenRepository", "TouchAsync",
            "Sliding-expiry bump on successful auth; explicitly non-critical (wrapped in a try/catch that swallows failure) local telemetry."),

        // ── Favorites: "Node-local, like the users they belong to — nothing here is synced to other
        // nodes." (file header of FavoriteEndpoints.cs).
        new("server/BeeMemoryBank.Api/Endpoints/FavoriteEndpoints.cs", "IFavoriteRepository", "AddAsync", "Favorites are node-local (file header) — never synced."),
        new("server/BeeMemoryBank.Api/Endpoints/FavoriteEndpoints.cs", "IFavoriteRepository", "RemoveAsync", "Favorites are node-local (file header) — never synced."),
        new("server/BeeMemoryBank.Api/Endpoints/FavoriteEndpoints.cs", "IFavoriteRepository", "SetSortOrdersAsync", "Favorites are node-local (file header) — never synced."),
        new("server/BeeMemoryBank.Api/Endpoints/FavoriteEndpoints.cs", "IFavoriteRepository", "ClearSortOrdersAsync", "Favorites are node-local (file header) — never synced."),

        // ── Folder ACL: "ACL entries are node-local: they live only on this node and are not
        // propagated via sync." (file header of RestrictionEndpoints.cs, docs/architecture.md → Node
        // Topology). Role-scoped ACL rules on the same page go through RoleService instead — only
        // the per-USER rules are written directly here.
        new("server/BeeMemoryBank.Api/Endpoints/RestrictionEndpoints.cs", "IFolderAclRepository", "AddAsync", "Folder ACL rows are node-local, not propagated via sync (file header)."),
        new("server/BeeMemoryBank.Api/Endpoints/RestrictionEndpoints.cs", "IFolderAclRepository", "SetReadOnlyAsync", "Folder ACL rows are node-local, not propagated via sync (file header)."),
        new("server/BeeMemoryBank.Api/Endpoints/RestrictionEndpoints.cs", "IFolderAclRepository", "RemoveByUserAndFolderAsync", "Folder ACL rows are node-local, not propagated via sync (file header)."),

        // ── DEK rotation and restore: both are LOCAL progress trackers for an operation whose actual
        // commit/checkpoint event is logged elsewhere (DekRotationService / RestoreService). Neither
        // row carries a LamportTs/SourceNodeId — they record what THIS node has seen and done so far,
        // not synced content.
        new("server/BeeMemoryBank.Api/Endpoints/DekRotationEndpoints.cs", "IDekRotationStateRepository", "UpdateStateAsync",
            "tbl_dek_rotation_state is this node's local progress tracker for an in-flight rotation, not synced content — the rotation commit itself is a separate, already-logged sync event."),
        new("server/BeeMemoryBank.Api/Services/RestoreInitiatorService.cs", "IRestoreEventStateRepository", "UpdateStateAsync",
            "RestoreEventStateRow carries no LamportTs/SourceNodeId — it is this node's own progress tracker for an already-logged restore_network event, not synced content."),

        // ── Concept-tag graph maintenance: a superadmin-only rebuild of a DERIVED cache (edges
        // computed from existing tag co-occurrence), not a content write — there is nothing for a
        // service to log because nothing new is being told to peers.
        new("server/BeeMemoryBank.Api/Endpoints/AdminEndpoints.cs", "IConceptTagRepository", "CheckAndRebuildEdgesAsync",
            "Rebuilds a derived cache (concept-tag graph edges) from data already present; not a content write with anything new to tell peers."),

        // ── The sync wire protocol's OWN endpoints, hosted under /api/sync/* in the Api project.
        // These calls ARE the mechanism the rest of this guardrail exists to protect — bookkeeping
        // for the protocol itself, not application content.
        new("server/BeeMemoryBank.Api/Endpoints/SyncEndpoints.cs", "ISyncPushPositionRepository", "UpdatePositionAsync",
            "Records how far a peer has pulled from this node — sync protocol bookkeeping, not content."),
        new("server/BeeMemoryBank.Api/Endpoints/SyncEndpoints.cs", "IBlobRepository", "StoreAsync",
            "Stores content-addressed blob bytes pushed by a peer during sync — this endpoint is the receiving side of the sync protocol itself."),
        new("server/BeeMemoryBank.Api/Endpoints/SnapshotEndpoints.cs", "IEventLogRepository", "AppendAsync",
            "Publishes a restore_network broadcast event: there is no LogRestoreNetworkAsync wrapper on IEventLogger for this rare event type, so the endpoint ticks the Lamport clock, signs and appends the SyncEvent itself — the same sequence IEventLogger's other Log*Async methods perform internally. This IS the event-logging path for this event type, not a bypass of one."),

        // ── Whitelist: WhitelistEndpoints owns whitelist mutations directly (no WhitelistService
        // exists); the expected discipline is that each handler publishes the corresponding mesh
        // event itself via IEventLogger before writing the row (see /superadmin and /address below,
        // which both do). ONE handler in this file does not — see the SUSPICIOUS note.
        new("server/BeeMemoryBank.Api/Endpoints/WhitelistEndpoints.cs", "IWhitelistRepository", "UpdateAsync",
            "WhitelistEndpoints owns whitelist mutations directly and is expected to publish the mesh event itself first (see the /superadmin and /address handlers, which do). " +
            "SUSPICIOUS — flagged in the night-13b task report, not fixed here: the plain PUT /{nodeId} handler (editing DisplayName/ApiAddress/CanGenerateEmbeddings) updates the row with NO preceding eventLogger call at all, unlike its siblings in this same file."),
        new("server/BeeMemoryBank.Api/Endpoints/WhitelistEndpoints.cs", "IWhitelistRepository", "SetAutoAcceptRestoreAsync",
            "Per-peer local preference (whether THIS node auto-accepts restores from that peer) — not mesh state, so there is nothing to publish."),
        new("server/BeeMemoryBank.Api/Endpoints/WhitelistEndpoints.cs", "IWhitelistRepository", "SetAutoAcceptDekRotationAsync",
            "Per-peer local preference (whether THIS node auto-accepts DEK rotations from that peer) — not mesh state, so there is nothing to publish."),
        new("server/BeeMemoryBank.Api/Endpoints/WhitelistEndpoints.cs", "IWhitelistRepository", "RevokeAsync",
            "Publishes eventLogger.LogWhitelistRevokeAsync FIRST and stamps the returned version before revoking, exactly the discipline this guardrail wants."),

        // ── Local admin-activity audit trail. Same reason everywhere it appears; see AuditLogReason.
        new("server/BeeMemoryBank.Api/Endpoints/AgentEndpoints.cs", "IAuditLogRepository", "LogAsync", AuditLogReason),
        new("server/BeeMemoryBank.Api/Endpoints/BrandingEndpoints.cs", "IAuditLogRepository", "LogAsync", AuditLogReason),
        new("server/BeeMemoryBank.Api/Endpoints/DekRotationEndpoints.cs", "IAuditLogRepository", "LogAsync", AuditLogReason),
        new("server/BeeMemoryBank.Api/Endpoints/RestrictionEndpoints.cs", "IAuditLogRepository", "LogAsync", AuditLogReason),
        new("server/BeeMemoryBank.Api/Endpoints/RoleEndpoints.cs", "IAuditLogRepository", "LogAsync", AuditLogReason),
        new("server/BeeMemoryBank.Api/Endpoints/SnapshotEndpoints.cs", "IAuditLogRepository", "LogAsync", AuditLogReason),
        new("server/BeeMemoryBank.Api/Endpoints/UserEndpoints.cs", "IAuditLogRepository", "LogAsync", AuditLogReason),
        new("server/BeeMemoryBank.Api/Endpoints/WhitelistEndpoints.cs", "IAuditLogRepository", "LogAsync", AuditLogReason),
    ];

    [Fact]
    public void RepositoryWriteMethods_AreOnlyCalledFromCoreOrTheAllowList()
    {
        var repoRoot = FindRepoRoot();
        var writeMethodsByInterface = DiscoverWriteMethodsByInterface();

        writeMethodsByInterface.Should().NotBeEmpty(
            "reflection must actually find I*Repository interfaces on the Core assembly — an empty " +
            "result would make every call site below look clean by accident");

        var allowedLookup = AllowList.ToLookup(a => (a.File, a.Interface, a.Method));

        // Longest-first purely defensively: none of today's interface names is a prefix of another,
        // but nothing guarantees that stays true forever, and alternation order would matter if it stopped.
        var declRegex = new Regex(
            @"\b(" + string.Join("|", writeMethodsByInterface.Keys.OrderByDescending(n => n.Length).Select(Regex.Escape)) + @")\??\s+([A-Za-z_][A-Za-z0-9_]*)\b");

        var failures = new List<string>();
        var filesScanned = 0;
        var declarationsFound = 0;

        foreach (var root in ScanRoots)
        {
            var dir = Path.Combine(repoRoot, root);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var normalized = file.Replace('\\', '/');
                if (normalized.Contains("/bin/") || normalized.Contains("/obj/")) continue;

                filesScanned++;
                var text = File.ReadAllText(file);
                var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');

                // Every repository-typed field/parameter/local declared in this file, by variable
                // name — covers constructor injection (fields and primary-constructor parameters)
                // and minimal-API handler parameters alike, since both are "Type name" text.
                var varToInterfaces = new Dictionary<string, HashSet<string>>();
                foreach (Match m in declRegex.Matches(text))
                {
                    var set = varToInterfaces.TryGetValue(m.Groups[2].Value, out var existing)
                        ? existing
                        : varToInterfaces[m.Groups[2].Value] = [];
                    set.Add(m.Groups[1].Value);
                }
                if (varToInterfaces.Count == 0) continue;
                declarationsFound += varToInterfaces.Count;

                foreach (var (varName, ifaces) in varToInterfaces)
                {
                    var methodToIfaces = new Dictionary<string, HashSet<string>>();
                    foreach (var iface in ifaces)
                    {
                        if (!writeMethodsByInterface.TryGetValue(iface, out var writeMethods)) continue;
                        foreach (var method in writeMethods)
                        {
                            var set = methodToIfaces.TryGetValue(method, out var existing) ? existing : methodToIfaces[method] = [];
                            set.Add(iface);
                        }
                    }
                    if (methodToIfaces.Count == 0) continue;

                    var callRegex = new Regex(
                        @"\b" + Regex.Escape(varName) + @"\s*\.\s*(" +
                        string.Join("|", methodToIfaces.Keys.Select(Regex.Escape)) + @")\s*\(");

                    foreach (Match cm in callRegex.Matches(text))
                    {
                        var method = cm.Groups[1].Value;
                        var candidateIfaces = methodToIfaces[method];
                        if (candidateIfaces.Any(iface => allowedLookup[(relative, iface, method)].Any()))
                            continue;

                        var lineNo = CountLines(text, cm.Index);
                        var snippet = LineAt(text, lineNo).Trim();
                        var ifaceLabel = string.Join(" or ", candidateIfaces.OrderBy(x => x, StringComparer.Ordinal));

                        failures.Add(
                            $"{relative}:{lineNo}: `{snippet}` — {ifaceLabel}.{method}(...) is a repository " +
                            "write called directly from outside BeeMemoryBank.Core. Route it through the " +
                            "service that logs the sync event, or — if this call site genuinely has no event " +
                            $"to log — add (\"{relative}\", \"{ifaceLabel}\", \"{method}\") to AllowList in " +
                            "RepositoryWriteGuardrailTests.cs with a one-line reason.");
                    }
                }
            }
        }

        filesScanned.Should().BeGreaterThan(50,
            "the scanner must actually be reading source files under server/desktop/mobile — an " +
            "empty result would look like a pass forever");
        declarationsFound.Should().BeGreaterThan(10,
            "the scanner must actually be finding repository-typed declarations in those files");

        failures.Should().BeEmpty(
            $"every direct repository write from outside BeeMemoryBank.Core must be justified in " +
            $"AllowList (see RepositoryWriteGuardrailTests.cs). All {failures.Count} finding(s):" +
            Environment.NewLine + string.Join(Environment.NewLine, failures.Select(f => "  - " + f)));
    }

    /// <summary>
    /// Reflects over every <c>I*Repository</c> interface declared in
    /// <c>BeeMemoryBank.Core.Interfaces</c> and classifies each PUBLIC method as a write unless its
    /// name starts with one of <see cref="ReadPrefixes"/>. Runs against whatever the interfaces
    /// currently look like, so a repository method renamed or added after this test was written is
    /// picked up automatically — nothing here hardcodes a method name.
    /// </summary>
    private static Dictionary<string, HashSet<string>> DiscoverWriteMethodsByInterface()
    {
        var repositoryInterfaces = typeof(IArticleRepository).Assembly.GetTypes()
            .Where(t => t.IsInterface
                     && t.Namespace == "BeeMemoryBank.Core.Interfaces"
                     && t.Name.StartsWith('I')
                     && t.Name.EndsWith("Repository", StringComparison.Ordinal));

        var result = new Dictionary<string, HashSet<string>>();
        foreach (var iface in repositoryInterfaces)
        {
            var writeMethods = new HashSet<string>();
            foreach (var method in iface.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (ReadPrefixes.Any(prefix => method.Name.StartsWith(prefix, StringComparison.Ordinal)))
                    continue;
                writeMethods.Add(method.Name);
            }
            result[iface.Name] = writeMethods;
        }
        return result;
    }

    private static int CountLines(string text, int uptoIndex)
    {
        var line = 1;
        for (var i = 0; i < uptoIndex && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    private static string LineAt(string text, int lineNo)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return lineNo - 1 < lines.Length ? lines[lineNo - 1] : "";
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
