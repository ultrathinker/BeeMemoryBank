using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// tree_review (plan 3.7): one look at the whole folder tree, as a map of folder paths and note counts, for up to ten
/// suggestions: merge two folders that are plainly the same, rename a folder with a placeholder name, or a note about a
/// structure that is hard to use. The prompt is <c>tree-1</c>; the model never sees a note, so it is told to say only
/// what the map supports and that a tree organised on several principles at once is normal.
///
/// <para>What is sent is paths and numbers: at most 500 folders to four levels deep (the largest first when there are
/// more), without system folders, excluded folders or anything below them, and without notes under the second password
/// (they are not in the corpus, so they are not in a count). All three kinds of suggestion are applied by the person, by
/// a prompt for a personal agent, never by code: <c>merge_folders</c> and <c>rename</c> change a path other tools may
/// write to, and the score of both is capped at <see cref="FolderReviewTask.RenameScoreCap"/>.</para>
///
/// <para>The task offers one candidate, keyed by the task alone, with the map itself as the content marker: an unchanged
/// tree is skipped by the examined registry, and a normal run does not look again within
/// <see cref="DreamThresholds.TreeReviewMinDays"/> days of the last look, asked of the examined store through
/// <see cref="IDreamExaminedLookup"/> (a required dependency; the engine checks the same limit before the phase starts, and
/// this is the second hold, so the limit does not rest on the engine alone). A trial run neither reads nor writes the
/// registry, so it is not held back.</para>
///
/// <para>The reply is read like folder_review's (schema check and one retry; a suggestion that does not fit the map is
/// dropped on its own, by reason).</para>
/// </summary>
public sealed class TreeReviewTask : IDreamModelTask
{
    /// <summary>Ten suggestions with a 400 character summary each need about a third of this; the rest is room for a model
    /// that reasons before it answers, whose reasoning counts against the cap (the client asks for low effort).</summary>
    internal const int MaxOutputTokens = 6000;

    /// <summary>The tree is not reviewed when it has fewer folders than this.</summary>
    internal const int MinFolders = 5;

    internal const int MaxNotePaths = 5;
    internal const int NoteTitleMaxLength = 80;

    private static readonly string[] Types = ["merge_folders", "rename", "note"];

    private readonly IDreamExaminedLookup _examined;
    private readonly ConditionalWeakTable<DreamRunContext, HashSet<string>> _offered = new();

    /// <summary>The task as the container makes it: the examined store is a required dependency, so there is no way to build
    /// the task in production with the seven-day limit silently off.</summary>
    public TreeReviewTask(DreamStore store) : this((IDreamExaminedLookup)store)
    {
    }

    /// <summary>A test's own lookup.</summary>
    internal TreeReviewTask(IDreamExaminedLookup examined)
    {
        _examined = examined ?? throw new ArgumentNullException(nameof(examined));
    }

    /// <summary>What the task asks about the last look at the tree (the store in production).</summary>
    internal IDreamExaminedLookup Examined => _examined;

    public string Name => DreamNames.TreeReviewTask;

    public string PromptVersion => DreamNames.TreePromptVersion;

    // ── The map ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The tree as the model sees it, and the set of paths its answer may name.</summary>
    internal sealed class TreeMap
    {
        public required string Text { get; init; }
        public required int Listed { get; init; }
        public required int Total { get; init; }

        /// <summary>The paths listed, "/" included (it may be named by a note, not by a merge or a rename).</summary>
        public required DreamPathSet Paths { get; init; }

        public required DreamTreeIndex Index { get; init; }

        /// <summary>Notes directly in a folder and in it with everything below it, by folder path.</summary>
        public required IReadOnlyDictionary<string, (int Direct, int Subtree)> Counts { get; init; }
    }

