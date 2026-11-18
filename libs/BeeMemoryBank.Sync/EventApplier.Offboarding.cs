using System.Globalization;
using System.Text.Json;
using BeeMemoryBank.Core.Models;
using Dapper;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>
/// An <c>offboarding_state</c> that would create a process this node does not know, from a node that is not the accounts node of record here (security
/// review N2). Deferred, not refused: the designation of that node as the accounts node may still be on its way (held until the node reports its
/// state); once it is in force here, the process applies. From any other node it never applies.
/// </summary>
public sealed class OffboardingProcessNotFromAccountsNodeException(Guid eventId, Guid author)
    : UnauthorizedAccessException($"offboarding_state {eventId}: node {author} is not the accounts node here and cannot start a removal."),
      IDeferrableSyncFailure;

/// <summary>
/// A <c>node_rekey_request</c> for a process whose <c>offboarding_state</c> is not here yet: deferred and retried, not
/// refused - refused, the asked node would never serve it, and the driver never asks twice.
/// </summary>
public sealed class NodeRekeyRequestBeforeItsProcessException(Guid eventId, Guid processId)
    : InvalidOperationException($"node_rekey_request {eventId}: process {processId} is not known here yet."), IDeferrableSyncFailure;

/// <summary>
/// The two control events of the removal of a superadmin (release 2.12.0, plan WS-E and R1). Both are superadmin-only (the gate in
/// <see cref="EventApplier"/> refuses them from anybody else before this code runs).
/// </summary>
public partial class EventApplier
{
    /// <summary>The two outcomes a tombstone may name.</summary>
    public static readonly IReadOnlySet<string> RemovalOutcomes = new HashSet<string>(StringComparer.Ordinal) { "deleted", "demoted" };

    /// <summary>
    /// A tombstone: stored in <c>tbl_removed_person</c> (insert-or-ignore; a later <c>deleted</c> supersedes a <c>demoted</c>), then enforced on
    /// this node's own accounts through <see cref="Core.Interfaces.IRemovedPersonEnforcer"/> (where one is registered: full nodes).
    /// </summary>
    private async Task ApplyPersonRemovedAsync(SyncEvent evt)
    {
        var p = Deserialize<PersonRemovedPayload>(evt.Payload);
        if (!Guid.TryParse(p.PersonId, out var personId) || personId == Guid.Empty)
            throw new InvalidDataException($"person_removed {evt.EventId}: the person id is not an id.");
        if (!RemovalOutcomes.Contains(p.Outcome ?? ""))
            throw new InvalidDataException($"person_removed {evt.EventId}: the outcome is neither deleted nor demoted.");

        // A blind copy stores and relays the event and nothing else: it holds no account and must keep no row outside its operational tables.
        if (await nodeIdentityRepo.GetAsync() is { } me && BlindNodeId.IsBlind(me.NodeId)) return;

        using (var conn = connFactory.CreateConnection())
        {
            if (!await TableExistsAsync(conn, "tbl_removed_person")) return;   // a schema older than 051 (a blind copy that is behind): stored and relayed only
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_removed_person (person_id, outcome, removed_at, event_id) VALUES (@PersonId, @Outcome, @RemovedAt, @EventId)
                  ON CONFLICT(person_id) DO UPDATE SET outcome = excluded.outcome, removed_at = excluded.removed_at, event_id = excluded.event_id
                  WHERE tbl_removed_person.outcome = 'demoted' AND excluded.outcome = 'deleted'",
                new
                {
                    PersonId = Id(personId), p.Outcome,
                    RemovedAt = p.RemovedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    EventId = Id(evt.EventId),
                });
        }

