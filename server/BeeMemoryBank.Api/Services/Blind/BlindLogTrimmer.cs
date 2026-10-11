using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;

namespace BeeMemoryBank.Api.Services;

/// <summary>
/// Keeps a blind node's event log from growing forever (plan 5.4). A blind node cannot compact the
/// ordinary way — that publishes a snapshot_checkpoint event, and it authors none — so it trims
/// locally: rows every peer has already read from it AND the last state anchor already covers go,
/// and a tbl_compaction_log row records the cut, so a peer asking for anything below it gets the
/// usual 410 instead of a silent hole.
/// </summary>
public sealed class BlindLogTrimmer(IServiceScopeFactory scopeFactory, ILogger<BlindLogTrimmer> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TrimAsync();
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Blind log trim failed; retrying in {Interval}", Interval);
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }

    /// <summary>
    /// Trims once; returns the new cut-off, or null when nothing could go. The rule is the blind store's
    /// (<see cref="BaselineStore.TrimLogAsync"/>): read by every peer and covered by the newest anchor, as before Step A;
    /// once activated also covered by the current baseline, and nothing at all while there is none (step-a-spec 9.5).
    /// </summary>
    public async Task<long?> TrimAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var activated = sp.GetService<ISyncProtocolPolicy>()?.SealsMetadata ?? false;
        var store = sp.GetService<IBaselineStore>();
        var factory = sp.GetRequiredService<IDbConnectionFactory>();
        if (store is not null && await BaselineStore.HasAdoptedAsync(factory) && !await BaselineStore.IsOpaqueAsync(factory))
        {
            // The first adoption's purge did not finish (no space for the rebuild, a reader, a crash): do it now, at start-up
            // and on every run, until the node can say its store holds no readable content (spec 9.3). It needs only that a
            // baseline was adopted - not that its files are servable now (a damaged or half-switched current/ must not keep
            // readable rows on the disk) and not that the activation was imported yet (a seed that failed after its adoption).
            try
            {
                await store.PurgeLegacyContentAsync();
            }
            catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
            {
                logger.LogWarning(ex, "Blind store: the purge of earlier readable content is still unfinished; retrying in {Interval}", Interval);
            }
        }
        // The trim, unlike the purge, is bounded by a baseline this node can serve: none (or a damaged one) means nothing goes.
        var current = store is not null ? await store.GetCurrentAsync() : null;
        var (cutoff, _) = await BaselineStore.TrimLogAsync(
            factory,
            sp.GetRequiredService<IEventLogRepository>(),
            sp.GetRequiredService<ISyncPushPositionRepository>(),
            activated, current?.LocalCut, logger);
        return cutoff;
    }
}
