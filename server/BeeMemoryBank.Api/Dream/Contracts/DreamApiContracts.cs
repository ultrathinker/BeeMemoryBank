using System.Text.Json.Nodes;

namespace BeeMemoryBank.Api.Dream;

// The request and response shapes of /api/dream (plan 3.8). JSON is camelCase with enums by wire name (the API's
// global options); errors are the existing ErrorResponse(message, code) with codes from DreamErrorCodes.

// ── GET /status ─────────────────────────────────────────────────────────────────────────────────────────────────

public sealed record DreamStatusResponse
{
    /// <summary>False when Dream cannot run here now; <see cref="Reason"/> says why (e.g. locked).</summary>
    public bool Available { get; init; }
    public string? Reason { get; init; }
    public DreamRunSummary? ActiveRun { get; init; }
    /// <summary>The latest finished run (a skipped night included), for the status line; null before the first.</summary>
    public DreamRunSummary? LastRun { get; init; }
    public DreamOpenAdviceCounts OpenAdvice { get; init; } = new(0, 0, 0, 0);
    public DreamStatusSettings Settings { get; init; } = new(false, false, 0, 0);
    /// <summary>A model is configured and a key is usable: a run with the model may start.</summary>
    public bool ModelReady { get; init; }
    public string? ModelLabel { get; init; }
    /// <summary>Why the model is not ready: <c>no_model</c> (no text model in the catalog, or the chosen one is gone) or
    /// <c>no_key</c> (no usable OpenRouter key); null when it is ready.</summary>
    public string? ModelProblem { get; init; }
    /// <summary>The built-in chat is usable (Do it with AI).</summary>
    public bool ChatAvailable { get; init; }
    public DreamEmbeddingsStatus Embeddings { get; init; } = new(0, 0);
    public DateTime? NextScheduledAt { get; init; }
}

/// <summary>Open (new and in_chat, trial excluded) advice by band.</summary>
public sealed record DreamOpenAdviceCounts(int Total, int High, int Medium, int Low);

public sealed record DreamStatusSettings(bool ScheduleEnabled, bool UseModel, int MaxAdviceScheduled, int MaxAdviceManual);

public sealed record DreamEmbeddingsStatus(int Total, int Pending);

// ── GET/PUT /settings ───────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Every field of dream_settings; the excluded folders as id and path.</summary>
public sealed record DreamSettingsResponse
{
    public bool ScheduleEnabled { get; init; }
    public string ScheduleStart { get; init; } = "03:00";
    public int ScheduleWindowMinutes { get; init; }
    public int MaxAdviceScheduled { get; init; }
    public int MaxAdviceManual { get; init; }
    public int MaxOpenAdvice { get; init; }
    public int MinScore { get; init; }
    public int ManualMaxMinutes { get; init; }
    public int MaxModelCalls { get; init; }
    public long MaxTokens { get; init; }
    public bool UseModel { get; init; }
    public Guid? AnalysisModelId { get; init; }
    public IReadOnlyList<DreamFolderRef> ExcludedFolders { get; init; } = [];
    public DateTime? UpdatedAt { get; init; }
    public int? UpdatedBy { get; init; }

    public static DreamSettingsResponse From(DreamSettings s, IReadOnlyList<DreamFolderRef> excludedFolders) => new()
    {
        ScheduleEnabled = s.ScheduleEnabled,
        ScheduleStart = s.ScheduleStart,
        ScheduleWindowMinutes = s.ScheduleWindowMinutes,
        MaxAdviceScheduled = s.MaxAdviceScheduled,
        MaxAdviceManual = s.MaxAdviceManual,
        MaxOpenAdvice = s.MaxOpenAdvice,
        MinScore = s.MinScore,
        ManualMaxMinutes = s.ManualMaxMinutes,
        MaxModelCalls = s.MaxModelCalls,
        MaxTokens = s.MaxTokens,
        UseModel = s.UseModel,
        AnalysisModelId = s.AnalysisModelId,
        ExcludedFolders = excludedFolders,
        UpdatedAt = s.UpdatedAt,
        UpdatedBy = s.UpdatedBy,
    };
}

public sealed record DreamFolderRef(Guid Id, string Path);

