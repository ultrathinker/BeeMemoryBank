namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// The JSON of one advice kept sealed under the chat key in dream_advice.payload_* (AAD <c>bmb-dream-advice-v1:{id}</c>),
/// plan 3.4. Titles, paths, tag names, the explanation and the fingerprint live only here: chat.db is open on an
/// unsealed node, and with metadata encryption those names are secret.
/// </summary>
public sealed record DreamAdvicePayload
{
    public const int CurrentVersion = 1;

    public int V { get; init; } = CurrentVersion;
    /// <summary>DreamFingerprint.Of: 32 lowercase hex.</summary>
    public string Fingerprint { get; init; } = "";
    /// <summary><c>pair|minId|maxId</c> for advice about a pair of articles (DreamNames.PairFamilyKey), else null.</summary>
    public string? FamilyKey { get; init; }
    /// <summary>The examined-registry key of the model candidate this advice came from, else null.</summary>
    public string? CandidateKey { get; init; }
    public string Title { get; init; } = "";
    /// <summary>What and why, at most DreamThresholds.SummaryMaxLength characters.</summary>
    public string Summary { get; init; } = "";
    public IReadOnlyList<DreamEvidence> Evidence { get; init; } = [];
    public IReadOnlyList<DreamAdviceObject> Objects { get; init; } = [];
    public DreamAction Action { get; init; } = DreamAction.Note();
    public IReadOnlyList<DreamPrecondition> Preconditions { get; init; } = [];
    public DreamReferences? References { get; init; }
    public DreamAdviceModelInfo? Model { get; init; }
    public DreamAppliedInfo? Applied { get; init; }
    public string? LastError { get; init; }

    /// <summary>The payload of a new advice made from a validated draft.</summary>
    public static DreamAdvicePayload FromDraft(DreamAdviceDraft draft) => new()
    {
        Fingerprint = DreamFingerprint.OfDraft(draft),
        FamilyKey = draft.FamilyKey,
        CandidateKey = draft.CandidateKey,
        Title = draft.Title,
        Summary = draft.Summary,
        Evidence = draft.Evidence,
        Objects = draft.Objects,
        Action = draft.Action,
        Preconditions = draft.Preconditions,
        References = draft.References,
        Model = draft.Model,
    };

    /// <summary>Every article id the advice names (objects, action, preconditions, evidence, reference samples): what
    /// the hard-delete sweep looks for. A sample carries a title, so a deleted one must not survive here.</summary>
    public IReadOnlyList<Guid> ArticleIds() =>
        Objects.Where(o => o.Type == DreamObjectType.Article && o.Id.HasValue).Select(o => o.Id!.Value)
            .Concat(Action.ArticleIdsNamed())
            .Concat(Preconditions.Where(p => p.Id.HasValue && (p.Type is DreamPreconditionType.ArticleUnchanged
                or DreamPreconditionType.ArticleExists or DreamPreconditionType.ArticleInPath)).Select(p => p.Id!.Value))
            .Concat(Evidence.Where(e => e.ArticleId.HasValue).Select(e => e.ArticleId!.Value))
            .Concat(References?.Samples.Select(s => s.Id) ?? [])
            .Distinct()
            .ToList();
}

/// <summary>One line of the reasoning shown on the advice card.</summary>
/// <param name="Value">A number the line is about (a similarity, a count), if any.</param>
/// <param name="ArticleId">The article a quote comes from, if any.</param>
public sealed record DreamEvidence(DreamEvidenceType Type, string Text, double? Value = null, Guid? ArticleId = null);

