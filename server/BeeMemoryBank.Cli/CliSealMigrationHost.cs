using BeeMemoryBank.Vault.Sealed;

namespace BeeMemoryBank.Cli;

/// <summary>
/// What the command line adds to "Encrypt this node" when <c>bmb init</c> sets a node up encrypted (BMB-306): nothing. The node has no data yet, no
/// chat database, no sync loop and no auto-unlock to warn about.
/// </summary>
internal sealed class CliSealMigrationHost : ISealMigrationHost
{
    public Task<IReadOnlyList<SealBlocker>> HostBlockersAsync(SealOptions options, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SealBlocker>>([]);

    public Task<IReadOnlyList<SealWarning>> HostWarningsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SealWarning>>([]);
}
