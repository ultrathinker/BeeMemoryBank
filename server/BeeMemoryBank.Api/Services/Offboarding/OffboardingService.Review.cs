using System.Globalization;
using System.Text.RegularExpressions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Vault.Sealed;
using Dapper;

namespace BeeMemoryBank.Api.Services.Offboarding;

/// <summary>One decision of the administrator in S3.</summary>
public sealed record OffboardingDecision(string ItemId, string Decision, string? Reason = null, OffboardingSuperadminTarget? Target = null);

/// <summary>What happens to another superadmin removed from the S3 review (that starts their own removal, which shares S1, S4 and S5).</summary>
public sealed record OffboardingSuperadminTarget(string Target, string? Role, IReadOnlyList<DemotionFolderRule>? FolderRules, bool AllowOpenRole);

/// <summary>
/// Step S3, devices and planted ways back (plan E4): one list of what remains and what the person could have planted while a superadmin. Unknown-owner
/// items and what the person created are pre-selected "remove"; keeping one needs a typed reason. Every item must be decided, and every "remove" carried
/// out, before the step is done.
/// </summary>
public sealed partial class OffboardingService
{
    /// <summary>The longest reason a "keep" may carry.</summary>
    public const int MaxReasonLength = 500;

    private static readonly Regex ByUser = new(@"\bby user #?(\d+)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Promoted = new(@"\brole changed from \S+ to superadmin\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OpenedByUser = new(@"the slot of user '.*' \(#(\d+)\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The kind of the review item that names the accounts node: kept or not, it is never "removed" here.</summary>
    public const string AccountsHomeKind = "accounts-home";

    private async Task RunReviewStepAsync(IServiceProvider sp, OffboardingRecord record, CancellationToken ct)
    {
        if (record.Progress.S5Done) return;
        await RefreshReviewAsync(sp, record);
        if (record.Progress.S3Done) return;
        var review = record.Review!;   // built by RefreshReviewAsync; a relisted item changes this same list
        var removals = review.Where(i => i.Decision == "remove" && !i.Applied && i.Kind != "superadmin").ToList();
        foreach (var item in removals)
            await ApplyRemovalAsync(sp, record, item, ct);
        // The basis was taken before these removals changed the network: kept, a device that comes back active would bring
        // the network back to exactly that basis, and the review would never look again. The next pass takes the basis as it is after them.
        if (removals.Any(i => i.Applied)) record.Progress.ReviewBasis = null;
        if (review.All(i => i.Decision == "keep" || (i.Decision == "remove" && i.Applied)))
        {
            record.Progress.S3Done = true;
            record.Progress.S3DoneAt = DateTime.UtcNow;
            await AuditAsync(sp, "offboarding_review_done", record.ProcessId,
                $"Review done: {review.Count(i => i.Decision == "remove")} item(s) removed, {review.Count(i => i.Decision == "keep")} kept.");
        }
    }

    /// <summary>
    /// The decisions of S3. A "keep" of a pre-selected item needs a reason (stored in the process and in the audit). <paramref name="acceptDefaults"/>
    /// decides every item still open as it is pre-selected (a "keep" of a pre-selected "remove" is never taken by default). Removing another superadmin
    /// starts that person's removal with <paramref name="password"/>.
    /// </summary>
    public async Task ResolveReviewAsync(Guid processId, IReadOnlyList<OffboardingDecision> decisions, bool acceptDefaults, int actorUserId,
        string? password = null, CancellationToken ct = default)
    {
        var joined = new List<(OffboardingReviewItem Item, OffboardingSuperadminTarget Target)>();
        await Gate.WaitAsync(ct);
        try
        {
            var store = new OffboardingStore(connFactory);
            var record = await store.GetAsync(processId) ?? throw new OffboardingRefusedException("There is no such removal.", 404);
            if (!record.IsOpen) throw new OffboardingRefusedException("This removal is closed.");
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var self = (await sp.GetRequiredService<INodeIdentityRepository>().GetAsync())?.NodeId;
            if (record.DriverNodeId != self || record.Origin != "local")
                throw new OffboardingRefusedException("This removal is driven by another node: decide there, or take it over.");
            if (!record.Progress.S0Done) throw new OffboardingRefusedException("The lock-out of this removal has not finished yet.");
            // Saved at once: a decision refused below (an item the rebuilt review no longer has) still leaves the page the review as it is now.
            if (await RefreshReviewAsync(sp, record)) await store.SaveAsync(record);
            var actor = await sp.GetRequiredService<IUserRepository>().GetByIdAsync(actorUserId);
            var who = actor is null ? "a local caller" : $"{actor.DisplayName} ({actor.Username})";

            foreach (var d in decisions)
            {
                var item = record.Review!.FirstOrDefault(i => i.Id == d.ItemId)
                    ?? throw new OffboardingRefusedException("That item is not on this review.", 404);
                if (item.Applied) continue;   // a removal already carried out stays done
                var decision = d.Decision?.Trim().ToLowerInvariant();
                if (decision is not ("remove" or "keep"))
                    throw new OffboardingRefusedException("Each item is either removed or kept.", 400);
                var reason = d.Reason?.Trim();
                if (decision == "remove" && item.Kind == AccountsHomeKind)
                    throw new OffboardingRefusedException("The accounts node is not removed in the review. Make another node the accounts node " +
                        "(Admin > Devices, Accounts node), or remove that node with its own item; then decide this item again.", 400);
                if (decision == "keep" && item.DefaultRemove && string.IsNullOrEmpty(reason))
                    throw new OffboardingRefusedException($"Say why '{item.Label}' stays: it was pre-selected for removal.", 400);
                if (reason is { Length: > MaxReasonLength })
                    throw new OffboardingRefusedException($"A reason is at most {MaxReasonLength} characters.", 400);
                if (decision == "remove" && item.Kind == "superadmin")
                {
                    if (d.Target is null || string.IsNullOrEmpty(password))
                        throw new OffboardingRefusedException("Removing another superadmin starts their removal: say what happens to their account and confirm with your password.", 400);
                    joined.Add((item, d.Target));
                }
                item.Decision = decision;
                item.Reason = reason;
                item.DecidedBy = who;
                await AuditAsync(sp, decision == "keep" ? "offboarding_review_kept" : "offboarding_review_removed", processId,
                    decision == "keep"
                        ? $"'{Printable(item.Label)}' kept by {Printable(who)}{(string.IsNullOrEmpty(reason) ? "" : $": {Printable(reason)}")}"
                        : $"'{Printable(item.Label)}' to be removed, by {Printable(who)}");
            }

            // "Decide every open item as pre-selected" never decides a superadmin: each one needs a human's own decision -
            // a puppet promoted before the audit window is not pre-selected, and a default "keep" would hide it. Nor an accounts node that is not
            // this node and was pre-selected. The one superadmin item it takes is the deciding administrator's own: it can only be kept (nobody
            // removes themselves), and the administrator is the one looking.
            if (acceptDefaults)
                foreach (var item in record.Review!.Where(i => i.Decision is null && !NeedsOwnDecision(i)))
                {
                    item.Decision = item.DefaultRemove ? "remove" : "keep";
                    item.DecidedBy = who;
                }
            if (record.Review!.FirstOrDefault(i => i.Id == $"superadmin:{actorUserId}" && i.Decision is null) is { } own && actor is not null)
            {
                own.Decision = "keep";
                own.Reason = "The administrator who decides this review.";
                own.DecidedBy = who;
            }
            await store.SaveAsync(record);
        }
        finally
        {
            Gate.Release();
        }

        // Another superadmin removed from here: their own process starts (it shares S1, S4 and S5 with this one).
        foreach (var (item, target) in joined)
        {
            var userId = int.Parse(item.Id["superadmin:".Length..], CultureInfo.InvariantCulture);
            try
            {
                await StartAsync(new OffboardingStartRequest(userId, target.Target, target.Role, target.FolderRules, target.AllowOpenRole, password!), actorUserId, ct);
            }
            catch (OffboardingRefusedException ex) when (ex.Message == "This person is already being removed.")
            {
                // Started before (a second click, a resumed call): the item is done all the same.
            }
            catch (Exception ex) when (ex is OffboardingRefusedException or InvalidOperationException or ArgumentException)
            {
                // Their removal did not start (a mistyped password, a passing refusal): the decision is taken back, so the page offers the
                // item again instead of showing it decided and never carried out. The refusal goes to the caller as it is.
                await ResetDecisionAsync(processId, item.Id);
                throw;
            }
            await MarkAppliedAsync(processId, item.Id);
        }
        await AdvanceAsync(processId, ct);
    }

    /// <summary>An item "decide as pre-selected" leaves open: every superadmin, and an accounts node pre-selected for a look.</summary>
    public static bool NeedsOwnDecision(OffboardingReviewItem item) =>
        item.Kind == "superadmin" || (item.Kind == AccountsHomeKind && item.DefaultRemove);

    /// <summary>
    /// The review follows the network while the process is open: when the devices, the accounts, the accounts node, the
    /// unknown-owner slots and agents or the invitations changed since it was built, it is built again. New items are added undecided, decisions
    /// taken stay, an undecided item whose subject is gone is dropped; a new item re-opens a review that was done (before the close).
    /// <para>
    /// A device this review revoked that is active again (re-joined with its own key through a node that had not applied the
    /// revocation yet, claiming to be shared) is a new item as well: undecided again, pre-selected "remove", and the review re-opens.
    /// </para>
    /// </summary>
    /// <returns>True when the review was built (again).</returns>
    private async Task<bool> RefreshReviewAsync(IServiceProvider sp, OffboardingRecord record)
    {
        var basis = await ReviewBasisAsync();
        if (record.Review is not null && record.Progress.ReviewBasis == basis) return false;
        var fresh = await BuildReviewAsync(sp, record);
        record.Progress.ReviewBasis = basis;
        if (record.Review is null)
        {
            record.Review = fresh;
            return true;
        }
        var added = new List<string>();
        foreach (var item in fresh)
        {
            var existing = record.Review.FirstOrDefault(i => i.Id == item.Id);
            if (existing is null)
            {
                record.Review.Add(item);
                added.Add(item.Label);
            }
            else if (existing is { Kind: "device", Decision: "remove", Applied: true })
            {
                // Listed only while active: revoked by this review, and back.
                existing.Label = item.Label;
                existing.Why = "Removed by this review, and active again since.";
                existing.DefaultRemove = true;
                existing.Decision = null;
                existing.Reason = null;
                existing.DecidedBy = null;
                existing.Applied = false;
                added.Add(item.Label);
            }
            else if (existing.Decision is null)
            {
                existing.Label = item.Label;
                existing.Why = item.Why;
                existing.DefaultRemove = item.DefaultRemove;
            }
        }
        record.Review.RemoveAll(i => i.Decision is null && !fresh.Any(f => f.Id == i.Id));
        if (added.Count == 0) return true;
        await AuditAsync(sp, "offboarding_review_rebuilt", record.ProcessId,
            $"{added.Count} new item(s) on the review: {Printable(string.Join(", ", added.Take(10)))}{(added.Count > 10 ? ", ..." : "")}.");
        if (record.Progress.S3Done)
        {
            record.Progress.S3Done = false;
            await AuditAsync(sp, "offboarding_review_reopened", record.ProcessId, "The review is open again: something new appeared while the removal runs.");
        }
        return true;
    }

    /// <summary>A digest of what the review is built from; cheap enough for every pass.</summary>
    internal async Task<string> ReviewBasisAsync()
    {
        using var conn = connFactory.CreateConnection();
        var parts = new List<string>();
        foreach (var sql in new[]
                 {
                     "SELECT node_id || '|' || ownership || '|' || COALESCE(owner_person_id, '') || '|' || is_superadmin FROM tbl_whitelist WHERE status = 'A' ORDER BY node_id",
                     "SELECT id || '|' || role || '|' || person_id || '|' || is_active || '|' || COALESCE(account_state, '') FROM tbl_user ORDER BY id",
                     "SELECT COALESCE(node_id, '') FROM tbl_accounts_home",
                     "SELECT slot_id || '|' || slot_type || '|' || COALESCE(owner_user_id, '') FROM tbl_key_slot ORDER BY slot_id",
                     "SELECT id || '|' || status || '|' || COALESCE(owner_user_id, '') || '|' || (encrypted_dek IS NOT NULL) FROM tbl_agent ORDER BY id",
                     "SELECT invite_id || '|' || state || '|' || COALESCE(redeemer_node_id, '') FROM tbl_blind_invite ORDER BY invite_id",
                 })
            parts.Add(string.Join(";", await conn.QueryAsync<string>(sql)));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("#", parts))));
    }

    private async Task ResetDecisionAsync(Guid processId, string itemId)
    {
        await Gate.WaitAsync();
        try
        {
            var store = new OffboardingStore(connFactory);
            var record = await store.GetAsync(processId);
            if (record?.Review?.FirstOrDefault(i => i.Id == itemId) is not { Applied: false } item) return;
            item.Decision = null;
            item.Reason = null;
            item.DecidedBy = null;
            await store.SaveAsync(record);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task MarkAppliedAsync(Guid processId, string itemId)
    {
        await Gate.WaitAsync();
        try
        {
            var store = new OffboardingStore(connFactory);
            var record = await store.GetAsync(processId);
            if (record?.Review?.FirstOrDefault(i => i.Id == itemId) is not { } item) return;
            item.Applied = true;
            if (item is { Kind: "device", Decision: "remove" }) item.RemovedOnce = true;
            await store.SaveAsync(record);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>An item of an account that is a superadmin now: the superadmin item it has to be (undecided, pre-selected "remove"), once.</summary>
    private static async Task RelistAsSuperadminAsync(IServiceProvider sp, OffboardingRecord record, OffboardingReviewItem item, User account)
    {
        var superadminId = $"superadmin:{account.Id}";
        if (record.Review!.Any(i => i.Id == superadminId))
            record.Review!.Remove(item);
        else
        {
            item.Id = superadminId;
            item.Kind = "superadmin";
            item.Label = $"{account.DisplayName} ({account.Username})";
            item.Why = "An account this person created, made a superadmin after this list was made.";
            item.DefaultRemove = true;
            item.Decision = null;
            item.Reason = null;
            item.DecidedBy = null;
            item.Applied = false;
        }
        await AuditAsync(sp, "offboarding_item_relisted", record.ProcessId,
            $"'{Printable(account.Username)}' is a superadmin now: listed again as one, to be decided.");
    }

    /// <summary>Carries out one "remove". A refusal (a last-door guard) leaves the item not applied, with the reason in its label for the page.</summary>
    private async Task ApplyRemovalAsync(IServiceProvider sp, OffboardingRecord record, OffboardingReviewItem item, CancellationToken ct)
    {
        var id = item.Id[(item.Kind.Length + 1)..];
        try
        {
            switch (item.Kind)
            {
                case "device":
                    await RevokeDeviceAsync(sp, Guid.Parse(id));
                    break;
                case "slot":
                {
                    var slotId = int.Parse(id, CultureInfo.InvariantCulture);
                    if ((await sp.GetRequiredService<IKeySlotRepository>().GetAllAsync()).Any(s => s.SlotId == slotId))
                        await sp.GetRequiredService<KeyManagementService>().RemoveSlotAsync(slotId, $"removal {record.ProcessId:D}");
                    break;
                }
                case "agent":
                {
                    var agentId = int.Parse(id, CultureInfo.InvariantCulture);
                    var agents = sp.GetRequiredService<IAgentRepository>();
                    if (await agents.GetByIdAsync(agentId) is not null)
                    {
                        var mirror = sp.GetService<IUnlockMaterialMirror>();
                        if (mirror != null) await mirror.RetireDoorAsync(BootDoorKinds.Agent, agentId, ct);
                        await agents.DeleteAsync(agentId);
                        await SlotDoors.RewriteAsync(mirror);
                    }
                    break;
                }
                case "invite":
                {
                    var invites = sp.GetRequiredService<IBlindInviteRepository>();
                    var now = DateTime.UtcNow;
                    if (!await invites.TryWithdrawAsync(id, now, "removal"))
                        await invites.TryRefuseAsync(id, now, "removal");
                    break;
                }
                case "user":
                {
                    var userId = int.Parse(id, CultureInfo.InvariantCulture);
                    var account = await sp.GetRequiredService<IUserRepository>().GetByIdAsync(userId);
                    if (account is { IsActive: true, Role: UserRoles.Superadmin })
                    {
                        // Made a superadmin after the list was made: deleting an ordinary account deletes nothing here, so the item is
                        // not done. It is listed again as the superadmin it is, undecided: removing a superadmin starts their own removal.
                        await RelistAsSuperadminAsync(sp, record, item, account);
                        return;
                    }
                    if (account is { IsActive: true })
                        await sp.GetRequiredService<UserService>().DeleteUserAsync(userId);
                    break;
                }
            }
            item.Applied = true;
            if (item.Kind == "device") item.RemovedOnce = true;
            item.Why = item.Why.Replace(" (could not be removed yet)", "", StringComparison.Ordinal);
            await AuditAsync(sp, "offboarding_item_removed", record.ProcessId, $"'{Printable(item.Label)}' removed.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            if (!item.Why.EndsWith(" (could not be removed yet)", StringComparison.Ordinal)) item.Why += " (could not be removed yet)";
            logger.LogWarning("An item of removal {Process} could not be removed now ({Failure}).", record.ProcessId, ex.GetType().Name);
        }
    }

    /// <summary>
    /// The review list (plan E4 S3): the devices that remain (with ownership), every other superadmin, the unknown-owner set, auto-unlock agents, open
    /// invitations, and what the person created while a superadmin (accounts created or promoted, devices joined with their door, recovery keys and
    /// agents made for others), from the records and the audit log.
    /// </summary>
    private async Task<List<OffboardingReviewItem>> BuildReviewAsync(IServiceProvider sp, OffboardingRecord record)
    {
        var items = new Dictionary<string, OffboardingReviewItem>(StringComparer.Ordinal);
        void Add(string id, string kind, string label, string why, bool remove)
        {
            if (items.TryGetValue(id, out var existing))
            {
                if (remove && !existing.DefaultRemove) { existing.DefaultRemove = true; existing.Why = why; }
                return;
            }
            items[id] = new OffboardingReviewItem { Id = id, Kind = kind, Label = label, Why = why, DefaultRemove = remove };
        }

        var person = record.UserId;
        var self = (await sp.GetRequiredService<INodeIdentityRepository>().GetAsync())?.NodeId;
        using var conn = connFactory.CreateConnection();

        // What the person did, from the audit log (the entries name the acting user by id).
        var createdUsers = new HashSet<int>();
        var joinedDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ownershipSetDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var createdAgents = new HashSet<int>();
        var homeSetBy = new Dictionary<string, (string? UserId, string At)>(StringComparer.OrdinalIgnoreCase);
        var audit = await conn.QueryAsync<(string Action, string EntityId, string? Details, string CreatedAt)>(
            @"SELECT action, entity_id, details, created_at FROM tbl_audit_log
               WHERE action IN ('user_created', 'user_updated', 'node_joined', 'agent_created', 'peer_ownership_updated', 'accounts_home_set') ORDER BY id");
        foreach (var (action, entityId, details, createdAt) in audit)
        {
            if (details is null) continue;
            if (action == "accounts_home_set")
            {
                // Who designated the accounts node, and when: the latest designation of each node.
                var setter = ByUser.Match(details);
                homeSetBy[entityId] = (setter.Success ? setter.Groups[1].Value : null, createdAt);
                continue;
            }
            if (person is not { } pid) continue;
            if (action == "node_joined")
            {
                var m = OpenedByUser.Match(details);
                if (m.Success && m.Groups[1].Value == pid.ToString(CultureInfo.InvariantCulture)) joinedDevices.Add(entityId);
                continue;
            }
            {
                var by = ByUser.Match(details);
                if (!by.Success || by.Groups[1].Value != pid.ToString(CultureInfo.InvariantCulture)) continue;
                if (action == "peer_ownership_updated")
                {
                    // The person said whose a device is (a copy of their own marked shared).
                    ownershipSetDevices.Add(entityId);
                    continue;
                }
                // A promotion is the applied role changing to superadmin, not a request that named it: a "change" to the role an account
                // already had promotes nobody. The audit log is what the review reads, and it is pruned (90 days by default): older actions are not seen.
                if (action == "user_created" || (action == "user_updated" && Promoted.IsMatch(details)))
                {
                    if (int.TryParse(entityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u)) createdUsers.Add(u);
                }
                else if (action == "agent_created" && int.TryParse(entityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a))
                    createdAgents.Add(a);
            }
        }

        // Devices that remain. Pre-selected: a device of this person by the replicated whitelist row (owner_person_id - an invariant, not an audit
        // guess: also a device joined elsewhere with their door); a device that joined after the lock-out
        // (through their door on a node that had not applied the lock-out yet, even one that claimed to be shared - the opener of a join is
        // only in that node's own audit); one joined here with their password; one whose ownership they set.
        var devices = await sp.GetRequiredService<IWhitelistRepository>().GetAllActiveAsync();
        // Devices enrolled through an invitation this person made, blind copy or server, in ANY state of the invitation: the
        // lock-out marks the person's redeemed invitations refused, which must not drop the device that already joined through one.
        var invitedDevices = person is { } pi
            ? (await conn.QueryAsync<string>(
                "SELECT redeemer_node_id FROM tbl_blind_invite WHERE created_by = @p AND redeemer_node_id IS NOT NULL",
                new { p = pi.ToString(CultureInfo.InvariantCulture) })).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in devices)
        {
            if (w.NodeId == self) continue;
            var ownership = w.Ownership switch
            {
                OwnershipKinds.Shared => "shared",
                OwnershipKinds.Personal => $"personal of {w.OwnerLabel ?? "a person"}",
                _ => "owner unknown",
            };
            var kind = BlindNodeId.IsBlind(w.NodeId) ? "blind copy" : w.IsSuperadmin ? "superadmin device" : "full copy";
            var id = w.NodeId.ToString("D");
            var why = w.OwnerPersonId == record.PersonId ? "A device of this person."
                : invitedDevices.Contains(id) ? "Joined through an invitation this person made."
                : record.Progress.S0Done && w.CreatedAt > record.Progress.S0At ? "Joined after this removal began."
                : joinedDevices.Contains(id) ? "Joined with this person's password."
                : ownershipSetDevices.Contains(id) ? "This person said whose device it is."
                : null;
            Add($"device:{id}", "device", $"{w.DisplayName} ({kind}, {ownership})", why ?? "A device of the bank.", why is not null);
        }

        // The accounts node: which node the shared nodes take their superadmins from, and who made it that. Pre-selected
        // for a look when it is another node and this person designated it, owns it, or it is pre-selected itself.
        if (await sp.GetRequiredService<IAccountsHomeRepository>().GetAsync() is { } home)
        {
            var homeId = home.NodeId.ToString("D");
            var homeRow = devices.FirstOrDefault(d => d.NodeId == home.NodeId);
            var homeName = home.NodeId == self ? "this node" : homeRow?.DisplayName ?? homeId;
            string designated;
            var byPerson = false;
            if (homeSetBy.TryGetValue(homeId, out var set))
            {
                var setterName = set.UserId is { } uid && int.TryParse(uid, NumberStyles.Integer, CultureInfo.InvariantCulture, out var setterId)
                    ? await sp.GetRequiredService<IUserRepository>().GetByIdAsync(setterId) is { } setter ? $"{setter.DisplayName} ({setter.Username})" : $"user #{uid}"
                    : "the system";
                byPerson = person is { } p0 && set.UserId == p0.ToString(CultureInfo.InvariantCulture);
                designated = $"Made the accounts node by {setterName} on {set.At[..Math.Min(10, set.At.Length)]}.";
            }
            else
            {
                var source = home.SourceNodeId is { } src
                    ? src == self ? "this node" : devices.FirstOrDefault(d => d.NodeId == src)?.DisplayName ?? src.ToString("D")
                    : "an unknown node";
                designated = $"Made the accounts node on {source} (who did it is in that node's audit log).";
            }
            var suspicious = home.NodeId != self && (byPerson || homeRow?.OwnerPersonId == record.PersonId
                                                     || (items.TryGetValue($"device:{homeId}", out var homeItem) && homeItem.DefaultRemove));
            Add($"{AccountsHomeKind}:{homeId}", AccountsHomeKind, $"Accounts node: {homeName}",
                "The shared nodes take their list of superadmins from it. " + designated +
                (suspicious ? " This person set it up or owns it: if it is not one of the organisation's own servers, make another node the accounts node." : ""),
                suspicious);
        }

        // Every other superadmin.
        foreach (var u in await sp.GetRequiredService<IUserRepository>().ListActiveAsync())
        {
            if (u.Role != UserRoles.Superadmin || u.Id == person || u.IsBeingRemoved) continue;
            var planted = createdUsers.Contains(u.Id);
            Add($"superadmin:{u.Id}", "superadmin", $"{u.DisplayName} ({u.Username})",
                planted ? "Made a superadmin by this person." : "A superadmin of the bank.", planted);
        }

        // Ordinary accounts the person created.
        foreach (var u in await sp.GetRequiredService<IUserRepository>().ListActiveAsync())
            if (u.Role != UserRoles.Superadmin && createdUsers.Contains(u.Id))
                Add($"user:{u.Id}", "user", $"{u.DisplayName} ({u.Username})", "An account this person created.", true);

        // The unknown-owner set (plan B2): ownerless recovery keys, the un-migrated legacy password slot, ownerless agents.
        foreach (var (slotId, type, createdBy) in await conn.QueryAsync<(long, string, long?)>(
                     @"SELECT slot_id, slot_type, created_by_user_id FROM tbl_key_slot
                        WHERE (slot_type = 'recovery' AND (owner_user_id IS NULL OR owner_user_id <= 0)) OR slot_type = 'password'"))
            Add($"slot:{slotId}", "slot", type == "password" ? "The old shared master password (from before user accounts)" : $"Recovery key #{slotId} (owner unknown)",
                type == "password" ? "Anybody who knew the old master password can still open this node with it." : "Nobody is on record as its owner.", true);
        foreach (var (agentId, name) in await conn.QueryAsync<(long, string)>(
                     "SELECT id, name FROM tbl_agent WHERE status = 'A' AND (owner_user_id IS NULL OR owner_user_id <= 0)"))
            Add($"agent:{agentId}", "agent", $"AI agent '{name}' (owner unknown)", "Nobody is on record as its owner.", true);

        // Recovery keys the person made for others (an owner other than them).
        if (person is { } p2)
            foreach (var slotId in await conn.QueryAsync<long>(
                         "SELECT slot_id FROM tbl_key_slot WHERE slot_type = 'recovery' AND created_by_user_id = @p AND owner_user_id IS NOT NULL AND owner_user_id <> @p",
                         new { p = p2 }))
                Add($"slot:{slotId}", "slot", $"Recovery key #{slotId}", "Issued by this person.", true);

        // Auto-unlock agents (they open a locked node) and agents the person made.
        foreach (var (agentId, name, ownerName, createdBy) in await conn.QueryAsync<(long, string, string?, long?)>(
                     @"SELECT a.id, a.name, u.display_name, a.created_by_user_id FROM tbl_agent a LEFT JOIN tbl_user u ON u.id = a.owner_user_id
                        WHERE a.status = 'A' AND a.owner_user_id > 0 AND (a.encrypted_dek IS NOT NULL OR a.created_by_user_id = @p)",
                     new { p = person ?? -1 }))
        {
            var planted = createdBy == person || createdAgents.Contains((int)agentId);
            Add($"agent:{agentId}", "agent", $"AI agent '{name}' of {ownerName ?? "an account"}",
                planted ? "Made by this person." : "Can open this node while it is locked.", planted);
        }
        foreach (var agentId in createdAgents)
            if (await sp.GetRequiredService<IAgentRepository>().GetByIdAsync(agentId) is { } agent)
                Add($"agent:{agentId}", "agent", $"AI agent '{agent.Name}'", "Made by this person.", true);

        // Open invitations to blind copies.
        foreach (var (inviteId, label, createdBy) in await conn.QueryAsync<(string, string, string)>(
                     "SELECT invite_id, label, created_by FROM tbl_blind_invite WHERE state IN ('issued', 'redeemed')"))
        {
            var planted = person is { } p3 && createdBy == p3.ToString(CultureInfo.InvariantCulture);
            Add($"invite:{inviteId}", "invite", $"Invitation '{label}'", planted ? "Made by this person." : "An invitation nobody has finished yet.", planted);
        }

        return items.Values.OrderByDescending(i => i.DefaultRemove).ThenBy(i => i.Kind, StringComparer.Ordinal).ThenBy(i => i.Label, StringComparer.Ordinal).ToList();
    }
}
