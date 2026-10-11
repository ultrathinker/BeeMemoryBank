using System.Data;
using System.Data.Common;
using System.Globalization;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>What the import hands an event to (the applier of the open node); a seam so that the import can be tested without a whole node.</summary>
public interface IRelayEventApplier
{
    Task<EventApplyResult> ApplyAsync(SyncEvent evt, ApplyContext context, CancellationToken ct = default);
}

/// <summary>The real applier: <see cref="EventApplier"/> inside the system scope, as a pushed event is applied (<c>POST /api/sync/events</c>).</summary>
public sealed class EventApplierRelayAdapter(EventApplier applier, CallerScopeHolder scopeHolder) : IRelayEventApplier
{
    public Task<EventApplyResult> ApplyAsync(SyncEvent evt, ApplyContext context, CancellationToken ct = default) =>
        scopeHolder.RunAsSystemAsync(() => applier.ApplyAsync(evt, context));
}

/// <summary>
/// The relay inbox's part of the open and close sequences (docs/design/step-b-spec.md 5.3, 5.4, 6.3, 6.6, 6.8): the sequence floor, the
/// replay of what a closed node received, and the pause of the relay's writes for a file copy.
/// </summary>
public sealed class RelayInboxImporter : ISealedRelayLifecycle
{
    /// <summary>The gap an open database leaves above its sequence numbers for the relay to number rows in after a crash (6.3).</summary>
    public const long FloorGap = 1_048_576;

    /// <summary>The floor is raised when the highest sequence number comes within this distance of it (6.3).</summary>
    public const long FloorRaiseMargin = 524_288;

    private const int PageSize = 100;

    private readonly IBootStore _boot;
    private readonly IRelayInbox _inbox;
    private readonly RelayWriteLock _writeLock;
    private readonly IDbConnectionFactory _vault;
    private readonly IServiceScopeFactory _scopes;
    private readonly IUnlockMaterialMirror? _mirror;
    private readonly IVaultGate? _gate;
    private readonly ILogger<RelayInboxImporter>? _logger;
    private readonly SemaphoreSlim _importing = new(1, 1);

    public RelayInboxImporter(IBootStore boot, IRelayInbox inbox, RelayWriteLock writeLock, IDbConnectionFactory vault,
        IServiceScopeFactory scopes, IUnlockMaterialMirror? mirror = null, IVaultGate? gate = null, ILogger<RelayInboxImporter>? logger = null)
    {
        _boot = boot;
        _inbox = inbox;
        _writeLock = writeLock;
        _vault = vault;
        _scopes = scopes;
        _mirror = mirror;
        _gate = gate;
        _logger = logger;
    }

    // ---- 6.3 sequence floor ---------------------------------------------------------------------------------------------------------

