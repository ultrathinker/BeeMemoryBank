using System.Text.Json;
using Dapper;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;

namespace BeeMemoryBank.Api.Services.Offboarding;

/// <summary>Steps S1 to S5 (plan E4): started at once after S0, confirmed per node from what the nodes report (plan E6), resumed at every call.</summary>
public sealed partial class OffboardingService
{
    /// <summary>The reason of the rotation that ends a removal (plan A5: every unlocked full node applies it by itself).</summary>
    public static string RotationReason(Guid processId) => "offboarding:" + processId.ToString("D");

    /// <summary>
    /// Moves a process as far as it can go now, without a password: confirmations are read, waits are re-evaluated, S3's decisions are carried out,
    /// and S4 runs if the administrator's password is still held from the start. Idempotent; the background driver calls it for every open process.
    /// </summary>
    public Task AdvanceAsync(Guid processId, CancellationToken ct = default) => AdvanceCoreAsync(processId, null, ct);

    /// <summary>"Continue": as <see cref="AdvanceAsync"/>, with the administrator's password for the rotation of S4 (and nothing else).</summary>
    public async Task ContinueAsync(Guid processId, string? password, int actorUserId, CancellationToken ct = default)
    {
        var record = await new OffboardingStore(connFactory).GetAsync(processId)
            ?? throw new OffboardingRefusedException("There is no such removal.", 404);
        PasswordHold? hold = null;
        AdminDoor? door = null;
        try
        {
            if (!string.IsNullOrEmpty(password))
            {
                if (!session.IsUnlocked) throw new OffboardingRefusedException("Unlock this node first.");
                using var scope = scopes.CreateScope();
                door = await VerifyAdministratorAsync(scope.ServiceProvider, actorUserId, password, record.UserId ?? -1);
                hold = new PasswordHold(password.ToCharArray(), door.User.Id, DateTime.UtcNow + PasswordHoldTime);
            }
            // A crash between S0's transaction and the work after it: that work is finished first (with the boxes when the password is here).
            await FinishLockOutAsync(processId, door, ct);
        }
        finally
        {
            door?.Clear();
        }
        await AdvanceCoreAsync(processId, hold, ct);
    }

    /// <summary>What the background driver and a restart do: finish S0's work if a crash interrupted it, then advance.</summary>
    public async Task ResumeAsync(Guid processId, CancellationToken ct = default)
    {
        await FinishLockOutAsync(processId, null, ct);
        await AdvanceAsync(processId, ct);
    }

