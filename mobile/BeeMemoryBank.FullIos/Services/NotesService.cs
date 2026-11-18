using BeeMemoryBank.Core;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.FullIos.Services;

/// <summary>One row of a folder's list: a sub-folder or a note.</summary>
public sealed record NoteListItem(string Name, bool IsFolder, string Path, Guid? NoteId, DateTime? UpdatedAt, bool IsProtected)
{
    public bool IsNote => !IsFolder;

    public string Glyph => IsFolder ? "\U0001F4C1" : IsProtected ? "\U0001F512" : "\U0001F4C4";

    public string Detail => IsFolder ? "" : UpdatedAt is { } at ? at.ToLocalTime().ToString("d MMM yyyy, HH:mm") : "";
}

/// <summary>A note as the note page shows it. <see cref="Body"/> is the text, or the sealed blob of a note protected by its own passphrase.</summary>
public sealed record NoteView(Guid Id, string Title, string Path, DateTime UpdatedAt, bool IsProtected, string? ProtectionHint, string Body);

/// <summary>A note in the trash.</summary>
public sealed record TrashItem(Guid Id, string Title, string Path, DateTime DeletedAt);

/// <summary>A concept tag as the tags page lists it: its name and how many articles carry it.</summary>
public sealed record TagItem(string Name, int Count);

/// <summary>An article carrying a tag, as the tag's articles page lists it.</summary>
public sealed record TagArticleItem(Guid Id, string Title, string Path);

/// <summary>A comment as the note page shows it: the decrypted text, or null when it cannot be read; locked while the article's passphrase is not given.</summary>
public sealed record CommentView(int Id, DateTime CreatedAt, string? Text, bool Locked);

/// <summary>A related article as the note page lists it (shared tags / close meaning).</summary>
public sealed record RelatedItem(Guid Id, string Title, string Path);

/// <summary>
/// The protection the editor wants after a save. <see cref="Enable"/> true seals the note under
/// <see cref="Passphrase"/> (with an optional <see cref="Hint"/>); false takes any protection off.
/// The page validates the passphrase (length, repeat) before handing it here.
/// </summary>
public sealed record ProtectSpec(bool Enable, string? Passphrase, string? Hint);

/// <summary>
/// What the note pages do, as calls of the product's own services - the same ones, in the same way, as the Android app's pages
/// (ArticleService, FolderService, SearchService, RestoreService, HardDeleteService's listing): a folder's list, a note, saving, deleting
/// to the trash and restoring from it, the two searches. Every write goes through ArticleService/FolderService, so it is versioned,
/// logged as a signed event and pushed by the next sync like on every node. Platform-free: the tests run it on a real vault.
/// </summary>
public sealed class NotesService(IServiceProvider services)
{
    /// <summary>Sub-folders first, then the notes directly in <paramref name="path"/>, each sorted the way the other apps sort them.</summary>
    public async Task<List<NoteListItem>> ListFolderAsync(string path)
    {
        path = NormalizePath(path);
        using var scope = services.CreateScope();
        var folders = await scope.ServiceProvider.GetRequiredService<IFolderRepository>().GetChildrenAsync(path == "/" ? null : path);
        var notes = await scope.ServiceProvider.GetRequiredService<IArticleRepository>().ListAsync(path);

        var items = new List<NoteListItem>();
        items.AddRange(folders
            .Where(f => !f.Name.StartsWith('.'))
            .OrderBy(f => f.Name, UnderscoreFirstComparer.Instance)
            .Select(f => new NoteListItem(f.Name, true, f.Path, null, null, false)));
        items.AddRange(notes
            .Where(a => NormalizePath(a.TreePath) == path)
            .OrderBy(a => a.Title, UnderscoreFirstComparer.Instance)
            .Select(a => new NoteListItem(a.Title, false, NormalizePath(a.TreePath), a.Id, a.UpdatedAt, a.Protected)));
        return items;
    }