/// <summary>The same fields; the excluded folders as paths (the server turns them into ids and answers 400 on an
/// unknown path or model).</summary>
public sealed record DreamSettingsUpdateRequest
{
    public bool ScheduleEnabled { get; init; }
    public string ScheduleStart { get; init; } = "03:00";
    public int ScheduleWindowMinutes { get; init; }
    public int MaxAdviceScheduled { get; init; }
    public int MaxAdviceManual { get; init; }
    public int MaxOpenAdvice { get; init; }
    public int MinScore { get; init; }
    public int ManualMaxMinutes { get; init; }
    public int MaxModelCalls { get; init; }
    public long MaxTokens { get; init; }
    public bool UseModel { get; init; }
    public Guid? AnalysisModelId { get; init; }
    public IReadOnlyList<string> ExcludedFolderPaths { get; init; } = [];
}

// ── POST /runs ──────────────────────────────────────────────────────────────────────────────────────────────────

/// <param name="UseModel">Null: as the settings say.</param>
/// <param name="ModelId">chat_model.id of the model for this run (a trial comparison); null: the settings' model.</param>
/// <param name="MaxAdvice">Null: the settings' manual limit.</param>
/// <param name="MaxMinutes">Null: the settings' manual limit.</param>
/// <param name="Phases">Wire names of DreamPhase; null: every phase.</param>
/// <param name="UseRules">The built-in rules (the detectors); default on, so a caller that does not know the flag gets what
/// it always got. With the rules off the model must be on: a start with both off is refused (400).</param>
public sealed record DreamStartRunRequest(
    DreamRunMode Mode = DreamRunMode.Normal,
    bool? UseModel = null,
    Guid? ModelId = null,
    int? MaxAdvice = null,
    int? MaxMinutes = null,
    IReadOnlyList<DreamPhase>? Phases = null,
    DreamSampleRequest? Sample = null,
    bool UseRules = true);

/// <summary>A deterministic candidate order for trial comparisons: the same seed gives the same order.</summary>
/// <param name="Pairs">How many pairs to take (null: no cap).</param>
/// <param name="Folders">How many folders to take (null: no cap).</param>
public sealed record DreamSampleRequest(int Seed, int? Pairs = null, int? Folders = null);

public sealed record DreamStartRunResponse(Guid RunId);

// ── Runs ────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>RunSummary of plan 3.8.</summary>
public record DreamRunSummary
{
    public Guid Id { get; init; }
    public DreamRunTrigger Trigger { get; init; }
    public DreamRunMode Mode { get; init; }
    public DreamRunState State { get; init; }
    public DreamRunOutcome? Outcome { get; init; }
    public DreamPhase? Phase { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public DreamUserRef? StartedBy { get; init; }
    public bool UseModel { get; init; }
    public string? ModelLabel { get; init; }
    /// <summary>Whether the built-in rules ran (off when the start asked so, or when its phases left the detectors out).
    /// Runs from before the flag existed say true.</summary>
    public bool UseRules { get; init; } = true;
    public DreamRunCounters Counters { get; init; } = new();
    public DreamRunLimits Limits { get; init; } = new(0, 0, 0, 0);
    public string? ErrorCode { get; init; }
    public string? ErrorText { get; init; }
}

public sealed record DreamUserRef(int Id, string Name);

public sealed record DreamRunCounters
{
    public int ArticlesInScope { get; init; }
    public int ArticlesSent { get; init; }
    public int CandidatesExamined { get; init; }
    public int AdviceCreated { get; init; }
    public int AdviceSuppressed { get; init; }
    public int ModelCalls { get; init; }
    public long PromptTokens { get; init; }
    public long CompletionTokens { get; init; }
    public int Errors { get; init; }
}

/// <summary>The limits captured at the start; 0 advice or minutes = none.</summary>
public sealed record DreamRunLimits(int MaxAdvice, int MaxMinutes, int MaxModelCalls, long MaxTokens);

/// <summary><c>GET /runs/{id}</c>: RunSummary plus the advice of the run by status and whether its journal is still
/// kept (seven days).</summary>
public sealed record DreamRunDetail : DreamRunSummary
{
    public DreamAdviceCounts AdviceCounts { get; init; } = new(0, 0, 0, 0, 0);
    public bool LogAvailable { get; init; }
}

public sealed record DreamAdviceCounts(int New, int InChat, int Applied, int Dismissed, int Expired);

/// <summary><c>GET /runs</c>, newest first.</summary>
public sealed record DreamRunListResponse(int Total, int Page, int PageSize, IReadOnlyList<DreamRunSummary> Items);

/// <summary><c>GET /runs/{id}/advice?afterSeq=N</c>: the live feed. <see cref="Activity"/> is the run's latest
/// journal line ("what I am doing now").</summary>
public sealed record DreamRunAdviceResponse(IReadOnlyList<DreamAdviceDto> Items, long LastSeq, DreamRunSummary Run, string? Activity);

/// <summary><c>GET /runs/{id}/events?afterSeq=N</c>.</summary>
public sealed record DreamRunEventsResponse(IReadOnlyList<DreamRunEventItem> Items, long LastSeq, bool LogAvailable);

public sealed record DreamRunEventItem(long Seq, DateTime At, DreamEventKind Kind, string Text, JsonObject? Data);

// ── Advice ──────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Advice of plan 3.8, as checked at the moment of the request.</summary>
public sealed record DreamAdviceDto
{
    public Guid Id { get; init; }
    public Guid RunId { get; init; }
    public long Seq { get; init; }
    public DreamAdviceKind Kind { get; init; }
    public DreamAdviceSource Source { get; init; }
    public int Score { get; init; }
    public DreamScoreBand Band { get; init; }
    public DreamAdviceStatus Status { get; init; }
    public DateTime? StatusAt { get; init; }
    public string? StatusNote { get; init; }
    public DreamApplyMode ApplyMode { get; init; }
    public bool Trial { get; init; }
    public DateTime CreatedAt { get; init; }
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public IReadOnlyList<DreamEvidence> Evidence { get; init; } = [];
    public IReadOnlyList<DreamAdviceObject> Objects { get; init; } = [];
    public DreamAction Action { get; init; } = DreamAction.Note();
    public DreamReferences? References { get; init; }
    public DreamAdviceModelLabel? Model { get; init; }
    public DreamAppliedInfo? Applied { get; init; }
    public string? LastError { get; init; }
    /// <summary>Why an expired advice expired (status_note of an expired row).</summary>
    public string? ExpiredReason { get; init; }
    public DreamAdviceCan Can { get; init; } = new(false, false, false, false, false);
}

public sealed record DreamAdviceModelLabel(string Label, string PromptVersion);

/// <summary>Which buttons the card shows.</summary>
public sealed record DreamAdviceCan(bool Apply, bool Chat, bool Prompt, bool Dismiss, bool MarkDone);

/// <summary><c>GET /advice</c>: open advice by default; trial advice is never listed here.</summary>
public sealed record DreamAdviceListResponse(int Total, IReadOnlyList<DreamAdviceDto> Items);

/// <summary><c>GET /advice/{id}/prompt?target=agent|chat</c>.</summary>
public sealed record DreamPromptResponse(string Prompt, DreamPromptTarget Target)
{
    /// <summary>The advice's status when the prompt was made.</summary>
    public DreamAdviceStatus? Status { get; init; }

