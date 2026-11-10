using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Api.Dream;

/// <summary>
/// folder_review (plan 3.7): for a folder with many notes of its own, up to eight suggestions: move a note, group notes
/// into a subfolder, rename the folder. The prompt is <c>folder-1</c>; it asks the model to judge a note against the
/// reason its folder exists (by project, by kind of document, by time) and not against its topic.
///
/// <para>What the model sees: the folder's path, its subfolders, its parent and siblings, the folders that hold notes
/// with a similar subject (at most 40 folders in all) and up to 80 notes, the most recently changed first: a label
/// (<c>n1</c>, <c>n2</c> ...), the title, up to five tags, the dates and the first 200 characters. A note is only ever
/// named by its label, so an id the model makes up has nowhere to go; a destination is a folder path that the input
/// listed, and anything else is dropped. Notes under the second password, notes that cannot be read, excluded folders and
/// system folders are never listed or named.</para>
///
/// <para>The reply is untrusted, as in pair_verdict: one JSON object, a schema check with one retry, fields cut, unknown
/// fields ignored. A suggestion that is well formed but does not fit the input (an unknown note, a path that was not
/// listed, a name that is taken) is dropped on its own and journaled by reason; the advice that comes out is built by this
/// code from the type alone (kind, action, apply mode), and a rename never scores above
/// <see cref="RenameScoreCap"/>: a folder's path is an interface other tools write to.</para>
/// </summary>
public sealed class FolderReviewTask : IDreamModelTask
{
    /// <summary>Eight suggestions with a 400 character summary each need about a third of this; the rest is room for a model
    /// that reasons before it answers, whose reasoning counts against the cap (the client asks for low effort).</summary>
    internal const int MaxOutputTokens = 5000;

    /// <summary>A rename (and a merge of folders) is advice about a path: never "almost certainly worth it".</summary>
    internal const int RenameScoreCap = 69;

    /// <summary>With fewer readable notes than this the folder is not reviewed.</summary>
    internal const int MinShownNotes = 3;

    /// <summary>A subfolder is for a group; the prompt asks for at least three notes.</summary>
    internal const int MinSubfolderNotes = 3;

    /// <summary>
    /// The most notes one subfolder suggestion moves. Every one of them is named in the advice (an object each, with its
    /// title) and held by a precondition, so a person can look at the whole list before pressing Apply; thirty is a list
    /// that can be read. A longer group is cut to the first thirty the model named, and the advice says how many were left out.
    /// </summary>
    internal const int MaxSplitNotes = 30;

    private const int MaxStrayProbe = 300;
    private const int MaxSimilarFolders = 8;
    private const int MinSimilarVotes = 3;
    private const int SnippetSourceChars = 600;
    private const int TitleChars = 200;

    private static readonly string[] Types = ["move", "subfolder", "rename"];

    // Folders already offered in this run: the tier the engine asks for does not matter, a folder is looked at once.
    private readonly ConditionalWeakTable<DreamRunContext, HashSet<string>> _offered = new();

    public string Name => DreamNames.FolderReviewTask;

    public string PromptVersion => DreamNames.FolderPromptVersion;

    // ── Candidates ──────────────────────────────────────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<DreamModelCandidate> CandidatesAsync(IDreamCorpus corpus, DreamRunContext ctx, int tier,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        var index = DreamTreeIndex.Of(corpus);
        var t = ctx.Thresholds;
        var min = Math.Max(1, t.FolderReviewMinArticles);
        var offered = _offered.GetOrCreateValue(ctx);

        var rows = new List<(IndexedFolder Folder, IReadOnlyList<DreamArticle> Members)>();
        foreach (var folder in index.FoldersByPath.Values)
        {
            var members = index.MembersOf(folder.Path);
            if (members.Count >= min) rows.Add((folder, members));
        }

