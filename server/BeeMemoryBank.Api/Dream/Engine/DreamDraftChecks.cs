using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// The check every proposal passes before it can be stored (plan 3.5): the kind and its action belong together, the
/// action has its parameters, the apply mode is one that kind may have, and every article, folder and tag it names is in
/// this run's corpus. This is the boundary against a confused detector and against a model that was talked into naming
/// something else: anything Protected or fenced off is simply not in the corpus, so it cannot be named.
/// </summary>
internal static class DreamDraftChecks
{
    private const int MaxEvidence = 20;

    private static readonly IReadOnlyDictionary<DreamAdviceKind, (DreamActionType Action, DreamApplyMode[] Modes)> Table =
        new Dictionary<DreamAdviceKind, (DreamActionType, DreamApplyMode[])>
        {
            [DreamAdviceKind.DuplicateExact] = (DreamActionType.TrashArticle, [DreamApplyMode.Code, DreamApplyMode.Chat]),
            [DreamAdviceKind.NearDuplicate] = (DreamActionType.ReviewPair, [DreamApplyMode.Chat]),
            [DreamAdviceKind.Duplicate] = (DreamActionType.MergeArticles, [DreamApplyMode.Chat]),
            [DreamAdviceKind.Superseded] = (DreamActionType.MarkSuperseded, [DreamApplyMode.Chat]),
            [DreamAdviceKind.Overlap] = (DreamActionType.ReviewPair, [DreamApplyMode.Chat]),
            [DreamAdviceKind.Contradiction] = (DreamActionType.ReviewPair, [DreamApplyMode.Prompt]),
            [DreamAdviceKind.MoveArticle] = (DreamActionType.MoveArticle, [DreamApplyMode.Code]),
            [DreamAdviceKind.SplitFolder] = (DreamActionType.MoveArticles, [DreamApplyMode.Code]),
            [DreamAdviceKind.RenameFolder] = (DreamActionType.RenameFolder, [DreamApplyMode.Prompt]),
            [DreamAdviceKind.MergeFolders] = (DreamActionType.MergeFolders, [DreamApplyMode.Prompt]),
            [DreamAdviceKind.StructureNote] = (DreamActionType.Note, [DreamApplyMode.Prompt]),
            [DreamAdviceKind.MergeTags] = (DreamActionType.MergeTags, [DreamApplyMode.Code]),
            [DreamAdviceKind.DeleteUnusedTags] = (DreamActionType.DeleteTags, [DreamApplyMode.Code]),
            [DreamAdviceKind.DeleteEmptyFolder] = (DreamActionType.DeleteFolder, [DreamApplyMode.Code]),
            [DreamAdviceKind.TestLeftovers] = (DreamActionType.ReviewFolder, [DreamApplyMode.Prompt]),
        };

    /// <summary>The action of a kind and the ways it may be applied (the rule book shows them).</summary>
    public static DreamRuleBookKind KindOf(DreamAdviceKind kind) =>
        Table.TryGetValue(kind, out var rule) ? new DreamRuleBookKind(kind, rule.Action, rule.Modes) : new DreamRuleBookKind(kind, DreamActionType.Note, []);

