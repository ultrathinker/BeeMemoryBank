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
    public async Task<Guid> SaveAsync(Guid? id, string title, string path, string body, string? passphrase = null)
    {
        title = (title ?? "").Trim();
        if (title.Length == 0) throw new ArgumentException("Give the note a title.");
        path = NormalizePath(path);

        using var scope = services.CreateScope();
        var articles = scope.ServiceProvider.GetRequiredService<ArticleService>();
        if (id is null)
            return (await articles.CreateAsync(title, path, [], body ?? "")).Id;

        if (passphrase is not null)
        {
            await articles.UpdateProtectedContentAsync(id.Value, body ?? "", passphrase);
            await articles.UpdateAsync(id.Value, title: title, treePath: path);
        }
        else
        {
            await articles.UpdateAsync(id.Value, title: title, treePath: path, plaintext: body ?? "");
        }
        return id.Value;
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

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "/") return "/";
        return "/" + path.Trim('/');
    }

    /// <summary>The last segment of a folder path, "Notes" for the top.</summary>
    public static string FolderTitle(string path) =>
        NormalizePath(path) == "/" ? "Notes" : NormalizePath(path)[(NormalizePath(path).LastIndexOf('/') + 1)..];
}
