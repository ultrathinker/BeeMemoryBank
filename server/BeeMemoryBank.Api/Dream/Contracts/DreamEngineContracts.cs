using System.Text.Json.Nodes;

namespace BeeMemoryBank.Api.Dream;

// The contracts between the engine (P0b), the detectors (D1, D2) and the model tasks (M1, M2): plan 3.5.

/// <summary>
/// What one run may look at, built once per run by DreamCorpusBuilder. Articles are the active ones that are not
/// Protected, not mirrored from a remote subscription, and not inside an excluded folder's subtree; nothing outside
/// that set is ever read, sent or named.
/// </summary>
public interface IDreamCorpus
{
    IReadOnlyList<DreamArticle> Articles { get; }

    /// <summary>The active folders, empty ones included (excluded subtrees left out).</summary>
    IReadOnlyList<DreamFolder> Folders { get; }

    /// <summary>Every tag with its count of active articles (a tag with none has count 0).</summary>
    IReadOnlyList<DreamTag> Tags { get; }

    IReadOnlyList<string> TagsOf(Guid articleId);

    /// <summary>The nearest articles by vector, best first, at most <paramref name="topK"/>; empty when the article has
    /// no vector.</summary>
    IReadOnlyList<DreamNeighbour> NeighboursOf(Guid articleId, int topK);

    /// <summary>
    /// Every neighbour that meets either threshold of plan 3.6: similarity at least
    /// <paramref name="minSimilarity"/> or Z at least <paramref name="minZ"/>. Unlike
    /// <see cref="NeighboursOf"/> this has no top-K cut, so a duplicate candidate is never hidden by its rank.
    /// The default implementation asks for the whole row and filters it; DreamCorpusBuilder should forward this to
    /// <see cref="DreamNeighbourIndex.CandidatesOf"/>, which scans the row once for the mean and standard deviation
    /// but does not sort it.
    /// </summary>
    IReadOnlyList<DreamNeighbour> CandidatesOf(Guid articleId, double minSimilarity, double minZ)
    {
        var matches = new List<DreamNeighbour>();
        foreach (var neighbour in NeighboursOf(articleId, int.MaxValue))
        {
            if (neighbour.Similarity >= minSimilarity || neighbour.Z >= minZ) matches.Add(neighbour);
        }
        return matches;
    }

    /// <summary>
    /// True for a path inside an excluded folder's subtree or a remote subscription: such a folder is left out of
    /// <see cref="Folders"/> and Dream never names it, offers it as a destination or reads it. A model task that is about
    /// to propose a path of its own (a new subfolder, a new name) asks this first, because the corpus cannot list what it
    /// leaves out and so cannot tell the task that the path already exists there. The default says "nothing is hidden";
    /// DreamCorpus answers from the excluded folders of the run's settings.
    /// </summary>
    bool IsHiddenPath(string path) => false;

    /// <summary>The article's plain text, or null when it may not be read (gone, Protected by flag or by content).</summary>
    Task<string?> ReadBodyAsync(Guid articleId, CancellationToken ct);

    /// <summary>How many other articles name this one's id in their text; -1 when the search is unavailable.</summary>
    Task<int> CountReferencesAsync(Guid articleId, CancellationToken ct);
}

/// <param name="TreePath">The article's folder path, as Article.TreePath.</param>
public sealed record DreamArticle(
    Guid Id,
    string Title,
    string TreePath,
    Guid? FolderId,
    long LamportTs,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool HasEmbedding);

/// <param name="DirectArticles">Articles directly in the folder.</param>
/// <param name="SubtreeArticles">Articles in the folder and every folder below it.</param>
public sealed record DreamFolder(Guid Id, string Path, int DirectArticles, int SubtreeArticles, bool IsSystem);

public sealed record DreamTag(string Name, int ArticleCount);

/// <param name="Similarity">Cosine similarity of the two articles' vectors.</param>
/// <param name="Z">(Similarity - mean) / std over the article's similarities to every other article.</param>
public sealed record DreamNeighbour(Guid Id, double Similarity, double Z);

/// <summary>A detector without a model (plan 3.6): pure logic over the corpus, yields drafts the engine validates.</summary>
public interface IDreamDetector
{
    string Name { get; }

    IAsyncEnumerable<DreamAdviceDraft> DetectAsync(IDreamCorpus corpus, DreamRunContext ctx, CancellationToken ct);
}

/// <summary>
/// An advice as a detector or a model task proposes it, before the engine checks schema and ids, applies dismiss
/// rules, suppresses repeats by fingerprint and drops it under min_score. The fingerprint is computed from Kind and
/// Action by the engine (DreamFingerprint.Of), not chosen by the proposer.
/// </summary>
public sealed record DreamAdviceDraft
{
    public required DreamAdviceKind Kind { get; init; }
    public required DreamAdviceSource Source { get; init; }
    public required DreamApplyMode ApplyMode { get; init; }
    /// <summary>0..100 (plan 3.4 anchors; rule scores by the formulas of 3.6).</summary>
    public required int Score { get; init; }
    public required string Title { get; init; }
    public required string Summary { get; init; }
    public required DreamAction Action { get; init; }
    public IReadOnlyList<DreamEvidence> Evidence { get; init; } = [];
    public IReadOnlyList<DreamAdviceObject> Objects { get; init; } = [];
    public IReadOnlyList<DreamPrecondition> Preconditions { get; init; } = [];
    public string? FamilyKey { get; init; }
    public string? CandidateKey { get; init; }
    public DreamReferences? References { get; init; }
    public DreamAdviceModelInfo? Model { get; init; }
}