    /// <summary>The note, decrypted (the vault must be open); null when it is gone (deleted here or by a sync).</summary>
    public async Task<NoteView?> GetAsync(Guid id)
    {
        using var scope = services.CreateScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var article = await articles.GetMetadataAsync(id);
        if (article is null) return null;
        var body = await articles.GetContentAsync(id);
        var isProtected = article.Protected || ProtectedContentCodec.IsProtected(body);
        return new NoteView(id, article.Title, NormalizePath(article.TreePath), article.UpdatedAt, isProtected, article.ProtectionHint, body);
    }

    /// <summary>The text of a note protected by its own passphrase; null for a wrong passphrase.</summary>
    public static string? OpenProtected(string sealedBody, string passphrase)
    {
        try
        {
            return ProtectedContentCodec.Unwrap(sealedBody, passphrase);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Creates a note (<paramref name="id"/> null) or saves an existing one; returns its id. <paramref name="passphrase"/> is the note's
    /// own passphrase when it is a protected note being edited: the text is sealed again under it.
    /// </summary>
    public async Task<Guid> SaveAsync(Guid? id, string title, string path, string body, string? passphrase = null, List<string>? tags = null, ProtectSpec? protect = null)
    {
        title = (title ?? "").Trim();
        if (title.Length == 0) throw new ArgumentException("Give the note a title.");
        path = NormalizePath(path);
        body ??= "";

        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var articles = sp.GetRequiredService<ArticleService>();
        var comments = sp.GetRequiredService<CommentService>();

        if (id is null)
        {
            // A new note is sealed before its first CREATE event, so only ciphertext ever ships.
            if (protect is { Enable: true, Passphrase: { } newPass })
                return (await articles.CreateAsync(title, path, tags ?? [], ProtectedContentCodec.Wrap(body, newPass), protect.Hint)).Id;
            return (await articles.CreateAsync(title, path, tags ?? [], body)).Id;
        }

        var value = id.Value;
        if (passphrase is not null)
        {
            // An existing protected note, opened with its verified passphrase.
            if (protect is { Enable: false })
            {
                // The switch turned off: take the protection off the body and the comments.
                await articles.UnprotectAsync(value, passphrase);
                await comments.ReprotectAsync(value, passphrase, null);
                await articles.UpdateAsync(value, title: title, treePath: path, tags: tags, plaintext: body);
                return value;
            }
            var effective = passphrase;
            if (protect is { Enable: true, Passphrase: { } changed } && changed != passphrase)
            {
                // A new passphrase: re-wrap the body and re-seal the comments under it.
                await articles.ChangePassphraseAsync(value, passphrase, changed, protect.Hint);
                await comments.ReprotectAsync(value, passphrase, changed);
                effective = changed;
            }
            await articles.UpdateProtectedContentAsync(value, body, effective);
            await articles.UpdateAsync(value, title: title, treePath: path, tags: tags, plaintext: null,
                protectionHint: protect?.Hint, updateHint: protect is not null);
            return value;
        }

        // A plain note.
        if (protect is { Enable: true, Passphrase: { } pass })
        {
            // Save the text, then seal it (and the comments).
            await articles.UpdateAsync(value, title: title, treePath: path, tags: tags, plaintext: body);
            await articles.ProtectAsync(value, pass, protect.Hint);
            await comments.ReprotectAsync(value, null, pass);
            return value;
        }
        await articles.UpdateAsync(value, title: title, treePath: path, tags: tags, plaintext: body);
        return value;
    }

    /// <summary>Takes a protected note's password off (the note menu's "remove password"): the body and the comments both lose the passphrase layer.</summary>
    public async Task UnprotectAsync(Guid id, string passphrase)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<ArticleService>().UnprotectAsync(id, passphrase);
        await sp.GetRequiredService<CommentService>().ReprotectAsync(id, passphrase, null);
    }

    /// <summary>Moves the note to the trash (a soft delete, synced like every write); it can be restored from there.</summary>
    public async Task DeleteAsync(Guid id)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ArticleService>().DeleteAsync(id);
    }

    /// <summary>Makes a folder <paramref name="name"/> inside <paramref name="parent"/>; returns its path.</summary>
    public async Task<string> CreateFolderAsync(string parent, string name)
    {
        name = (name ?? "").Trim().Trim('/');
        if (name.Length == 0) throw new ArgumentException("Give the folder a name.");
        if (name.Contains('/')) throw new ArgumentException("A folder name cannot contain '/'.");
        var path = TreePathCanonicalizer.Canonicalize(NormalizePath(parent).TrimEnd('/') + "/" + name);
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FolderService>().CreateAsync(path);
        return path;
    }

