using System.Collections.Concurrent;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Dream;

/// <summary>Why a run did not start.</summary>
public enum DreamStartError
{
    None,
    Locked,
    Busy,
    AlreadyRunning,
    /// <summary>The request named a model that is not in the catalog, or asked for the model while none can be used.</summary>
    ModelNotReady,
    /// <summary>The built-in rules are off and the model cannot run (not asked for, or not ready): nothing would run.</summary>
    NothingToRun,
}

/// <param name="UserId">Who pressed Start; null for the schedule.</param>
/// <param name="DeadlineUtc">The end of the schedule's window; null: the manual time limit.</param>
/// <param name="UseRules">The built-in rules; the schedule always runs them.</param>
public sealed record DreamStartCommand(
    DreamRunTrigger Trigger,
    DreamRunMode Mode,
    int? UserId,
    bool? UseModel = null,
    Guid? ModelId = null,
    int? MaxAdvice = null,
    int? MaxMinutes = null,
    IReadOnlyList<DreamPhase>? Phases = null,
    DreamSampleRequest? Sample = null,
    DateTime? DeadlineUtc = null,
    bool UseRules = true);

public sealed record DreamStartResult(Guid? RunId, DreamStartError Error);

/// <summary>
/// Singleton: the one active run of the node (plan 3.5). It holds the <c>dream-run</c> lease of LongOperationGate for
/// the run's whole life, starts the run in its own DI scope with no request context, and ends it when Stop is pressed,
/// the vault locks or the host stops.
/// </summary>
public sealed class DreamCoordinator : IDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly LongOperationGate _operations;
    private readonly SessionService _session;
    private readonly MaintenanceModeService _maintenance;
    private readonly ILogger<DreamCoordinator> _logger;
    private readonly DreamEngineOptions _options;
    private readonly CancellationTokenRegistration _stopping;
    private readonly ConcurrentDictionary<Guid, (DreamRunResult Result, DreamRunCounters Counters)> _unwritten = new();
    private volatile ActiveRun? _active;

    private sealed record ActiveRun(Guid Id, DreamRunControl Control, DreamRunProgress Progress)
    {
        public Task Work { get; set; } = Task.CompletedTask;
    }

    public DreamCoordinator(IServiceScopeFactory scopes, LongOperationGate operations, SessionService session, MaintenanceModeService maintenance,
        IHostApplicationLifetime lifetime, ILogger<DreamCoordinator> logger, DreamEngineOptions? options = null)
    {
        _scopes = scopes;
        _operations = operations;
        _session = session;
        _maintenance = maintenance;
        _logger = logger;
        _options = options ?? DreamEngineOptions.Default;
        _session.Locked += OnLocked;
        _stopping = lifetime.ApplicationStopping.Register(() => _active?.Control.Request(DreamRunOutcome.Interrupted));
    }

    public Guid? ActiveRunId => _active?.Id;

    /// <summary>Whether something heavy is going on beside Dream: a run neither starts nor continues then.</summary>
    public bool IsHeavyOperationRunning => DreamHeavyOperations.IsRunning(_operations, _maintenance);

    public async Task<DreamStartResult> StartAsync(DreamStartCommand command)
    {
        if (!_session.IsUnlocked) return new DreamStartResult(null, DreamStartError.Locked);
        if (IsHeavyOperationRunning) return new DreamStartResult(null, DreamStartError.Busy);

        var lease = _operations.TryEnter(DreamNames.LongOperationKey, "A Dream run");
        if (lease is null) return new DreamStartResult(null, DreamStartError.AlreadyRunning);

        var started = false;
        try
        {
            using var scope = _scopes.CreateScope();
            var plan = await BuildPlanAsync(scope.ServiceProvider, command);
            if (plan.Error != DreamStartError.None) return new DreamStartResult(null, plan.Error);

            var settings = plan.Plan!.Settings;
            var budget = new DreamRunBudget(settings.MaxModelCalls, settings.MaxTokens);
            await scope.ServiceProvider.GetRequiredService<DreamStore>().CreateRunAsync(new DreamRunRecord
            {
                Id = plan.Plan.RunId,
                Trigger = command.Trigger,
                Mode = command.Mode,
                State = DreamRunState.Queued,
                StartedByUserId = command.UserId,
                CreatedAt = plan.Plan.StartedAtUtc,
                HeartbeatAt = plan.Plan.StartedAtUtc,
                UseModel = plan.Plan.UseModel,
                UseRules = plan.Plan.UseRules && plan.Plan.Includes(DreamPhase.Detectors),
                ModelId = plan.Plan.ModelId,
                ModelLabel = plan.Plan.ModelLabel,
                PromptVersions = plan.PromptVersions,
                MaxAdvice = plan.Plan.MaxAdvice,
                MaxMinutes = plan.Plan.MaxMinutes,
                MaxModelCalls = budget.MaxModelCalls,
                MaxTokens = budget.MaxTokens,
            });

            var active = new ActiveRun(plan.Plan.RunId, new DreamRunControl(), new DreamRunProgress(budget));
            _active = active;

            // No request context may flow into the run: the caller scope is read from HttpContext when there is one, and the
            // run must outlive the request and work under the system scope.
            using (ExecutionContext.SuppressFlow())
                active.Work = Task.Run(() => RunAndFinishAsync(active, plan.Plan, lease));
            started = true;
            return new DreamStartResult(active.Id, DreamStartError.None);
        }
        finally
        {
            if (!started) lease.Dispose();
        }
    }

    private async Task<(DreamRunPlan? Plan, string? PromptVersions, DreamStartError Error)> BuildPlanAsync(IServiceProvider services, DreamStartCommand command)
    {
        var settings = await services.GetRequiredService<DreamStore>().GetSettingsAsync();
        var now = _options.Clock.GetUtcNow().UtcDateTime;

        // Naming a model for this run is asking for it, unless the request also says no model.
        var wantsModel = command.UseModel ?? (command.ModelId is not null || settings.UseModel);
        var named = command.UseModel == true || command.ModelId is not null;
        string? modelId = null, modelLabel = null, notice = null;
        Guid? modelRowId = null;
        if (wantsModel)
        {
            var chat = services.GetRequiredService<ChatSettingsRepository>();
            var router = services.GetRequiredService<BeeMemoryBank.Api.Services.Providers.ModelRouter>();
            var model = await DreamModelResolver.ResolveAsync(chat, command.ModelId ?? settings.AnalysisModelId);
            if (model is null || await DreamModelResolver.ProblemAsync(router, model) is not null)
            {
                if (named) return (null, null, DreamStartError.ModelNotReady);
                if (!command.UseRules) return (null, null, DreamStartError.NothingToRun);
                wantsModel = false;
                notice = "The model is not ready: this run uses the rules only.";
            }
            else
            {
                modelId = model.ModelId;
                modelLabel = await DreamModelResolver.LabelAsync(
                    services.GetRequiredService<BeeMemoryBank.Api.Services.Providers.ChatProviderRepository>(), model);
                modelRowId = model.Id;
            }
        }

        // The night always runs the rules; a manual start may leave them out, but then the model has to be there.
        var useRules = command.UseRules || command.Trigger == DreamRunTrigger.Schedule;
        if (!useRules && !wantsModel) return (null, null, DreamStartError.NothingToRun);

        var scheduled = command.Trigger == DreamRunTrigger.Schedule;
        var maxAdvice = scheduled ? settings.MaxAdviceScheduled : command.MaxAdvice ?? settings.MaxAdviceManual;
        var maxMinutes = scheduled
            ? (command.DeadlineUtc is { } end ? (int)Math.Ceiling((end - now).TotalMinutes) : settings.ScheduleWindowMinutes)
            : command.MaxMinutes ?? settings.ManualMaxMinutes;
        var deadline = command.DeadlineUtc ?? (maxMinutes > 0 ? now.AddMinutes(maxMinutes) : null);

        var versions = wantsModel
            ? string.Join(',', services.GetServices<IDreamModelTask>().Select(t => t.PromptVersion))
            : null;

        return (new DreamRunPlan
        {
            RunId = Guid.NewGuid(),
            Trigger = command.Trigger,
            Mode = command.Mode,
            Settings = settings,
            UseModel = wantsModel,
            UseRules = useRules,
            ModelId = modelId,
            ModelLabel = modelLabel,
            ModelRowId = modelRowId,
            MaxAdvice = maxAdvice,
            MaxMinutes = maxMinutes,
            Phases = command.Phases?.ToHashSet(),
            Sample = command.Sample,
            StartedAtUtc = now,
            DeadlineUtc = deadline,
            Notice = notice,
        }, versions, DreamStartError.None);
    }

    private async Task RunAndFinishAsync(ActiveRun active, DreamRunPlan plan, IDisposable lease)
    {
        try
        {
            DreamRunResult result;
            using var scope = _scopes.CreateScope();
            try
            {
                result = await scope.ServiceProvider.GetRequiredService<DreamRunner>().RunAsync(plan, active.Control, active.Progress);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A Dream run could not start");
                result = new DreamRunResult(DreamRunOutcome.Failed, "internal_error", ex.GetType().Name);
            }

            await WriteFinishAsync(scope.ServiceProvider.GetRequiredService<DreamStore>(), active.Id, result, active.Progress.Snapshot());
        }
        finally
        {
            _active = null;
            active.Control.Dispose();
            lease.Dispose();
        }
    }

    private async Task WriteFinishAsync(DreamStore store, Guid id, DreamRunResult result, DreamRunCounters counters)
    {
        try
        {
            await store.FinishRunAsync(id, result.Outcome, result.ErrorCode, result.ErrorText, counters, _options.Clock.GetUtcNow().UtcDateTime);
        }
        catch (Exception ex) when (ex is VaultSealedException or SessionLockedException)
        {
            // A sealed node's chat.db closed with the lock. The outcome stays here until the sweep can write it, rather than
            // the run looking interrupted after the next unlock.
            _unwritten[id] = (result, counters);
        }
    }

    /// <summary>Writes the outcomes of runs that ended while chat.db was closed. Called by the sweep.</summary>
    public async Task FlushUnwrittenAsync(DreamStore store)
    {
        foreach (var (id, finish) in _unwritten)
        {
            await store.FinishRunAsync(id, finish.Result.Outcome, finish.Result.ErrorCode, finish.Result.ErrorText, finish.Counters, _options.Clock.GetUtcNow().UtcDateTime);
            _unwritten.TryRemove(id, out _);
        }
    }

    /// <summary>Stops the run if it is the active one; the run writes its own end. Stopping a run that has ended is harmless.</summary>
    public async Task StopAsync(Guid id, DreamStore store)
    {
        var active = _active;
        if (active is null || active.Id != id) return;
        await store.MarkRunStoppingAsync(id);
        active.Control.Request(DreamRunOutcome.Stopped);
    }

    /// <summary>The active run's work, for a test or a shutdown to wait on.</summary>
    public Task WaitForIdleAsync() => _active?.Work ?? Task.CompletedTask;

    private void OnLocked() => _active?.Control.Request(DreamRunOutcome.Locked);

    public void Dispose()
    {
        _session.Locked -= OnLocked;
        _stopping.Dispose();
    }
}
