using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;
using Dapper;
using BeeMemoryBank.Sync.Metadata;

namespace BeeMemoryBank.Api.Services.Offboarding;

/// <summary>One node a step waits for, and why (plan E5): the two actions are "wait" (with "open this node once" when it is locked) and "revoke".</summary>
public sealed record OffboardingWaitView(Guid NodeId, string NodeName, string State, string? Why, bool OpenOnce, bool CanRevoke);

/// <summary>One step as the page shows it.</summary>
public sealed record OffboardingStepView(int Number, string Title, string State, string? Sentence, IReadOnlyList<OffboardingWaitView> Waits);

/// <summary>A removal as the pages, the API and the CLI show it. No secret, no exception text.</summary>
public sealed record OffboardingView(
    Guid ProcessId, Guid PersonId, string Person, string Target, string? TargetRole, int Step, int StepCount, bool Open, bool DrivenHere,
    bool CanTakeOver, string? Outcome, DateTime StartedAt, DateTime? ClosedAt, string StartedBy, bool NeedsPassword,
    IReadOnlyList<OffboardingStepView> Steps, IReadOnlyList<OffboardingReviewItem> Review, IReadOnlyList<string> Bridges, IReadOnlyList<string> Reminders)
{
    /// <summary>"being removed, step k of n" (plan E5).</summary>
    public string Status => Open ? $"being removed, step {Math.Min(Step, OffboardingSteps.Count - 1) + 1} of {StepCount}" : $"{Outcome ?? "closed"}";
}

public sealed partial class OffboardingService
{
    /// <summary>Every removal this node knows of, open ones first.</summary>
    public async Task<IReadOnlyList<OffboardingView>> ListAsync(CancellationToken ct = default)
    {
        var list = new List<OffboardingView>();
        foreach (var record in await new OffboardingStore(connFactory).ListAsync())
            list.Add(await ViewAsync(record, ct));
        return list;
    }

    public async Task<OffboardingView?> GetAsync(Guid processId, CancellationToken ct = default) =>
        await new OffboardingStore(connFactory).GetAsync(processId) is { } record ? await ViewAsync(record, ct) : null;

    /// <summary>The open removal of a local account, if any (the Users page shows "being removed, step k of n").</summary>
    public async Task<IReadOnlyDictionary<Guid, OffboardingView>> OpenByPersonAsync(CancellationToken ct = default)
    {
        var map = new Dictionary<Guid, OffboardingView>();
        foreach (var record in await new OffboardingStore(connFactory).ListAsync(openOnly: true))
            map.TryAdd(record.PersonId, await ViewAsync(record, ct));
        return map;
    }

