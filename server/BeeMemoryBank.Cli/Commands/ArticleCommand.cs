using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Commands;

public static class ArticleCommand
{
    public static async Task<int> HandleCreateAsync(
        string dataPath,
        string title,
        string treePath,
        string content,
        string password,
        TextWriter? output = null)
    {
        output ??= Console.Out;
        await using var services = await CliServiceProvider.CreateAsync(dataPath);
        using var scope = services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<SessionService>();

        if (!await session.UnlockAsync(password))
        {
            await output.WriteLineAsync("Error: invalid password.");
            return 1;
        }

        var articleSvc = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var article = await articleSvc.CreateAsync(title, treePath, [], content);
        await output.WriteLineAsync($"Article created: {article.Id}");
        await output.WriteLineAsync($"  Title: {article.Title}");
        await output.WriteLineAsync($"  Path:  {article.TreePath}");
        return 0;
    }

    public static async Task<int> HandleListAsync(
        string dataPath,
        string? treePath = null,
        TextWriter? output = null)
    {
        output ??= Console.Out;
        await using var services = await CliServiceProvider.CreateAsync(dataPath);
        using var scope = services.CreateScope();
        var articleSvc = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var conceptTagRepo = scope.ServiceProvider.GetRequiredService<IConceptTagRepository>();
        var articles = await articleSvc.ListAsync(treePath);

        if (articles.Count == 0)
        {
            await output.WriteLineAsync("No articles found.");
            return 0;
        }

        foreach (var a in articles)
        {
            var conceptTags = await conceptTagRepo.GetByArticleIdAsync(a.Id);
            var tagStr = conceptTags.Count > 0 ? $" [{string.Join(", ", conceptTags)}]" : "";
            await output.WriteLineAsync($"{a.Id}  {a.TreePath,-20}  {a.Title}{tagStr}");
        }
        return 0;
    }

    public static async Task<int> HandleGetAsync(
        string dataPath,
        Guid id,
        bool showContent = false,
        string? password = null,
        TextWriter? output = null)
    {
        output ??= Console.Out;
        await using var services = await CliServiceProvider.CreateAsync(dataPath);
        using var scope = services.CreateScope();
        var articleSvc = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var conceptTagRepo = scope.ServiceProvider.GetRequiredService<IConceptTagRepository>();

        var article = await articleSvc.GetMetadataAsync(id);
        if (article == null)
        {
            await output.WriteLineAsync($"Article {id} not found.");
            return 1;
        }

        var conceptTags = await conceptTagRepo.GetByArticleIdAsync(id);

        await output.WriteLineAsync($"ID:           {article.Id}");
        await output.WriteLineAsync($"Title:        {article.Title}");
        await output.WriteLineAsync($"Path:         {article.TreePath}");
        await output.WriteLineAsync($"ConceptTags:  {string.Join(", ", conceptTags)}");
        await output.WriteLineAsync($"Created:      {article.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC");
        await output.WriteLineAsync($"Updated:      {article.UpdatedAt:yyyy-MM-dd HH:mm:ss} UTC");

        if (showContent)
        {
            if (string.IsNullOrEmpty(password))
            {
                await output.WriteLineAsync("Error: --password is required to view content.");
                return 1;
            }

            var session = scope.ServiceProvider.GetRequiredService<SessionService>();
            if (!await session.UnlockAsync(password))
            {
                await output.WriteLineAsync("Error: invalid password.");
                return 1;
            }

            var body = await articleSvc.GetContentAsync(id);
            await output.WriteLineAsync();
            await output.WriteLineAsync("--- Content ---");
            await output.WriteLineAsync(body);
        }

        return 0;
    }

    public static async Task<int> HandleDeleteAsync(
        string dataPath,
        Guid id,
        string? password = null,
        TextWriter? output = null)
    {
        output ??= Console.Out;
        await using var services = await CliServiceProvider.CreateAsync(dataPath);
        using var scope = services.CreateScope();
        var articleSvc = scope.ServiceProvider.GetRequiredService<ArticleService>();
        var session = scope.ServiceProvider.GetRequiredService<SessionService>();

        // Delete logs an event via EventLogger which signs with the node's Ed25519 key.
        // For v=1 identity rows the unlocked master DEK is required to decrypt the wrapped
        // seed. Unlock if a password is provided.
        if (!string.IsNullOrEmpty(password) && !session.IsUnlocked)
        {
            if (!await session.UnlockAsync(password))
            {
                await output.WriteLineAsync("Error: invalid password.");
                return 1;
            }
        }

        var article = await articleSvc.GetMetadataAsync(id);
        if (article == null)
        {
            await output.WriteLineAsync($"Article {id} not found.");
            return 1;
        }

        await articleSvc.DeleteAsync(id);
        await output.WriteLineAsync($"Article '{article.Title}' deleted.");
        return 0;
    }
}