    /// <summary>Why the draft is refused (in English, no article text), or null with <paramref name="clean"/> set to the
    /// draft with its paths canonical and its texts cut to length.</summary>
    public static string? Check(DreamAdviceDraft draft, DreamCorpus corpus, DreamThresholds t, out DreamAdviceDraft clean)
    {
        clean = draft;
        if (!Table.TryGetValue(draft.Kind, out var rule)) return "an unknown kind";
        if (draft.Action.Type != rule.Action) return $"{DreamWire.ToWire(draft.Kind)} cannot carry the action {DreamWire.ToWire(draft.Action.Type)}";
        if (!rule.Modes.Contains(draft.ApplyMode)) return $"{DreamWire.ToWire(draft.Kind)} cannot be applied by {DreamWire.ToWire(draft.ApplyMode)}";
        if (draft.Score is < 0 or > 100) return "a score outside 0..100";
        if (string.IsNullOrWhiteSpace(draft.Title)) return "an empty title";
        if (string.IsNullOrWhiteSpace(draft.Summary)) return "an empty summary";

        if (ActionProblem(draft.Action, corpus, out var action) is { } actionProblem) return actionProblem;

        foreach (var id in draft.Action.ArticleIdsNamed().Concat(draft.Objects.Where(o => o.Type == DreamObjectType.Article).Select(o => o.Id ?? Guid.Empty)))
            if (!corpus.HasArticle(id)) return "an article outside this run";
        foreach (var e in draft.Evidence)
            if (e.ArticleId is { } evidenceId && !corpus.HasArticle(evidenceId)) return "evidence from an article outside this run";
        foreach (var p in draft.Preconditions)
            if (p.Id is { } id && !corpus.HasArticle(id)) return "a precondition on an article outside this run";
        foreach (var o in draft.Objects)
        {
            if (o.Type == DreamObjectType.Folder)
            {
                if (o.Id is { } folderId && !corpus.HasFolder(folderId)) return "a folder outside this run";
                if (o.Path is null || TreePathCanonicalizer.TryCanonicalize(o.Path) is not { } path || corpus.IsHiddenPath(path)) return "a folder path outside this run";
            }
            else if (o.Type == DreamObjectType.Tag && (o.Name is null || !corpus.HasTag(o.Name)))
            {
                return "a tag outside this run";
            }
        }

        clean = draft with
        {
            Action = action,
            Title = Cut(draft.Title, t.TitleMaxLength),
            Summary = Cut(draft.Summary, t.SummaryMaxLength),
            Evidence = draft.Evidence.Take(MaxEvidence).Select(e => e with { Text = Cut(e.Text, t.QuoteMaxLength) }).ToList(),
        };
        return null;
    }

    private static string? ActionProblem(DreamAction action, DreamCorpus corpus, out DreamAction canonical)
    {
        canonical = action;
        var missing = "an action without its parameters";
        switch (action.Type)
        {
            case DreamActionType.TrashArticle:
                if (action.ArticleId is null || action.KeepId is null) return missing;
                break;
            case DreamActionType.ReviewPair:
                if (action.AId is null || action.BId is null) return missing;
                break;
            case DreamActionType.MergeArticles:
                if (action.KeepId is null || action.OtherId is null) return missing;
                break;
            case DreamActionType.MarkSuperseded:
                if (action.CurrentId is null || action.OldId is null) return missing;
                break;
            case DreamActionType.MoveArticle:
                if (action.ArticleId is null) return missing;
                if (Path(action.ToPath, corpus, out var moveTo) is { } moveProblem) return moveProblem;
                canonical = action with { ToPath = moveTo };
                break;
            case DreamActionType.MoveArticles:
                if (action.ArticleIds is not { Count: > 0 }) return missing;
                if (Path(action.ToPath, corpus, out var moveAllTo) is { } moveAllProblem) return moveAllProblem;
                canonical = action with { ToPath = moveAllTo };
                break;
            case DreamActionType.RenameFolder:
                if (string.IsNullOrWhiteSpace(action.NewName)) return missing;
                if (Path(action.Path, corpus, out var renamed) is { } renameProblem) return renameProblem;
                canonical = action with { Path = renamed };
                break;
            case DreamActionType.MergeFolders:
                if (Path(action.From, corpus, out var from) is { } fromProblem) return fromProblem;
                if (Path(action.Into, corpus, out var into) is { } intoProblem) return intoProblem;
                canonical = action with { From = from, Into = into };
                break;
            case DreamActionType.Note:
                break;
            case DreamActionType.MergeTags:
                if (action.Sources is not { Count: > 0 } || string.IsNullOrWhiteSpace(action.Target)) return missing;
                if (!action.Sources.Append(action.Target).All(corpus.HasTag)) return "a tag outside this run";
                break;
            case DreamActionType.DeleteTags:
                if (action.Names is not { Count: > 0 }) return missing;
                if (!action.Names.All(corpus.HasTag)) return "a tag outside this run";
                break;
            case DreamActionType.DeleteFolder:
                if (action.FolderId is not { } folderId) return missing;
                if (!corpus.HasFolder(folderId)) return "a folder outside this run";
                if (Path(action.Path, corpus, out var deleted) is { } deleteProblem) return deleteProblem;
                canonical = action with { Path = deleted };
                break;
            case DreamActionType.ReviewFolder:
                if (Path(action.Path, corpus, out var reviewed) is { } reviewProblem) return reviewProblem;
                canonical = action with { Path = reviewed };
                break;
            default:
                return "an unknown action";
        }
        return null;
    }

