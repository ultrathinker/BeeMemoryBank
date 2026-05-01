using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Commands;

public static class StatusCommand
{
    /// <summary>
    /// Displays node status: whether initialized, name, article count.
    /// Returns 0 on success, 1 if the node is not initialized.
    /// </summary>
    public static async Task<int> HandleAsync(string dataPath, TextWriter? output = null)
    {
        output ??= Console.Out;
        await using var services = await CliServiceProvider.CreateAsync(dataPath);
        using var scope = services.CreateScope();
        var initSvc = scope.ServiceProvider.GetRequiredService<InitializationService>();

        if (!await initSvc.IsInitializedAsync())
        {
            await output.WriteLineAsync("Node is not initialized. Run: bmb init --name <name> --password <password>");
            return 1;
        }

        var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
        var identity = await nodeRepo.GetAsync();
        var articleRepo = scope.ServiceProvider.GetRequiredService<IArticleRepository>();
        var articles = await articleRepo.ListAsync();

        await output.WriteLineAsync($"Node:      {identity!.DisplayName}");
        await output.WriteLineAsync($"ID:        {identity.NodeId}");
        await output.WriteLineAsync($"Path:      {dataPath}");
        await output.WriteLineAsync($"Articles:  {articles.Count}");
        await output.WriteLineAsync($"Created:   {identity.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC");
        return 0;
    }
}