    /// <summary>The advice was applied or dismissed: the text is for reference only and was not checked against the notes as
    /// they are now.</summary>
    public bool ForReference { get; init; }
}

/// <summary><c>POST /advice/{id}/apply</c>: the new status and what was done (applied.result).</summary>
public sealed record DreamApplyResponse(DreamAdviceStatus Status, string? Result);

/// <param name="ExpiresDays">180 or null (never expires).</param>
public sealed record DreamDismissRequest(DreamRuleScope Scope = DreamRuleScope.Advice, string? Reason = null, int? ExpiresDays = 180);

/// <summary><c>POST /advice/{id}/status</c>: only in_chat, applied and new, and only the transitions of plan 3.4.</summary>
public sealed record DreamStatusChangeRequest(DreamAdviceStatus Status);

// ── Rules ───────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One dismiss rule as <c>GET /rules</c> lists it (its sealed payload opened).</summary>
public sealed record DreamRuleDto
{
    public Guid Id { get; init; }
    public DreamRuleScope Scope { get; init; }
    public DreamAdviceKind? Kind { get; init; }
    public DateTime CreatedAt { get; init; }
    public int? CreatedByUserId { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public string? Fingerprint { get; init; }
    public Guid? ObjectId { get; init; }
    public string? Path { get; init; }
    public string? TagName { get; init; }
    public string? Title { get; init; }
    public string? Reason { get; init; }
}

public sealed record DreamRuleListResponse(IReadOnlyList<DreamRuleDto> Items);

// ── GET /compare ────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The trial-run comparison: one row per candidate any of the runs examined, common candidates first, in the order
/// the runs looked at them.</summary>
/// <param name="Runs">The runs in the order asked for (the page labels them A, B, C in its own order).</param>
/// <param name="Common">Candidates every run examined.</param>
/// <param name="Agreement">Share (0..1) of the common candidates on which every run gave the same verdict; an error is not a
/// verdict, so a common candidate that any run failed on is not counted (see <see cref="AgreementBasis"/>).</param>
public sealed record DreamCompareResponse(IReadOnlyList<DreamRunSummary> Runs, IReadOnlyList<DreamCompareRow> Rows, int Common, double Agreement)
{
    /// <summary>How many common candidates every run gave a verdict on: the number <see cref="Agreement"/> is a share of.</summary>
    public int AgreementBasis { get; init; }

