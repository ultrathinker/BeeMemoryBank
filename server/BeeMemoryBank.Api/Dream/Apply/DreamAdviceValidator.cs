using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using Dapper;

namespace BeeMemoryBank.Api.Dream;

/// <summary>What a check found about one advice. <see cref="Problem"/> is null while the advice still stands.</summary>
public sealed record DreamCheckResult
{
    public static readonly DreamCheckResult Holds = new();

    /// <summary>Why the advice no longer stands, in plain English without any name (it goes to the plaintext status_note).</summary>
    public string? Problem { get; init; }

    /// <summary>An article_unchanged no longer holds (the note changed or went away): an in_chat advice then counts as
    /// done after the chat (plan 3.4).</summary>
    public bool ArticleChanged { get; init; }

    /// <summary>The items of a many-item action (the notes of move_articles, the tags of merge_tags and delete_tags) that
    /// no longer hold and are left alone: article ids in "D" form, tag names normalized (DreamFingerprint.NormalizeName).</summary>
    public IReadOnlySet<string> SkippedItems { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public bool Ok => Problem is null;
}

/// <summary>
/// The precondition checks of plan 3.4. The cheap one runs over a page of a list with one metadata read for the whole
/// page (articles, folders, tags) and needs no key; the strict one adds what the action itself needs right before it runs
/// (Apply), or before a prompt or a hand-off to the chat. An advice whose preconditions no longer hold is expired, or,
/// when it was handed to the chat and a note it names has changed, counted as done after the chat.
///
/// <para>A many-item action stands while one of its items does: the notes of a split that changed are left where they
/// are, a tag that has been given to a note since is not deleted, a merged source tag that is already gone is skipped.</para>
///
/// <para>A move is checked against the live tree and the live settings, not the advice's snapshot (the request of package
/// M2): the target must be a canonical path, not a system folder or inside one, and not at or below a root Dream hides now
/// (DreamCorpusBuilder.HiddenRootsAsync, the same roots the corpus uses); and no note it would move may sit in such a place
/// now, whatever else is true of that note. Any of these ends the whole advice before anything is moved.</para>
/// </summary>
public sealed class DreamAdviceValidator(IDbConnectionFactory mainDb, DreamStore store, DreamEngineOptions? options = null)
{
    private const int SweepPage = 200;

    internal static class Reasons
    {
        public const string ArticleChanged = "A note it is about has changed since.";
        public const string ArticleGone = "A note it is about is gone or in the trash.";
        public const string ArticleMoved = "A note it is about is in another folder now.";
        public const string ArticleProtected = "A note it is about is protected by a second password or is a remote copy now.";
        public const string FolderGone = "A folder it is about is gone or has been renamed.";
        public const string FolderNotEmpty = "The folder is not empty any more.";
        public const string TagGone = "A tag it is about is gone.";
        public const string TagUsed = "A tag it is about is given to a note now.";
        public const string NothingLeft = "Nothing it is about is left to change.";
        public const string TargetInvalid = "The folder it would move notes to is not a usable path.";
        public const string TargetSystem = "The folder it would move notes to is a system folder or inside one.";
        public const string TargetHidden = "The folder it would move notes to is one Dream leaves alone now (excluded, or a remote copy).";
        public const string SourceHidden = "A note it would move is now in a folder Dream leaves alone, or in a system folder.";
    }

    private readonly DreamEngineOptions _options = options ?? DreamEngineOptions.Default;

    private DateTime Now => _options.Clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// The cheap check of a page of advice as a list serves it: the open ones whose preconditions no longer hold are
    /// expired (or counted as done after the chat) and come back in their new state. Advice being applied is left alone.
    /// </summary>
    public async Task<IReadOnlyList<DreamAdviceRecord>> CheckListAsync(IReadOnlyList<DreamAdviceRecord> page)
    {
        var open = page.Where(IsOpen).ToList();
        if (open.Count == 0) return page;

        var facts = await LoadFactsAsync(open, strict: false);
        var settled = new Dictionary<Guid, DreamAdviceRecord>();
        foreach (var advice in open)
        {
            var result = Evaluate(advice, facts, strict: false);
            if (!result.Ok) settled[advice.Id] = await SettleAsync(advice, result, null);
        }
        return settled.Count == 0 ? page : page.Select(a => settled.GetValueOrDefault(a.Id, a)).ToList();
    }

