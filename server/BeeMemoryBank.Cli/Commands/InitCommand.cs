using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Commands;

public static class InitCommand
{
    /// <summary>
    /// Initializes the node: creates DB, generates keys, saves the password slot - and encrypts the database under that master password with metadata
    /// encryption already on, as every setup does (BMB-306; there is no way to set a node up in the open). The node starts locked.
    /// Returns 0 on success, 1 on error.
    /// </summary>
    public static async Task<int> HandleAsync(
        string dataPath,
        string name,
        string password,
        string passwordHint,
        bool iSavedTheKey = false,
        TextWriter? output = null)
    {
        output ??= Console.Out;

        // Mandatory at every entry point with a person: the sign-in page has nothing else to show. The policy's
        // sentence is safe to print.
        if (PasswordHintPolicy.Validate(passwordHint, password, name) is { } hintProblem)
        {
            await output.WriteLineAsync($"Error: {hintProblem}");
            return 1;
        }
        using var flow = BeeMemoryBank.AppPaths.SealIntent.Flow.Enter();   // what a take-back may remove is what THIS command began
        await using var services = await CliServiceProvider.CreateAsync(dataPath, forFreshSeal: true);
        using var scope = services.CreateScope();
        var initSvc = scope.ServiceProvider.GetRequiredService<InitializationService>();

        if (await initSvc.IsInitializedAsync())
        {
            await output.WriteLineAsync("Error: node is already initialized.");
            return 1;
        }

        // A folder that holds a vault already (an identity row or a key slot) is not set up over, whatever shape it has.
        if (await initSvc.IsClaimedAsync())
        {
            await output.WriteLineAsync("Error: this folder already holds a node (an identity row or a key slot); it cannot be initialized over.");
            return 1;
        }

        // The sealing service is required before anything is written: a process that cannot encrypt the node does not set it up.
        var sealing = scope.ServiceProvider.GetRequiredService<FreshNodeSealing>();

        // CLI init: use node name as admin username (admin can rename via Profile later). The seal intent begins inside, before the first write.
        try { await initSvc.InitializeAsync(name, name, password, passwordHint: passwordHint); }
        catch
        {
            await scope.ServiceProvider.GetRequiredService<NodeResetService>().DiscardPendingSetupAsync("cli init: the vault could not be written");
            throw;
        }

        // A new node owes the blocking recovery-key step. Written while the database is still plain: after the seal it
        // is encrypted and closed, and only an unlock could write to it.
        var nodeRepo = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
        await nodeRepo.SetRecoveryKeyRequiredAsync(true);

        // The recovery key is minted while the database is still plain and this process holds the password, so the
        // slot is encrypted together with everything else by the seal below. Failure here is not fatal: the node can
        // still be set up, and the web sign-in will then show the recovery-key step.
        string? recoveryKey = null;
        try
        {
            var session = scope.ServiceProvider.GetRequiredService<SessionService>();
            if (await session.UnlockAsync(password))
            {
                recoveryKey = await scope.ServiceProvider.GetRequiredService<KeyManagementService>().AddRecoveryKeyAsync();
                session.Lock();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await output.WriteLineAsync($"Note: no recovery key could be issued now ({ex.Message}). Sign in to the web UI to issue one.");
        }

        try
        {
            await sealing.SealAsync(password, activateMetadata: true);
        }
        catch (FreshNodeSealingException ex)
        {
            await output.WriteLineAsync($"Error: {ex.Message}");
            if (!ex.LeaveAsItIs)
            {
                // Nothing is in the vault but the identity just given: take it back, so that no node is left in the open (a node that is neither taken
                // back nor encrypted is wiped at its next start: the seal intent is still on disk).
                try
                {
                    var reset = scope.ServiceProvider.GetRequiredService<NodeResetService>();
                    if (!await reset.DiscardPendingSetupAsync("cli init: encrypting the new node failed"))
                        await reset.ResetAsync(password, initiatedBy: "cli init: encrypting the new node failed");
                }
                catch (Exception resetEx) when (resetEx is not OperationCanceledException)
                {
                    await output.WriteLineAsync($"The setup could not be undone either ({resetEx.GetType().Name}): run 'bmb init reset --yes' and set the node up again.");
                    return 1;
                }
                await output.WriteLineAsync("Nothing was kept: run 'bmb init' again.");
            }
            return 1;
        }

        await output.WriteLineAsync($"Node '{name}' successfully initialized in {dataPath}");
        await output.WriteLineAsync("Its database is encrypted under the master password you gave, and metadata encryption is on. The node starts locked: the password opens it.");

        // The recovery-key step a new node owes (release 2.7.5). The CLI can finish it here - it has the key and a
        // person at the terminal - so the web gate does not have to show it again. No e-mail recovery exists, so
        // this is the one chance to keep the key.
        if (recoveryKey is null)
        {
            await output.WriteLineAsync("No recovery key was issued. Sign in to the web UI: it will show one and ask you to save it.");
            return 0;
        }

        await output.WriteLineAsync("");
        await output.WriteLineAsync("Recovery key (write it down; it is shown only this once):");
        await output.WriteLineAsync("  " + recoveryKey);
        await output.WriteLineAsync("Without e-mail recovery, this key is the way back in if the master password is forgotten.");

        var acknowledged = iSavedTheKey || ConfirmKeyWasSaved();
        if (acknowledged)
        {
            // The node is sealed and closed now: open it for this one write, then lock it again.
            var session = scope.ServiceProvider.GetRequiredService<SessionService>();
            if (await session.UnlockAsync(password))
            {
                await nodeRepo.SetRecoveryKeyAcknowledgedAtAsync(DateTime.UtcNow);
                session.Lock();
                await output.WriteLineAsync("Recorded that you saved the recovery key.");
            }
            else
            {
                await output.WriteLineAsync("The confirmation could not be recorded; sign in to the web UI, where the recovery-key step will appear.");
            }
        }
        else
        {
            await output.WriteLineAsync("Not confirmed. The node will ask for the key again in the web app. Sign in to the web UI: the recovery-key step will appear and you can save a key there.");
        }
        return 0;
    }

    /// <summary>Asks for the confirmation when a person is at the terminal; a redirected stdin is never read.</summary>
    private static bool ConfirmKeyWasSaved()
    {
        if (Console.IsInputRedirected) return false;
        Console.Error.Write("Type 'yes' once you have written down the recovery key: ");
        return string.Equals(Console.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Wipes the node back to the pre-Setup state. The web UI has the same operation on the
    /// superadmin-only Admin page; this exists as the host-only path for when nobody can sign in
    /// any more (every superadmin account lost, or the Web layer itself broken). A node-wiping
    /// master-password check must never exist as an anonymous, unauthenticated form — that is a
    /// master-password oracle.
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
