using Dapper;

namespace BeeMemoryBank.Api.Dream;

/// <summary>One row of dream_run.</summary>
public sealed record DreamRunRecord
{
    public required Guid Id { get; init; }
    public required DreamRunTrigger Trigger { get; init; }
    public required DreamRunMode Mode { get; init; }
    public required DreamRunState State { get; init; }
    public DreamRunOutcome? Outcome { get; init; }
    public DreamPhase? Phase { get; init; }
    public int? StartedByUserId { get; init; }
    public required DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public DateTime? HeartbeatAt { get; init; }
    public bool UseModel { get; init; }
    /// <summary>Whether the built-in rules ran (see DreamRunSummary.UseRules).</summary>
    public bool UseRules { get; init; } = true;
    public string? ModelId { get; init; }
    public string? ModelLabel { get; init; }
    public string? PromptVersions { get; init; }
    public int MaxAdvice { get; init; }
    public int MaxMinutes { get; init; }
    public int MaxModelCalls { get; init; }
    public long MaxTokens { get; init; }
    public DreamRunCounters Counters { get; init; } = new();
    public string? ErrorCode { get; init; }
    public string? ErrorText { get; init; }

    public bool IsActive => State != DreamRunState.Finished;

    /// <summary>The API form; <typeparamref name="T"/> is RunSummary or its detail.</summary>
    public T To<T>(DreamUserRef? startedBy) where T : DreamRunSummary, new() => new()
    {
        Id = Id,
        Trigger = Trigger,
        Mode = Mode,
        State = State,
        Outcome = Outcome,
        Phase = Phase,
        CreatedAt = CreatedAt,
        StartedAt = StartedAt,
        FinishedAt = FinishedAt,
        StartedBy = startedBy,
        UseModel = UseModel,
        ModelLabel = ModelLabel,
        UseRules = UseRules,
        Counters = Counters,
        Limits = new DreamRunLimits(MaxAdvice, MaxMinutes, MaxModelCalls, MaxTokens),
        ErrorCode = ErrorCode,
        ErrorText = ErrorText,
    };
}

// dream_run: Plaintext, only numbers, enumerations and ids. error_text never carries a title or article text.
public sealed partial class DreamStore
{
    private const string RunColumns =
        """
        id AS Id, "trigger" AS RunTrigger, mode AS Mode, state AS State, outcome AS Outcome, phase AS Phase,
        started_by_user_id AS StartedByUserId, created_at AS CreatedAt, started_at AS StartedAt,
        finished_at AS FinishedAt, heartbeat_at AS HeartbeatAt, use_model AS UseModel, use_rules AS UseRules, model_id AS ModelId,
        model_label AS ModelLabel, prompt_versions AS PromptVersions, max_advice AS MaxAdvice, max_minutes AS MaxMinutes,
        max_model_calls AS MaxModelCalls, max_tokens AS MaxTokens, articles_in_scope AS ArticlesInScope,
        articles_sent AS ArticlesSent, candidates_examined AS CandidatesExamined, advice_created AS AdviceCreated,
        advice_suppressed AS AdviceSuppressed, model_calls AS ModelCalls, prompt_tokens AS PromptTokens,
        completion_tokens AS CompletionTokens, errors AS Errors, error_code AS ErrorCode, error_text AS ErrorText
        """;

    private const string ActiveStates = "('queued', 'running', 'stopping')";