/// <summary>A source of pairs for the model, by tier (T1 duplicate candidates, T2, T3: DreamThresholds).</summary>
/// <param name="Tier">1, 2 or 3.</param>
public sealed record DreamPairCandidate(Guid AId, Guid BId, double Similarity, double Z, int Tier);

/// <summary>
/// One run's fixed inputs and shared services, made by DreamRunner at the start and handed to every detector and
/// model task. Limits here are the ones captured at the start (dream_run.max_*).
/// </summary>
public sealed class DreamRunContext
{
    public required Guid RunId { get; init; }
    public required DreamRunTrigger Trigger { get; init; }
    public required DreamRunMode Mode { get; init; }
    public required DreamSettings Settings { get; init; }
    public DreamThresholds Thresholds { get; init; } = DreamThresholds.Default;

    public bool UseModel { get; init; }
    /// <summary>The built-in rules run in this run (the detectors phase). Off only when a manual start asked so.</summary>
    public bool UseRules { get; init; } = true;
    /// <summary>The OpenRouter model id string of the analysis model; null when the run uses no model.</summary>
    public string? ModelId { get; init; }
    public string? ModelLabel { get; init; }

    /// <summary>New advice the run may create; 0 = no limit (manual runs only).</summary>
    public int MaxAdvice { get; init; }
    /// <summary>0 = no time limit (manual runs only).</summary>
    public int MaxMinutes { get; init; }

    /// <summary>The phases this run goes through (POST /runs phases?); null = all of them.</summary>
    public IReadOnlySet<DreamPhase>? Phases { get; init; }
    /// <summary>A trial run's deterministic sample (POST /runs sample?); null = the normal order.</summary>
    public DreamSampleRequest? Sample { get; init; }

    public required DreamRunBudget Budget { get; init; }
    public required IDreamRunLog Log { get; init; }
    public TimeProvider Clock { get; init; } = TimeProvider.System;
    public DateTime StartedAtUtc { get; init; }

    public bool IsTrial => Mode == DreamRunMode.Trial;
    public bool Includes(DreamPhase phase) => Phases == null || Phases.Contains(phase);
}

/// <summary>The run's journal (dream_run_event): one line per call, sealed by the store. Never an article's text.</summary>
public interface IDreamRunLog
{
    Task WriteAsync(DreamEventKind kind, string text, IReadOnlyList<Guid>? articleIds = null, JsonObject? data = null,
        CancellationToken ct = default);
}

/// <summary>
/// The model budget of one run: calls and tokens (input plus output), shared by every model task. A call is one
/// outgoing request, not one answer: the model client takes a call slot for every attempt, each key of a failover and
/// each retry round included. A request is made only after <see cref="TryReserve"/> admits its upper estimate
/// (characters / 3 plus the output limit, plan 3.7); what it really cost is then recorded with <see cref="Record"/>.
/// The token limit is an estimate, not a hard cap: characters / 3 is not an upper bound for every text and no tokenizer
/// of the chosen model is at hand, so the last request can take a run a little past it. Calls are strictly sequential,
/// but the counters are interlocked so the status endpoint can read them while the run works.
/// </summary>
public sealed class DreamRunBudget(int maxModelCalls, long maxTokens)
{
    private int _calls;
    private long _promptTokens;
    private long _completionTokens;

    public int MaxModelCalls { get; } = maxModelCalls;
    public long MaxTokens { get; } = maxTokens;

    public int ModelCalls => Volatile.Read(ref _calls);
    public long PromptTokens => Interlocked.Read(ref _promptTokens);
    public long CompletionTokens => Interlocked.Read(ref _completionTokens);
    public long TokensUsed => PromptTokens + CompletionTokens;

    /// <summary>True when one more call whose input and output are at most <paramref name="estimatedTokens"/> stays
    /// inside both limits. Does not count anything by itself.</summary>
    public bool TryReserve(long estimatedTokens) =>
        ModelCalls < MaxModelCalls && TokensUsed + Math.Max(0, estimatedTokens) <= MaxTokens;

    /// <summary>Counts one call: a request that was sent, whatever came of it (an answer, a refusal, a timeout), with the
    /// tokens it is charged (none for a refusal by status).</summary>
    public void Record(long promptTokens, long completionTokens)
    {
        Interlocked.Increment(ref _calls);
        Interlocked.Add(ref _promptTokens, Math.Max(0, promptTokens));
        Interlocked.Add(ref _completionTokens, Math.Max(0, completionTokens));
    }