    private async Task AdvanceCoreAsync(Guid processId, PasswordHold? given, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var store = new OffboardingStore(connFactory);
            var record = await store.GetAsync(processId);
            if (record is null || !record.IsOpen) return;
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var self = (await sp.GetRequiredService<INodeIdentityRepository>().GetAsync())?.NodeId;
            if (self is null || record.DriverNodeId != self.Value || record.Origin != "local") return;   // somebody else drives it: shown, not moved here
            if (!record.Progress.S0Done) return;   // the work after S0 runs first (FinishLockOutAsync)

            var before = record.Progress.CurrentStep;
            // The review first: a device it revokes is then never waited for by S1 and S2 in the same pass.
            await RunReviewStepAsync(sp, record, ct);
            await RunRekeyStepAsync(sp, record, self.Value, ct);
            await RunSharedNodesStepAsync(sp, record, self.Value, ct);
            await RunRotationStepAsync(sp, record, self.Value, given ?? TakeHold(processId), ct);
            await RunCloseStepAsync(sp, record, ct);
            if (BeforeSavingPassForTests is { } crash) await crash(record.Progress);

            if (record.Progress.CurrentStep != before || record.ClosedAt is not null)
                await PublishStateAsync(sp, record);
            else
                await store.SaveAsync(record);
        }
        finally
        {
            given?.Clear();
            Gate.Release();
        }
    }

    /// <summary>The password held since the start, while it is fresh; an expired one is cleared and forgotten.</summary>
    private PasswordHold? TakeHold(Guid processId)
    {
        if (!_holds.TryGetValue(processId, out var hold)) return null;
        if (hold.Expires > DateTime.UtcNow) return hold;
        if (_holds.TryRemove(processId, out var stale)) stale.Clear();
        return null;
    }

    private void DropHold(Guid processId)
    {
        if (_holds.TryRemove(processId, out var hold)) hold.Clear();
    }

    // ── S1: identity re-key ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The nodes S1 re-keys: this node and every active shared full node (plan D5; not the personal devices of those who stay).</summary>
    private static async Task<List<Guid>> SharedFullNodesAsync(IServiceProvider sp, Guid self) =>
        (await sp.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync())
            .Where(w => w.NodeId != self && !BlindNodeId.IsBlind(w.NodeId) && w.Ownership == OwnershipKinds.Shared)
            .Select(w => w.NodeId).ToList();

    /// <summary>The baseline of an S1 row that waits for a device that cannot follow a new identity key, not for its own re-key.</summary>
    public const string CannotFollowBaseline = "cannot-follow";

    private async Task RunRekeyStepAsync(IServiceProvider sp, OffboardingRecord record, Guid self, CancellationToken ct)
    {
        var p = record.Progress;
        var step = sp.GetRequiredService<IIdentityRekeyStep>();
        var store = new OffboardingStore(connFactory);

        // The wait set follows the CURRENT shared full nodes and this node on every pass, before "none waiting" is asked: a shared full
        // node that appears while the process is open (a join, a restore that re-joins) is waited for too, and re-opens a step that was done. So is
        // every device that cannot follow a new identity key: this node re-keys without waiting for it, and the step waits
        // until it is updated (and shows it can follow) or revoked - until then a stolen backup of this node still opens the network through it.
        var shared = await SharedFullNodesAsync(sp, self);
        var laggards = step.IsAvailable ? (await step.CannotFollowAsync(self, ct)).ToList() : [];
        var known = (await store.NodesAsync(record.ProcessId)).Where(r => r.Step == OffboardingSteps.Rekey).Select(r => r.NodeId).ToHashSet();
        var appeared = shared.Prepend(self).Where(n => !known.Contains(n)).ToList();
        var lagging = laggards.Where(n => !known.Contains(n) && !shared.Contains(n)).ToList();
        if (p.S1Done && appeared.Count == 0 && lagging.Count == 0) return;
        foreach (var node in appeared)
            await store.EnsureNodeAsync(record.ProcessId, node, OffboardingSteps.Rekey, await step.CurrentKeyFingerprintAsync(node, ct));
        foreach (var node in lagging)
            await store.EnsureNodeAsync(record.ProcessId, node, OffboardingSteps.Rekey, CannotFollowBaseline);
        appeared.AddRange(lagging);
        if (p.S1Done)
        {
            p.S1Done = false;
            await AuditAsync(sp, "offboarding_rekey_reopened", record.ProcessId, $"New identity keys asked again: {appeared.Count} shared node(s) appeared.");
        }

        if (step.IsAvailable)
        {
            // Idempotent per process (the IIdentityRekeyStep contract): a node that has not re-keyed for this process is asked (again) - one that
            // appeared, one whose request was lost with a crash, one that was offline.
            await step.StartAsync(record.ProcessId, shared, p.S0At, ct);
            if (!p.S1Started)
            {
                p.S1Started = true;
                await AuditAsync(sp, "offboarding_rekey_started", record.ProcessId, $"New identity keys asked of this node and {shared.Count} shared node(s).");
            }
        }

        foreach (var row in (await store.NodesAsync(record.ProcessId)).Where(r => r.Step == OffboardingSteps.Rekey && r.State == "waiting"))
        {
            if (await IsRevokedAsync(sp, row.NodeId, self)) { await store.SetNodeStateAsync(record.ProcessId, row.NodeId, row.Step, "revoked"); continue; }
            if (row.Baseline == CannotFollowBaseline)
            {
                // Updated: it shows it can follow, and applies this node's key change when it next syncs.
                if (!laggards.Contains(row.NodeId)) await store.SetNodeStateAsync(record.ProcessId, row.NodeId, row.Step, "confirmed");
                continue;
            }
            // The key this node trusts for that node (its own, or the peer's whitelist key) moved since the step began: the node's rotation, signed by
            // its old key, was applied here. Not its node_state report: that can lag behind the trusted key, and a stale report taken against a newer
            // baseline would confirm a node that never re-keyed for this process.
            var now = await step.CurrentKeyFingerprintAsync(row.NodeId, ct);
            if (now is not null && row.Baseline is not null && !string.Equals(now, row.Baseline, StringComparison.OrdinalIgnoreCase))
                await store.SetNodeStateAsync(record.ProcessId, row.NodeId, row.Step, "confirmed");
        }
        if (step.IsAvailable && record.Progress.S1Started && await NoneWaitingAsync(record.ProcessId, OffboardingSteps.Rekey))
            record.Progress.S1Done = true;
    }

    // ── S2: shared nodes ───────────────────────────────────────────────────────────────────────────────────────

    private async Task RunSharedNodesStepAsync(IServiceProvider sp, OffboardingRecord record, Guid self, CancellationToken ct)
    {
        var p = record.Progress;
        var step = sp.GetRequiredService<ISharedDoorsStep>();
        var store = new OffboardingStore(connFactory);

        // As in S1: the wait set is the CURRENT shared full nodes, and one that appears re-opens the step - also a step that was done
        // because there was no shared node at all, whose list then goes out now.
        var shared = await SharedFullNodesAsync(sp, self);
        var known = (await store.NodesAsync(record.ProcessId)).Where(r => r.Step == OffboardingSteps.SharedNodes).Select(r => r.NodeId).ToHashSet();
        var appeared = shared.Where(n => !known.Contains(n)).ToList();
        if (p.S2Done && appeared.Count == 0) return;
        foreach (var node in appeared)
            await store.EnsureNodeAsync(record.ProcessId, node, OffboardingSteps.SharedNodes, null);
        if (p.S2Done)
        {
            p.S2Done = false;
            await AuditAsync(sp, "offboarding_doors_reopened", record.ProcessId, $"The shared nodes are waited for again: {appeared.Count} appeared.");
        }

        var published = p.S2Published || (p.S2Started && p.S2ListVersion > 0);
        // The list goes out only from the accounts node (ADR 0014): while this node is not it, or none is set, the step waits and the page says what to
        // set (SharedDoorsStep.WhyNotPublishingAsync). A publication that did not happen (version 0) is not counted. And only once this node has its
        // new identity key: a list signed with the old key can be outdone by one forged with a stolen copy of that key; one
        // signed with the new key replaces any list signed with the old one.
        if (shared.Count > 0 && step.IsAvailable && !published && await SelfRekeyedAsync(sp, record, self) && await step.WhyNotPublishingAsync(ct) is null)
        {
            var version = await step.PublishWithoutAsync(await OpenRemovalPersonsAsync(), ct);
            if (version > 0)
            {
                p.S2ListVersion = version;
                p.S2Published = published = true;
                await AuditAsync(sp, "offboarding_doors_list", record.ProcessId,
                    $"The list of superadmins without the person was published (version {p.S2ListVersion}).");
            }
        }
        p.S2Started = true;

        foreach (var row in (await store.NodesAsync(record.ProcessId)).Where(r => r.Step == OffboardingSteps.SharedNodes && r.State == "waiting"))
        {
            if (await IsRevokedAsync(sp, row.NodeId, self)) { await store.SetNodeStateAsync(record.ProcessId, row.NodeId, row.Step, "revoked"); continue; }
            if (!step.IsAvailable) continue;
            var report = await step.ReportAsync(row.NodeId, record.Progress.S2ListVersion, ct);
            if (report.Confirmed) await store.SetNodeStateAsync(record.ProcessId, row.NodeId, row.Step, "confirmed");
        }
        // Done when nobody is waited for: no shared full node at all, or every one confirmed the published list (or was revoked).
        if (await NoneWaitingAsync(record.ProcessId, OffboardingSteps.SharedNodes) && (shared.Count == 0 || published || !step.IsAvailable))
            p.S2Done = true;
    }

    /// <summary>Whether this node has its new identity key for this removal (its step-1 row confirmed), or S1 is not available on this build.</summary>
    private async Task<bool> SelfRekeyedAsync(IServiceProvider sp, OffboardingRecord record, Guid self) =>
        !sp.GetRequiredService<IIdentityRekeyStep>().IsAvailable
        || (await new OffboardingStore(connFactory).NodesAsync(record.ProcessId))
            .Any(r => r.Step == OffboardingSteps.Rekey && r.NodeId == self && r.State == "confirmed");

    private async Task<IReadOnlyCollection<Guid>> OpenRemovalPersonsAsync() =>
        (await new OffboardingStore(connFactory).ListAsync(openOnly: true)).Select(r => r.PersonId).Distinct().ToList();

    // ── S4: rotation ───────────────────────────────────────────────────────────────────────────────────────────

    private async Task RunRotationStepAsync(IServiceProvider sp, OffboardingRecord record, Guid self, PasswordHold? hold, CancellationToken ct)
    {
        var p = record.Progress;
        if (p.S4Done || !(p.S1Done && p.S2Done && p.S3Done)) return;
        var store = new OffboardingStore(connFactory);
        var rotation = sp.GetRequiredService<DekRotationService>();
        var states = sp.GetRequiredService<IDekRotationStateRepository>();

        // A rotation the process holds that will never apply here: cancelled, or rejected by this node. Accept refuses it for ever, so
        // the process lets it go and a new one is proposed. A Failed one is kept: its commit is public, peers may have applied it, and accepting it
        // again is how a failed or deferred accept is retried (DekRotationService.AcceptCommitAsync); a new proposal would split this node from them.
        if (p.S4CommitId is { } held && p.S4NewDekFp is null
            && (await states.GetAsync(held))?.State is DekRotationState.Cancelled or DekRotationState.Rejected)
        {
            p.S4CommitId = null;
            p.S4NewDekFp = null;
            await store.SaveAsync(record);
            await AuditAsync(sp, "offboarding_rotation_dropped", record.ProcessId, $"Rotation {held} ended without being applied here; a new one is proposed.");
        }

        if (p.S4CommitId is null)
        {
            // A crash between the proposal below and its save: the rotation is on disk - its commit event and its state row - and names
            // this process. It is this process's own: adopted, never proposed a second time (a second proposal is refused while one is pending).
            var own = (await LiveRotationsAsync(sp)).FirstOrDefault(r => r.Reason == RotationReason(record.ProcessId));
            if (own.CommitId is not null)
            {
                p.S4CommitId = own.CommitId;
                await store.SaveAsync(record);
                await AuditAsync(sp, "offboarding_rotation_adopted", record.ProcessId, $"The rotation this removal had proposed before a restart was taken up (rotation {own.CommitId}).");
            }
        }

        if (p.S4CommitId is null)
        {
            // Several removals share one rotation (plan E5): a removal rotation (reason offboarding:<some process>) proposed AFTER this
            // one's S3 was done serves this one too. Never one proposed before: it would have re-sealed to the new key a door this one's review removed
            // later. Never a rotation without that reason: peers apply it by their own auto-accept rule, not by themselves.
            var ready = p.S3DoneAt ?? DateTime.MaxValue;
            var shared = (await LiveRotationsAsync(sp)).FirstOrDefault(r => DekRotationReasons.IsRemoval(r.Reason) && r.CreatedAt >= ready);
            if (shared.CommitId is not null) p.S4CommitId = shared.CommitId;
        }

        if (p.S4CommitId is null)
        {
            if (hold is null) return;   // waits for "continue" with the administrator's password
            try
            {
                var commitId = await rotation.ProposeRotationAsync(new string(hold.Password), hold.AdminUserId, RotationReason(record.ProcessId));
                if (AfterRotationProposedForTests is { } crash) await crash();
                p.S4CommitId = commitId.ToString("D");
                // Saved before the rotation is applied here. The proposal itself is already on disk: a crash before this save is met by the adoption above.
                await store.SaveAsync(record);
                await AuditAsync(sp, "offboarding_rotation_proposed", record.ProcessId, $"New master key proposed (rotation {p.S4CommitId}).");
            }
            catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
            {
                logger.LogWarning("The rotation of removal {Process} could not start now ({Failure}); it is tried again.", record.ProcessId, ex.GetType().Name);
                return;
            }
        }

        if (p.S4NewDekFp is null)
        {
            var row = await states.GetAsync(p.S4CommitId);
            if (row?.State != DekRotationState.Applied)
            {
                if (hold is null) return;
                try
                {
                    await rotation.AcceptCommitAsync(p.S4CommitId, new string(hold.Password), hold.AdminUserId);
                }
                catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException)
                {
                    logger.LogWarning("The rotation of removal {Process} could not be applied here now ({Failure}); it is tried again.", record.ProcessId, ex.GetType().Name);
                    return;
                }
                row = await states.GetAsync(p.S4CommitId);
                if (row?.State != DekRotationState.Applied) return;
            }
            if (!session.IsUnlocked) return;
            var dek = session.GetMasterDek();
            try { p.S4NewDekFp = DekFingerprint.Of(dek); }
            finally { Array.Clear(dek); }
            DropHold(record.ProcessId);
            await AuditAsync(sp, "offboarding_rotated", record.ProcessId, "The master key was changed on this node; waiting for every full node to report it.");

            var fullNodes = (await sp.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync())
                .Where(w => !BlindNodeId.IsBlind(w.NodeId)).Select(w => w.NodeId).Prepend(self);
            foreach (var node in fullNodes)
                await store.EnsureNodeAsync(record.ProcessId, node, OffboardingSteps.Rotate, null);
        }

        var nodeStates = sp.GetRequiredService<NodeStateStore>();
        foreach (var row in (await store.NodesAsync(record.ProcessId)).Where(r => r.Step == OffboardingSteps.Rotate && r.State == "waiting"))
        {
            if (row.NodeId == self)
            {
                if (!session.IsUnlocked) continue;
                var dek = session.GetMasterDek();
                try
                {
                    if (DekFingerprint.Of(dek) == p.S4NewDekFp) await store.SetNodeStateAsync(record.ProcessId, row.NodeId, row.Step, "confirmed");
                }
                finally { Array.Clear(dek); }
                continue;
            }
            if (await IsRevokedAsync(sp, row.NodeId, self)) { await store.SetNodeStateAsync(record.ProcessId, row.NodeId, row.Step, "revoked"); continue; }
            if ((await nodeStates.GetAsync(row.NodeId))?.DekFp is { } fp && fp == p.S4NewDekFp)
                await store.SetNodeStateAsync(record.ProcessId, row.NodeId, row.Step, "confirmed");
        }
        if (await NoneWaitingAsync(record.ProcessId, OffboardingSteps.Rotate))
            p.S4Done = true;
    }

    /// <summary>
    /// The rotations that are pending, done or may still be accepted again on this node (state Proposed, Committing, Applied or Failed - a restart turns
    /// this node's own Committing row into Failed, and a Failed commit is accepted again), oldest first, each with the reason its commit event carries
    /// (<see cref="DekRotationReasons"/>; null for a rotation with none). Rows whose commit event this node does not have are left out.
    /// </summary>
    private static async Task<List<(string? CommitId, string? Reason, DateTime CreatedAt, DekRotationState State)>> LiveRotationsAsync(IServiceProvider sp)
    {
        var states = sp.GetRequiredService<IDekRotationStateRepository>();
        var events = sp.GetRequiredService<IEventLogRepository>();
        var list = new List<(string?, string?, DateTime, DekRotationState)>();
        foreach (var state in new[] { DekRotationState.Proposed, DekRotationState.Committing, DekRotationState.Applied, DekRotationState.Failed })
            foreach (var row in await states.GetByStateAsync(state))
            {
                var commit = await events.GetByIdAsync(row.EventId);
                if (commit is null || commit.EventType != EventTypes.DekRotationCommit) continue;   // a proposal's own row, or a commit not here
                var created = DateTime.TryParse(row.CreatedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                    ? at.ToUniversalTime() : DateTime.MinValue;
                list.Add((row.EventId, DekRotationReasons.Of(commit), created, row.State));
            }
        return list.OrderBy(r => r.Item3).ToList();
    }

    // ── S5: close ──────────────────────────────────────────────────────────────────────────────────────────────

    private async Task RunCloseStepAsync(IServiceProvider sp, OffboardingRecord record, CancellationToken ct)
    {
        var p = record.Progress;
        // Every step before it: S1 or S2 may have opened again for a shared node that appeared after the rotation was proposed.
        if (!(p.S1Done && p.S2Done && p.S3Done && p.S4Done) || p.S5Done) return;
        // Not while a node says something is wrong that the removal exists to end: the page says what to do.
        if ((await CloseBlockersAsync(sp, record, ct)).Count > 0) return;
        // The person's devices once more: one that became theirs after S0 (a join through their door on a node that had not
        // applied the lock-out yet, an ownership change) goes now, whatever the review decided about it then.
        // So is a device this review revoked that is active again, unless it was kept since.
        var self = (await sp.GetRequiredService<INodeIdentityRepository>().GetAsync())?.NodeId;
        var removedByReview = (record.Review ?? [])
            .Where(i => i is { Kind: "device", RemovedOnce: true } && i.Decision != "keep")
            .Select(i => Guid.TryParse(i.Id["device:".Length..], out var g) ? g : Guid.Empty)
            .ToHashSet();
        foreach (var device in (await sp.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync())
                     .Where(w => w.NodeId != self && (w.OwnerPersonId == record.PersonId || removedByReview.Contains(w.NodeId))))
        {
            await RevokeDeviceAsync(sp, device.NodeId);
            await AuditAsync(sp, "offboarding_device_revoked", record.ProcessId, device.OwnerPersonId == record.PersonId
                ? $"Device '{Printable(device.DisplayName)}' ({device.NodeId}) of the person revoked at the close."
                : $"Device '{Printable(device.DisplayName)}' ({device.NodeId}), removed by the review and active again, revoked at the close.");
        }
        var userService = sp.GetRequiredService<UserService>();
        if (record.UserId is { } userId)
        {
            var rules = string.IsNullOrEmpty(record.TargetRulesJson) ? null : JsonSerializer.Deserialize<List<DemotionFolderRule>>(record.TargetRulesJson);
            await userService.CloseRemovalAsync(userId, record.IsDelete, record.TargetRole, rules, record.AllowOpenRole, ct);
        }
        else
        {
            // A process taken over on a shared node: the person's row here is a mirror; it goes (the role of a demotion never travels).
            foreach (var user in (await sp.GetRequiredService<IUserRepository>().ListActiveAsync()).Where(u => u.PersonId == record.PersonId))
                await userService.EnforceTombstoneAsync(user, null, ct);
        }

        p.Bridges.Clear();
        if (sp.GetService<BeeMemoryBank.Sync.DekRotation.DekBridgeService>() is { } bridges)
        {
            try
            {
                var report = await bridges.GetReportAsync();
                if (report.BridgePresent)
                    p.Bridges.Add("A bridge from the old master key is still on this node for: " +
                                  string.Join(", ", report.Doors.Select(d => d.OwnerName ?? d.Kind).Distinct()) +
                                  ". Remove it on the Master key page once these people have signed in, or now (they then reset their passwords).");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("The bridge report could not be read at the close of removal {Process} ({Failure}).", record.ProcessId, ex.GetType().Name);
            }
        }
        // Every other full node that confirmed the new master key: its own report in node_state (plan E6), read from tbl_node_state, never guessed.
        p.Bridges.AddRange(await PeerBridgesAsync(sp, record.ProcessId));

        p.S5Done = true;
        record.ClosedAt = DateTime.UtcNow;
        record.Outcome = record.OutcomeWord;
        DropHold(record.ProcessId);
        // Once: a crash after this pass and before its save runs the close again on resume (the account change is idempotent; the record is too).
        bool closedBefore;
        using (var conn = connFactory.CreateConnection())
            closedBefore = await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM tbl_audit_log WHERE action = 'offboarding_closed' AND entity_id = @p",
                new { p = record.ProcessId.ToString("D") }) > 0;
        if (!closedBefore)
        {
            await AuditAsync(sp, "offboarding_closed", record.ProcessId,
                $"Removal closed: the account was {record.OutcomeWord}; identity keys changed, shared nodes updated, " +
                $"{record.Review?.Count(i => i.Decision == "remove") ?? 0} item(s) removed and {record.Review?.Count(i => i.Decision == "keep") ?? 0} kept in the review, " +
                $"master key changed{(p.Bridges.Count > 0 ? "; a bridge from the old key remains" : "")}.");
            logger.LogWarning("Removal {Process} closed: the account was {Outcome}.", record.ProcessId, record.OutcomeWord);
        }
    }

    /// <summary>
    /// What keeps the removal from closing, one plain sentence each with what to do: a node - this one or any active peer -
    /// that reports events signed by a retired key nobody acknowledged, events signed as itself that it never wrote, or a list of superadmins it
    /// could not apply. Each means somebody may still hold a key the removal was meant to take away; closing would hide it.
    /// </summary>
    internal async Task<List<string>> CloseBlockersAsync(IServiceProvider sp, OffboardingRecord record, CancellationToken ct)
    {
        var lines = new List<string>();
        var self = (await sp.GetRequiredService<INodeIdentityRepository>().GetAsync())?.NodeId ?? Guid.Empty;
        var whitelist = sp.GetRequiredService<IWhitelistRepository>();
        var states = sp.GetRequiredService<NodeStateStore>();

        // This node: its own flags, as its next node_state would carry them (not a report that may lag behind).
        var draft = new NodeStateDraft();
        foreach (var contributor in sp.GetServices<INodeStateContributor>())
        {
            try { await contributor.ContributeAsync(draft, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug("A node_state contributor failed while checking the close ({Failure}).", ex.GetType().Name);
            }
        }
        lines.AddRange(BlockingFlagLines("this node", draft.AttentionFlags));

        foreach (var peer in await whitelist.GetAllActiveAsync())
        {
            if (peer.NodeId == self || await states.GetAsync(peer.NodeId) is not { } state) continue;
            var name = string.IsNullOrWhiteSpace(peer.DisplayName) ? peer.NodeId.ToString("D") : peer.DisplayName;
            lines.AddRange(BlockingFlagLines(name, state.AttentionFlags));
        }

        // The accounts node: not a node this review removed, nor one that left the network. The shared nodes take their list
        // of superadmins from it; one the person controls would keep publishing them.
        if (await sp.GetRequiredService<IAccountsHomeRepository>().GetAsync() is { } home && home.NodeId != self)
        {
            var row = await whitelist.GetByNodeIdAsync(home.NodeId, includeDeleted: true);
            var removedHere = record.Review?.Any(i => i.Id == $"device:{home.NodeId:D}" && i.Decision == "remove") == true;
            if (removedHere || row is null || row.Status == "R")
            {
                var name = string.IsNullOrWhiteSpace(row?.DisplayName) ? home.NodeId.ToString("D") : row!.DisplayName;
                lines.Add($"Not closed yet: the accounts node, {name}, was {(removedHere ? "removed in this review" : "removed from the network")}. The shared " +
                          "nodes take their list of superadmins from the accounts node: make this node the accounts node (Admin > Devices, Accounts node).");
            }
        }
        return lines;
    }

    /// <summary>The attention flags that keep a removal open.</summary>
    public static readonly IReadOnlyList<string> CloseBlockingFlags =
    [
        EventApplier.RetiredKeyAttentionFlag, SignedAsThisNode.Flag, BeeMemoryBank.Vault.SharedDoors.SharedDoorsNodeStateContributor.SharedDoorsErrorFlag,
        BeeMemoryBank.Vault.SharedDoors.SharedDoorsNodeStateContributor.AccountsHomeChangedFlag,
    ];

    private static IEnumerable<string> BlockingFlagLines(string name, IEnumerable<string> flags)
    {
        var set = flags.ToHashSet(StringComparer.Ordinal);
        var on = name == "this node" ? "here" : $"on {name}";
        // A peer's own report can raise these two at will: the remedy that always works on a hostile node is to revoke it.
        // This node cannot revoke itself, so its own lines do not offer it.
        var orRevoke = name == "this node" ? "" : "; or revoke that node";
        if (set.Contains(EventApplier.RetiredKeyAttentionFlag))
            yield return $"Not closed yet: {Capital(name)} took changes signed by an old identity key of another device that the device's key change did " +
                         $"not list. Look at them on the Node identity page {on} (Admin > Identity key) and acknowledge them there; if they are not your " +
                         "device's, revoke the device they came from.";
        if (set.Contains(SignedAsThisNode.Flag))
            yield return $"Not closed yet: {Capital(name)} received changes signed with its own identity key that it never wrote - somebody holds a copy " +
                         $"of its key. Give it a new identity key, then acknowledge the warning on the Node identity page {on}{orRevoke}.";
        if (set.Contains(BeeMemoryBank.Vault.SharedDoors.SharedDoorsNodeStateContributor.SharedDoorsErrorFlag))
            yield return $"Not closed yet: {Capital(name)} could not apply the list of superadmins, so it may still let an old password in. See the " +
                         $"Devices page {on}; update it, or revoke it.";
        if (set.Contains(BeeMemoryBank.Vault.SharedDoors.SharedDoorsNodeStateContributor.AccountsHomeChangedFlag))
            yield return $"Not closed yet: {Capital(name)} says the accounts node changed and nobody has looked at it there yet. " +
                         $"Check on the Devices page {on} (Accounts node) that the accounts node is one of the organisation's own servers, then " +
                         $"acknowledge it there; if it is not, make another node the accounts node first{orRevoke}.";
    }

    private static string Capital(string name) => name == "this node" ? "This node" : name;

    /// <summary>
    /// The bridges from an old master key that the OTHER full nodes of the rotation step report in their <c>node_state</c>
    /// (<see cref="NodeStateExtraKeys.DekBridge"/>, <see cref="NodeStateExtraKeys.V1Doors"/>), one plain line each.
    /// </summary>
    private async Task<List<string>> PeerBridgesAsync(IServiceProvider sp, Guid processId)
    {
        var lines = new List<string>();
        var self = (await sp.GetRequiredService<INodeIdentityRepository>().GetAsync())?.NodeId ?? Guid.Empty;
        var states = sp.GetRequiredService<NodeStateStore>();
        var whitelist = sp.GetRequiredService<IWhitelistRepository>();
        foreach (var row in (await new OffboardingStore(connFactory).NodesAsync(processId))
                     .Where(n => n.Step == OffboardingSteps.Rotate && n.State == "confirmed" && n.NodeId != self))
        {
            if (await BridgeNoteAsync(states, row.NodeId) is not { } note) continue;
            var name = (await whitelist.GetByNodeIdAsync(row.NodeId, includeDeleted: true))?.DisplayName ?? row.NodeId.ToString("D");
            lines.Add(note == NodeStateExtraKeys.Unknown
                ? $"Whether a bridge from the old master key is still on {name} is not known: its last report could not tell. Look at that node's Master key page."
                : $"A bridge from the old master key is still on {name} ({note}). Remove it on that node's Master key page once those people have signed in there.");
        }
        return lines;
    }

    /// <summary>
    /// "N old door(s)" when <paramref name="nodeId"/>'s last report says it keeps a bridge from an old master key, <see cref="NodeStateExtraKeys.Unknown"/>
    /// when it says it could not tell, null when it says it keeps none (or says nothing of it).
    /// </summary>
    internal static async Task<string?> BridgeNoteAsync(NodeStateStore states, Guid nodeId)
    {
        var extra = (await states.GetAsync(nodeId))?.Extra;
        if (extra is null || !extra.TryGetValue(NodeStateExtraKeys.DekBridge, out var bridge)) return null;
        if (bridge == NodeStateExtraKeys.Unknown) return NodeStateExtraKeys.Unknown;
        if (bridge != "1") return null;
        return extra.TryGetValue(NodeStateExtraKeys.V1Doors, out var doors) ? $"{doors} old door(s)" : "old doors";
    }

    /// <summary>The plain reminders of the close (plan E4 S5).</summary>
    public static readonly IReadOnlyList<string> Reminders =
    [
        "Snapshot files made before this removal can no longer be restored on this network: the master key they were made with is gone.",
        "If this person knew the passwords of where the bank is hosted (servers, cloud accounts, backup storage), change those passwords now.",
        "A stolen device of somebody who stays keeps its access until you revoke it (Admin > Devices): this removal takes away only what this person held.",
        "Which tool answers which theft: a copied database or backup of a node lets somebody pose as that node - give that node a new identity key " +
        "(Admin > Identity key, or bmb node rekey). A copy of the whole vault opens with the master key it was made with - the new master key " +
        "keeps what is written from now on out of it; to make every copy of the vault useless, re-key the vault offline (bmb rekey).",
    ];

    // ── shared ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<bool> IsRevokedAsync(IServiceProvider sp, Guid nodeId, Guid self)
    {
        if (nodeId == self) return false;
        var row = await sp.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(nodeId, includeDeleted: true);
        return row is null || row.Status == "R";
    }

    private async Task<bool> NoneWaitingAsync(Guid processId, int step) =>
        !(await new OffboardingStore(connFactory).NodesAsync(processId)).Any(r => r.Step == step && r.State == "waiting");

    /// <summary>
    /// "Revoke that node" (plan E5): the node is revoked network-wide and is never waited for again, by any open removal.
    /// </summary>
    public async Task RevokeNodeAsync(Guid processId, Guid nodeId, int actorUserId, CancellationToken ct = default)
    {
        using (var scope = scopes.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var self = (await sp.GetRequiredService<INodeIdentityRepository>().GetAsync())?.NodeId;
            if (nodeId == self)
                throw new OffboardingRefusedException("This node cannot revoke itself.");
            var record = await new OffboardingStore(connFactory).GetAsync(processId) ?? throw new OffboardingRefusedException("There is no such removal.", 404);
            if (!record.IsOpen) throw new OffboardingRefusedException("This removal is closed.");
            if (!(await new OffboardingStore(connFactory).NodesAsync(processId)).Any(r => r.NodeId == nodeId))
                throw new OffboardingRefusedException("This removal does not wait for that node.", 404);
            if (!session.IsUnlocked) throw new OffboardingRefusedException("Unlock this node first.");
            await RevokeDeviceAsync(sp, nodeId);
            await AuditAsync(sp, "offboarding_node_revoked", processId, $"Node {nodeId} was revoked by user #{actorUserId} instead of being waited for.");
        }
        await AdvanceAsync(processId, ct);
    }
}
