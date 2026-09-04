using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Commands;

public static class InitCommand
{
    /// <summary>
    /// Initializes the node: creates DB, generates keys, saves the password slot.
    /// Returns 0 on success, 1 on error.
    /// </summary>
    public static async Task<int> HandleAsync(
        string dataPath,
        string name,
        string password,
        TextWriter? output = null)
    {
        output ??= Console.Out;
        await using var services = await CliServiceProvider.CreateAsync(dataPath);
        using var scope = services.CreateScope();
        var initSvc = scope.ServiceProvider.GetRequiredService<InitializationService>();

        if (await initSvc.IsInitializedAsync())
        {
            await output.WriteLineAsync("Error: node is already initialized.");
            return 1;
        }

        // CLI init: use node name as admin username (admin can rename via Profile later).
        await initSvc.InitializeAsync(name, name, password);
        await output.WriteLineAsync($"Node '{name}' successfully initialized in {dataPath}");
        return 0;
    }

    /// <summary>
    /// Wipes the node back to the pre-Setup state. The web UI has the same operation on the
    /// superadmin-only Admin page; this exists as the host-only path for when nobody can sign in
    /// any more (every superadmin account lost, or the Web layer itself broken) — the situation the
    /// old anonymous "Reset &amp; rejoin" form on the Login screen used to cover, at the cost of
    /// exposing a node-wiping master-password oracle to anyone who could load that page.
    /// Returns 0 on success, 1 on error.
    /// </summary>
    public static async Task<int> ResetAsync(
        string dataPath,
        string masterPassword,
        bool yes,
        TextWriter? output = null)
    {
        output ??= Console.Out;

        if (!yes)
        {
            await output.WriteLineAsync(
                "Refusing to reset without --yes. This DELETES every article, folder, user, key and " +
                "sync-state row on this node and returns it to first-run Setup. It cannot be undone.");
            return 1;
        }

        await using var services = await CliServiceProvider.CreateAsync(dataPath);
        using var scope = services.CreateScope();
        var resetSvc = scope.ServiceProvider.GetRequiredService<NodeResetService>();

        var result = await resetSvc.ResetAsync(masterPassword, initiatedBy: "cli");
        switch (result.Outcome)
        {
            case NodeResetOutcome.NotInitialized:
                await output.WriteLineAsync("Error: node is not initialized — nothing to reset.");
                return 1;
            case NodeResetOutcome.InvalidPassword:
                await output.WriteLineAsync("Error: invalid master password.");
                return 1;
            default:
                await output.WriteLineAsync(
                    $"Node {result.OldNodeId} was reset. Open the web UI (/Setup) to initialize a new " +
                    "vault or join a network.");
                return 0;
        }
    }
}
