using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// What a test may change about the engine. Not registered in production: the coordinator, the runner and the scheduler
/// fall back to <see cref="Default"/>.
/// </summary>
public sealed class DreamEngineOptions
{
    public static readonly DreamEngineOptions Default = new();

    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public DreamThresholds Thresholds { get; init; } = DreamThresholds.Default;

    /// <summary>A pause after each advice of the detector phase, so a test can watch them arrive one at a time. Zero in production.</summary>
    public TimeSpan EmitPause { get; init; } = TimeSpan.Zero;
}

/// <summary>The fixed inputs of one run, captured when it starts.</summary>
public sealed record DreamRunPlan
{
    public required Guid RunId { get; init; }
    public required DreamRunTrigger Trigger { get; init; }
    public required DreamRunMode Mode { get; init; }
    public required DreamSettings Settings { get; init; }
    public bool UseModel { get; init; }
    /// <summary>The detectors phase runs; off only for a manual start that asked so (the model then runs alone).</summary>
    public bool UseRules { get; init; } = true;
    public string? ModelId { get; init; }
    public string? ModelLabel { get; init; }
    public int MaxAdvice { get; init; }
    public int MaxMinutes { get; init; }
    public IReadOnlySet<DreamPhase>? Phases { get; init; }
    public DreamSampleRequest? Sample { get; init; }
    public required DateTime StartedAtUtc { get; init; }
    /// <summary>When the time limit or the schedule window runs out; null: never.</summary>
    public DateTime? DeadlineUtc { get; init; }
    /// <summary>A line for the journal's start (why a run has no model although the settings asked for one).</summary>
    public string? Notice { get; init; }

    public bool Includes(DreamPhase phase) => Phases == null || Phases.Contains(phase);
}

/// <summary>How a run ended.</summary>
public sealed record DreamRunResult(DreamRunOutcome Outcome, string? ErrorCode = null, string? ErrorText = null);

/// <summary>Ends a run from inside a phase with an outcome that is not an error.</summary>
internal sealed class DreamRunEndException(DreamRunOutcome outcome) : Exception(outcome.ToString())
{
    public DreamRunOutcome Outcome { get; } = outcome;
}

/// <summary>
/// The stop button of one run: the first reason wins and cancels the run's token, so a detector or a model call in
/// progress ends at once rather than at the next check.
/// </summary>
public sealed class DreamRunControl : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private int _reason = -1;

    public CancellationToken Token => _cts.Token;

    public DreamRunOutcome? Reason => Volatile.Read(ref _reason) is var value && value >= 0 ? (DreamRunOutcome)value : null;

    public void Request(DreamRunOutcome reason)
    {
        if (Interlocked.CompareExchange(ref _reason, (int)reason, -1) != -1) return;
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run has already ended.
        }
    }

    public void Dispose() => _cts.Dispose();
}

/// <summary>The counters of a run in progress; the live feed and the final record read them.</summary>
public sealed class DreamRunProgress(DreamRunBudget budget)
{
    private int _articlesInScope;
    private int _articlesSent;
    private int _candidatesExamined;
    private int _adviceCreated;
    private int _adviceSuppressed;
    private int _errors;
    private int _phase = -1;

    public DreamRunBudget Budget { get; } = budget;

    public int AdviceCreated => Volatile.Read(ref _adviceCreated);

    public DreamPhase? Phase
    {
        get => Volatile.Read(ref _phase) is var value && value >= 0 ? (DreamPhase)value : null;
        set => Volatile.Write(ref _phase, value is { } phase ? (int)phase : -1);
    }

    public void SetArticlesInScope(int count) => Volatile.Write(ref _articlesInScope, count);
    public void AddArticlesSent(int count) => Interlocked.Add(ref _articlesSent, count);
    public void AddCandidateExamined() => Interlocked.Increment(ref _candidatesExamined);
    public void AddAdviceCreated() => Interlocked.Increment(ref _adviceCreated);
    public void AddAdviceSuppressed() => Interlocked.Increment(ref _adviceSuppressed);
    public void AddError() => Interlocked.Increment(ref _errors);

    public DreamRunCounters Snapshot() => new()
    {
        ArticlesInScope = Volatile.Read(ref _articlesInScope),
        ArticlesSent = Volatile.Read(ref _articlesSent),
        CandidatesExamined = Volatile.Read(ref _candidatesExamined),
        AdviceCreated = AdviceCreated,
        AdviceSuppressed = Volatile.Read(ref _adviceSuppressed),
        ModelCalls = Budget.ModelCalls,
        PromptTokens = Budget.PromptTokens,
        CompletionTokens = Budget.CompletionTokens,
        Errors = Volatile.Read(ref _errors),
    };
}

/// <summary>Whether something heavy is going on that a run must not start beside, or must stop for (plan 3.3).</summary>
public static class DreamHeavyOperations
{
    public static bool IsRunning(LongOperationGate gate, MaintenanceModeService maintenance) =>
        HeavyOperationLock.Instance.CurrentCount == 0
        || maintenance.IsInMaintenance
        || gate.Running().Any(o => o.Key != DreamNames.LongOperationKey);
}

/// <summary>Which model a run would use: the settings' choice, or the chat's default text model.</summary>
public static class DreamModelResolver
{
    /// <returns>The model, or null when the id is unknown, is not a text model, or the catalog has none.</returns>
    public static async Task<ChatModelRow?> ResolveAsync(ChatSettingsRepository chat, Guid? modelId)
    {
        if (modelId is { } id)
        {
            var pinned = await chat.GetModelByIdAsync(id);
            return pinned is { IsText: true } ? pinned : null;
        }
        var defaults = await chat.GetDefaultModelIdsAsync();
        return await chat.ResolveEffectiveModelAsync("is_text", defaults.TextId);
    }

    /// <summary>True when at least one key can be used right now.</summary>
    public static async Task<bool> HasUsableKeyAsync(ChatSettingsRepository chat) =>
        (await chat.ListAvailableOrderedAsync()).Count > 0;
}

/// <summary>A window of the nightly schedule, in UTC (plan 3.5: the settings hold node-local time).</summary>
public readonly record struct DreamScheduleWindow(DateTime StartUtc, DateTime EndUtc)
{
    /// <summary>The most recent window that has opened, whether or not it is still open; null when the start time is unreadable.</summary>
    public static DreamScheduleWindow? Latest(DateTime nowUtc, DreamSettings settings, TimeZoneInfo zone)
    {
        if (!DreamSettings.TryParseStart(settings.ScheduleStart, out var start)) return null;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone));
        for (var back = 0; back <= 2; back++)
        {
            var open = ToUtc(today.AddDays(-back), start, zone);
            if (open <= nowUtc) return new DreamScheduleWindow(open, open.AddMinutes(settings.ScheduleWindowMinutes));
        }
        return null;
    }

    /// <summary>The next window to open after <paramref name="nowUtc"/>.</summary>
    public static DateTime? NextStart(DateTime nowUtc, DreamSettings settings, TimeZoneInfo zone)
    {
        if (!DreamSettings.TryParseStart(settings.ScheduleStart, out var start)) return null;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone));
        for (var ahead = 0; ahead <= 2; ahead++)
        {
            var open = ToUtc(today.AddDays(ahead), start, zone);
            if (open > nowUtc) return open;
        }
        return null;
    }

    private static DateTime ToUtc(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(day.ToDateTime(time), DateTimeKind.Unspecified);
        // A start inside the hour the clocks skip does not exist that night; the window opens right after it.
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(local, zone), DateTimeKind.Utc);
    }
}