    /// <summary>The cheap check of one advice (GET /advice/{id}).</summary>
    public async Task<DreamAdviceRecord> CheckOneAsync(DreamAdviceRecord advice) => (await CheckListAsync([advice]))[0];

    /// <summary>The strict check right before Apply, a prompt or a hand-off: the preconditions and what the action itself
    /// needs (the note is still active and not protected, the folder is still empty and still the same folder, the target
    /// tag still exists). Changes nothing.</summary>
    public async Task<DreamCheckResult> CheckStrictAsync(DreamAdviceRecord advice)
    {
        var facts = await LoadFactsAsync([advice], strict: true);
        return Evaluate(advice, facts, strict: true);
    }

    /// <summary>
    /// Records what a failed check means for the advice: in_chat with a changed note becomes applied with the note
    /// changed_after_chat, anything else open becomes expired with the reason. Returns the advice as it is now.
    /// </summary>
    public async Task<DreamAdviceRecord> SettleAsync(DreamAdviceRecord advice, DreamCheckResult result, int? userId)
    {
        if (result.Ok) return advice;

        // Open advice only: an Apply ends its own attempt through its token (DreamStore.TryEndApplyingAsync).
        if (advice.Status is not (DreamAdviceStatus.New or DreamAdviceStatus.InChat)) return advice;

        // Conditional on the status the check was made in: a list's check must never end an Apply that began meanwhile.
        var now = Now;
        if (advice.Status == DreamAdviceStatus.InChat && result.ArticleChanged)
            await store.TrySetAdviceStatusAsync(advice.Id, [DreamAdviceStatus.InChat], DreamAdviceStatus.Applied, DreamStatusNotes.ChangedAfterChat, userId, now);
        else
            await store.TrySetAdviceStatusAsync(advice.Id, [advice.Status], DreamAdviceStatus.Expired, result.Problem, userId, now);
        return await store.GetAdviceAsync(advice.Id) ?? advice;
    }

    /// <summary>
    /// The part of the sweep that is about the notes: every open advice whose preconditions no longer hold is settled. Run
    /// by the scheduler's step and at the start of a run, so that an advice that no longer stands never hides a fresh one
    /// as its "repeat". Skipped while the chat key cannot be had. Returns how many advice it settled.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var settled = 0;
        var after = "";
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<DreamAdviceRecord> page;
            string? last;
            try
            {
                (page, last) = await store.ListOpenAdvicePageAsync(after, SweepPage);
            }
            catch (Exception ex) when (ex is SessionLockedException or System.Security.Cryptography.CryptographicException)
            {
                // Locked, or the brief window of a key rotation (ChatDataProtector.AcquireAsync): the next step tries again.
                return settled;
            }

