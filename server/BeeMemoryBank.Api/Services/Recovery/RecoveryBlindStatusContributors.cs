using BeeMemoryBank.Api.Services.BlindBackup;
using BeeMemoryBank.Api.Services.BlindStatus;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Sync.Recovery;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary><c>anchor{created_at, confirmed}</c> of /api/blind/status (CONTRACTS §5).</summary>
/// <remarks>
/// "confirmed" recomputes the state digest at the anchor's cut — every blob hashed — so it is kept until
/// the newest anchor or the event log head changes (<see cref="RecoveryAnchorStatusCache"/>), not redone
/// on every poll of the console. An activated blind host computes none (step-a-spec 10.3): "confirmed" is null
/// and the anchor says who checks it instead, with its id and author.
/// </remarks>
public sealed class RecoveryAnchorStatusContributor(
    RecoveryStatusService status, IEventLogRepository events, RecoveryAnchorStatusCache cache) : IBlindStatusContributor
{
    public async Task ContributeAsync(BlindStatusBuilder b, CancellationToken ct)
    {
        var newest = await status.NewestAnchorAsync();
        if (newest == null)
        {
            b.Set("anchor", null);
            return;
        }
        var key = (newest.AnchorId, await events.GetMaxSequenceAsync());
        var matches = await cache.GetAsync(key, async () => await status.NewestAnchorStateAsync() is { } state ? state.StateMatches : false);
        var anchor = new Dictionary<string, object?> { ["created_at"] = newest.CreatedAt, ["confirmed"] = matches };
        if (matches is null)
        {
            anchor["id"] = newest.AnchorId;
            anchor["author"] = newest.AuthorNodeId;
            anchor["note"] = RecoveryStatusService.CheckedByARestoringNode;
        }
        b.Set("anchor", anchor);
    }
}

/// <summary>The last anchor verdict, by (anchor, event log head).</summary>
public sealed class RecoveryAnchorStatusCache
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ((string AnchorId, long Head) Key, bool? Matches)? _last;

    public async Task<bool?> GetAsync((string AnchorId, long Head) key, Func<Task<bool?>> compute)
    {
        await _gate.WaitAsync();
        try
        {
            if (_last is { } l && l.Key == key) return l.Matches;
            var matches = await compute();
            _last = (key, matches);
            return matches;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary><c>boxes{count, strong, device, current_key}</c> of /api/blind/status (CONTRACTS §5).</summary>
public sealed class RecoveryBoxesStatusContributor(RecoveryStatusService status) : IBlindStatusContributor
{
    public async Task ContributeAsync(BlindStatusBuilder b, CancellationToken ct)
    {
        var s = await status.GetAsync(slotId: null);
        b.Set("boxes", new Dictionary<string, object?>
        {
            ["count"] = s.ActiveBoxes,
            ["strong"] = s.StrongBoxes,
            ["device"] = s.DeviceBoxes,
            ["current_key"] = new Dictionary<string, object?>
            {
                ["fingerprint"] = s.CurrentKeyFingerprint,
                ["covered"] = s.CurrentKeyCovered,
                ["strong"] = s.CurrentKeyHasStrongBox,
            },
        });
    }
}

/// <summary>The blind node's backup asks this for the recovery set it writes next to the restic repository.</summary>
public sealed class RecoverySetSource(IServiceScopeFactory scopes) : IRecoverySetSource
{
    public async Task<string?> BuildAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RecoverySetBuilder>().BuildJsonAsync();
    }
}
