using System.Security.Cryptography;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Infrastructure.AutoUnlock;
using BeeMemoryBank.Infrastructure.OsAutoUnlock;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Vault.Sealed;
using Dapper;

namespace BeeMemoryBank.Api.Services.Sealed;

/// <summary>
/// What the Api adds to "Encrypt this node" (docs/design/step-b-spec.md 10): the sentinel a vault older than it has to be given before it is
/// copied, and the warning that auto-unlock is on. No copy of the plain database is kept: encrypting a node is not undone from the node.
/// </summary>
public sealed class ApiSealMigrationHost(IServiceProvider services) : ISealMigrationHost
{
    /// <summary>
    /// A vault older than the sentinel has none: <c>tbl_node_identity.sentinel_value</c> is NULL (the setup of those days did not write it; a password
    /// unlock heals it since 2.6.2, but a node that was only ever opened by a door without a password has not had one). The verification of the
    /// encrypted copy names the master key through it, so it is made here, once, from the key of the unlocked session (the one the owner just proved
    /// with the password), before the files are folded and copied.
    /// </summary>
    public async Task PrepareVaultAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>();
        if (!MasterKeyManager.IsSentinelMissing(await identity.GetSentinelAsync())) return;

        var dek = services.GetRequiredService<SessionService>().GetMasterDek();
        try
        {
            var sentinel = MasterKeyManager.ComputeSentinel(dek);
            if (!MasterKeyManager.VerifySentinel(sentinel, dek))
                throw new SealMigrationException(SealFailures.Sentinel, "The sentinel of this vault could not be made; nothing was changed.");
            // The check and the write are one statement: a sentinel another unlock, rotation or heal stored since the read above is never overwritten
            // (no rows changed = somebody else supplied it, and what they stored is the one the verification names).
            await identity.StoreSentinelIfMissingAsync(sentinel);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public Task<IReadOnlyList<SealBlocker>> HostBlockersAsync(SealOptions options, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SealBlocker>>([]);

    public async Task<IReadOnlyList<SealWarning>> HostWarningsAsync(CancellationToken ct)
    {
        var on = false;
        try
        {
            if (services.GetService<ServerAutoUnlockService>() is { } server) on |= (await server.GetStatusAsync(ct)).Enabled;
            if (services.GetService<OsAutoUnlockService>() is { IsSupported: true } os) on |= await os.IsEnabledAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A secret store that cannot answer is not a reason to hide the other half of the answer.
        }
        return on
            ? [new SealWarning("auto_unlock_on", "Auto-unlock is on: this node will open its vault by itself, so encrypting it does not protect against a stolen disk.")]
            : [];
    }
}