    public async Task CreateRunAsync(DreamRunRecord run)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO dream_run
                (id, "trigger", mode, state, outcome, phase, started_by_user_id, created_at, started_at, finished_at, heartbeat_at,
                 use_model, use_rules, model_id, model_label, prompt_versions, max_advice, max_minutes, max_model_calls, max_tokens,
                 error_code, error_text)
            VALUES
                (@Id, @RunTrigger, @Mode, @State, @Outcome, @Phase, @StartedByUserId, @CreatedAt, @StartedAt, @FinishedAt, @HeartbeatAt,
                 @UseModel, @UseRules, @ModelId, @ModelLabel, @PromptVersions, @MaxAdvice, @MaxMinutes, @MaxModelCalls, @MaxTokens,
                 @ErrorCode, @ErrorText)
            """,
            new
            {
                Id = Id(run.Id),
                RunTrigger = DreamWire.ToWire(run.Trigger),
                Mode = DreamWire.ToWire(run.Mode),
                State = DreamWire.ToWire(run.State),
                Outcome = run.Outcome is { } outcome ? DreamWire.ToWire(outcome) : null,
                Phase = run.Phase is { } phase ? DreamWire.ToWire(phase) : null,
                run.StartedByUserId,
                CreatedAt = Time(run.CreatedAt),
                StartedAt = Time(run.StartedAt),
                FinishedAt = Time(run.FinishedAt),
                HeartbeatAt = Time(run.HeartbeatAt),
                UseModel = run.UseModel ? 1 : 0,
                UseRules = run.UseRules ? 1 : 0,
                run.ModelId,
                run.ModelLabel,
                run.PromptVersions,
                run.MaxAdvice,
                run.MaxMinutes,
                run.MaxModelCalls,
                run.MaxTokens,
                run.ErrorCode,
                run.ErrorText,
            });
    }

    public async Task<DreamRunRecord?> GetRunAsync(Guid id)
    {
        using var conn = OpenConnection();
        var row = await conn.QuerySingleOrDefaultAsync<RunRow>($"SELECT {RunColumns} FROM dream_run WHERE id = @id", new { id = Id(id) });
        return row?.ToRecord();
    }

    /// <summary>Newest first; <paramref name="page"/> starts at 1.</summary>
    public async Task<(int Total, IReadOnlyList<DreamRunRecord> Items)> ListRunsAsync(DreamRunListMode mode, int page, int pageSize)
    {
        var filter = mode == DreamRunListMode.All ? "" : "WHERE mode = @mode";
        var args = new
        {
            mode = mode == DreamRunListMode.Trial ? DreamWire.ToWire(DreamRunMode.Trial) : DreamWire.ToWire(DreamRunMode.Normal),
            pageSize,
            offset = (page - 1) * pageSize,
        };
        using var conn = OpenConnection();
        var total = await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM dream_run {filter}", args);
        var rows = await conn.QueryAsync<RunRow>(
            $"SELECT {RunColumns} FROM dream_run {filter} ORDER BY created_at DESC, id LIMIT @pageSize OFFSET @offset", args);
        return (total, rows.Select(r => r.ToRecord()).ToList());
    }

    /// <summary>The latest finished run, for the status line (GET /status lastRun).</summary>
    public async Task<DreamRunRecord?> GetLastFinishedRunAsync()
    {
        using var conn = OpenConnection();
        var row = await conn.QuerySingleOrDefaultAsync<RunRow>(
            $"SELECT {RunColumns} FROM dream_run WHERE state = 'finished' ORDER BY created_at DESC LIMIT 1");
        return row?.ToRecord();
    }

    public async Task<IReadOnlyList<DreamRunRecord>> ListActiveRunsAsync()
    {
        using var conn = OpenConnection();
        var rows = await conn.QueryAsync<RunRow>($"SELECT {RunColumns} FROM dream_run WHERE state IN {ActiveStates}");
        return rows.Select(r => r.ToRecord()).ToList();
    }

    /// <summary>queued to running. A run stopped while still queued stays stopping.</summary>
    public async Task MarkRunStartedAsync(Guid id, DateTime nowUtc)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            """
            UPDATE dream_run
            SET state = CASE state WHEN 'queued' THEN 'running' ELSE state END, started_at = @now, heartbeat_at = @now
            WHERE id = @id
            """,
            new { id = Id(id), now = Time(nowUtc) });
    }

    /// <summary>The heartbeat, the phase and the counters, written together so the live feed sees them move.</summary>
    public async Task UpdateRunProgressAsync(Guid id, DreamPhase? phase, DreamRunCounters c, DateTime nowUtc)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync(
            """
            UPDATE dream_run
            SET heartbeat_at = @now, phase = @phase, articles_in_scope = @ArticlesInScope, articles_sent = @ArticlesSent,
                candidates_examined = @CandidatesExamined, advice_created = @AdviceCreated, advice_suppressed = @AdviceSuppressed,
                model_calls = @ModelCalls, prompt_tokens = @PromptTokens, completion_tokens = @CompletionTokens, errors = @Errors
            WHERE id = @id AND state != 'finished'
            """,
            new
            {
                id = Id(id),
                now = Time(nowUtc),
                phase = phase is { } value ? DreamWire.ToWire(value) : null,
                c.ArticlesInScope, c.ArticlesSent, c.CandidatesExamined, c.AdviceCreated, c.AdviceSuppressed,
                c.ModelCalls, c.PromptTokens, c.CompletionTokens, c.Errors,
            });
    }

    /// <summary>queued or running to stopping; anything else stays as it is, so a second Stop is harmless.</summary>
    public async Task MarkRunStoppingAsync(Guid id)
    {
        using var conn = OpenConnection();
        await conn.ExecuteAsync("UPDATE dream_run SET state = 'stopping' WHERE id = @id AND state IN ('queued', 'running')", new { id = Id(id) });
    }

    /// <summary>The last write of a run. A finished row is not rewritten.</summary>
    public async Task<bool> FinishRunAsync(Guid id, DreamRunOutcome outcome, string? errorCode, string? errorText, DreamRunCounters c, DateTime nowUtc)
    {
        using var conn = OpenConnection();
        var changed = await conn.ExecuteAsync(
            $"""
            UPDATE dream_run
            SET state = 'finished', outcome = @outcome, finished_at = @now, heartbeat_at = @now, error_code = @errorCode, error_text = @errorText,
                articles_in_scope = @ArticlesInScope, articles_sent = @ArticlesSent, candidates_examined = @CandidatesExamined,
                advice_created = @AdviceCreated, advice_suppressed = @AdviceSuppressed, model_calls = @ModelCalls,
                prompt_tokens = @PromptTokens, completion_tokens = @CompletionTokens, errors = @Errors
            WHERE id = @id AND state IN {ActiveStates}
            """,
            new
            {
                id = Id(id),
                outcome = DreamWire.ToWire(outcome),
                now = Time(nowUtc),
                errorCode,
                errorText,
                c.ArticlesInScope, c.ArticlesSent, c.CandidatesExamined, c.AdviceCreated, c.AdviceSuppressed,
                c.ModelCalls, c.PromptTokens, c.CompletionTokens, c.Errors,
            });
        return changed > 0;
    }

    /// <summary>
    /// Whether a scheduled run (a skipped one included) already belongs to this window. A skipped record is written
    /// after its window has passed and carries the window's start as created_at, so one query answers for both.
    /// </summary>
    public async Task<bool> HasScheduledRunInWindowAsync(DateTime startUtc, DateTime endUtc)
    {
        using var conn = OpenConnection();
        return await conn.ExecuteScalarAsync<long>(
            """SELECT COUNT(*) FROM dream_run WHERE "trigger" = 'schedule' AND created_at >= @start AND created_at < @end""",
            new { start = Time(startUtc), end = Time(endUtc) }) > 0;
    }

    /// <summary>A manual-free record of a night run that did not start (plan 3.3 skipped). Its reason is in error_code.</summary>
    public Task InsertSkippedRunAsync(Guid id, DateTime windowStartUtc, DateTime nowUtc, string reason, DreamSettings settings) =>
        CreateRunAsync(new DreamRunRecord
        {
            Id = id,
            Trigger = DreamRunTrigger.Schedule,
            Mode = DreamRunMode.Normal,
            State = DreamRunState.Finished,
            Outcome = DreamRunOutcome.Skipped,
            CreatedAt = windowStartUtc,
            FinishedAt = nowUtc,
            MaxAdvice = settings.MaxAdviceScheduled,
            MaxModelCalls = settings.MaxModelCalls,
            MaxTokens = settings.MaxTokens,
            ErrorCode = reason,
        });

    private sealed class RunRow
    {
        public string Id { get; set; } = "";
        public string RunTrigger { get; set; } = "";
        public string Mode { get; set; } = "";
        public string State { get; set; } = "";
        public string? Outcome { get; set; }
        public string? Phase { get; set; }
        public long? StartedByUserId { get; set; }
        public string CreatedAt { get; set; } = "";
        public string? StartedAt { get; set; }
        public string? FinishedAt { get; set; }
        public string? HeartbeatAt { get; set; }
        public long UseModel { get; set; }
        public long UseRules { get; set; } = 1;
        public string? ModelId { get; set; }
        public string? ModelLabel { get; set; }
        public string? PromptVersions { get; set; }
        public long MaxAdvice { get; set; }
        public long MaxMinutes { get; set; }
        public long MaxModelCalls { get; set; }
        public long MaxTokens { get; set; }
        public long ArticlesInScope { get; set; }
        public long ArticlesSent { get; set; }
        public long CandidatesExamined { get; set; }
        public long AdviceCreated { get; set; }
        public long AdviceSuppressed { get; set; }
        public long ModelCalls { get; set; }
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
        public long Errors { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorText { get; set; }

        public DreamRunRecord ToRecord() => new()
        {
            Id = Guid.Parse(Id),
            Trigger = DreamWire.Parse<DreamRunTrigger>(RunTrigger),
            Mode = DreamWire.Parse<DreamRunMode>(Mode),
            State = DreamWire.Parse<DreamRunState>(State),
            Outcome = Outcome is null ? null : DreamWire.Parse<DreamRunOutcome>(Outcome),
            Phase = Phase is null ? null : DreamWire.Parse<DreamPhase>(Phase),
            StartedByUserId = (int?)StartedByUserId,
            CreatedAt = ParseTime(CreatedAt),
            StartedAt = ParseTimeOrNull(StartedAt),
            FinishedAt = ParseTimeOrNull(FinishedAt),
            HeartbeatAt = ParseTimeOrNull(HeartbeatAt),
            UseModel = UseModel != 0,
            UseRules = UseRules != 0,
            ModelId = ModelId,
            ModelLabel = ModelLabel,
            PromptVersions = PromptVersions,
            MaxAdvice = (int)MaxAdvice,
            MaxMinutes = (int)MaxMinutes,
            MaxModelCalls = (int)MaxModelCalls,
            MaxTokens = MaxTokens,
            Counters = new DreamRunCounters
            {
                ArticlesInScope = (int)ArticlesInScope,
                ArticlesSent = (int)ArticlesSent,
                CandidatesExamined = (int)CandidatesExamined,
                AdviceCreated = (int)AdviceCreated,
                AdviceSuppressed = (int)AdviceSuppressed,
                ModelCalls = (int)ModelCalls,
                PromptTokens = PromptTokens,
                CompletionTokens = CompletionTokens,
                Errors = (int)Errors,
            },
            ErrorCode = ErrorCode,
            ErrorText = ErrorText,
        };
    }
}
