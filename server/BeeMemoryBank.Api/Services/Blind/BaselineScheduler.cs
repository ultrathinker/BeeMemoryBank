using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Api.Services;

/// <summary>Where this node's log ends now (the repositories behind it are scoped, the scheduler is not).</summary>
public interface IBaselineLogHead
{
    Task<long> GetAsync(CancellationToken ct = default);
}

public sealed class ScopedBaselineLogHead(IServiceScopeFactory scopes) : IBaselineLogHead
{
    public async Task<long> GetAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetMaxSequenceAsync();
    }
}

/// <summary>What the scheduler asks to be produced; <see cref="BaselineProducer"/> is the one implementation.</summary>
public interface IBaselineProducer
{
    Task<BaselineFiles> ProduceAsync(string reason, CancellationToken ct = default);
}

/// <summary>
/// When a full node produces a baseline (docs/design/step-a-spec.md 7.5). Early requests (activation, a hard delete, a protocol-3
/// straggler, a key rotation, a blind reseed, the Admin button) produce one at the next tick, but never within
/// <see cref="Debounce"/> of the previous attempt, so a burst of requests costs one baseline and the one after it waits its turn. Besides
/// that, an activated node whose newest baseline is older than <see cref="MaxAge"/> while its log grew since makes a fresh one, and an
/// activated node with none at all makes the first. Every precondition lives in the producer: a locked or held node is refused there and
/// asked again at the next tick, a failure of the baseline itself waits <see cref="Debounce"/> before the next attempt.
/// </summary>
public sealed class BaselineScheduler(
    IBaselineProducer producer,
    IBaselineSource source,
    ISyncProtocolPolicy policy,
    IBaselineLogHead logHead,
    ILogger<BaselineScheduler> logger,
    TimeProvider? time = null,
    IVaultGate? gate = null) : BackgroundService, IBaselineScheduler
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _lock = new();
    private string? _pendingReason;
    private long _requests;
    private DateTime _lastAttemptAt = DateTime.MinValue;
    private DateTime _nextFailureRetryAt = DateTime.MinValue;
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>The reason of the request waiting to be served, or null (a test and status reads it).</summary>
    public string? PendingReason
    {
        get { lock (_lock) return _pendingReason; }
    }

    public void RequestEarly(string reasonCode)
    {
        lock (_lock)
        {
            _pendingReason ??= reasonCode;
            _requests++;
        }
        try { _wake.Release(); } catch (SemaphoreFullException) { /* a wake-up is already waiting */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // An encrypted node whose database is closed has no baseline to make or to judge: wait until a door opens it (spec 5.5).
                await VaultGateWait.WaitUsableAsync(gate, stoppingToken);
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (VaultSealedException)
            {
                logger.LogDebug("Baseline scheduler: the database closed during the tick; waiting for the next unlock");
            }
            catch (Exception ex)
            {
                logger.LogWarning("Baseline scheduler tick failed: {Reason}", ex.Message);
            }

            // Sleep until the next tick, or until a request wakes the loop.
            using var sleep = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var woken = _wake.WaitAsync(sleep.Token);
            var tick = Task.Delay(TickInterval, _time, sleep.Token);
            await Task.WhenAny(woken, tick);
            sleep.Cancel();
            await Task.WhenAll(woken, tick).ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>One evaluation: produces a baseline when one is due. Returns whether one was produced.</summary>
    public async Task<bool> TickAsync(CancellationToken ct = default)
    {
        if (!policy.SealsMetadata) return false;
        var now = _time.GetUtcNow().UtcDateTime;

        string? reason;
        long seen;
        lock (_lock)
        {
            reason = _pendingReason;
            seen = _requests;
        }
        reason ??= await ScheduledReasonAsync(now);
        if (reason is null) return false;
        if (now - _lastAttemptAt < Debounce || now < _nextFailureRetryAt) return false;

        _lastAttemptAt = now;
        try
        {
            var files = await producer.ProduceAsync(reason, ct);
            // A request that came in while this one was made may need a later cut (a hard delete): it stays.
            lock (_lock)
            {
                if (_requests == seen) _pendingReason = null;
            }
            logger.LogInformation("Baseline {BaselineId} produced on schedule ({Reason})", files.Manifest.BaselineId, reason);
            return true;
        }
        catch (BaselineRefusedException ex) when (ex.Reason is BaselineRefusal.Locked or BaselineRefusal.EventsHeld or BaselineRefusal.NoMetadataKey)
        {
            // Not now: the vault is locked or waits for changes to apply. The request stays, and the next tick asks again.
            _lastAttemptAt = DateTime.MinValue;
            logger.LogDebug("Baseline ({Reason}) waits: {Why}", reason, ex.Reason);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _nextFailureRetryAt = now + Debounce;
            logger.LogWarning("Baseline ({Reason}) was not produced: {Why}", reason, ex.Message);
            return false;
        }
    }

    private async Task<string?> ScheduledReasonAsync(DateTime now)
    {
        var newest = await source.GetServableAsync();
        if (newest is null) return BaselineReasons.Scheduled;
        if (now - newest.Files.Manifest.CreatedAt < MaxAge) return null;
        return await logHead.GetAsync() > newest.Files.Manifest.ProducerLogHead ? BaselineReasons.Scheduled : null;
    }
}