/// <summary>
/// An article, folder or tag the advice is about. Articles carry Id, Title, TreePath, LamportTs, UpdatedAt; folders Id
/// (null for a folder that does not exist yet, e.g. a split target) and Path; tags Name and ArticleCount.
/// </summary>
public sealed record DreamAdviceObject
{
    public DreamObjectRole? Role { get; init; }
    public DreamObjectType Type { get; init; }
    public Guid? Id { get; init; }
    public string? Title { get; init; }
    public string? TreePath { get; init; }
    public long? LamportTs { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public string? Path { get; init; }
    public string? Name { get; init; }
    public int? ArticleCount { get; init; }

    public static DreamAdviceObject ForArticle(DreamArticle a, DreamObjectRole? role = null) => new()
    {
        Role = role, Type = DreamObjectType.Article, Id = a.Id, Title = a.Title, TreePath = a.TreePath,
        LamportTs = a.LamportTs, UpdatedAt = a.UpdatedAt,
    };

    public static DreamAdviceObject ForFolder(Guid? id, string path, DreamObjectRole? role = null) => new()
    {
        Role = role, Type = DreamObjectType.Folder, Id = id, Path = path,
    };

    public static DreamAdviceObject ForTag(string name, int articleCount) => new()
    {
        Type = DreamObjectType.Tag, Name = name, ArticleCount = articleCount,
    };
}

/// <summary>
/// <c>action</c> of an advice: its type and the parameters of that type, flat (<c>{ "type": "move_article",
/// "articleId": "...", "toPath": "/..." }</c>). Build one through the factory of its type; the parameters each type
/// uses are listed on <see cref="DreamActionType"/>. Paths are canonical tree paths (TreePathCanonicalizer).
/// </summary>
public sealed record DreamAction
{
    public DreamActionType Type { get; init; }

    public Guid? ArticleId { get; init; }
    public Guid? KeepId { get; init; }
    public Guid? AId { get; init; }
    public Guid? BId { get; init; }
    public Guid? OtherId { get; init; }
    public Guid? CurrentId { get; init; }
    public Guid? OldId { get; init; }
    public Guid? FolderId { get; init; }
    public IReadOnlyList<Guid>? ArticleIds { get; init; }
    public string? ToPath { get; init; }
    public string? Path { get; init; }
    public string? NewName { get; init; }
    public string? From { get; init; }
    public string? Into { get; init; }
    public IReadOnlyList<string>? Sources { get; init; }
    public string? Target { get; init; }
    public IReadOnlyList<string>? Names { get; init; }

    public static DreamAction TrashArticle(Guid articleId, Guid keepId) => new() { Type = DreamActionType.TrashArticle, ArticleId = articleId, KeepId = keepId };
    public static DreamAction ReviewPair(Guid aId, Guid bId) => new() { Type = DreamActionType.ReviewPair, AId = aId, BId = bId };
    public static DreamAction MergeArticles(Guid keepId, Guid otherId) => new() { Type = DreamActionType.MergeArticles, KeepId = keepId, OtherId = otherId };
    public static DreamAction MarkSuperseded(Guid currentId, Guid oldId) => new() { Type = DreamActionType.MarkSuperseded, CurrentId = currentId, OldId = oldId };
    public static DreamAction MoveArticle(Guid articleId, string toPath) => new() { Type = DreamActionType.MoveArticle, ArticleId = articleId, ToPath = toPath };
    public static DreamAction MoveArticles(string toPath, IReadOnlyList<Guid> articleIds) => new() { Type = DreamActionType.MoveArticles, ToPath = toPath, ArticleIds = articleIds };
    public static DreamAction RenameFolder(string path, string newName) => new() { Type = DreamActionType.RenameFolder, Path = path, NewName = newName };
    public static DreamAction MergeFolders(string from, string into) => new() { Type = DreamActionType.MergeFolders, From = from, Into = into };
    public static DreamAction Note() => new() { Type = DreamActionType.Note };
    public static DreamAction MergeTags(IReadOnlyList<string> sources, string target) => new() { Type = DreamActionType.MergeTags, Sources = sources, Target = target };
    public static DreamAction DeleteTags(IReadOnlyList<string> names) => new() { Type = DreamActionType.DeleteTags, Names = names };
    public static DreamAction DeleteFolder(string path, Guid folderId) => new() { Type = DreamActionType.DeleteFolder, Path = path, FolderId = folderId };
    public static DreamAction ReviewFolder(string path) => new() { Type = DreamActionType.ReviewFolder, Path = path };

    /// <summary>Every article id among the parameters.</summary>
    public IEnumerable<Guid> ArticleIdsNamed()
    {
        foreach (var id in new[] { ArticleId, KeepId, AId, BId, OtherId, CurrentId, OldId })
            if (id.HasValue) yield return id.Value;
        if (ArticleIds != null)
            foreach (var id in ArticleIds) yield return id;
    }
}

/// <summary>
/// A condition that must still hold for the advice to stand (plan 3.4). Checked cheaply when a list is served,
/// strictly before Apply, before a prompt or a chat hand-off, and by the sweep.
/// </summary>
public sealed record DreamPrecondition
{
    public DreamPreconditionType Type { get; init; }
    public Guid? Id { get; init; }
    public long? LamportTs { get; init; }
    public string? Path { get; init; }
    public string? Name { get; init; }