        // Folders where most notes look out of place first, then the larger ones. A trial run with a sample is reordered by
        // the engine's sampler (DreamSampler) whatever order is offered here, so it does not pay for the signal.
        var stray = new Dictionary<string, int>(StringComparer.Ordinal);
        if (ctx.Sample == null)
            foreach (var r in rows) stray[r.Folder.Path] = StrayCount(corpus, index, r.Folder.Path, r.Members, t, ct);
        var ordered = rows.OrderByDescending(r => stray.GetValueOrDefault(r.Folder.Path)).ThenByDescending(r => r.Members.Count)
            .ThenBy(r => r.Folder.Path, StringComparer.Ordinal);

        foreach (var (folder, members) in ordered.ToList())
        {
            ct.ThrowIfCancellationRequested();
            var key = DreamFingerprint.CandidateKey(Name, [folder.Id]);
            lock (offered)
            {
                if (!offered.Add(key)) continue;
            }

            yield return new DreamModelCandidate(
                Task: Name,
                CandidateKey: key,
                ContentMarker: MarkerOf(index, folder, members),
                Tier: tier,
                ArticleIds: [],
                FolderIds: [folder.Id],
                FamilyKey: null,
                Data: new JsonObject
                {
                    ["directNotes"] = members.Count,
                    ["subfolders"] = index.ChildrenOf(folder.Path).Count(),
                });
        }
    }

    /// <summary>How many notes of the folder look out of place by their vectors: enough strong neighbours, almost none of
    /// them in this folder (the thresholds of the misplaced-article rule, DreamThresholds). Only used to order folders.</summary>
    private static int StrayCount(IDreamCorpus corpus, DreamTreeIndex index, string path, IReadOnlyList<DreamArticle> members,
        DreamThresholds t, CancellationToken ct)
    {
        var count = 0;
        foreach (var a in members.Take(MaxStrayProbe))
        {
            ct.ThrowIfCancellationRequested();
            if (!a.HasEmbedding) continue;
            var strong = corpus.NeighboursOf(a.Id, t.StrongNeighbourTopK)
                .Where(n => n.Z >= t.StrongNeighbourMinZ && index.PathById.ContainsKey(n.Id)).ToList();
            if (strong.Count < t.MisplacedMinStrongNeighbours) continue;
            var own = strong.Count(n => index.PathById[n.Id] == path);
            if ((double)own / strong.Count <= t.MisplacedMaxOwnShare) count++;
        }
        return count;
    }

    /// <summary>What the folder looked like when examined: its notes and where they were last changed, and the folders
    /// around it. A new note, an edited one or a new neighbour makes the folder worth another look.</summary>
    private static string MarkerOf(DreamTreeIndex index, IndexedFolder folder, IReadOnlyList<DreamArticle> members)
    {
        var near = Nearby(index, folder.Path);
        var paths = near.Children.Select(f => f.Path).Concat(near.Siblings.Select(f => f.Path))
            .Append(near.Parent?.Path ?? "").OrderBy(p => p, StringComparer.Ordinal);
        var notes = DreamFingerprint.ContentMarker(members.Select(m => (m.Id, m.LamportTs)));
        return DreamFingerprint.ContentMarker($"{folder.Path}|{notes}|{string.Join('\n', paths)}");
    }

    // ── The neighbourhood of a folder ───────────────────────────────────────────────────────────────────────────

    private sealed record Neighbourhood(IReadOnlyList<IndexedFolder> Children, IndexedFolder? Parent, IReadOnlyList<IndexedFolder> Siblings);

    private static Neighbourhood Nearby(DreamTreeIndex index, string path)
    {
        var children = index.ChildrenOf(path).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var parentPath = DreamTree.ParentOf(path);
        IndexedFolder? parent = null;
        if (parentPath != null && parentPath != DreamTree.Root && index.FoldersByPath.TryGetValue(parentPath, out var p)) parent = p;
        var siblings = parentPath == null
            ? []
            : index.ChildrenOf(parentPath).Where(f => f.Path != path).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        return new Neighbourhood(children, parent, siblings);
    }

    // ── Examination ─────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<DreamTaskResult> ExamineAsync(DreamModelCandidate c, IDreamCorpus corpus, IDreamModelClient model,
        DreamRunContext ctx, CancellationToken ct)
    {
        if (c.FolderIds.Count != 1) return DreamTaskResult.Failed("The candidate is not one folder.");
        if (string.IsNullOrWhiteSpace(ctx.ModelId)) return DreamTaskResult.Failed("No analysis model is set for this run.");

        var index = DreamTreeIndex.Of(corpus);
        if (!index.FoldersById.TryGetValue(c.FolderIds[0], out var folder)) return DreamTaskResult.Nothing("unreadable");
        var members = index.MembersOf(folder.Path);
        var t = ctx.Thresholds;

        // The notes to show: the most recently changed. Nothing is built, and nothing leaves, before every text is in hand;
        // a note that may not be read, or whose text is a protected blob, is left out altogether (not even its title goes).
        var shown = new List<ShownNote>();
        foreach (var a in members.OrderByDescending(m => m.UpdatedAt).ThenBy(m => m.Id).Take(Math.Max(1, t.FolderReviewMaxArticles)))
        {
            ct.ThrowIfCancellationRequested();
            var body = await corpus.ReadBodyAsync(a.Id, ct);
            if (body == null || ProtectedContentCodec.IsProtected(body)) continue;
            var snippet = DreamTree.Line(DreamTree.Head(body, SnippetSourceChars), t.FolderReviewSnippetChars);
            shown.Add(new ShownNote($"n{shown.Count + 1}", a, snippet));
        }
        if (shown.Count < MinShownNotes) return DreamTaskResult.Nothing("unreadable");

        var input = BuildInput(corpus, index, folder, members.Count, shown, t, ct);
        var system = DreamPromptResources.Load(PromptVersion);
        var user = BuildUserText(corpus, input, t);

        var outcome = await DreamModelExchange.AskAsync(model, ctx, system, user, MaxOutputTokens, "folder_review", [], c.Tier,
            content =>
            {
                var r = ReadReply(content, input, t, out var error);
                return (r, error);
            }, ct);

        if (outcome.Value == null)
            return DreamTaskResult.Failed(DreamModelExchange.FailureText(outcome.Error), articlesSent: shown.Count);

        var reply = outcome.Value;
        foreach (var reason in reply.Dropped)
            await ctx.Log.WriteAsync(DreamEventKind.Skip, $"folder_review suggestion dropped: {reason}", null, null, ct);

        if (reply.Suggestions.Count == 0)
            return DreamTaskResult.Nothing(reply.Dropped.Count > 0 ? "dropped" : "none", articlesSent: shown.Count);

        var drafts = reply.Suggestions.Select(s => BuildDraft(s, input, c, ctx, outcome.PromptTokens, outcome.CompletionTokens)).ToList();
        var best = reply.Suggestions.OrderByDescending(s => s.Score).First();
        var verdict = string.Join("+", reply.Suggestions.Select(s => s.Type).Distinct().Order(StringComparer.Ordinal));
        return new DreamTaskResult(DreamExaminedVerdict.Advice, drafts, verdict, best.Score, best.Summary, null, shown.Count);
    }

    // ── The input ───────────────────────────────────────────────────────────────────────────────────────────────

    internal sealed record ShownNote(string Label, DreamArticle Article, string Snippet);

    /// <summary>What the model was shown, and so what its answer is checked against.</summary>
    internal sealed class FolderInput
    {
        private readonly Dictionary<string, DreamArticle> _byLabel;

        public FolderInput(DreamTreeIndex index, IndexedFolder folder, int totalNotes, IReadOnlyList<ShownNote> shown,
            IReadOnlyList<(IndexedFolder Folder, int Notes)> children, IReadOnlyList<(IndexedFolder Folder, int Notes)> around,
            IReadOnlyList<(IndexedFolder Folder, int Notes, int Votes)> similar)
        {
            Index = index;
            Folder = folder;
            TotalNotes = totalNotes;
            Shown = shown;
            Children = children;
            Around = around;
            Similar = similar;
            AllPaths = new DreamPathSet(index.FoldersByPath.Keys);
            _byLabel = shown.ToDictionary(s => s.Label, s => s.Article, StringComparer.Ordinal);
            Destinations = new DreamPathSet(children.Select(x => x.Folder.Path).Concat(around.Select(x => x.Folder.Path))
                .Concat(similar.Select(x => x.Folder.Path)));
            Votes = similar.ToDictionary(x => x.Folder.Path, x => x.Votes, StringComparer.Ordinal);
            OtherPathsLower = index.FoldersByPath.Keys.Where(p => p != folder.Path).Select(p => p.ToLowerInvariant())
                .ToHashSet(StringComparer.Ordinal);
        }

        public DreamTreeIndex Index { get; }
        public IndexedFolder Folder { get; }
        public int TotalNotes { get; }
        public IReadOnlyList<ShownNote> Shown { get; }
        public IReadOnlyList<(IndexedFolder Folder, int Notes)> Children { get; }
        public IReadOnlyList<(IndexedFolder Folder, int Notes)> Around { get; }
        public IReadOnlyList<(IndexedFolder Folder, int Notes, int Votes)> Similar { get; }

        /// <summary>The folders a note may be sent to: exactly the ones listed.</summary>
        public DreamPathSet Destinations { get; }

        /// <summary>Every usable folder, for "does this subfolder exist".</summary>
        public DreamPathSet AllPaths { get; }

        public IReadOnlyDictionary<string, int> Votes { get; }

        /// <summary>The lower-cased paths of every other folder: a new name must not collide with one.</summary>
        public IReadOnlySet<string> OtherPathsLower { get; }

        public DreamArticle? ByLabel(string? raw)
        {
            var label = raw?.Trim().ToLowerInvariant();
            return label != null && _byLabel.TryGetValue(label, out var a) ? a : null;
        }
    }

    private static FolderInput BuildInput(IDreamCorpus corpus, DreamTreeIndex index, IndexedFolder folder, int total,
        List<ShownNote> shown, DreamThresholds t, CancellationToken ct)
    {
        int NotesIn(IndexedFolder f) => index.MembersOf(f.Path).Count;
        var near = Nearby(index, folder.Path);

        // Where the notes with a similar subject live: each shown note votes once for every other folder that holds a
        // strong neighbour of it. This is what lets a note be sent somewhere that is not next door.
        var votes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var s in shown)
        {
            ct.ThrowIfCancellationRequested();
            if (!s.Article.HasEmbedding) continue;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in corpus.NeighboursOf(s.Article.Id, t.StrongNeighbourTopK))
            {
                if (n.Z < t.StrongNeighbourMinZ || !index.PathById.TryGetValue(n.Id, out var path)) continue;
                if (path == folder.Path || path == DreamTree.Root || !index.FoldersByPath.ContainsKey(path)) continue;
                if (seen.Add(path)) votes[path] = votes.GetValueOrDefault(path) + 1;
            }
        }

        var cap = Math.Max(1, t.FolderReviewMaxNearbyFolders);
        var around = new List<(IndexedFolder, int)>();
        if (near.Parent != null) around.Add((near.Parent, NotesIn(near.Parent)));
        around.AddRange(near.Siblings.Select(f => (f, NotesIn(f))).OrderByDescending(x => x.Item2)
            .ThenBy(x => x.f.Path, StringComparer.Ordinal));

        var listed = new HashSet<string>(near.Children.Select(f => f.Path).Concat(around.Select(a => a.Item1.Path)), StringComparer.Ordinal);
        var similar = votes.Where(v => v.Value >= MinSimilarVotes && !listed.Contains(v.Key))
            .OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.Ordinal)
            .Take(Math.Min(MaxSimilarFolders, cap))
            .Select(v => (Folder: index.FoldersByPath[v.Key], Notes: NotesIn(index.FoldersByPath[v.Key]), Votes: v.Value)).ToList();

        var room = Math.Max(0, cap - similar.Count);
        var children = near.Children.Select(f => (f, NotesIn(f))).OrderByDescending(x => x.Item2)
            .ThenBy(x => x.f.Path, StringComparer.Ordinal).Take(room).ToList();
        var aroundShown = around.Take(Math.Max(0, room - children.Count)).ToList();

        return new FolderInput(index, folder, total, shown,
            children.Select(x => (x.f, x.Item2)).ToList(), aroundShown.Select(x => (x.Item1, x.Item2)).ToList(), similar);
    }

    private static string BuildUserText(IDreamCorpus corpus, FolderInput input, DreamThresholds t)
    {
        var sb = new StringBuilder();
        sb.Append("Review the folder below and answer with the JSON object described in the instructions.\n\n");
        sb.Append("<folder>\n");
        sb.Append("Path: ").Append(DreamTree.Line(input.Folder.Path, 300)).Append('\n');
        sb.Append("Notes directly in this folder: ").Append(input.TotalNotes.ToString(CultureInfo.InvariantCulture));
        if (input.Shown.Count < input.TotalNotes)
            sb.Append(" (").Append(input.Shown.Count.ToString(CultureInfo.InvariantCulture)).Append(" of them are listed below, the most recently changed first)");
        sb.Append('\n');

        AppendFolders(sb, "Subfolders of this folder (path | notes directly in it):", input.Children.Select(x => (x.Folder.Path, x.Notes, (int?)null)));
        AppendFolders(sb, "The parent folder and the folders next to this one (path | notes directly in it):",
            input.Around.Select(x => (x.Folder.Path, x.Notes, (int?)null)));
        AppendFolders(sb, "Other folders that hold notes on similar subjects (path | notes directly in it | how many of the listed notes have close neighbours there):",
            input.Similar.Select(x => (x.Folder.Path, x.Notes, (int?)x.Votes)));
        sb.Append("</folder>\n");

        foreach (var s in input.Shown)
        {
            sb.Append("\n<note id=\"").Append(s.Label).Append("\">\n");
            sb.Append("Title: ").Append(DreamTree.Line(s.Article.Title, TitleChars)).Append('\n');
            var tags = corpus.TagsOf(s.Article.Id).Take(t.FolderReviewMaxTagsPerArticle).Select(x => DreamTree.Line(x, 40)).ToList();
            if (tags.Count > 0) sb.Append("Tags: ").Append(string.Join(", ", tags)).Append('\n');
            sb.Append("Created: ").Append(s.Article.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("Updated: ").Append(s.Article.UpdatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("Start: ").Append(s.Snippet.Length == 0 ? "(empty)" : s.Snippet).Append('\n');
            sb.Append("</note>\n");
        }
        return sb.ToString();
    }

    private static void AppendFolders(StringBuilder sb, string heading, IEnumerable<(string Path, int Notes, int? Votes)> folders)
    {
        sb.Append(heading).Append('\n');
        var any = false;
        foreach (var (path, notes, votes) in folders)
        {
            any = true;
            sb.Append(DreamTree.Line(path, 300)).Append(" | ").Append(notes.ToString(CultureInfo.InvariantCulture));
            if (votes != null) sb.Append(" | ").Append(votes.Value.ToString(CultureInfo.InvariantCulture));
            sb.Append('\n');
        }
        if (!any) sb.Append("(none)\n");
    }

    // ── The reply ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A suggestion that passed both the schema check and the check against the input.</summary>
    internal sealed record FolderSuggestion(string Type, int Score, string Summary)
    {
        /// <summary>move: the note.</summary>
        public DreamArticle? Article { get; init; }

        /// <summary>move: the destination; subfolder: the subfolder's path.</summary>
        public string? Path { get; init; }

        /// <summary>subfolder and rename: the name.</summary>
        public string? Name { get; init; }

        /// <summary>subfolder: the notes that go in it (at most <see cref="MaxSplitNotes"/>).</summary>
        public IReadOnlyList<DreamArticle> Articles { get; init; } = [];

        /// <summary>subfolder: how many more notes the model named than <see cref="MaxSplitNotes"/> allows.</summary>
        public int Omitted { get; init; }
    }

    /// <summary>The suggestions to turn into advice, and, by reason only (never a value), what was left out.</summary>
    internal sealed record FolderReply(IReadOnlyList<FolderSuggestion> Suggestions, IReadOnlyList<string> Dropped);

    private sealed record RawSuggestion(int Index, string Type, int Score, string Summary, string? One, string? Two, List<string> Many);

    /// <summary>
    /// The schema check of a folder_review reply: the reply read, or null with <paramref name="error"/> naming the field
    /// that failed (never its value). A structurally wrong reply (not an object, no list, an element of an unknown type, a
    /// missing field, a score out of range) is a miss. A well formed suggestion that does not fit the input is dropped
    /// alone: an unknown note label, a destination that was not listed, a name that is not one folder name or that is
    /// taken, a note used twice, a group that is too small or is the whole folder, a second rename.
    /// </summary>
    internal static FolderReply? ReadReply(string? content, FolderInput input, DreamThresholds t, out string? error)
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
        var take = Math.Min(array.Count, Math.Max(0, t.FolderReviewMaxSuggestions));
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
                case "move":
                    one = f.Text("note", 40, required: true);
                    two = f.Text("toPath", 2000, required: true);
                    break;
                case "subfolder":
                    one = f.Text("name", 200, required: true);
                    many = ReadLabels(f, "notes");
                    break;
                case "rename":
                    one = f.Text("newName", 200, required: true);
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
        return CheckAgainstInput(raws, input);
    }

    private static List<string>? ReadLabels(DreamJsonFields f, string name)
    {
        var array = f.Array(name, required: true);
        if (array == null) return null;
        var labels = new List<string>();
        foreach (var item in array)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var s)) labels.Add(s);
            else
            {
                f.Fail($"'{name}' must hold strings.");
                return null;
            }
        }
        return labels;
    }

    private static FolderReply CheckAgainstInput(List<RawSuggestion> raws, FolderInput input)
    {
        var accepted = new List<FolderSuggestion>();
        var dropped = new List<string>();
        var claimed = new HashSet<Guid>();
        var renamed = false;
        var own = input.Folder.Path;

        // The stronger suggestion claims a note first.
        foreach (var r in raws.OrderByDescending(r => r.Score).ThenBy(r => r.Index))
        {
            switch (r.Type)
            {
                case "move":
                {
                    var article = input.ByLabel(r.One);
                    if (article == null) { dropped.Add("unknown note"); break; }
                    if (claimed.Contains(article.Id)) { dropped.Add("note already used"); break; }
                    var to = input.Destinations.Match(r.Two);
                    if (to == null || to == own || input.Index.IsBlocked(to)) { dropped.Add("destination not in the tree that was listed"); break; }
                    claimed.Add(article.Id);
                    accepted.Add(new FolderSuggestion("move", r.Score, r.Summary) { Article = article, Path = to });
                    break;
                }
                case "subfolder":
                {
                    var name = DreamTree.CleanSegment(r.One);
                    if (name == null) { dropped.Add("bad folder name"); break; }
                    var articles = r.Many.Select(l => input.ByLabel(l)).OfType<DreamArticle>().DistinctBy(a => a.Id)
                        .Where(a => !claimed.Contains(a.Id)).ToList();
                    if (articles.Count < MinSubfolderNotes) { dropped.Add("too few notes for a subfolder"); break; }
                    if (articles.Count >= input.TotalNotes) { dropped.Add("every note of the folder"); break; }
                    // The listing leaves out what is hidden (an excluded subtree, a remote subscription) and what is a system
                    // folder, so it cannot say that the new path is already there; this check can. Moving notes into a folder
                    // the person fenced off is refused here, and Apply asks again against the live tree.
                    var wanted = DreamTree.Join(own, name);
                    if (input.Index.IsBlocked(wanted)) { dropped.Add("destination not allowed"); break; }
                    var path = input.AllPaths.Match(wanted) ?? wanted;
                    if (input.Index.IsBlocked(path)) { dropped.Add("destination not allowed"); break; }
                    var omitted = Math.Max(0, articles.Count - MaxSplitNotes);
                    if (omitted > 0) articles = articles.Take(MaxSplitNotes).ToList();
                    foreach (var a in articles) claimed.Add(a.Id);
                    accepted.Add(new FolderSuggestion("subfolder", r.Score, r.Summary)
                        { Path = path, Name = DreamTree.NameOf(path), Articles = articles, Omitted = omitted });
                    break;
                }
                default:
                {
                    if (own == DreamTree.Root) { dropped.Add("the top level cannot be renamed"); break; }
                    if (renamed) { dropped.Add("second rename"); break; }
                    var name = DreamTree.CleanSegment(r.One);
                    if (name == null) { dropped.Add("bad folder name"); break; }
                    if (name == DreamTree.NameOf(own)) { dropped.Add("same name"); break; }
                    var newPath = DreamTree.Join(DreamTree.ParentOf(own)!, name);
                    if (input.OtherPathsLower.Contains(newPath.ToLowerInvariant()) || input.Index.IsBlocked(newPath)) { dropped.Add("name already taken"); break; }
                    renamed = true;
                    accepted.Add(new FolderSuggestion("rename", Math.Min(r.Score, RenameScoreCap), r.Summary) { Name = name });
                    break;
                }
            }
        }
        return new FolderReply(accepted, dropped);
    }

    // ── The advice ──────────────────────────────────────────────────────────────────────────────────────────────

    private static DreamAdviceDraft BuildDraft(FolderSuggestion s, FolderInput input, DreamModelCandidate c, DreamRunContext ctx,
        long promptTokens, long completionTokens)
    {
        var t = ctx.Thresholds;
        var folder = input.Folder;
        var folderLabel = DreamJsonReader.CleanText(folder.Path, 120);
        var context = new DreamEvidence(DreamEvidenceType.Note,
            $"Found while reviewing the folder {folderLabel} ({input.TotalNotes.ToString(CultureInfo.InvariantCulture)} notes of its own).");
        var model = new DreamAdviceModelInfo(ctx.ModelId!, ctx.ModelLabel ?? ctx.ModelId!, DreamNames.FolderPromptVersion,
            promptTokens, completionTokens);

        DreamAdviceDraft Make(DreamAdviceKind kind, DreamApplyMode mode, string title, DreamAction action,
            IReadOnlyList<DreamEvidence> evidence, IReadOnlyList<DreamAdviceObject> objects, IReadOnlyList<DreamPrecondition> pre) => new()
        {
            Kind = kind,
            Source = DreamAdviceSource.Model,
            ApplyMode = mode,
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

        switch (s.Type)
        {
            case "move":
            {
                var article = s.Article!;
                var to = s.Path!;
                var evidence = new List<DreamEvidence> { context };
                if (input.Votes.TryGetValue(to, out var votes))
                    evidence.Add(new DreamEvidence(DreamEvidenceType.Neighbours,
                        $"{votes.ToString(CultureInfo.InvariantCulture)} of the {input.Shown.Count.ToString(CultureInfo.InvariantCulture)} notes listed have close neighbours in {DreamJsonReader.CleanText(to, 120)}.",
                        votes));
                return Make(DreamAdviceKind.MoveArticle, DreamApplyMode.Code,
                    $"Move \"{DreamJsonReader.CleanText(article.Title, 70)}\" to {DreamJsonReader.CleanText(to, 100)}",
                    DreamAction.MoveArticle(article.Id, to), evidence,
                    [DreamAdviceObject.ForArticle(article, DreamObjectRole.Subject), DreamAdviceObject.ForFolder(FolderIdOf(input, to), to, DreamObjectRole.Target)],
                    [DreamPrecondition.ArticleUnchanged(article.Id, article.LamportTs), DreamPrecondition.FolderExists(to)]);
            }
            case "subfolder":
            {
                var path = s.Path!;
                var exists = input.Index.FoldersByPath.ContainsKey(path);
                var title = exists
                    ? $"Move {s.Articles.Count} notes of {DreamJsonReader.CleanText(folder.Path, 70)} into {DreamJsonReader.CleanText(path, 70)}"
                    : $"Group {s.Articles.Count} notes of {DreamJsonReader.CleanText(folder.Path, 70)} into a new subfolder \"{DreamJsonReader.CleanText(s.Name!, 70)}\"";
                var objects = new List<DreamAdviceObject>
                {
                    DreamAdviceObject.ForFolder(folder.Id, folder.Path, DreamObjectRole.Subject),
                    DreamAdviceObject.ForFolder(FolderIdOf(input, path), path, DreamObjectRole.Target),
                };
                // Every note the action moves is an object of the advice, so the card and the confirmation can list them all.
                objects.AddRange(s.Articles.Select(a => DreamAdviceObject.ForArticle(a, DreamObjectRole.Other)));
                var pre = new List<DreamPrecondition> { DreamPrecondition.FolderExists(folder.Path) };
                pre.AddRange(s.Articles.Select(a => DreamPrecondition.ArticleUnchanged(a.Id, a.LamportTs)));
                var evidence = new List<DreamEvidence>
                {
                    context,
                    new(DreamEvidenceType.Count,
                        $"{s.Articles.Count.ToString(CultureInfo.InvariantCulture)} of the {input.TotalNotes.ToString(CultureInfo.InvariantCulture)} notes in this folder would move.", s.Articles.Count),
                };
                if (s.Omitted > 0)
                    evidence.Add(new DreamEvidence(DreamEvidenceType.Note,
                        $"The model grouped {s.Omitted.ToString(CultureInfo.InvariantCulture)} more notes; only the first {MaxSplitNotes.ToString(CultureInfo.InvariantCulture)} are proposed, so that every one can be checked."));
                return Make(DreamAdviceKind.SplitFolder, DreamApplyMode.Code, title,
                    DreamAction.MoveArticles(path, s.Articles.Select(a => a.Id).ToList()),
                    evidence, objects, pre);
            }
            default:
                return Make(DreamAdviceKind.RenameFolder, DreamApplyMode.Prompt,
                    $"Rename folder {DreamJsonReader.CleanText(folder.Path, 100)} to \"{DreamJsonReader.CleanText(s.Name!, 70)}\"",
                    DreamAction.RenameFolder(folder.Path, s.Name!),
                    [context, new DreamEvidence(DreamEvidenceType.Note,
                        "A rename changes the folder's path: tools, scripts and agents that write there by path will need the new one.")],
                    [DreamAdviceObject.ForFolder(folder.Id, folder.Path, DreamObjectRole.Subject)],
                    [DreamPrecondition.FolderExists(folder.Path)]);
        }
    }

    private static Guid? FolderIdOf(FolderInput input, string path) =>
        input.Index.FoldersByPath.TryGetValue(path, out var f) ? f.Id : null;
}