    /// <summary>Runs that examined candidates but whose journal no longer holds them (it is kept seven days): the table has
    /// nothing to say about them.</summary>
    public IReadOnlyList<Guid> RunsWithoutLog { get; init; } = [];
}

/// <param name="Results">By run id ("D" form); null where that run did not examine the candidate.</param>
public sealed record DreamCompareRow(
    string CandidateKey,
    string Task,
    IReadOnlyList<DreamAdviceObject> Objects,
    IReadOnlyDictionary<string, DreamCompareResult?> Results);

/// <summary>What one run made of one candidate. <see cref="Verdict"/> is the task's own word (null on an error), <see cref="Kind"/>
/// the advice kind it became (null when nothing was proposed).</summary>
public sealed record DreamCompareResult(string? Verdict, DreamAdviceKind? Kind, int? Score, string? Summary)
{
    /// <summary>advice, nothing or error: an error means the run looked and got no answer, which is not the same as "not examined".</summary>
    public DreamExaminedVerdict Outcome { get; init; } = DreamExaminedVerdict.Nothing;
}

// ── GET /rulebook ───────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>What the "View all rules" dialog shows: every built-in rule and model task described, with the numbers they use
/// read from the code at request time (DreamRuleBook).</summary>
public sealed record DreamRuleBookResponse
{
    public IReadOnlyList<string> Intro { get; init; } = [];
    /// <summary>What similarity and Z mean, for the rules and the pair verdict that use them.</summary>
    public IReadOnlyList<string> Similarity { get; init; } = [];
    /// <summary>What every rule and task leaves out.</summary>
    public IReadOnlyList<string> Skips { get; init; } = [];
    public IReadOnlyList<DreamRuleBookRule> Rules { get; init; } = [];
    public IReadOnlyList<DreamRuleBookTask> ModelTasks { get; init; } = [];
    public IReadOnlyList<DreamRuleBookFact> Limits { get; init; } = [];
    public IReadOnlyList<DreamRuleBookBand> Bands { get; init; } = [];
}

/// <summary>One built-in rule (a detector).</summary>
public sealed record DreamRuleBookRule
{
    /// <summary>The detector's name (IDreamDetector.Name).</summary>
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>False for a detector the rule book has no words for (a test fails on it).</summary>
    public bool Described { get; init; } = true;
    public IReadOnlyList<DreamAdviceKind> Kinds { get; init; } = [];
    /// <summary>The ways an advice of each kind can be carried out (DreamDraftChecks), filled in by the endpoint.</summary>
    public IReadOnlyList<DreamRuleBookKind> KindDetails { get; init; } = [];
    /// <summary>When the rule runs at all, if not always.</summary>
    public string? RunsWhen { get; init; }
    public string Finds { get; init; } = "";
    public string Example { get; init; } = "";
    public IReadOnlyList<string> How { get; init; } = [];
    public IReadOnlyList<DreamRuleBookFact> Thresholds { get; init; } = [];
    public string Score { get; init; } = "";
    public string Apply { get; init; } = "";
    public string Never { get; init; } = "";
    public IReadOnlyList<string> Skips { get; init; } = [];
}

/// <summary>One model task.</summary>
public sealed record DreamRuleBookTask
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool Described { get; init; } = true;
    public string PromptVersion { get; init; } = "";
    public IReadOnlyList<DreamAdviceKind> Kinds { get; init; } = [];
    public IReadOnlyList<DreamRuleBookKind> KindDetails { get; init; } = [];
    public IReadOnlyList<string> Candidates { get; init; } = [];
    public string Asked { get; init; } = "";
    public string Answers { get; init; } = "";
    public IReadOnlyList<string> Checks { get; init; } = [];
    public IReadOnlyList<DreamRuleBookFact> Limits { get; init; } = [];
}

/// <summary>A kind of advice, the action it carries and how it can be applied: <c>code</c> (Apply), <c>chat</c>, <c>prompt</c>.</summary>
public sealed record DreamRuleBookKind(DreamAdviceKind Kind, DreamActionType Action, IReadOnlyList<DreamApplyMode> ApplyModes);

public sealed record DreamRuleBookFact(string Name, string Value);

public sealed record DreamRuleBookBand(DreamScoreBand Band, int From, int To, string Meaning);
