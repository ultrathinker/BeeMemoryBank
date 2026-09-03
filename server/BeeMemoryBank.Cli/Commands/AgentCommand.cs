using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Cli.Commands;

public static class AgentCommand
{
    /// <summary>
    /// Creates a new agent. Requires master password to encrypt the agent DEK.
    /// --owner-id specifies the owning user; falls back to any active user if omitted.
    /// </summary>
    public static async Task<int> HandleCreateAsync(
        string dataPath,
        string name,
        string? description,
        int ownerId,
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

        // Resolve owner
        var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        User? owner;
        if (ownerId > 0)
        {
            owner = await userRepo.GetByIdAsync(ownerId);
            if (owner == null)
            {
                await output.WriteLineAsync($"Error: user {ownerId} not found.");
                return 1;
            }
        }
        else
        {
            // Default: first active user
            var users = await userRepo.ListActiveAsync();
            owner = users.FirstOrDefault();
            if (owner == null)
            {
                await output.WriteLineAsync("Error: no users found. Specify --owner-id.");
                return 1;
            }
        }

        var apiKey = AgentKeyHelper.GenerateApiKey();

        var agent = new Agent
        {
            Name = name.Trim(),
            Description = description?.Trim(),
            KeyPrefix = AgentKeyHelper.GetKeyPrefix(apiKey),
            KeyHash = AgentKeyHelper.ComputeKeyHash(apiKey),
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            OwnerUserId = owner.Id
        };

        // H6 hybrid model (see AgentEndpoints for the full reasoning): only a superadmin's
        // agent gets a wrapped master DEK and can therefore auto-unlock a locked vault. An
        // ordinary user's CLI-created agent is otherwise identical — it authenticates and
        // works normally whenever the vault is already unlocked.
        if (owner.Role == UserRoles.Superadmin)
        {
            byte[] masterDek;
            try { masterDek = session.GetMasterDek(); }
            catch
            {
                await output.WriteLineAsync("Error: session is locked.");
                return 1;
            }

            try
            {
                var (ciphertext, iv, salt) = AgentKeyHelper.EncryptDekV1(apiKey, masterDek);
                agent.EncryptedDek = ciphertext;
                agent.DekIV = iv;
                agent.Salt = salt;
                agent.KdfVersion = 1;
            }
            finally
            {
                Array.Clear(masterDek);
            }
        }

        var agentRepo = scope.ServiceProvider.GetRequiredService<IAgentRepository>();
        agent.Id = await agentRepo.CreateAsync(agent);

        await output.WriteLineAsync($"Agent created: {agent.Id}");
        await output.WriteLineAsync($"  Name:  {agent.Name}");
        await output.WriteLineAsync($"  Owner: {owner.DisplayName} (id={owner.Id})");
        await output.WriteLineAsync(agent.CanAutoUnlock
            ? "  Owner is a superadmin: this key can auto-unlock a locked vault."
            : "  Owner is not a superadmin: this key cannot unlock a locked vault; it only works while the vault is already unlocked.");
        await output.WriteLineAsync($"  API Key (shown once — copy it now!):");
        await output.WriteLineAsync($"  {apiKey}");
        return 0;
    }
}