    internal static TreeMap BuildMap(IDreamCorpus corpus, DreamThresholds t)
    {
        var index = DreamTreeIndex.Of(corpus);

        // Direct counts from the notes the corpus really holds, and the counts with everything below added up the tree.
        var direct = new Dictionary<string, int>(StringComparer.Ordinal);
        var subtree = new Dictionary<string, int>(StringComparer.Ordinal);
        var all = 0;
        foreach (var (path, members) in index.MembersByPath)
        {
            direct[path] = members.Count;
            all += members.Count;
            for (var p = path; p != null; p = DreamTree.ParentOf(p))
                subtree[p] = subtree.GetValueOrDefault(p) + members.Count;
        }
        subtree[DreamTree.Root] = all;

        var maxDepth = Math.Max(1, t.TreeReviewMaxDepth);
        var maxLines = Math.Max(1, t.TreeReviewMaxLines);
        var eligible = index.FoldersByPath.Keys.Where(p => p != DreamTree.Root && DreamTree.DepthOf(p) <= maxDepth).ToList();
        var chosen = eligible
            .OrderByDescending(p => subtree.GetValueOrDefault(p)).ThenBy(p => p, StringComparer.Ordinal)
            .Take(maxLines).OrderBy(DreamTree.TreeOrderKey, StringComparer.Ordinal).ToList();
        var total = index.FoldersByPath.Keys.Count(p => p != DreamTree.Root);

        var counts = new Dictionary<string, (int, int)>(StringComparer.Ordinal)
        {
            [DreamTree.Root] = (direct.GetValueOrDefault(DreamTree.Root), all),
        };
        var sb = new StringBuilder();
        sb.Append("Review the folder tree below and answer with the JSON object described in the instructions.\n\n");
        sb.Append("<tree>\n");
        sb.Append("Columns: folder path | notes directly in it | notes in it and in all folders below it.\n");
        sb.Append("Folders listed: ").Append(chosen.Count.ToString(CultureInfo.InvariantCulture)).Append(" of ")
            .Append(total.ToString(CultureInfo.InvariantCulture)).Append(". Only folders up to ")
            .Append(maxDepth.ToString(CultureInfo.InvariantCulture)).Append(" levels deep are listed; when there are too many, the largest.\n");
        AppendLine(sb, DreamTree.Root, direct.GetValueOrDefault(DreamTree.Root), all);
        foreach (var path in chosen)
        {
            var d = direct.GetValueOrDefault(path);
            var s = subtree.GetValueOrDefault(path);
            counts[path] = (d, s);
            AppendLine(sb, path, d, s);
        }
        sb.Append("</tree>\n");

        return new TreeMap
        {
            Text = sb.ToString(),
            Listed = chosen.Count,
            Total = total,
            Paths = new DreamPathSet(chosen.Prepend(DreamTree.Root)),
            Index = index,
            Counts = counts,
        };
    }

    private static void AppendLine(StringBuilder sb, string path, int direct, int subtree) =>
        sb.Append(DreamTree.Line(path, 300)).Append(" | ").Append(direct.ToString(CultureInfo.InvariantCulture)).Append(" | ")
            .Append(subtree.ToString(CultureInfo.InvariantCulture)).Append('\n');

    // ── Candidates ──────────────────────────────────────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<DreamModelCandidate> CandidatesAsync(IDreamCorpus corpus, DreamRunContext ctx, int tier,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        var t = ctx.Thresholds;
        var map = BuildMap(corpus, t);
        if (map.Total < MinFolders) yield break;

        var key = DreamFingerprint.CandidateKey(Name, []);
        var offered = _offered.GetOrCreateValue(ctx);
        bool first;
        lock (offered)
        {
            first = offered.Add(key);
        }
        if (!first) yield break;