            if (page.Count > 0)
            {
                var facts = await LoadFactsAsync(page, strict: false);
                foreach (var advice in page)
                {
                    var result = Evaluate(advice, facts, strict: false);
                    if (result.Ok) continue;
                    await SettleAsync(advice, result, null);
                    settled++;
                }
            }
            if (last is null) return settled;
            after = last;
        }
    }

    private static bool IsOpen(DreamAdviceRecord a) => a.Status is DreamAdviceStatus.New or DreamAdviceStatus.InChat;

    // ── The decision ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Whether the advice still stands against <paramref name="facts"/>. Pure: the tests drive it directly.</summary>
    internal static DreamCheckResult Evaluate(DreamAdviceRecord advice, DreamFacts facts, bool strict)
    {
        var payload = advice.Payload;
        var action = payload.Action;
        var items = ItemsOf(action);
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        var skippedForChange = false;
        string? problem = null;
        var changed = false;

        void Fail(string? item, string reason, bool articleChanged)
        {
            if (item is not null && items.Contains(item))
            {
                skipped.Add(item);
                skippedForChange |= articleChanged;
                return;
            }
            problem ??= reason;
            changed |= articleChanged;
        }

        foreach (var p in payload.Preconditions)
        {
            switch (p.Type)
            {
                case DreamPreconditionType.ArticleUnchanged when p.Id is { } id:
                    if (facts.ActiveArticle(id) is not { } unchanged) Fail(Item(id), Reasons.ArticleGone, true);
                    else if (unchanged.LamportTs != p.LamportTs) Fail(Item(id), Reasons.ArticleChanged, true);
                    break;
                case DreamPreconditionType.ArticleExists when p.Id is { } id:
                    if (facts.ActiveArticle(id) is null) Fail(Item(id), Reasons.ArticleGone, false);
                    break;
                case DreamPreconditionType.ArticleInPath when p.Id is { } id:
                    if (facts.ActiveArticle(id) is not { } placed) Fail(Item(id), Reasons.ArticleGone, false);
                    else if (!string.Equals(placed.TreePath, p.Path, StringComparison.Ordinal)) Fail(Item(id), Reasons.ArticleMoved, false);
                    break;
                case DreamPreconditionType.FolderExists when p.Path is { } path:
                    if (facts.Folder(path) is null) Fail(null, Reasons.FolderGone, false);
                    break;
                case DreamPreconditionType.FolderEmpty when p.Path is { } path:
                    if (facts.Folder(path) is not { } folder) Fail(null, Reasons.FolderGone, false);
                    else if (!folder.IsEmpty) Fail(null, Reasons.FolderNotEmpty, false);
                    break;
                case DreamPreconditionType.TagExists when p.Name is { } name:
                    if (facts.Tag(name) is null) Fail(TagItem(name), Reasons.TagGone, false);
                    break;
                case DreamPreconditionType.TagUnused when p.Name is { } name:
                    if (facts.Tag(name) is not { } tag) Fail(TagItem(name), Reasons.TagGone, false);
                    else if (tag.ArticleCount > 0) Fail(TagItem(name), Reasons.TagUsed, false);
                    break;
            }
        }

        if (strict) StrictChecks(action, facts, Fail);

        if (problem is null && items.Count > 0 && skipped.Count >= items.Count)
        {
            problem = Reasons.NothingLeft;
            changed |= skippedForChange;
        }
        return new DreamCheckResult { Problem = problem, ArticleChanged = changed, SkippedItems = skipped };
    }

    /// <summary>What the action itself needs, whatever preconditions its maker wrote down.</summary>
    private static void StrictChecks(DreamAction action, DreamFacts facts, Action<string?, string, bool> fail)
    {
        void Article(Guid? id, bool item)
        {
            if (id is not { } articleId) return;
            if (facts.ActiveArticle(articleId) is not { } a) fail(item ? Item(articleId) : null, Reasons.ArticleGone, false);
            else if (a.Protected || a.Remote) fail(item ? Item(articleId) : null, Reasons.ArticleProtected, false);
        }

        switch (action.Type)
        {
            case DreamActionType.TrashArticle:
                Article(action.ArticleId, false);
                Article(action.KeepId, false);
                break;
            case DreamActionType.MoveArticle:
                Article(action.ArticleId, false);
                MoveChecks(action, facts, fail);
                break;
            case DreamActionType.MoveArticles:
                foreach (var id in action.ArticleIds ?? []) Article(id, true);
                MoveChecks(action, facts, fail);
                break;
            case DreamActionType.MergeTags:
                if (action.Target is null || facts.Tag(action.Target) is null) fail(null, Reasons.TagGone, false);
                foreach (var source in action.Sources ?? [])
                    if (facts.Tag(source) is null) fail(TagItem(source), Reasons.TagGone, false);
                break;
            case DreamActionType.DeleteTags:
                foreach (var name in action.Names ?? [])
                {
                    if (facts.Tag(name) is not { } tag) fail(TagItem(name), Reasons.TagGone, false);
                    else if (tag.ArticleCount > 0) fail(TagItem(name), Reasons.TagUsed, false);
                }
                break;
            case DreamActionType.DeleteFolder:
                if (action.Path is null || facts.Folder(action.Path) is not { } folder || folder.Id != action.FolderId) fail(null, Reasons.FolderGone, false);
                else if (!folder.IsEmpty) fail(null, Reasons.FolderNotEmpty, false);
                break;
        }
    }

    /// <summary>
    /// The target of a move and the current folder of every note it would move, against the live tree and settings. Every
    /// failure here is the whole advice's, never an item's: a stale advice must not move a note out of a folder Dream was
    /// told to leave alone, nor into one, nor half of its notes.
    /// </summary>
    private static void MoveChecks(DreamAction action, DreamFacts facts, Action<string?, string, bool> fail)
    {
        // Canonicalize reads an empty path as the root: a move without a target is refused, not sent to "/".
        if (string.IsNullOrWhiteSpace(action.ToPath) || TreePathCanonicalizer.TryCanonicalize(action.ToPath) is not { } target)
            fail(null, Reasons.TargetInvalid, false);
        else if (facts.IsSystemOrUnder(target))
            fail(null, Reasons.TargetSystem, false);
        else if (DreamCorpusBuilder.IsUnderAny(target, facts.HiddenRoots))
            fail(null, Reasons.TargetHidden, false);

        foreach (var id in action.ArticleIdsNamed())
        {
            if (!facts.Articles.TryGetValue(id, out var note)) continue;
            if (facts.IsSystemOrUnder(note.TreePath) || DreamCorpusBuilder.IsUnderAny(note.TreePath, facts.HiddenRoots))
            {
                fail(null, Reasons.SourceHidden, false);
                break;
            }
        }
    }

    /// <summary>The items of a many-item action: one that fails is skipped, not the whole advice.</summary>
    private static HashSet<string> ItemsOf(DreamAction action) => action.Type switch
    {
        DreamActionType.MoveArticles => (action.ArticleIds ?? []).Select(Item).ToHashSet(StringComparer.Ordinal),
        DreamActionType.MergeTags => (action.Sources ?? []).Select(TagItem).ToHashSet(StringComparer.Ordinal),
        DreamActionType.DeleteTags => (action.Names ?? []).Select(TagItem).ToHashSet(StringComparer.Ordinal),
        _ => new HashSet<string>(StringComparer.Ordinal),
    };

    internal static string Item(Guid id) => id.ToString("D");

    internal static string TagItem(string name) => DreamFingerprint.NormalizeName(name);

    // ── The facts ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One read of the main database for everything the advice name: the articles, the folders (with what is
    /// under them where emptiness matters) and the tags with their active articles. Plain metadata, no key.</summary>
    private async Task<DreamFacts> LoadFactsAsync(IReadOnlyList<DreamAdviceRecord> advice, bool strict)
    {
        var articleIds = new HashSet<Guid>();
        var folderPaths = new HashSet<string>(StringComparer.Ordinal);
        var emptyPaths = new HashSet<string>(StringComparer.Ordinal);
        var tagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in advice)
        {
            foreach (var p in a.Payload.Preconditions)
            {
                if (p.Id is { } id) articleIds.Add(id);
                if (p.Type is DreamPreconditionType.FolderExists or DreamPreconditionType.FolderEmpty && p.Path is { } path) folderPaths.Add(path);
                if (p.Type == DreamPreconditionType.FolderEmpty && p.Path is { } emptyPath) emptyPaths.Add(emptyPath);
                if (p.Name is { } name) tagNames.Add(name);
            }
            if (!strict) continue;
            var action = a.Payload.Action;
            foreach (var id in action.ArticleIdsNamed()) articleIds.Add(id);
            if (action.Type == DreamActionType.DeleteFolder && action.Path is { } folder)
            {
                folderPaths.Add(folder);
                emptyPaths.Add(folder);
            }
            foreach (var name in (action.Sources ?? []).Concat(action.Names ?? []).Concat(action.Target is { } t ? [t] : Array.Empty<string>()))
                tagNames.Add(name);
        }

        var facts = new DreamFacts();
        using var conn = mainDb.CreateConnection();

        if (strict && advice.Any(a => a.Payload.Action.Type is DreamActionType.MoveArticle or DreamActionType.MoveArticles))
        {
            var settings = await store.GetSettingsAsync();
            facts.HiddenRoots = await DreamCorpusBuilder.HiddenRootsAsync(conn, settings.ExcludedFolderIds);
            facts.SystemPaths = (await conn.QueryAsync<string>("SELECT path FROM tbl_folder WHERE status = 'A' AND is_system != 0")).ToList();
        }

        foreach (var batch in articleIds.Chunk(250))
        {
            // A Guid is written to the main database in the case its writer bound it in (DreamCorpusBuilder.FindExistingArticlesAsync).
            var spellings = batch.SelectMany(id => new[] { id.ToString("D"), id.ToString("D").ToUpperInvariant() }).ToArray();
            var rows = await conn.QueryAsync<ArticleRow>(
                """
                SELECT a.id AS Id, a.lamport_ts AS LamportTs, a.status AS Status, COALESCE(f.path, '/') AS TreePath,
                       a.protected AS IsProtected, a.remote_subscription_id AS RemoteSubscriptionId
                FROM tbl_article a LEFT JOIN tbl_folder f ON f.id = a.folder_id
                WHERE a.id IN @spellings
                """, new { spellings });
            foreach (var r in rows)
                facts.Articles[Guid.Parse(r.Id)] = new DreamArticleFact(r.LamportTs, r.Status == "A", r.TreePath, r.IsProtected != 0, r.RemoteSubscriptionId != null);
        }

        if (folderPaths.Count > 0)
        {
            var rows = await conn.QueryAsync<(string Id, string Path)>(
                "SELECT id, path FROM tbl_folder WHERE status = 'A' AND path IN @paths", new { paths = folderPaths.ToArray() });
            foreach (var (id, path) in rows)
            {
                var (articles, subfolders) = emptyPaths.Contains(path) ? await CountUnderAsync(conn, path) : (0, 0);
                facts.Folders[path] = new DreamFolderFact(Guid.Parse(id), articles == 0 && subfolders == 0);
            }
        }

        if (tagNames.Count > 0)
        {
            var rows = await conn.QueryAsync<(string Name, long Count)>(
                """
                SELECT ct.name, COUNT(a.id)
                FROM tbl_concept_tag ct
                LEFT JOIN tbl_article_concept_tag act ON act.concept_tag_id = ct.id
                LEFT JOIN tbl_article a ON a.id = act.article_id AND a.status = 'A'
                WHERE ct.name COLLATE NOCASE IN @names
                GROUP BY ct.id
                """, new { names = tagNames.ToArray() });
            foreach (var (name, count) in rows)
            {
                var key = DreamFingerprint.NormalizeName(name);
                var known = facts.Tags.GetValueOrDefault(key);
                facts.Tags[key] = new DreamTagFact((known?.ArticleCount ?? 0) + (int)count);
            }
        }

        return facts;
    }

    // ── The last look before a delete ───────────────────────────────────────────────────────────────────────────────

    // Neither the tag service nor the folder service takes a lock or a transaction a caller could join, so Apply asks
    // again the moment before it calls them, one tag or folder at a time: the strict check of a fifty-tag advice is
    // otherwise as old as the deletes before the last one.

    /// <summary>How many active notes carry the tag right now; null when the tag does not exist any more.</summary>
    public async Task<int?> TagUsesNowAsync(string name)
    {
        using var conn = mainDb.CreateConnection();
        var rows = (await conn.QueryAsync<long>(
            """
            SELECT COUNT(a.id)
            FROM tbl_concept_tag ct
            LEFT JOIN tbl_article_concept_tag act ON act.concept_tag_id = ct.id
            LEFT JOIN tbl_article a ON a.id = act.article_id AND a.status = 'A'
            WHERE ct.name = @name COLLATE NOCASE
            GROUP BY ct.id
            """, new { name })).ToList();
        return rows.Count == 0 ? null : (int)rows.Sum();
    }

    /// <summary>Null while the folder at <paramref name="path"/> is still the folder <paramref name="folderId"/> and still
    /// empty (no active note anywhere below it, no active subfolder); otherwise why it may not be deleted now.</summary>
    public async Task<string?> FolderProblemNowAsync(Guid folderId, string path)
    {
        using var conn = mainDb.CreateConnection();
        var id = await conn.QuerySingleOrDefaultAsync<string>("SELECT id FROM tbl_folder WHERE status = 'A' AND path = @path", new { path });
        if (id is null || !Guid.TryParse(id, out var current) || current != folderId) return Reasons.FolderGone;
        var (articles, subfolders) = await CountUnderAsync(conn, path);
        return articles == 0 && subfolders == 0 ? null : Reasons.FolderNotEmpty;
    }

    /// <summary>Active articles anywhere under the folder (Protected, remote and excluded ones too: a folder holding
    /// only those is not empty) and active folders below it.</summary>
    private static async Task<(long Articles, long Subfolders)> CountUnderAsync(System.Data.IDbConnection conn, string path) =>
        await conn.QuerySingleAsync<(long, long)>(
            """
            SELECT (SELECT COUNT(*) FROM tbl_article a LEFT JOIN tbl_folder f ON f.id = a.folder_id
                    WHERE a.status = 'A'
                      AND (COALESCE(f.path, a.tree_path) = @path OR substr(COALESCE(f.path, a.tree_path), 1, length(@path) + 1) = @path || '/')),
                   (SELECT COUNT(*) FROM tbl_folder WHERE status = 'A' AND substr(path, 1, length(@path) + 1) = @path || '/')
            """, new { path });

    private sealed class ArticleRow
    {
        public string Id { get; set; } = "";
        public long LamportTs { get; set; }
        public string Status { get; set; } = "";
        public string TreePath { get; set; } = "/";
        public long IsProtected { get; set; }
        public string? RemoteSubscriptionId { get; set; }
    }
}