    /// <summary>
    /// Notes and folders matching <paramref name="query"/>: titles, paths and ids through the full-text index, and with
    /// <paramref name="inText"/> the decrypted texts too (the vault must be open for that).
    /// </summary>
    public async Task<List<NoteListItem>> SearchAsync(string query, bool inText)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0) return [];
        using var scope = services.CreateScope();
        var search = scope.ServiceProvider.GetRequiredService<SearchService>();
        var results = inText ? await search.SearchWithContentAsync(query) : await search.SearchAsync(query);
        var items = new List<NoteListItem>();
        items.AddRange(results.Folders.Where(f => !f.Name.StartsWith('.'))
            .Select(f => new NoteListItem(f.Name, true, f.Path, null, null, false)));
        items.AddRange(results.Articles.Select(a => new NoteListItem(a.Title, false, NormalizePath(a.TreePath), a.Id, a.UpdatedAt, a.Protected)));
        return items;
    }

    /// <summary>The live note titled exactly <paramref name="title"/> (the newest if several), or null.</summary>
    public async Task<Guid?> FindByTitleAsync(string title)
    {
        using var scope = services.CreateScope();
        var all = await scope.ServiceProvider.GetRequiredService<IArticleRepository>().ListAsync();
        return all.Where(a => a.Title == title).OrderByDescending(a => a.UpdatedAt).Select(a => (Guid?)a.Id).FirstOrDefault();
    }

    /// <summary>The notes in the trash, newest first.</summary>
    public async Task<List<TrashItem>> TrashAsync(int max = 200)
    {
        using var scope = services.CreateScope();
        var page = await scope.ServiceProvider.GetRequiredService<HardDeleteService>()
            .ListAsync(1, max, null, HardDeleteStatusFilter.DeletedOnly, CancellationToken.None);
        return page.Items
            .Where(i => i.Type == "article" && i.Id is not null)
            .OrderByDescending(i => i.UpdatedAt)
            .Select(i => new TrashItem(i.Id!.Value, i.Title, NormalizePath(i.Path), i.UpdatedAt))
            .ToList();
    }

    /// <summary>Brings a note back from the trash as a new note "[RESTORED] title" at the top level (RestoreService's rule on every node).</summary>
    public async Task<Guid> RestoreAsync(Guid id)
    {
        using var scope = services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<RestoreService>().RestoreArticleAsync(id)).NewArticleId;
    }

    // ---- parity with the Android tree: rename, move, delete (folders and articles) ----

    /// <summary>Renames a folder through FolderService (versioned, synced); the path's children follow.</summary>
    public async Task RenameFolderAsync(string path, string newName)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var folder = await sp.GetRequiredService<IFolderRepository>().GetByPathAsync(NormalizePath(path));
        if (folder is null) throw new ArgumentException("The folder is gone.");
        await sp.GetRequiredService<FolderService>().RenameAsync(folder.Id, newName.Trim());
    }

    /// <summary>Moves a folder under <paramref name="newParentPath"/>; the core refuses a cycle into itself.</summary>
    public async Task MoveFolderAsync(string path, string newParentPath)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var folder = await sp.GetRequiredService<IFolderRepository>().GetByPathAsync(NormalizePath(path));
        if (folder is null) throw new ArgumentException("The folder is gone.");
        await sp.GetRequiredService<FolderService>().MoveAsync(folder.Id, NormalizePath(newParentPath));
    }

    /// <summary>Deletes a folder: its subfolders go with it, the articles inside move to the root (the core's rule, as the confirm says).</summary>
    public async Task DeleteFolderAsync(string path)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var folder = await sp.GetRequiredService<IFolderRepository>().GetByPathAsync(NormalizePath(path));
        if (folder is null) throw new ArgumentException("The folder is gone.");
        await sp.GetRequiredService<FolderService>().DeleteAsync(folder.Id);
    }

    public async Task RenameArticleAsync(Guid id, string newTitle)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ArticleService>().UpdateAsync(id, title: newTitle.Trim());
    }

    public async Task MoveArticleAsync(Guid id, string path)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ArticleService>().MoveAsync(id, NormalizePath(path));
    }

    /// <summary>Every active folder path, sorted; the picker's tree.</summary>
    public async Task<List<string>> FolderPathsAsync()
    {
        using var scope = services.CreateScope();
        var folders = await scope.ServiceProvider.GetRequiredService<IFolderRepository>().GetAllActiveAsync();
        return folders.Select(f => NormalizePath(f.Path)).Where(p => p != "/")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Creates the folder when it does not exist yet (a destination typed in the picker).</summary>
    public async Task EnsureFolderAsync(string path)
    {
        path = NormalizePath(path);
        if (path == "/") return;
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        if (await sp.GetRequiredService<IFolderRepository>().GetByPathAsync(path) is null)
            await sp.GetRequiredService<FolderService>().CreateAsync(path);
    }

    // ---- parity with the Android tags ----

    public async Task<List<string>> TagsOfAsync(Guid id)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ConceptTagService>().GetByArticleIdAsync(id);
    }

    /// <summary>Sets the article's tags through ArticleService (the same write as a save, embeddings when the model is there).</summary>
    public async Task SetTagsAsync(Guid id, List<string> tags)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ArticleService>().UpdateAsync(id, tags: tags);
    }

    public async Task<List<TagItem>> AllTagsAsync(string? filter)
    {
        using var scope = services.CreateScope();
        var tags = await scope.ServiceProvider.GetRequiredService<ConceptTagService>().ListAsync(filter, 200);
        return tags.Select(t => new TagItem(t.Name, t.ArticleCount)).ToList();
    }

    public async Task RenameTagAsync(string oldName, string newName)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ConceptTagService>().RenameAsync(oldName, newName.Trim());
    }

    public async Task MergeTagAsync(string source, string target)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ConceptTagService>().MergeAsync(source, target.Trim());
    }

    public async Task DeleteTagAsync(string name)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ConceptTagService>().DeleteAsync(name);
    }

    public async Task<List<TagArticleItem>> ArticlesByTagAsync(string tag)
    {
        using var scope = services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<ConceptTagService>().SearchByConceptAsync(tag);
        return rows.Select(a => new TagArticleItem(a.Id, a.Title, NormalizePath(a.TreePath))).ToList();
    }

    // ---- parity with the Android comments ----

    /// <summary>The article's comments, oldest first; the text decrypted with the article's passphrase when given, locked or unreadable otherwise.</summary>
    public async Task<List<CommentView>> CommentsAsync(Guid id, string? passphrase)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var comments = await sp.GetRequiredService<ICommentRepository>().GetByArticleIdAsync(id);
        var svc = sp.GetRequiredService<CommentService>();
        var views = new List<CommentView>();
        foreach (var c in comments.OrderBy(c => c.CreatedAt))
        {
            // The outer layer is the article's key (the vault is open); a passphrase-sealed
            // comment without its passphrase reads as the service's LockedText, as on Android.
            try
            {
                var text = await svc.DecryptTextAsync(c, passphrase);
                views.Add(new CommentView(c.Id, c.CreatedAt, text, text == CommentService.LockedText));
            }
            catch (Exception)
            {
                views.Add(new CommentView(c.Id, c.CreatedAt, null, false));
            }
        }
        return views;
    }

    public async Task AddCommentAsync(Guid id, string text, string? passphrase)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CommentService>().CreateAsync(id, text, passphrase);
    }

    public async Task DeleteCommentAsync(int id)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CommentService>().DeleteAsync(id);
    }

    // ---- parity part 2: related articles, images, the search's tag filter and recent list ----

    /// <summary>The note's related articles (shared tags / close meaning), as the note page lists them.</summary>
    public async Task<List<RelatedItem>> RelatedAsync(Guid id)
    {
        using var scope = services.CreateScope();
        var related = await scope.ServiceProvider.GetRequiredService<ConceptTagService>().GetRelatedArticlesAsync(id);
        return related.Select(r => new RelatedItem(r.Id, r.Title, NormalizePath(r.TreePath))).ToList();
    }

    /// <summary>
    /// Replaces every <c>![alt](/api/media/{id})</c> reference with the picture's own data URI (the
    /// note view's policy loads nothing but embedded pictures). A missing or unreadable picture
    /// becomes an italic "unavailable" line built from <paramref name="unavailableFormat"/> ({0} = alt).
    /// </summary>
    public async Task<string> ResolveImagesAsync(string markdown, string unavailableFormat)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(markdown, @"!\[([^\]]*)\]\(/api/media/([0-9a-f\-]{36})\)");
        if (matches.Count == 0) return markdown;
        using var scope = services.CreateScope();
        var media = scope.ServiceProvider.GetRequiredService<MediaService>();
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (!Guid.TryParse(match.Groups[2].Value, out var mediaId)) continue;
            try
            {
                var content = await media.GetContentAsync(mediaId);
                if (content is null)
                {
                    markdown = markdown.Replace(match.Value, "*" + string.Format(unavailableFormat, match.Groups[1].Value) + "*");
                    continue;
                }
                var (data, contentType, _) = content.Value;
                var dataUri = $"data:{contentType};base64,{Convert.ToBase64String(data)}";
                markdown = markdown.Replace(match.Value, $"![{match.Groups[1].Value}]({dataUri})");
            }
            catch (Exception)
            {
                markdown = markdown.Replace(match.Value, "*" + string.Format(unavailableFormat, match.Groups[1].Value) + "*");
            }
        }
        return markdown;
    }

    /// <summary>Stores a picture through MediaService (encrypted, synced) and returns its id; the editor then inserts the Markdown reference.</summary>
    public async Task<Guid> AddImageAsync(Guid? articleId, string fileName, string contentType, byte[] data)
    {
        using var scope = services.CreateScope();
        var media = await scope.ServiceProvider.GetRequiredService<MediaService>().CreateAsync(fileName, contentType, data, articleId);
        return media.Id;
    }

    /// <summary>
    /// The search page's list, as the Android search tab: an empty query with no tag shows the recent
    /// notes; a query goes through the full-text index (and the texts when <paramref name="inText"/>);
    /// a tag narrows the result to the notes carrying it.
    /// </summary>
    public async Task<List<NoteListItem>> SearchFilteredAsync(string query, bool inText, string? tag)
    {
        query = (query ?? "").Trim();
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var repo = sp.GetRequiredService<IArticleRepository>();

        if (query.Length == 0 && tag is null)
        {
            var recent = await repo.ListAsync();
            return recent.OrderByDescending(a => a.UpdatedAt).Take(20)
                .Select(a => new NoteListItem(a.Title, false, NormalizePath(a.TreePath), a.Id, a.UpdatedAt, a.Protected)).ToList();
        }

        HashSet<Guid>? tagIds = null;
        if (tag is not null)
        {
            var tagged = await sp.GetRequiredService<ConceptTagService>().SearchByConceptAsync(tag);
            tagIds = tagged.Select(t => t.Id).ToHashSet();
        }

        List<Article> found;
        if (query.Length > 0)
        {
            var search = sp.GetRequiredService<SearchService>();
            var results = inText ? await search.SearchWithContentAsync(query) : await search.SearchAsync(query);
            found = results.Articles;
        }
        else
        {
            found = await repo.ListAsync();
        }

        if (tagIds is not null) found = found.Where(a => tagIds.Contains(a.Id)).ToList();
        if (query.Length == 0) found = found.OrderByDescending(a => a.UpdatedAt).ToList();
        return found.Select(a => new NoteListItem(a.Title, false, NormalizePath(a.TreePath), a.Id, a.UpdatedAt, a.Protected)).ToList();
    }

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return "/";
        return "/" + path.Trim('/');
    }

    /// <summary>The last segment of a folder path, "Notes" for the top.</summary>
    public static string FolderTitle(string path) =>
        NormalizePath(path) == "/" ? "Notes" : NormalizePath(path)[(NormalizePath(path).LastIndexOf('/') + 1)..];
}
