using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Embeddings;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// The nightly run and the sweep, every five minutes, modelled on ChatHistoryBackfillProcessor: waits until the vault
/// is usable and skips while it is locked (plan 3.5). Registered on a full node only. The loop's shape (the gate at its
/// top, the step interval, the closed-database catch) is fixed here because SealedApiGateTests reads it.
/// </summary>
public sealed class DreamScheduler(
    IServiceScopeFactory scopeFactory,
    DreamCoordinator coordinator,
    ILogger<DreamScheduler> logger,
    DreamEngineOptions? options = null) : BackgroundService
{
    private readonly DreamEngineOptions _options = options ?? DreamEngineOptions.Default;

    // Why the current window has not started yet: said by the skipped record if the window passes.
    private (DateTime WindowStart, string Reason)? _blocked;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup (chat.db schema, node init, first unlock) settle first, as the chat backfill does.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            // chat.db and the vault are not opened while a sealed node is closed (docs/design/step-b-spec.md 5.5).
            await scopeFactory.WaitUsableAsync(stoppingToken);

            try
            {
                await StepAsync(stoppingToken);
            }
            catch (VaultSealedException)
            {
                logger.LogDebug("A Dream scheduler step stopped: the database was closed");
            }
            catch (SessionLockedException)
            {
                logger.LogDebug("A Dream scheduler step stopped: the vault was locked");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error in a Dream scheduler step");
            }

            await Task.Delay(_options.Thresholds.SchedulerStep, stoppingToken);
        }
    }

    /// <summary>One step: the sweep, then the nightly run when its window is open and nothing stands in the way, or one
    /// skipped record for a window that passed without a start (plan 3.5).</summary>
    public async Task StepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var store = services.GetRequiredService<DreamStore>();
        var corpus = services.GetRequiredService<DreamCorpusBuilder>();
        var now = _options.Clock.GetUtcNow().UtcDateTime;

        await coordinator.FlushUnwrittenAsync(store);
        var swept = await store.SweepAsync(_options.Thresholds, coordinator.ActiveRunId, now, corpus.FindExistingArticlesAsync, ct);
        if (swept.RowsUnreadable + swept.RowsNamingDeleted + swept.RunsInterrupted + swept.AdviceReturned > 0)
            logger.LogInformation(
                "Dream sweep: {Unreadable} unreadable row(s) and {Deleted} row(s) naming deleted articles removed, {Interrupted} run(s) interrupted, {Returned} advice returned",
                swept.RowsUnreadable, swept.RowsNamingDeleted, swept.RunsInterrupted, swept.AdviceReturned);

        // Advice whose notes, folders or tags have changed since are expired here too (plan 3.5), not only when a list shows them.
        var expired = await services.GetRequiredService<DreamAdviceValidator>().SweepAsync(ct);
        if (expired > 0) logger.LogInformation("Dream sweep: {Expired} open advice no longer stood and was settled", expired);

        await ScheduleAsync(services, store, corpus, now, ct);
    }

    private async Task ScheduleAsync(IServiceProvider services, DreamStore store, DreamCorpusBuilder corpus, DateTime now, CancellationToken ct)
    {
        var settings = await store.GetSettingsAsync();
        if (!settings.ScheduleEnabled) return;
        if (DreamScheduleWindow.Latest(now, settings, _options.Clock.LocalTimeZone) is not { } window) return;
        if (await store.HasScheduledRunInWindowAsync(window.StartUtc, window.EndUtc)) return;

        if (now >= window.EndUtc)
        {
            // Turned on after this window had passed: the window never was the schedule's to miss.
            if (settings.UpdatedAt is { } updated && updated > window.StartUtc) return;

            // Nothing remembered means this process was not up to see the window, and a stopped node's vault is not open.
            var reason = _blocked is { } blocked && blocked.WindowStart == window.StartUtc ? blocked.Reason : DreamErrorCodes.Locked;
            await store.InsertSkippedRunAsync(Guid.NewGuid(), window.StartUtc, now, reason, settings);
            _blocked = null;
            return;
        }

        var obstacle = await ObstacleAsync(services, store, corpus, settings, ct);
        if (obstacle is null)
        {
            var started = await coordinator.StartAsync(new DreamStartCommand(DreamRunTrigger.Schedule, DreamRunMode.Normal, null, DeadlineUtc: window.EndUtc));
            if (started.RunId is { } runId)
            {
                await services.GetRequiredService<IAuditLogRepository>().LogAsync("dream", runId.ToString(), "dream_run_started", "system", $"Scheduled Dream run {runId} started");
                _blocked = null;
                return;
            }
            obstacle = started.Error == DreamStartError.Locked ? DreamErrorCodes.Locked : DreamErrorCodes.Busy;
        }
        _blocked = (window.StartUtc, obstacle);
    }

    private async Task<string?> ObstacleAsync(IServiceProvider services, DreamStore store, DreamCorpusBuilder corpus, DreamSettings settings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!services.GetRequiredService<SessionService>().IsUnlocked) return DreamErrorCodes.Locked;
        if (coordinator.ActiveRunId is not null || coordinator.IsHeavyOperationRunning) return DreamErrorCodes.Busy;
        if ((await store.CountOpenAdviceAsync()).Total >= settings.MaxOpenAdvice) return DreamErrorCodes.OpenListFull;

        // Vectors missing and no way of getting them: the run could only repeat what the rules said the night before.
        var coverage = await corpus.GetEmbeddingCoverageAsync(settings);
        if (coverage.Total > 0 && (double)coverage.WithoutVector / coverage.Total > _options.Thresholds.MaxShareWithoutVector && !await CanGenerateVectorsAsync(services))
            return DreamErrorCodes.NoEmbeddings;
        return null;
    }

    private static async Task<bool> CanGenerateVectorsAsync(IServiceProvider services)
    {
        if (services.GetService<PendingEmbeddingProcessor>() is null) return false;
        var identity = await services.GetRequiredService<INodeIdentityRepository>().GetAsync();
        return identity is { CanGenerateEmbeddings: true };
    }
}