/// <summary>The state of what some advice names, as the main database has it now.</summary>
internal sealed class DreamFacts
{
    public Dictionary<Guid, DreamArticleFact> Articles { get; } = [];
    /// <summary>Active folders by their exact path.</summary>
    public Dictionary<string, DreamFolderFact> Folders { get; } = new(StringComparer.Ordinal);
    /// <summary>Tags by normalized name (DreamFingerprint.NormalizeName).</summary>
    public Dictionary<string, DreamTagFact> Tags { get; } = new(StringComparer.Ordinal);

    public DreamArticleFact? ActiveArticle(Guid id) => Articles.TryGetValue(id, out var a) && a.Active ? a : null;

    public DreamFolderFact? Folder(string path) => Folders.GetValueOrDefault(path);

    public DreamTagFact? Tag(string name) => Tags.GetValueOrDefault(DreamFingerprint.NormalizeName(name));

    /// <summary>The roots Dream hides now (read by the strict check of a move only).</summary>
    public IReadOnlyList<string> HiddenRoots { get; set; } = [];

    /// <summary>The paths of the active folders marked system (read by the strict check of a move only).</summary>
    public IReadOnlyList<string> SystemPaths { get; set; } = [];

    /// <summary>True when the path, or a folder above it, is a reserved system root or a folder marked system.</summary>
    public bool IsSystemOrUnder(string path) =>
        DreamCorpusBuilder.SelfAndAncestors(path).Any(SystemFolders.IsReservedSystemPath) || DreamCorpusBuilder.IsUnderAny(path, SystemPaths);
}

internal sealed record DreamArticleFact(long LamportTs, bool Active, string TreePath, bool Protected, bool Remote);

internal sealed record DreamFolderFact(Guid Id, bool IsEmpty);

internal sealed record DreamTagFact(int ArticleCount);