    private async Task<OffboardingView> ViewAsync(OffboardingRecord record, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var self = (await sp.GetRequiredService<INodeIdentityRepository>().GetAsync())?.NodeId ?? Guid.Empty;
        var whitelist = sp.GetRequiredService<IWhitelistRepository>();
        var states = sp.GetRequiredService<NodeStateStore>();
        var users = sp.GetRequiredService<IUserRepository>();

        var personRow = record.UserId is { } uid ? await users.GetByIdAsync(uid) : null;
        personRow ??= (await users.ListActiveAsync()).FirstOrDefault(u => u.PersonId == record.PersonId);
        var person = personRow is null ? $"a superadmin (person {record.PersonId.ToString("D")[..8]})" : $"{personRow.DisplayName} ({personRow.Username})";

        var drivenHere = record.DriverNodeId == self && record.Origin == "local";
        var accountsRow = record.AccountsNodeId == self ? null : await whitelist.GetByNodeIdAsync(record.AccountsNodeId, includeDeleted: true);
        var canTakeOver = record.IsOpen && !drivenHere && record.Origin == "replicated" && accountsRow is { Status: "R" };

        var nodes = await new OffboardingStore(connFactory).NodesAsync(record.ProcessId);
        var rekeyStep = sp.GetRequiredService<IIdentityRekeyStep>();
        var sharedStep = sp.GetRequiredService<ISharedDoorsStep>();
        var cannotFollowNow = rekeyStep.IsAvailable ? (await rekeyStep.CannotFollowAsync(self, ct)).ToHashSet() : [];
        async Task<IReadOnlyList<OffboardingWaitView>> WaitsAsync(int step)
        {
            var waits = new List<OffboardingWaitView>();
            foreach (var row in nodes.Where(n => n.Step == step))
            {
                var name = row.NodeId == self ? "this node" : (await whitelist.GetByNodeIdAsync(row.NodeId, includeDeleted: true))?.DisplayName ?? row.NodeId.ToString("D");
                if (row.State != "waiting")
                {
                    // A node that uses the new master key but still keeps a bridge from the old one says so in its node_state (plan E6).
                    var note = row.State == "revoked" ? "Revoked: never waited for."
                        : step == OffboardingSteps.Rotate && row.NodeId != self && await BridgeNoteAsync(states, row.NodeId) is { } bridge
                            ? bridge == NodeStateExtraKeys.Unknown
                                ? "Uses the new master key; whether a bridge from the old one remains there is not known."
                                : $"Uses the new master key; a bridge from the old one remains there ({bridge})."
                            : null;
                    waits.Add(new OffboardingWaitView(row.NodeId, name, row.State, note, false, false));
                    continue;
                }
                var (why, openOnce) = await WhyWaitingAsync(whitelist, states, row, self, step, record.Progress.S4NewDekFp is not null, session.IsUnlocked);
                // A device that cannot follow a new identity key: this node re-keyed without it, and it stays cut off until then.
                var cannotFollow = step == OffboardingSteps.Rekey && row.NodeId != self && cannotFollowNow.Contains(row.NodeId);
                if (cannotFollow)
                {
                    why = "It cannot follow the new identity key: it runs a version without it, or has not been seen since the update. Update it (it then " +
                          "follows by itself), or revoke it.";
                    openOnce = false;
                }
                // What the step itself knows better than "not yet" (ADR 0014): a node whose identity key is kept outside its database (pair it again or
                // revoke it), a refused re-key, a shared node that has not applied the list or still keeps its own superadmin doors.
                if (!openOnce && !cannotFollow && step == OffboardingSteps.Rekey && await rekeyStep.WhyWaitingAsync(record.ProcessId, row.NodeId, ct) is { } rekeyWhy)
                    why = rekeyWhy;
                else if (!openOnce && step == OffboardingSteps.SharedNodes && drivenHere && record.Progress.S2ListVersion > 0
                         && (await sharedStep.ReportAsync(row.NodeId, record.Progress.S2ListVersion, ct)).Waiting is { } doorsWhy)
                    why = doorsWhy;
                waits.Add(new OffboardingWaitView(row.NodeId, name, "waiting", why, openOnce, row.NodeId != self && record.IsOpen && drivenHere));
            }
            return waits;
        }

        var p = record.Progress;
        // What keeps the close back once every step before it is done.
        var closeBlockers = drivenHere && record.IsOpen && p.S1Done && p.S2Done && p.S3Done && p.S4Done && !p.S5Done
            ? await CloseBlockersAsync(sp, record, ct) : [];
        var needsPassword = drivenHere && record.IsOpen && p.S1Done && p.S2Done && p.S3Done && !p.S4Done && TakeHold(record.ProcessId) is null
                            && (p.S4CommitId is null || p.S4NewDekFp is null);
        // A replica (a process another node drives) has no progress of its own: its row carries the step the driver last reported.
        var current = record.Origin == "replicated" ? Math.Clamp(record.Step, OffboardingSteps.LockOut, OffboardingSteps.Closed) : p.CurrentStep;
        string State(bool done, int number) => done || record.ClosedAt is not null ? "done" : current == number ? "waiting" : number < current ? "done" : "pending";
        bool Done(bool flag, int number) => record.ClosedAt is not null || (record.Origin == "replicated" ? number < current : flag);

        // S0 says where the person is locked out, and what is still open on the network: "out everywhere" only once it is.
        string LockOutSentence()
        {
            if (!Done(p.S0Done, 0)) return "Locking out.";
            var open = new List<string>();
            if (!Done(p.S1Done, 1)) open.Add("new identity keys (step 2)");
            if (!Done(p.S2Done, 2)) open.Add("the shared nodes' list of superadmins (step 3)");
            if (!Done(p.S3Done, 3)) open.Add("the review of devices and ways back (step 4)");
            if (!Done(p.S4Done, 4)) open.Add("the new master key (step 5)");
            return open.Count == 0
                ? "Signed out everywhere; no password, recovery key, agent, token or personal device of theirs works any more."
                : "Locked out on this node: no password, recovery key, agent, token or personal device of theirs works here any more. Still open on the " +
                  $"network: {string.Join(", ", open)}.";
        }

        var steps = new List<OffboardingStepView>
        {
            new(0, OffboardingSteps.Titles[0], State(p.S0Done, 0), LockOutSentence(), []),
            new(1, OffboardingSteps.Titles[1], p.S1Done ? "done" : State(false, 1),
                p.S1Done ? "This node and the shared nodes have new identity keys."
                    : rekeyStep.IsAvailable
                        ? "Waiting for the nodes to report their new identity keys. Until every node named below has its new key, or is updated or " +
                          "revoked where it cannot follow, a stolen backup of this node still opens the network."
                        : rekeyStep.UnavailableReason,
                await WaitsAsync(OffboardingSteps.Rekey)),
            new(2, OffboardingSteps.Titles[2], p.S2Done ? "done" : State(false, 2),
                p.S2Done ? "The shared nodes no longer accept this person's password." : !sharedStep.IsAvailable ? sharedStep.UnavailableReason
                    : drivenHere && !p.S2Published && nodes.Any(n => n.Step == OffboardingSteps.SharedNodes && n.State == "waiting")
                      && await sharedStep.WhyNotPublishingAsync(ct) is { } notPublishing ? notPublishing
                    : drivenHere && !p.S2Published && nodes.Any(n => n.Step == OffboardingSteps.SharedNodes && n.State == "waiting")
                      && !await SelfRekeyedAsync(sp, record, self)
                        ? "The new list of superadmins goes out once this node has its new identity key (step 1): a list signed with the old key " +
                          "could be outdone by one made with a stolen copy of it."
                    : "Waiting for the shared nodes to apply the new list.",
                await WaitsAsync(OffboardingSteps.SharedNodes)),
            new(3, OffboardingSteps.Titles[3], p.S3Done ? "done" : State(false, 3),
                p.S3Done ? "Every remaining device and way in was reviewed." : "Decide every item of the review below.", []),
            new(4, OffboardingSteps.Titles[4], p.S4Done ? "done" : State(false, 4),
                p.S4Done ? "Every full node uses the new master key." : needsPassword ? "Enter your password to change the master key." :
                    p.S4NewDekFp is not null ? "Waiting for every full node to report the new master key." : "Starts when the steps above are done.",
                await WaitsAsync(OffboardingSteps.Rotate)),
            new(5, OffboardingSteps.Titles[5], p.S5Done ? "done" : closeBlockers.Count > 0 ? "waiting" : "pending",
                p.S5Done ? (record.IsDelete ? "The account was deleted." : "The account is an ordinary user's.")
                    : closeBlockers.Count > 0 ? string.Join(" ", closeBlockers) : "The account is closed at the end.", []),
        };

        return new OffboardingView(record.ProcessId, record.PersonId, person, record.Target, record.TargetRole, record.IsOpen ? current : OffboardingSteps.Closed,
            OffboardingSteps.Count, record.IsOpen, drivenHere, canTakeOver, record.Outcome, record.StartedAt, record.ClosedAt, record.StartedBy, needsPassword,
            steps, record.Review ?? [], p.Bridges, record.IsOpen ? [] : Reminders);
    }

