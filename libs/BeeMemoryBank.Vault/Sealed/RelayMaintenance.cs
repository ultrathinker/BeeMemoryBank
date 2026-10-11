using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Storage.Boot;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Vault.Sealed;

/// <summary>
/// The two timers of the relay (docs/design/step-b-spec.md 6.3, 6.6), both only while the database is open: every 30 s the sequence floor is
/// raised when the log comes close to it, and every 60 s the inbox is imported again while rows are left (a deferrable failure stopped an
/// earlier pass). Idle on a plain node, on a closed one, and on a host without a gate.
/// </summary>
public sealed class RelayMaintenance(RelayInboxImporter importer, IRelayInbox inbox, IVaultGate? gate = null,
    ILogger<RelayMaintenance>? logger = null, TimeSpan? floorInterval = null, int importEvery = 2) : BackgroundService
{
    public static readonly TimeSpan DefaultFloorInterval = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _interval = floorInterval ?? DefaultFloorInterval;
    private int _ticks;

    /// <summary>One tick: the floor check, and every <c>importEvery</c>-th tick the retry of the import. Never throws for a database that closed meanwhile.</summary>
    public async Task TickAsync(CancellationToken ct = default)
    {
        if (gate is not { IsSealed: true, State: VaultState.Open }) return;
        _ticks++;
        try
        {
            await importer.RaiseFloorIfNeededAsync(ct).ConfigureAwait(false);
            if (_ticks % Math.Max(1, importEvery) != 0) return;

            var left = await inbox.GetSummaryAsync(ct).ConfigureAwait(false);
            var reports = await inbox.ListReportsAsync(ct).ConfigureAwait(false);
            if (left.Events + left.Blobs + reports.Count == 0) return;
            var result = await importer.ImportAsync(ct).ConfigureAwait(false);
            logger?.LogInformation("Relay inbox retry: {Applied} applied, {Held} held, {Refused} refused, {Waiting} waiting.",
                result.Applied, result.Held, result.Refused, result.Waiting);
        }
        catch (VaultSealedException)
        {
            logger?.LogDebug("The database closed during the relay maintenance.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "The relay maintenance failed; it is repeated at the next tick.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await TickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