    private static string? Path(string? value, DreamCorpus corpus, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(value)) return "an action without its path";
        if (TreePathCanonicalizer.TryCanonicalize(value) is not { } path) return "a path that cannot be used";
        if (corpus.IsHiddenPath(path)) return "a path outside this run";
        canonical = path;
        return null;
    }

    /// <summary>At most <paramref name="max"/> characters, never ending in half of a surrogate pair.</summary>
    internal static string Cut(string text, int max)
    {
        if (text.Length <= max) return text;
        var length = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..length];
    }
}

/// <summary>
/// The dismiss rules in force, loaded once at the start of a run (plan 3.4). A rule must at least cover the advice it was
/// made from (DreamDismissRules builds it from that advice the way the Dismiss dialog describes it), so <c>object</c>
/// matches anything the advice names, and <c>folder</c> any advice about something under the folder: its notes and folders
/// other than a destination, and the folder an action works on (a rename, a deletion, the source of a merge). The place a
/// note or folder would be sent to does not count: "this kind of advice in /Archive" leaves what is in /Archive alone, and
/// still lets Dream suggest moving a note from elsewhere into /Archive.
/// </summary>
internal sealed class DreamRuleSet(IReadOnlyList<DreamRuleRecord> rules)
{
    public bool Suppresses(DreamAdviceDraft draft, string fingerprint)
    {
        foreach (var rule in rules)
        {
            var matches = rule.Scope switch
            {
                DreamRuleScope.Advice => rule.Payload.Fingerprint == fingerprint,
                DreamRuleScope.Kind => rule.Kind == draft.Kind,
                DreamRuleScope.Object => rule.Kind == draft.Kind && NamesObject(rule.Payload, draft),
                DreamRuleScope.Folder => rule.Kind == draft.Kind && IsInFolder(rule.Payload.Path, draft),
                _ => false,
            };
            if (matches) return true;
        }
        return false;
    }

    private static bool NamesObject(DreamRulePayload rule, DreamAdviceDraft draft)
    {
        if (rule.ObjectId is { } id &&
            (draft.Objects.Any(o => o.Id == id) || draft.Action.ArticleIdsNamed().Contains(id) || draft.Action.FolderId == id))
            return true;

        // A folder that had no id when the rule was made (one an advice proposed to create) is named by its path.
        if (rule.ObjectId is null && rule.TagName is null && rule.Path is { } folderPath)
        {
            var wantedPath = DreamFingerprint.NormalizePath(folderPath);
            return draft.Objects.Where(o => o.Type == DreamObjectType.Folder).Select(o => o.Path)
                .Concat(ActionPaths(draft.Action))
                .OfType<string>()
                .Any(p => DreamFingerprint.NormalizePath(p) == wantedPath);
        }
        if (rule.TagName is not { } tag) return false;

        var wanted = DreamFingerprint.NormalizeName(tag);
        var named = draft.Objects.Where(o => o.Type == DreamObjectType.Tag).Select(o => o.Name!)
            .Concat(draft.Action.Sources ?? [])
            .Concat(draft.Action.Names ?? [])
            .Concat(draft.Action.Target is { } target ? [target] : Array.Empty<string>());
        return named.Any(n => DreamFingerprint.NormalizeName(n) == wanted);
    }

    private static bool IsInFolder(string? root, DreamAdviceDraft draft)
    {
        if (root is null) return false;
        var wanted = DreamFingerprint.NormalizePath(root);
        var paths = draft.Objects
            .Where(o => o.Role != DreamObjectRole.Target)
            .Select(o => o.Type == DreamObjectType.Article ? o.TreePath : o.Path)
            .OfType<string>()
            .Concat(SourcePaths(draft.Action));
        return paths.Any(p => DreamCorpusBuilder.IsUnder(DreamFingerprint.NormalizePath(p), wanted));
    }

    /// <summary>The folders an action works on, not where it sends things (toPath, into).</summary>
    private static IEnumerable<string> SourcePaths(DreamAction action) =>
        new[] { action.Path, action.From }.OfType<string>();

    private static IEnumerable<string> ActionPaths(DreamAction action) =>
        new[] { action.Path, action.From, action.Into, action.ToPath }.OfType<string>();
}
