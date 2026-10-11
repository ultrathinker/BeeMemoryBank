using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Vault.Metadata;

/// <summary>
/// Applies the events this full node received but could not apply yet (docs/design/step-a-spec.md 6.7): in local sequence
/// order, one at a time through <see cref="EventApplier.ApplyHeldAsync"/>, until none is left, one still cannot apply, the
/// vault locks, or a network restore still in progress stands before the next row (the restore barrier: content received
/// after a restore is applied after it, as on a node that was unlocked). Runs after every unlock (chained after the DEK
/// rotation retry, so a key of the rotation just accepted opens first), on <see cref="RequestDrain"/> (a new key, a new
/// hold while unlocked) and every <see cref="Period"/> while unlocked rows wait. A <c>key</c> hold whose key no
/// <c>metadata_key_set</c> named within <see cref="UnknownKeyAfter"/> is settled as "unknown metadata key" (state 3).
/// One run at a time; a request during a run schedules one more.
/// </summary>
public sealed class HeldEventDrainService : IHeldEventDrain, IDisposable
{
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan UnknownKeyAfter = TimeSpan.FromDays(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly SessionService _session;
    private readonly IDbConnectionFactory _connFactory;
    private readonly IHeldEventStatus _status;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly ITimer _timer;
    private int _requested;

    public HeldEventDrainService(
        IServiceScopeFactory scopes,
        SessionService session,
        IDbConnectionFactory connFactory,
        IHeldEventStatus status,
        ILogger<HeldEventDrainService>? logger = null,
        TimeProvider? time = null)
    {
        _scopes = scopes;
        _session = session;
        _connFactory = connFactory;
        _status = status;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<HeldEventDrainService>.Instance;
        _timer = _time.CreateTimer(_ => OnTimer(), null, Period, Period);
    }

    /// <summary>Runs a drain soon. A locked vault drains at unlock anyway, so a request while locked is not kept.</summary>
    public void RequestDrain()
    {
        if (!_session.IsUnlocked) return;
        if (Interlocked.Exchange(ref _requested, 1) == 1) return; // one run is already scheduled
        _ = Task.Run(async () =>
        {
            try
            {
                await DrainAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Held event drain failed; it runs again at the next request, unlock or period");
            }
        });
    }

    /// <summary>One drain run; returns how many held rows it settled (applied, relayed, dropped by a gate or quarantined).</summary>
    public async Task<int> DrainAsync(CancellationToken ct = default)
    {
        await _running.WaitAsync(ct);
        try
        {
            // A request from here on schedules the next run.
            Interlocked.Exchange(ref _requested, 0);
            return await DrainCoreAsync(ct);
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task<int> DrainCoreAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var eventLog = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
        var applier = scope.ServiceProvider.GetRequiredService<EventApplier>();
        var settled = 0;
        while (_session.IsUnlocked && !ct.IsCancellationRequested)
        {
            var next = (await eventLog.GetHeldSequencesAsync(0, 1)).FirstOrDefault();
            if (next <= 0) break;

            if (await RestoreBarrierAsync() is { } barrier && barrier < next)
            {
                _logger.LogInformation("Held events wait behind the network restore at sequence {Sequence}", barrier);
                break;
            }

            var result = await applier.ApplyHeldAsync(next, ct);
            if (result.Outcome != HeldApplyOutcome.StillHeld)
            {
                settled++;
                continue;
            }
            if (result.HoldReason == HoldReasons.Key && await SettleUnknownKeyAsync(eventLog, next))
            {
                settled++;
                continue;
            }
            break;
        }
        if (settled > 0) _logger.LogInformation("Held event drain settled {Count} event(s)", settled);
        return settled;
    }

    /// <summary>The lowest sequence of a <c>restore_network</c> row whose restore is still Pending, Downloading or Applying.</summary>
    private async Task<long?> RestoreBarrierAsync()
    {
        using var conn = _connFactory.CreateConnection();
        return await conn.ExecuteScalarAsync<long?>(
            @"SELECT MIN(e.sequence_num) FROM tbl_event e
              JOIN tbl_restore_event_state s ON s.event_id = e.event_id COLLATE NOCASE
              WHERE e.event_type = @Type AND s.state IN ('PENDING', 'DOWNLOADING', 'APPLYING')",
            new { Type = EventTypes.RestoreNetwork });
    }

    /// <summary>
    /// A <c>key</c> hold older than <see cref="UnknownKeyAfter"/> whose key this node has no row for: no
    /// <c>metadata_key_set</c> named it, so it never will open (spec 6.7). A key row that exists but does not open yet (a
    /// rotation this node has not accepted) keeps the hold.
    /// </summary>
    private async Task<bool> SettleUnknownKeyAsync(IEventLogRepository eventLog, long sequence)
    {
        if (await eventLog.GetHeldAsync(sequence) is not { HoldReason: HoldReasons.Key, HeldAt: { } heldAt } row) return false;
        if (_time.GetUtcNow().UtcDateTime - DateTime.SpecifyKind(heldAt, DateTimeKind.Utc) < UnknownKeyAfter) return false;

        string keyId;
        try
        {
            keyId = SealedEnvelope.Parse(row.Event.Payload).KeyId;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        using (var conn = _connFactory.CreateConnection())
        {
            if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_metadata_key WHERE key_id = @keyId", new { keyId }) > 0)
                return false;
        }

        await eventLog.SetApplyStateAsync(sequence, EventApplyStates.QuarantinedAfterReceipt);
        _logger.LogWarning("Held event {EventId}: no metadata key {KeyId} arrived within {Days} days; settled as unknown metadata key",
            row.Event.EventId, keyId, (int)UnknownKeyAfter.TotalDays);
        return true;
    }

    private void OnTimer()
    {
        if (!_session.IsUnlocked) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (await _status.AnyHeldAsync()) RequestDrain();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Held event check failed");
            }
        });
    }

    public void Dispose() => _timer.Dispose();
}
