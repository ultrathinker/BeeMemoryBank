using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Storage.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.BlindMobile.Services.Blind;

/// <summary>Runs the phone's pull-only protocol-3 sync through the call-code pinned client.</summary>
public sealed class BlindPhoneSync(
    IBlindPhonePullClient client, BlindHttpClientProvider http, BlindPhoneState state) : IBlindPhoneSync
{
    /// <summary>The limit guard: asked before a round starts (a pause is thrown as <see cref="BlindPausedException"/>, nothing goes out). Null for a composition that has none.</summary>
    public BlindLimitGuard? Guard { get; init; }

    /// <summary>Tells the source when a round stops for a pause the source is told about.</summary>
    public BlindPauseReporter? Reporter { get; init; }

    /// <summary>What the round counted is saved when it ends, however it ends.</summary>
    public BlindUsageLedger? Usage { get; init; }

    /// <summary>What the copy holds is measured again when a round ends (the counters that were kept live during it are re-synced with the folders).</summary>
    public BlindStoredBytes? Stored { get; init; }

    /// <summary>Where the copy's whitelist and key history are read after a round (the hub's re-key, <see cref="FollowHubKeyAsync"/>). Null: the pin never moves here.</summary>
    public IServiceProvider? Services { get; init; }

    public async Task SyncOnceAsync(BlindCallCode target, CancellationToken ct)
    {
        try
        {
            await RunAsync(target, ct);
        }
        finally
        {
            Usage?.Flush();
            Stored?.Resync();
        }
    }

    private async Task RunAsync(BlindCallCode target, CancellationToken ct)
    {
        // A pause is not a failure: before the round starts the guard says so, and the source is told (once a day while it lasts); nothing else goes out.
        if (Guard?.Check() is { } held)
        {
            await TellAsync(target, held, ct);
            throw new BlindPausedException(held);
        }

        try
        {
            await http.WithClientAsync(target, BlindHubCall.Sync, exempt: false, (web, hub) => client.SyncOnceAsync(web, hub, ct), ct);
            await FollowHubKeyAsync(target);
            // A round ran to its end: a pause that was told is over, the next one is told at once.
            Reporter?.Clear();
        }
        catch (BlindPausedException ex)
        {
            // A limit was reached DURING the round: the page in flight was not applied (the cursor is where the last applied event put it); the source is told.
            await TellAsync(target, ex.Pause, ct);
            throw;
        }
        catch (SnapshotRequiredException)
        {
            // The hourly heavy worker owns replica download. Marking
            // this stale position incomplete routes the next permitted heavy pass through it.
            state.InitialLoadDone = false;
            throw;
        }
    }

    /// <summary>
    /// After a round: when the copy applied the hub's <c>node_key_rotated</c> (its whitelist row for the hub carries the new key, and its key history the
    /// links both keys signed), the copy moves its pin of the hub key along that chain (release 2.12, plan WS-D D4; <see cref="BlindPhoneState.FollowHubKey"/>),
    /// so that the packages the hub signs from now on are checked by its new key. No request goes out for it; a chain that does not reach the row's key
    /// moves nothing. Best effort: a failure here is not the round's.
    /// </summary>
    private async Task FollowHubKeyAsync(BlindCallCode target)
    {
        if (Services is null) return;
        try
        {
            if (Services.GetService<IWhitelistRepository>() is not { } whitelist || Services.GetService<IDbConnectionFactory>() is not { } db) return;
            if (await whitelist.GetByNodeIdAsync(target.NodeId) is not { Ed25519PublicKey.Length: 32 } row) return;
            var chain = await new NodeKeyHistoryStore(db).ChainAsync(target.NodeId);
            if (chain.Count == 0) return;
            state.FollowHubKey(new RemoteIdentity(target.NodeId, "", Convert.ToBase64String(row.Ed25519PublicKey),
                KeyChain: chain.Select(NodeKeyLinkDto.Of).ToList()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The pin stays; the next round tries again.
        }
    }

    private Task TellAsync(BlindCallCode target, BlindPause pause, CancellationToken ct) =>
        Reporter is { } reporter ? reporter.ReportIfDueAsync(target, pause, ct) : Task.CompletedTask;
}
