using BeeMemoryBank.Core.Interfaces;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>A node whose standing in the network is known and fixed.</summary>
internal sealed class FixedOwnStanding(bool superadmin) : IOwnStandingProvider
{
    public Task<bool> IsSuperadminAsync(CancellationToken ct = default) => Task.FromResult(superadmin);
}