    public async Task OnOpenedAsync(CancellationToken ct = default)
    {
        var served = (await _inbox.GetSummaryAsync(ct).ConfigureAwait(false)).MaxRelaySeq;
        long raised;
        using (var connection = _vault.CreateConnection())
        {
            var (head, sequence) = ReadHeads(connection);
            raised = Math.Max(Math.Max(head, sequence), served);
            // Every append from here on, imported or authored, gets a number above every row a peer has been served.
            if (raised > sequence) SetSqliteSequence(connection, raised);
        }

        await _boot.SetMetaAsync(BootMetaKeys.SeqFloor, (raised + 1 + FloorGap).ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await _boot.SetMetaAsync(BootMetaKeys.CleanClose, "0", ct).ConfigureAwait(false);
        _logger?.LogInformation("The relay inbox is numbered above {Sequence} while the database is open.", raised);
    }

    public async Task OnClosingAsync(CancellationToken ct = default)
    {
        long head;
        long sequence;
        using (var connection = _vault.CreateConnection())
            (head, sequence) = ReadHeads(connection);

        // The floor is above every number ever given: sqlite_sequence is never below the head, and is above it when the last rows were
        // deleted (compaction) or the numbers were raised by the open (6.3).
        await _boot.SetMetaAsync(BootMetaKeys.SealedHead, head.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await _boot.SetMetaAsync(BootMetaKeys.SeqFloor, (Math.Max(head, sequence) + 1).ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await _boot.SetMetaAsync(BootMetaKeys.CleanClose, "1", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The 30 s timer of 6.3: while the database is open, keeps <c>seq_floor</c> above what the log can reach before the next check, so that a
    /// crash leaves a floor that is still above every sequence number. True when it raised the floor.
    /// </summary>
    public async Task<bool> RaiseFloorIfNeededAsync(CancellationToken ct = default)
    {
        long head;
        long sequence;
        using (var connection = _vault.CreateConnection())
            (head, sequence) = ReadHeads(connection);
        var top = Math.Max(head, sequence);

        var floor = long.TryParse(await _boot.GetMetaAsync(BootMetaKeys.SeqFloor, ct).ConfigureAwait(false), NumberStyles.None,
            CultureInfo.InvariantCulture, out var f) ? f : 0;
        if (top + FloorRaiseMargin <= floor) return false;

        await _boot.SetMetaAsync(BootMetaKeys.SeqFloor, (top + 1 + FloorGap).ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        return true;
    }

    private static (long Head, long Sequence) ReadHeads(IDbConnection connection) =>
        (connection.ExecuteScalar<long?>("SELECT MAX(sequence_num) FROM tbl_event") ?? 0,
         connection.ExecuteScalar<long?>("SELECT seq FROM sqlite_sequence WHERE name = 'tbl_event'") ?? 0);

    private static void SetSqliteSequence(IDbConnection connection, long value)
    {
        var updated = connection.Execute("UPDATE sqlite_sequence SET seq = @value WHERE name = 'tbl_event'", new { value });
        if (updated == 0) connection.Execute("INSERT INTO sqlite_sequence (name, seq) VALUES ('tbl_event', @value)", new { value });
    }

    // ---- 6.6 import -------------------------------------------------------------------------------------------------------------------

    private sealed class Counters
    {
        public int Applied;
        public int Held;
        public int AlreadyPresent;
        public int Refused;
    }

    public async Task<RelayImportResult> ImportAsync(CancellationToken ct = default)
    {
        await _importing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var counters = new Counters();
            using var scope = _scopes.CreateScope();
            var services = scope.ServiceProvider;

            // Passes without the lock first: a large inbox must not keep the pushers waiting. They end when the inbox is read to its end or
            // a deferrable failure stopped them.
            var stopped = await RunPassAsync(services, counters, ct).ConfigureAwait(false);

            // The last pass and the switch hold the relay's lock (6.6 step 5): a push that arrives meanwhile waits and lands after it.
            using (await _writeLock.EnterExclusiveAsync(ct).ConfigureAwait(false))
            {
                if (!stopped) await RunPassAsync(services, counters, ct).ConfigureAwait(false);
                await MergeReportsAsync(services, ct).ConfigureAwait(false);
                await RewriteMirrorAsync(ct).ConfigureAwait(false);
            }

            var left = await _inbox.GetSummaryAsync(ct).ConfigureAwait(false);
            return new RelayImportResult(counters.Applied, counters.Held, counters.AlreadyPresent, counters.Refused,
                (int)Math.Min(int.MaxValue, left.Events));
        }
        finally
        {
            _importing.Release();
        }
    }

    /// <summary>True when the pass stopped before the end of the inbox (a deferrable failure, or the database closed meanwhile).</summary>
    private async Task<bool> RunPassAsync(IServiceProvider services, Counters counters, CancellationToken ct)
    {
        // Blobs first (6.6 step 1): an event that names one finds it.
        var blobs = services.GetRequiredService<IBlobRepository>();
        foreach (var hash in await _inbox.ListBlobHashesAsync(ct).ConfigureAwait(false))
        {
            var data = await _inbox.GetBlobAsync(hash, ct).ConfigureAwait(false);
            if (data is null) continue;
            await blobs.StoreAsync(data).ConfigureAwait(false);
            await _inbox.DeleteBlobsAsync([hash], ct).ConfigureAwait(false);
        }

        var cursor = 0L;
        while (true)
        {
            var page = await _inbox.ReadAfterAsync(cursor, PageSize, ct).ConfigureAwait(false);
            if (page.Count == 0) return false;
            foreach (var row in page)
            {
                ct.ThrowIfCancellationRequested();
                // A database that closed meanwhile: nothing is read from it, nothing is deleted from the inbox.
                if (_gate is { IsSealed: true } && _gate.State is not (VaultState.Importing or VaultState.Open)) return true;
                if (!await ImportRowAsync(services, row, counters, ct).ConfigureAwait(false)) return true;
                cursor = row.RelaySeq;
            }
        }
    }

    /// <summary>False when the pass must stop here (the row and every later one stay, in order).</summary>
    private async Task<bool> ImportRowAsync(IServiceProvider services, RelayEventRow row, Counters counters, CancellationToken ct)
    {
        var evt = row.Event;
        var eventLog = services.GetRequiredService<IEventLogRepository>();

        // A repeat of a row the sealed log already holds (a crash between the apply and the delete of an earlier import, or a peer that
        // reached this node by two routes while it was closed): dropped from the inbox.
        if (await eventLog.ExistsAsync(evt.EventId).ConfigureAwait(false))
        {
            await _inbox.DeleteAsync([evt.EventId], ct).ConfigureAwait(false);
            counters.AlreadyPresent++;
            return true;
        }

        var quarantine = services.GetRequiredService<ISyncQuarantineRepository>();
        try
        {
            // Without Originators: the whitelist lookup runs on the sealed whitelist as it stands after the rows before this one (6.6 step 2).
            var result = await services.GetRequiredService<IRelayEventApplier>()
                .ApplyAsync(evt, new ApplyContext(ReceivedFrom: row.OriginPeer, ReceivedSeq: row.OriginSeq), ct).ConfigureAwait(false);
            switch (result)
            {
                case EventApplyResult.Held: counters.Held++; break;
                case EventApplyResult.SilentlyDropped: counters.Refused++; break;
                default: counters.Applied++; break;
            }
            try
            {
                await SyncEventQuarantine.ClearFailureAsync(quarantine, evt.EventId).ConfigureAwait(false);
            }
            catch (Exception clearEx) when (clearEx is not OperationCanceledException)
            {
                _logger?.LogWarning(clearEx, "Could not clear the failure streak of event {EventId}.", evt.EventId);
            }
            await _inbox.DeleteAsync([evt.EventId], ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is VaultSealedException or VaultBusyException)
        {
            _logger?.LogInformation("The database closed during the import; event {EventId} stays in the inbox.", evt.EventId);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var exhausted = false;
            try
            {
                exhausted = await SyncEventQuarantine.RecordFailureAsync(quarantine, evt.EventId, evt.EventType, evt.NodeId, ex).ConfigureAwait(false);
            }
            catch (Exception recordEx) when (recordEx is not OperationCanceledException)
            {
                _logger?.LogError(recordEx, "Could not record the failure of event {EventId}.", evt.EventId);
            }

            // A permanent failure of the event itself ends it here (the pull path quarantines it the same way); a failure of the storage under
            // it (a locked file, a full disk) is not the event's fault and is retried until the quarantine budget is spent, like a deferrable one.
            var storage = ex is DbException or IOException;
            if ((SyncFailureClassifier.Classify(ex) == SyncFailureKind.Permanent && !storage) || exhausted)
            {
                _logger?.LogWarning(ex, "Event {EventId} ({EventType}) received while the node was closed is refused at the import.", evt.EventId, evt.EventType);
                await _inbox.DeleteAsync([evt.EventId], ct).ConfigureAwait(false);
                counters.Refused++;
                return true;
            }

            _logger?.LogWarning(ex, "Event {EventId} ({EventType}) cannot be applied yet; it and the rows after it stay in the inbox.", evt.EventId, evt.EventType);
            return false;
        }
    }

    private async Task MergeReportsAsync(IServiceProvider services, CancellationToken ct)
    {
        var reports = await _inbox.ListReportsAsync(ct).ConfigureAwait(false);
        if (reports.Count == 0) return;

        var positions = services.GetRequiredService<ISyncPushPositionRepository>();
        var head = await services.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync().ConfigureAwait(false);
        foreach (var (peer, sequence) in reports)
        {
            // The rule of report-position (SyncEndpoints.cs): not above the head, not below what is recorded.
            var recorded = (await positions.GetAsync(peer).ConfigureAwait(false))?.ReportedSeq ?? 0;
            if (sequence > head || sequence < recorded) continue;
            await positions.RecordReportedPositionAsync(peer, sequence).ConfigureAwait(false);
        }
        await _inbox.ClearReportsAsync(ct).ConfigureAwait(false);
    }

    private async Task RewriteMirrorAsync(CancellationToken ct)
    {
        if (_mirror is null) return;
        try
        {
            await _mirror.RewriteAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort (4.3 rule 3): the watcher and the close rewrite it again.
            _logger?.LogWarning(ex, "The boot store could not be rewritten after the import.");
        }
    }

    // ---- 6.8 pause ---------------------------------------------------------------------------------------------------------------------

    public Task<IAsyncDisposable> PauseWritesAsync(CancellationToken ct = default)
    {
        if (_gate is not { State: VaultState.Closed })
            throw new InvalidOperationException("The relay's writes are paused only while the database is closed.");
        return _writeLock.PauseAsync(ct);
    }
}
