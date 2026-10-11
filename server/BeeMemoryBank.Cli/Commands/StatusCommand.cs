using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Vault.Sealed;
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

        // An encrypted folder is described from its boot store and, when one runs, from the node: no password, no migration, no write
        // (docs/design/step-b-spec.md 5.9). A folder that cannot start says why, by name.
        var diagnosis = VaultStoreFiles.Diagnose(dataPath);
        if (BrokenStart.From(diagnosis) is { } broken)
        {
            await output.WriteLineAsync($"Node:      cannot start ({broken.Reason})");
            await output.WriteLineAsync(broken.Message);
            return 1;
        }
        if (diagnosis.Mode == VaultStoreMode.Sealed)
        {
            await VaultCommand.SealedStatusLinesAsync(dataPath, output, includeIdentity: true);
            await output.WriteLineAsync("Articles:  not counted here: the database is encrypted, and bmb status never asks for the password.");
            return 0;
        }

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

        // Recorded by the running node's sync scheduler (this is another process): a full peer compacted past us.
        if (SnapshotRequiredState.Read(scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>()) is { } required)
            await output.WriteLineAsync(
                $"Sync:      {SnapshotRequiredState.Describe(required.RemoteUrl, required.LastCompactionCp, required.CurrentHeadSeq)} " +
                $"(since {required.SinceUtc:yyyy-MM-dd HH:mm} UTC)");

        // Events received but not applied yet (docs/design/step-a-spec.md 6.9), and those that turned out not to apply.
        var held = await (scope.ServiceProvider.GetService<IHeldEventStatus>()
                          ?? new HeldEventStatus(scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>())).GetSummaryAsync();
        if (HeldEventStatus.Describe(held, DateTime.UtcNow) is { } line)
            await output.WriteLineAsync(
                $"Held:      {line} (locked {held.HeldLocked}, key {held.HeldKey}, order {held.HeldOrder}; " +
                $"oldest {held.OldestHeldAt:yyyy-MM-dd HH:mm} UTC; applied through #{held.AppliedThroughSequence} of #{held.HeadSequence})");
        if (held.QuarantinedAfterReceipt > 0)
            await output.WriteLineAsync(
                $"Held:      {held.QuarantinedAfterReceipt} received change(s) could not be applied (a metadata key this node does not hold)");
        return 0;
    }
}