    public static DreamPrecondition ArticleUnchanged(Guid id, long lamportTs) => new() { Type = DreamPreconditionType.ArticleUnchanged, Id = id, LamportTs = lamportTs };
    public static DreamPrecondition ArticleExists(Guid id) => new() { Type = DreamPreconditionType.ArticleExists, Id = id };
    public static DreamPrecondition ArticleInPath(Guid id, string path) => new() { Type = DreamPreconditionType.ArticleInPath, Id = id, Path = path };
    public static DreamPrecondition FolderExists(string path) => new() { Type = DreamPreconditionType.FolderExists, Path = path };
    public static DreamPrecondition FolderEmpty(string path) => new() { Type = DreamPreconditionType.FolderEmpty, Path = path };
    public static DreamPrecondition TagExists(string name) => new() { Type = DreamPreconditionType.TagExists, Name = name };
    public static DreamPrecondition TagUnused(string name) => new() { Type = DreamPreconditionType.TagUnused, Name = name };
}

/// <summary>What the reference check found: other articles whose text names this one's id.</summary>
public sealed record DreamReferences(bool Checked, int Count, IReadOnlyList<DreamReferenceSample> Samples);

public sealed record DreamReferenceSample(Guid Id, string Title);

/// <summary>Which model and prompt produced an advice, and what that one examination cost.</summary>
/// <param name="Id">The OpenRouter model id string.</param>
public sealed record DreamAdviceModelInfo(string Id, string Label, string PromptVersion, long PromptTokens, long CompletionTokens);

/// <summary>Set when Apply succeeded: what was and what became, so a person can undo it by hand.</summary>
public sealed record DreamAppliedInfo(DateTime At, int ByUserId, string Result);

/// <summary>The JSON of a dismiss rule, sealed in dream_rule.payload_* (AAD <c>bmb-dream-rule-v1:{id}</c>).</summary>
/// <param name="Fingerprint">Scope advice: the fingerprint it suppresses.</param>
/// <param name="ObjectId">Scope object: the article or folder id.</param>
/// <param name="Path">Scope folder: the subtree root; scope object on a folder: its path (for display).</param>
/// <param name="TagName">Scope object on a tag: the tag name.</param>
/// <param name="Title">What the rule was made from, for the rules list (an advice title).</param>
/// <param name="Reason">The person's words, if any.</param>
/// <param name="AdviceId">The advice whose Dismiss made the rule: putting that advice back in the list removes it again.</param>
public sealed record DreamRulePayload(
    string? Fingerprint = null,
    Guid? ObjectId = null,
    string? Path = null,
    string? TagName = null,
    string? Title = null,
    string? Reason = null,
    Guid? AdviceId = null)
{
    public int V { get; init; } = 1;
}

/// <summary>The JSON of a journal line, sealed in dream_run_event.payload_* (AAD <c>bmb-dream-event-v1:{id}</c>).
/// Never an article's text.</summary>
public sealed record DreamEventPayload(string Text, IReadOnlyList<Guid> ArticleIds, System.Text.Json.Nodes.JsonObject? Data)
{
    public int V { get; init; } = 1;
}

/// <summary>
/// The <c>data</c> of a <see cref="DreamEventKind.Candidate"/> event: one examined model candidate and what came of
/// it. The compare table (GET /api/dream/compare) is built from these, so every run writes the same shape.
/// </summary>
/// <param name="Verdict">The task's own word (pair_verdict: duplicate, superseded, overlap, contradiction, related,
/// unrelated), or null on an error.</param>
/// <param name="Kind">The advice kind made from it, null when nothing was proposed.</param>
/// <param name="Summary">The model's explanation, cut to DreamThresholds.SummaryMaxLength.</param>
public sealed record DreamCandidateEventData(
    string CandidateKey,
    string Task,
    int Tier,
    IReadOnlyList<Guid> ArticleIds,
    IReadOnlyList<Guid> FolderIds,
    DreamExaminedVerdict Outcome,
    string? Verdict,
    DreamAdviceKind? Kind,
    int? Score,
    string? Summary);