    public bool Exhausted => ModelCalls >= MaxModelCalls || TokensUsed >= MaxTokens;

    /// <summary>The upper estimate of a prompt's tokens: characters divided by 3, rounded up (plan 3.7).</summary>
    public static long EstimateTokens(string system, string user, int maxOutputTokens) =>
        (system.Length + user.Length + 2) / 3 + Math.Max(0, maxOutputTokens);
}

/// <summary>
/// A model task (plan 3.7): pair_verdict, folder_review, tree_review. The engine asks it for candidates of a tier,
/// skips those the examined registry already has (same candidate key, content marker and prompt version), checks
/// limits and stop between candidates, and calls <see cref="ExamineAsync"/> for the rest.
/// </summary>
public interface IDreamModelTask
{
    /// <summary>DreamNames.PairVerdictTask, FolderReviewTask or TreeReviewTask.</summary>
    string Name { get; }

    /// <summary>The embedded prompt's file name without extension (DreamNames.PairPromptVersion ...).</summary>
    string PromptVersion { get; }

    IAsyncEnumerable<DreamModelCandidate> CandidatesAsync(IDreamCorpus corpus, DreamRunContext ctx, int tier, CancellationToken ct);

    Task<DreamTaskResult> ExamineAsync(DreamModelCandidate c, IDreamCorpus corpus, IDreamModelClient model, DreamRunContext ctx,
        CancellationToken ct);
}

/// <summary>One thing a model task may examine.</summary>
/// <param name="CandidateKey">DreamFingerprint.CandidateKey(Task, ids): the examined registry's key.</param>
/// <param name="ContentMarker">DreamFingerprint.ContentMarker of what is examined: a changed article or tree makes a
/// new look worth paying for.</param>
/// <param name="FamilyKey">For a pair, DreamNames.PairFamilyKey.</param>
/// <param name="Data">Task-specific numbers for the journal (similarity, tier reason), never text.</param>
public sealed record DreamModelCandidate(
    string Task,
    string CandidateKey,
    string ContentMarker,
    int Tier,
    IReadOnlyList<Guid> ArticleIds,
    IReadOnlyList<Guid> FolderIds,
    string? FamilyKey = null,
    JsonObject? Data = null);

/// <summary>What one examination gave. The engine writes the registry row (unless the run is a trial), the candidate
/// event (DreamCandidateEventData) and passes every draft through the usual checks.</summary>
/// <param name="Verdict">The task's own word for the journal and the compare table, e.g. "related".</param>
/// <param name="Summary">The model's explanation (already cut), for the compare table only.</param>
/// <param name="Error">What went wrong, without article text; set when Outcome is Error.</param>
public sealed record DreamTaskResult(
    DreamExaminedVerdict Outcome,
    IReadOnlyList<DreamAdviceDraft> Drafts,
    string? Verdict = null,
    int? Score = null,
    string? Summary = null,
    string? Error = null,
    int ArticlesSent = 0)
{
    public static DreamTaskResult Nothing(string? verdict = null, int? score = null, string? summary = null, int articlesSent = 0) =>
        new(DreamExaminedVerdict.Nothing, [], verdict, score, summary, null, articlesSent);

    public static DreamTaskResult Failed(string error, int articlesSent = 0) =>
        new(DreamExaminedVerdict.Error, [], null, null, null, error, articlesSent);
}

/// <summary>The model step's one way out (plan 3.7): strictly sequential calls, no tools.</summary>
public interface IDreamModelClient
{
    /// <summary>Checks the run's budget first (DreamRunBudget.TryReserve) and records the call after. Throws
    /// <see cref="DreamBudgetExceededException"/> when the budget does not admit the call and
    /// <see cref="DreamProviderException"/> when every key failed after the waits.</summary>
    Task<DreamModelReply> CompleteAsync(DreamModelRequest request, CancellationToken ct);
}

/// <param name="ModelId">The OpenRouter model id string.</param>
/// <param name="Budget">The run's budget the call is checked against and counted in.</param>
public sealed record DreamModelRequest(string ModelId, string System, string User, int MaxOutputTokens, DreamRunBudget Budget);

/// <param name="Model">The model the provider says answered.</param>
/// <param name="CutOff">The answer was stopped by the output limit: the provider said <c>finish_reason</c> length, or a
/// model that reasons sent its reasoning and no content, so the cap was spent before any answer was written. A reply that
/// is cut off is usually not valid JSON; the tasks name it as such in the journal instead of "reply is empty".</param>
public sealed record DreamModelReply(string Content, long PromptTokens, long CompletionTokens, string? Model, bool CutOff = false);

/// <summary>The run's model budget (calls or tokens) does not admit the next call: the run ends with budget_limit.</summary>
public sealed class DreamBudgetExceededException(string message) : Exception(message);

/// <summary>Every key is exhausted or the provider keeps failing after the waits: the run ends with provider_error.
/// The message carries no key and no article text.</summary>
public sealed class DreamProviderException(string message, Exception? inner = null) : Exception(message, inner);
