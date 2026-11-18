using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Vault.SharedDoors;

internal static class SharedDoorsGuards
{
    public static async Task<(Guid? NodeId, string? DisplayName)> ResolveHomeAsync(
        IAccountsHomeRepository? accountsHomeRepo,
        INodeIdentityRepository? nodeRepo,
        IWhitelistRepository? whitelistRepo)
    {
        if (accountsHomeRepo is null)
            return (null, null);

        var home = await accountsHomeRepo.GetAsync().ConfigureAwait(false);
        if (home is null)
            return (null, null);

        if (nodeRepo is not null)
        {
            var identity = await nodeRepo.GetAsync().ConfigureAwait(false);
            if (identity is not null && home.NodeId == identity.NodeId)
                return (home.NodeId, identity.DisplayName);
        }

        if (whitelistRepo is not null)
        {
            var peer = await whitelistRepo.GetByNodeIdAsync(home.NodeId, includeDeleted: true).ConfigureAwait(false);
            return (home.NodeId, peer?.DisplayName);
        }

        return (home.NodeId, null);
    }

    public static async Task<ReplicatedUserException> ReplicatedUserRefusalAsync(
        IAccountsHomeRepository? accountsHomeRepo,
        INodeIdentityRepository? nodeRepo,
        IWhitelistRepository? whitelistRepo)
    {
        var (id, name) = await ResolveHomeAsync(accountsHomeRepo, nodeRepo, whitelistRepo).ConfigureAwait(false);
        return ReplicatedUserException.ForReplicatedUser(name, id);
    }

    public static async Task<ReplicatedUserException> SharedSuperadminWriteRefusalAsync(
        IAccountsHomeRepository? accountsHomeRepo,
        INodeIdentityRepository? nodeRepo,
        IWhitelistRepository? whitelistRepo)
    {
        var (id, name) = await ResolveHomeAsync(accountsHomeRepo, nodeRepo, whitelistRepo).ConfigureAwait(false);
        return ReplicatedUserException.ForSharedSuperadminWrite(name, id);
    }

    public static async Task<bool> IsSharedNonHomeNodeAsync(
        IAccountsHomeRepository? accountsHomeRepo,
        INodeIdentityRepository? nodeRepo,
        ISharedDoorsStateRepository? stateRepo,
        IDbConnectionFactory? connFactory)
    {
        if (nodeRepo is null)
            return false;
        var identity = await nodeRepo.GetAsync().ConfigureAwait(false);
        if (identity is null || BlindNodeId.IsBlind(identity.NodeId))
            return false;

        if (accountsHomeRepo is not null)
        {
            var home = await accountsHomeRepo.GetAsync().ConfigureAwait(false);
            if (home is not null && home.NodeId == identity.NodeId)
                return false;
        }

        if (stateRepo is not null)
        {
            var state = await stateRepo.GetAsync().ConfigureAwait(false);
            if (state.SelfOwnership == OwnershipKinds.Shared)
                return true;
            if (state.SelfOwnership == OwnershipKinds.Personal)
                return false;
        }

        if (connFactory is not null)
        {
            var resolved = await WhitelistHistory.ResolveSelfOwnershipAsync(connFactory, identity.NodeId).ConfigureAwait(false);
            return resolved?.Ownership == OwnershipKinds.Shared;
        }

        return false;
    }
}