        if (removedPersons is not null)
        {
            var changed = await removedPersons.EnforceAsync(personId);
            if (changed > 0)
                logger.LogWarning("A removed person's account was closed on this node ({Count} account(s)), as the tombstone of node {Node} says.", changed, evt.NodeId);
        }
    }

    /// <summary>
    /// The state of a removal process, newest event wins (by Lamport time). Stored as a replicated row of <c>tbl_offboarding</c>. Only the driver speaks
    /// (author == driver, checked below), and a row is changed only by a strictly newer event: this node's own echo carries the version its row
    /// already has and is skipped.
    /// <para>
    /// Who may speak for a process: the driver of record HERE advances it as before. Any other node - a take-over, or a rogue
    /// superadmin peer with a huge Lamport time closing a removal it does not drive - is heard only when that driver of record is revoked on this node,
    /// the precondition of a take-over (plan E2); otherwise its state is refused, the driver keeps driving and nothing about the person changes here.
    /// This node never counts itself as revoked, so a state from another node for a process this node drives is always refused (the node that drove
    /// it before a take-over is revoked everywhere else and cannot sync). A process this node does not know may be created only by the accounts node
    /// of record here, or by any superadmin node while no accounts node is designated here.
    /// </para>
    /// </summary>
    private async Task ApplyOffboardingStateAsync(SyncEvent evt)
    {
        var p = Deserialize<OffboardingStatePayload>(evt.Payload);
        if (!Guid.TryParse(p.ProcessId, out var processId) || processId == Guid.Empty
            || !Guid.TryParse(p.PersonId, out var personId) || personId == Guid.Empty
            || !Guid.TryParse(p.AccountsNodeId, out var accountsNode)
            || !Guid.TryParse(p.DriverNodeId, out var driver))
            throw new InvalidDataException($"offboarding_state {evt.EventId}: an id is not an id.");
        if (p.Target is not ("delete" or "demote"))
            throw new InvalidDataException($"offboarding_state {evt.EventId}: the target is neither delete nor demote.");
        if (p.Step is < 0 or > 6)
            throw new InvalidDataException($"offboarding_state {evt.EventId}: the step is out of range.");
        if (p.Outcome is not null && !RemovalOutcomes.Contains(p.Outcome))
            throw new InvalidDataException($"offboarding_state {evt.EventId}: the outcome is neither deleted nor demoted.");
        // Only the node that drives a process speaks for it.
        if (driver != evt.NodeId)
            throw new UnauthorizedAccessException($"offboarding_state {evt.EventId}: node {evt.NodeId} does not drive process {processId}.");

        var self = (await nodeIdentityRepo.GetAsync())?.NodeId;
        if (self is { } selfId && BlindNodeId.IsBlind(selfId)) return;
        var open = p.ClosedAt is null && p.Step < 6;
        using (var conn = connFactory.CreateConnection())
        {
            if (!await TableExistsAsync(conn, "tbl_offboarding")) return;
            using var tx = conn.BeginTransaction();
            var stored = await conn.QueryFirstOrDefaultAsync<(long Version, string Origin, string Driver)?>(
                "SELECT version, origin, driver_node_id FROM tbl_offboarding WHERE process_id = @Id",
                new { Id = Id(processId) }, tx);
            // What this node itself published about a process it drives is already in its row.
            var ownEcho = self is { } me && evt.NodeId == me;
            if (stored is { } row && (row.Version >= evt.LamportTs || ownEcho))
            {
                tx.Commit();
                return;
            }
            if (stored is { } known)
            {
                // Not the driver of record here: heard only once that driver is revoked here (the take-over precondition).
                if (Guid.TryParse(known.Driver, out var driverOfRecord) && driverOfRecord != evt.NodeId
                    && (driverOfRecord == self || await whitelistRepo.GetByNodeIdAsync(driverOfRecord, includeDeleted: true) is not { Status: WhitelistStatuses.Revoked }))
                    throw new UnauthorizedAccessException(
                        $"offboarding_state {evt.EventId}: node {evt.NodeId} does not drive process {processId} here, and its driver is not revoked here.");
            }
            else if (!ownEcho && await ReadAccountsHomeAsync() is { } home && home.NodeId != evt.NodeId)
            {
                throw new OffboardingProcessNotFromAccountsNodeException(evt.EventId, evt.NodeId);
            }
            // The row stays this node's own (origin local) only while this node drives it.
            var origin = stored is { } kept && self is { } selfNode && driver == selfNode ? kept.Origin : "replicated";

            var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_offboarding
                      (process_id, person_id, user_id, accounts_node_id, driver_node_id, target, step, state_json, started_by, started_at, updated_at,
                       closed_at, outcome, version, origin)
                  VALUES (@Id, @Person, NULL, @Accounts, @Driver, @Target, @Step, '{}', '', @StartedAt, @Now, @ClosedAt, @Outcome, @Version, 'replicated')
                  ON CONFLICT(process_id) DO UPDATE SET
                      driver_node_id = excluded.driver_node_id, step = excluded.step, updated_at = excluded.updated_at,
                      closed_at = excluded.closed_at, outcome = excluded.outcome, version = excluded.version, origin = @Origin",
                new
                {
                    Origin = origin,
                    Id = Id(processId), Person = Id(personId), Accounts = Id(accountsNode), Driver = Id(driver), p.Target, p.Step,
                    StartedAt = p.StartedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), Now = now,
                    ClosedAt = p.ClosedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), p.Outcome, Version = evt.LamportTs,
                }, tx);
            tx.Commit();
        }

        if (offboardingObserver is not null)
            await offboardingObserver.OnReplicatedStateAsync(processId, personId, open);
    }

    /// <summary>The reason a node gives its own re-key when a removal asks for it: <c>offboarding:&lt;process id&gt;</c>.</summary>
    public static string RemovalRekeyReason(Guid processId) => "offboarding:" + processId.ToString("D");

    /// <summary>
    /// The driver of a removal asks nodes for a new identity key (step S1). Checked here, served later: the asked node re-keys itself from the removal's
    /// background pass (<c>OffboardingService.ServeRekeyRequestsAsync</c>), never inside an apply (a re-key quiesces the event writes). Only the node that
    /// drives the process may ask, and the reason must name that process: the request is the same on every node and idempotent per process.
    /// </summary>
    private async Task ApplyNodeRekeyRequestAsync(SyncEvent evt)
    {
        var p = Deserialize<NodeRekeyRequestPayload>(evt.Payload);
        if (!Guid.TryParse(p.ProcessId, out var processId) || processId == Guid.Empty)
            throw new InvalidDataException($"node_rekey_request {evt.EventId}: the process id is not an id.");
        if (!string.Equals(p.Reason, RemovalRekeyReason(processId), StringComparison.Ordinal))
            throw new InvalidDataException($"node_rekey_request {evt.EventId}: the reason does not name the process.");
        if (p.Nodes is not { Count: > 0 } || p.Nodes.Any(n => !Guid.TryParse(n, out var id) || id == Guid.Empty || BlindNodeId.IsBlind(id)))
            throw new InvalidDataException($"node_rekey_request {evt.EventId}: the nodes are not full node ids.");

        if (await nodeIdentityRepo.GetAsync() is { } me && BlindNodeId.IsBlind(me.NodeId)) return;
        using var conn = connFactory.CreateConnection();
        if (!await TableExistsAsync(conn, "tbl_offboarding")) return;
        // The driver published the process (offboarding_state) before it asks: its row is here, and names the author as the driver. Not here yet
        // (the state failed on its own, or is held): deferred until it is.
        var driver = await conn.ExecuteScalarAsync<string?>("SELECT driver_node_id FROM tbl_offboarding WHERE process_id = @Id", new { Id = Id(processId) });
        if (driver is null)
            throw new NodeRekeyRequestBeforeItsProcessException(evt.EventId, processId);
        if (!Guid.TryParse(driver, out var driverId) || driverId != evt.NodeId)
            throw new UnauthorizedAccessException($"node_rekey_request {evt.EventId}: node {evt.NodeId} does not drive process {processId}.");
    }

    private static async Task<bool> TableExistsAsync(System.Data.IDbConnection conn, string table) =>
        await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table", new { table }) > 0;
}