        if (!ctx.IsTrial && t.TreeReviewMinDays > 0
            && await _examined.LastExaminedAtAsync(Name, ct) is { } last
            && ctx.Clock.GetUtcNow() - new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc)) < TimeSpan.FromDays(t.TreeReviewMinDays))
            yield break;

        yield return new DreamModelCandidate(
            Task: Name,
            CandidateKey: key,
            ContentMarker: DreamFingerprint.ContentMarker(map.Text),
            Tier: tier,
            ArticleIds: [],
            FolderIds: [],
            FamilyKey: null,
            Data: new JsonObject
            {
                ["folders"] = map.Total,
                ["listed"] = map.Listed,
            });
    }

    // ── Examination ─────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<DreamTaskResult> ExamineAsync(DreamModelCandidate c, IDreamCorpus corpus, IDreamModelClient model,
        DreamRunContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ctx.ModelId)) return DreamTaskResult.Failed("No analysis model is set for this run.");

        var t = ctx.Thresholds;
        var map = BuildMap(corpus, t);
        if (map.Total < MinFolders) return DreamTaskResult.Nothing("small");

        var system = DreamPromptResources.Load(PromptVersion);
        var outcome = await DreamModelExchange.AskAsync(model, ctx, system, map.Text, MaxOutputTokens, "tree_review", [], c.Tier,
            content =>
            {
                var r = ReadReply(content, map, t, out var error);
                return (r, error);
            }, ct);

        if (outcome.Value == null)
            return DreamTaskResult.Failed(DreamModelExchange.FailureText(outcome.Error));

        var reply = outcome.Value;
        foreach (var reason in reply.Dropped)
            await ctx.Log.WriteAsync(DreamEventKind.Skip, $"tree_review suggestion dropped: {reason}", null, null, ct);

        if (reply.Suggestions.Count == 0)
            return DreamTaskResult.Nothing(reply.Dropped.Count > 0 ? "dropped" : "none");

        var drafts = reply.Suggestions.Select(s => BuildDraft(s, map, c, ctx, outcome.PromptTokens, outcome.CompletionTokens)).ToList();
        var best = reply.Suggestions.OrderByDescending(s => s.Score).First();
        var verdict = string.Join("+", reply.Suggestions.Select(s => s.Type).Distinct().Order(StringComparer.Ordinal));
        return new DreamTaskResult(DreamExaminedVerdict.Advice, drafts, verdict, best.Score, best.Summary, null, 0);
    }

    // ── The reply ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A suggestion that passed both the schema check and the check against the map.</summary>
    internal sealed record TreeSuggestion(string Type, int Score, string Summary)
    {
        /// <summary>merge_folders: the folder that goes; rename: the folder renamed.</summary>
        public string? From { get; init; }

        /// <summary>merge_folders: the folder it goes into.</summary>
        public string? Into { get; init; }

        /// <summary>rename: the new name; note: the title.</summary>
        public string? Name { get; init; }

        /// <summary>note: the listed paths it is about, the first one its anchor.</summary>
        public IReadOnlyList<string> Paths { get; init; } = [];
    }

    internal sealed record TreeReply(IReadOnlyList<TreeSuggestion> Suggestions, IReadOnlyList<string> Dropped);

    private sealed record RawSuggestion(int Index, string Type, int Score, string Summary, string? One, string? Two, List<string> Many);

    /// <summary>The schema check of a tree_review reply (see <see cref="FolderReviewTask.ReadReply"/>): a structural miss
    /// is an error naming the field; a suggestion that does not fit the map (a path that was not listed, the top level in
    /// a merge or a rename, a folder used twice, a target inside its source, a taken name, a note without a listed path)
    /// is dropped alone.</summary>
    internal static TreeReply? ReadReply(string? content, TreeMap map, DreamThresholds t, out string? error)
    {
        if (!DreamJsonReader.TryReadObject(content, out var obj, out error)) return null;

        var root = new DreamJsonFields(obj);
        var array = root.Array("suggestions", required: true);
        if (array == null)
        {
            error = root.Error;
            return null;
        }

        var raws = new List<RawSuggestion>();
        var take = Math.Min(array.Count, Math.Max(0, t.TreeReviewMaxSuggestions));
        for (var i = 0; i < take && root.Ok; i++)
        {
            if (array[i] is not JsonObject o)
            {
                root.Fail($"'suggestions[{i}]' must be an object.");
                break;
            }
            var f = new DreamJsonFields(o);
            var type = f.OneOf("type", Types, required: true);
            var score = f.Integer("score", 0, 100, required: true);
            var summary = f.Text("summary", t.SummaryMaxLength, required: true);
            string? one = null, two = null;
            List<string>? many = null;
            switch (type)
            {
                case "merge_folders":
                    one = f.Text("from", 2000, required: true);
                    two = f.Text("into", 2000, required: true);
                    break;
                case "rename":
                    one = f.Text("path", 2000, required: true);
                    two = f.Text("newName", 200, required: true);
                    break;
                case "note":
                    one = f.Text("title", 1000, required: true);
                    many = ReadStrings(f, "paths");
                    break;
            }
            if (!f.Ok)
            {
                root.Fail($"suggestions[{i}]: {f.Error}");
                break;
            }
            raws.Add(new RawSuggestion(i, type!, score!.Value, summary!, one, two, many ?? []));
        }
        if (!root.Ok)
        {
            error = root.Error;
            return null;
        }

        error = null;
        return CheckAgainstMap(raws, map);
    }

    private static List<string>? ReadStrings(DreamJsonFields f, string name)
    {
        var array = f.Array(name, required: true);
        if (array == null) return null;
        var items = new List<string>();
        foreach (var item in array)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var s)) items.Add(s);
            else
            {
                f.Fail($"'{name}' must hold strings.");
                return null;
            }
        }
        return items;
    }

    private static TreeReply CheckAgainstMap(List<RawSuggestion> raws, TreeMap map)
    {
        var accepted = new List<TreeSuggestion>();
        var dropped = new List<string>();
        var sources = new HashSet<string>(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var r in raws.OrderByDescending(r => r.Score).ThenBy(r => r.Index))
        {
            switch (r.Type)
            {
                case "merge_folders":
                {
                    var from = map.Paths.Match(r.One);
                    var into = map.Paths.Match(r.Two);
                    if (from == null || into == null) { dropped.Add("folder not in the tree that was listed"); break; }
                    if (from == DreamTree.Root || into == DreamTree.Root) { dropped.Add("the top level cannot be merged"); break; }
                    if (from == into) { dropped.Add("same folder"); break; }
                    if (DreamTree.IsUnder(into, from)) { dropped.Add("target inside the source"); break; }
                    if (sources.Contains(from) || targets.Contains(from) || sources.Contains(into)) { dropped.Add("folder already used"); break; }
                    sources.Add(from);
                    targets.Add(into);
                    accepted.Add(new TreeSuggestion("merge_folders", Math.Min(r.Score, FolderReviewTask.RenameScoreCap), r.Summary)
                        { From = from, Into = into });
                    break;
                }
                case "rename":
                {
                    var path = map.Paths.Match(r.One);
                    if (path == null) { dropped.Add("folder not in the tree that was listed"); break; }
                    if (path == DreamTree.Root) { dropped.Add("the top level cannot be renamed"); break; }
                    var name = DreamTree.CleanSegment(r.Two);
                    if (name == null) { dropped.Add("bad folder name"); break; }
                    if (name == DreamTree.NameOf(path)) { dropped.Add("same name"); break; }
                    if (sources.Contains(path) || targets.Contains(path)) { dropped.Add("folder already used"); break; }
                    var wanted = DreamTree.Join(DreamTree.ParentOf(path)!, name).ToLowerInvariant();
                    // A folder the corpus hides (excluded, remote) is not in the index: the name may be taken there.
                    if (map.Index.IsBlocked(DreamTree.Join(DreamTree.ParentOf(path)!, name))
                        || map.Index.FoldersByPath.Keys.Any(p => p != path && p.ToLowerInvariant() == wanted))
                    {
                        dropped.Add("name already taken");
                        break;
                    }
                    sources.Add(path);
                    accepted.Add(new TreeSuggestion("rename", Math.Min(r.Score, FolderReviewTask.RenameScoreCap), r.Summary)
                        { From = path, Name = name });
                    break;
                }
                default:
                {
                    var title = DreamJsonReader.CleanText(r.One ?? "", NoteTitleMaxLength);
                    var paths = r.Many.Select(p => map.Paths.Match(p)).OfType<string>().Distinct(StringComparer.Ordinal)
                        .Take(MaxNotePaths).ToList();
                    if (paths.Count == 0) { dropped.Add("no folder in the tree that was listed"); break; }
                    accepted.Add(new TreeSuggestion("note", r.Score, r.Summary) { Name = title, Paths = paths });
                    break;
                }
            }
        }
        return new TreeReply(accepted, dropped);
    }

    // ── The advice ──────────────────────────────────────────────────────────────────────────────────────────────

    private static DreamAdviceDraft BuildDraft(TreeSuggestion s, TreeMap map, DreamModelCandidate c, DreamRunContext ctx,
        long promptTokens, long completionTokens)
    {
        var t = ctx.Thresholds;
        var model = new DreamAdviceModelInfo(ctx.ModelId!, ctx.ModelLabel ?? ctx.ModelId!, DreamNames.TreePromptVersion,
            promptTokens, completionTokens);
        const string PathWarning = "A rename or a merge changes a path: tools, scripts and agents that write there by path will need the new one.";

        Guid? IdOf(string path) => map.Index.FoldersByPath.TryGetValue(path, out var f) ? f.Id : null;
        string Show(string path, int max = 100) => DreamJsonReader.CleanText(path, max);

        DreamAdviceDraft Make(DreamAdviceKind kind, string title, DreamAction action, IReadOnlyList<DreamEvidence> evidence,
            IReadOnlyList<DreamAdviceObject> objects, IReadOnlyList<DreamPrecondition> pre) => new()
        {
            Kind = kind,
            Source = DreamAdviceSource.Model,
            ApplyMode = DreamApplyMode.Prompt,
            Score = DreamScore.Clamp(s.Score),
            Title = DreamJsonReader.Cut(title, t.TitleMaxLength),
            Summary = s.Summary,
            Action = action,
            Evidence = evidence,
            Objects = objects,
            Preconditions = pre,
            CandidateKey = c.CandidateKey,
            Model = model,
        };

        string Notes(string path) =>
            map.Counts.TryGetValue(path, out var n)
                ? $"{Show(path, 70)} holds {n.Direct.ToString(CultureInfo.InvariantCulture)} notes directly, {n.Subtree.ToString(CultureInfo.InvariantCulture)} with its subfolders."
                : $"{Show(path, 70)} is below the depth that was listed.";

        switch (s.Type)
        {
            case "merge_folders":
                return Make(DreamAdviceKind.MergeFolders, $"Merge folder {Show(s.From!, 70)} into {Show(s.Into!, 70)}",
                    DreamAction.MergeFolders(s.From!, s.Into!),
                    [new DreamEvidence(DreamEvidenceType.Count, $"{Notes(s.From!)} {Notes(s.Into!)}"),
                        new DreamEvidence(DreamEvidenceType.Note, PathWarning)],
                    [DreamAdviceObject.ForFolder(IdOf(s.From!), s.From!, DreamObjectRole.Subject),
                        DreamAdviceObject.ForFolder(IdOf(s.Into!), s.Into!, DreamObjectRole.Target)],
                    [DreamPrecondition.FolderExists(s.From!), DreamPrecondition.FolderExists(s.Into!)]);

            case "rename":
                return Make(DreamAdviceKind.RenameFolder, $"Rename folder {Show(s.From!)} to \"{Show(s.Name!, 70)}\"",
                    DreamAction.RenameFolder(s.From!, s.Name!),
                    [new DreamEvidence(DreamEvidenceType.Count, Notes(s.From!)), new DreamEvidence(DreamEvidenceType.Note, PathWarning)],
                    [DreamAdviceObject.ForFolder(IdOf(s.From!), s.From!, DreamObjectRole.Subject)],
                    [DreamPrecondition.FolderExists(s.From!)]);

            default:
            {
                // A note carries no change, but its fingerprint must tell one observation from another: the anchor folder
                // goes into the action, so dismissing one note does not silence every other.
                var anchor = s.Paths[0];
                var objects = s.Paths.Select((p, i) =>
                    DreamAdviceObject.ForFolder(IdOf(p), p, i == 0 ? DreamObjectRole.Subject : DreamObjectRole.Other)).ToList();
                var pre = s.Paths.Where(p => p != DreamTree.Root).Select(DreamPrecondition.FolderExists).ToList();
                return Make(DreamAdviceKind.StructureNote, $"Structure: {s.Name}",
                    DreamAction.Note() with { Path = anchor },
                    [new DreamEvidence(DreamEvidenceType.Note, "Seen on the map of folders alone: the notes themselves were not read."),
                        .. s.Paths.Where(map.Counts.ContainsKey).Select(p => new DreamEvidence(DreamEvidenceType.Count, Notes(p)))],
                    objects, pre);
            }
        }
    }
}