    /// <summary>
    /// Why a node has not confirmed a step yet, in plain words, and whether "open this node once" is the cure - wherever a locked node is the cause, at
    /// every step: a locked full node reports no master-key fingerprint, and does nothing of S1, S2 or S4 until it is opened.
    /// </summary>
    private static async Task<(string Why, bool OpenOnce)> WhyWaitingAsync(IWhitelistRepository whitelist, NodeStateStore states,
        OffboardingNodeRow row, Guid self, int step, bool rotatedHere, bool selfUnlocked)
    {
        if (row.NodeId == self)
        {
            if (step == OffboardingSteps.Rotate)
            {
                // Once this node applied the rotation itself, what it waits for is to be open: then it reports the new key at the next pass.
                if (rotatedHere)
                    return selfUnlocked
                        ? ("This node uses the new master key; it is confirmed at the next pass.", false)
                        : ("This node is locked. Open this node once (sign in or unlock it); it then confirms the new master key.", true);
                return ("This node changes its master key when you enter your password.", false);
            }
            return selfUnlocked
                ? ("This node has not changed its identity key yet.", false)
                : ("This node is locked. Open this node once (sign in or unlock it); it then goes on by itself.", true);
        }
        var entry = await whitelist.GetByNodeIdAsync(row.NodeId, includeDeleted: true);
        var state = await states.GetAsync(row.NodeId);
        var caps = MetadataCapabilities.Parse(entry?.LastCapabilities);
        if (entry?.LastCapabilitiesSeenAt is not null && !MetadataCapabilities.HasNodeStateV1(caps))
            return ("It runs an older version that cannot report this. Update it, or revoke it.", false);
        if (state is null)
            return ("It has not reported since the removal began: it may be offline. Wait for it, or revoke it.", false);
        if (string.IsNullOrEmpty(state.DekFp) && !BlindNodeId.IsBlind(row.NodeId))
            return (step == OffboardingSteps.Rotate
                ? "It is locked. Open this node once (sign in or unlock it); it then applies the new key by itself."
                : "It is locked. Open this node once (sign in or unlock it); it then does this step by itself.", true);
        var since = DateTime.UtcNow - state.UpdatedAt;
        return since > TimeSpan.FromHours(1)
            ? ($"Last report {Math.Round(since.TotalHours)} hour(s) ago: it may be offline. Wait for it, or revoke it.", false)
            : ("It has not applied this step yet.", false);
    }

    /// <summary>
    /// Take-over (plan E2): a superadmin on a shared full node finishes a removal whose accounts node was revoked. This node becomes the driver,
    /// publishes the state under its own name and runs the remaining steps; S0 was done on the accounts node, and the tombstone applies to the accounts
    /// data when it is restored.
    /// </summary>
    public async Task TakeOverAsync(Guid processId, string password, int actorUserId, CancellationToken ct = default)
    {
        if (!session.IsUnlocked) throw new OffboardingRefusedException("Unlock this node first.");
        await Gate.WaitAsync(ct);
        AdminDoor? door = null;
        try
        {
            var store = new OffboardingStore(connFactory);
            var record = await store.GetAsync(processId) ?? throw new OffboardingRefusedException("There is no such removal.", 404);
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var self = (await sp.GetRequiredService<INodeIdentityRepository>().GetAsync())?.NodeId
                ?? throw new OffboardingRefusedException("This node is not set up.");
            if (!record.IsOpen) throw new OffboardingRefusedException("This removal is closed.");
            if (record.DriverNodeId == self && record.Origin == "local") throw new OffboardingRefusedException("This node drives this removal already.");
            var accounts = await sp.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(record.AccountsNodeId, includeDeleted: true);
            if (accounts is not { Status: "R" })
                throw new OffboardingRefusedException("The node where this removal started is still in the network. Revoke it first if it is lost; then take the removal over.");
            if ((await sp.GetRequiredService<IUserRepository>().ListActiveAsync()).FirstOrDefault(u => u.PersonId == record.PersonId) is { Id: var personHere }
                && personHere == actorUserId)
                throw new OffboardingRefusedException("You cannot remove yourself. Another superadmin has to do it.");
            // Single writer: a removal another node has taken over already, or a network whose accounts node moved to another active
            // node, is not taken over here as well. Two take-overs at once would leave the nodes split between two accounts nodes (ADR 0014 says how
            // to recover: revoke the stale one).
            var whitelist = sp.GetRequiredService<IWhitelistRepository>();
            if (record.DriverNodeId != record.AccountsNodeId && record.DriverNodeId != self
                && await whitelist.GetByNodeIdAsync(record.DriverNodeId, includeDeleted: true) is { Status: not "R" } otherDriver)
                throw new OffboardingRefusedException($"\"{otherDriver.DisplayName}\" has taken this removal over already. Continue it there.");
            var homes = sp.GetRequiredService<IAccountsHomeRepository>();
            var home = await homes.GetAsync();
            var becomeHome = false;
            if (home is not null && home.NodeId != self)
            {
                var homeRow = await whitelist.GetByNodeIdAsync(home.NodeId, includeDeleted: true);
                if (homeRow is not { Status: "R" })
                    throw new OffboardingRefusedException(
                        $"The accounts node is \"{homeRow?.DisplayName ?? home.NodeId.ToString("D")}\", which is still in the network. Take the removal over " +
                        "there, or revoke it first if it is lost.");
                becomeHome = true;   // the accounts node was lost with the node where the removal started: this node takes over both
            }
            // Not from a node that keeps superadmin doors of its own while it is not the accounts node: it never applied a list of
            // superadmins, so an account of the person that is not linked to them may still open it - and drive their own removal from here.
            if (home is null || home.NodeId != self)
            {
                var doors = sp.GetServices<INodeStateContributor>().OfType<BeeMemoryBank.Vault.SharedDoors.SharedDoorsNodeStateContributor>().FirstOrDefault();
                if (doors is not null)
                {
                    var draft = new NodeStateDraft();
                    await doors.ContributeAsync(draft, ct);
                    if (draft.Extra.TryGetValue(BeeMemoryBank.Vault.SharedDoors.SharedDoorsNodeStateContributor.LocalSuperadminDoorCountKey, out var text)
                        && long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var local) && local > 0)
                        throw new OffboardingRefusedException(
                            "This node keeps superadmin accounts of its own: it has not applied a list of superadmins from the accounts node, so an account of " +
                            "the person being removed may still open it. Take the removal over on a shared node that has applied one (its Devices page " +
                            "shows it).");
                }
            }
            // Nor by an account the person created or made a superadmin, as far as this node's audit log tells.
            if (await MadeByThePersonAsync(record.PersonId, actorUserId))
                throw new OffboardingRefusedException("Your account was created, or made a superadmin, by the person being removed. Another superadmin has to take this removal over.");
            door = await VerifyAdministratorAsync(sp, actorUserId, password, -1);
            if (becomeHome)
                await BecomeAccountsNodeAsync(sp, door.User, password, ct);

            record.DriverNodeId = self;
            record.Origin = "local";
            record.UserId = null;
            record.Progress = new OffboardingProgress { S0Done = true, S0At = record.StartedAt, TombstoneEventId = Guid.NewGuid() };
            record.Review = null;
            _holds[processId] = new PasswordHold(password.ToCharArray(), door.User.Id, DateTime.UtcNow + PasswordHoldTime);
            await AuditAsync(sp, "offboarding_taken_over", processId, $"Removal taken over by user #{door.User.Id} on this node: the node where it started was revoked.");
            await sp.GetRequiredService<IEventLogger>().LogPersonRemovedAsync(record.PersonId, record.OutcomeWord, record.StartedAt, record.Progress.TombstoneEventId);
            await PublishStateAsync(sp, record);
        }
        finally
        {
            door?.Clear();
            Gate.Release();
        }
        await AdvanceAsync(processId, ct);
    }

    /// <summary>Whether this node's audit log says an account of <paramref name="personId"/> created <paramref name="userId"/> or made it a superadmin.</summary>
    private async Task<bool> MadeByThePersonAsync(Guid personId, int userId)
    {
        using var conn = connFactory.CreateConnection();
        var theirs = (await conn.QueryAsync<long>("SELECT id FROM tbl_user WHERE person_id = @p COLLATE NOCASE", new { p = personId.ToString("D") }))
            .Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
        if (theirs.Count == 0) return false;
        foreach (var (action, details) in await conn.QueryAsync<(string, string?)>(
                     "SELECT action, details FROM tbl_audit_log WHERE action IN ('user_created', 'user_updated') AND entity_id = @u",
                     new { u = userId.ToString(System.Globalization.CultureInfo.InvariantCulture) }))
        {
            if (details is null || (action == "user_updated" && !Promoted.IsMatch(details))) continue;
            var by = ByUser.Match(details);
            if (by.Success && theirs.Contains(by.Groups[1].Value)) return true;
        }
        return false;
    }

    /// <summary>
    /// The take-over makes this node the accounts node, so the list without the person goes out from here (S2). Only a node where a superadmin has an
    /// account of its own may be it: when the administrator taking over holds only a mirror of the lost accounts node's list here,
    /// that mirror becomes their own account on this node, with the password they just proved by opening their door. This node then says so in its
    /// node_state before it names itself, so the other nodes accept the change.
    /// </summary>
    private static async Task BecomeAccountsNodeAsync(IServiceProvider sp, User admin, string password, CancellationToken ct)
    {
        if (admin.Replicated)
        {
            using var conn = sp.GetRequiredService<IDbConnectionFactory>().CreateConnection();
            await conn.ExecuteAsync("UPDATE tbl_user SET replicated = 0, password_hash = @hash WHERE id = @id AND replicated = 1",
                new { hash = UserService.HashPassword(password), id = admin.Id });
            CallerStateCache.Invalidate(admin.Id);
        }
        if (sp.GetService<NodeStatePublisher>() is { } publisher && await publisher.BuildDraftAsync(ct) is { } draft)
            await publisher.PublishAsync(draft, ct);
        var self = (await sp.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;
        try
        {
            await sp.GetRequiredService<BeeMemoryBank.Vault.SharedDoors.AccountsHomeService>().SetAsync(self, ct, admin.Username, "user");
        }
        catch (InvalidOperationException ex)
        {
            throw new OffboardingRefusedException(ex.Message);
        }
    }
}
