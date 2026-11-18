using System.Text.Json;
using System.Text.Json.Serialization;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Sync;

/// <summary>Builds, signs via <see cref="INodeAuthSigner"/>, logs, and applies this node's own <c>node_state</c> control event.</summary>
public sealed class NodeStatePublisher(
    INodeIdentityRepository nodeRepo,
    IEventLogRepository eventLogRepo,
    ILamportClock clock,
    INodeAuthSigner authSigner,
    EventApplier applier,
    ISyncTrigger syncTrigger,
    ISyncProtocolPolicy protocolPolicy,
    IEnumerable<INodeStateContributor> contributors)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<NodeStateDraft?> BuildDraftAsync(CancellationToken ct = default)
    {
        var identity = await nodeRepo.GetAsync();
        if (identity is null) return null;
        var draft = new NodeStateDraft();
        foreach (var contributor in contributors.OrderBy(c => c.Order))
            await contributor.ContributeAsync(draft, ct);
        return draft;
    }

    public async Task<SyncEvent> PublishAsync(NodeStateDraft draft, CancellationToken ct = default)
    {
        // An own event is signed and logged inside the event-write gate, so an identity re-key (which quiesces it) never finds one signed by the old
        // key between its list of the node's own events and its commit.
        using var writeGate = await EventWriteGate.Instance.EnterAsync();
        using var ownerFlow = EventWriteGate.EnterOwnerFlow();
        var identity = await nodeRepo.GetAsync() ?? throw new InvalidOperationException("Node is not initialized.");
        var flags = draft.AttentionFlags.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var payload = new NodeStatePayload(
            identity.NodeId.ToString("D"),
            draft.AppVersion,
            NodePlatform.Normalize(draft.Platform),
            NodeRuntimeKinds.Normalize(draft.RuntimeKind),
            draft.IdentityKeyFp,
            BlindNodeId.IsBlind(identity.NodeId) ? null : draft.DekFp,
            flags,
            draft.Extra.Count > 0 ? new Dictionary<string, string>(draft.Extra, StringComparer.Ordinal) : null);

        var payloadJson = JsonSerializer.Serialize(payload, JsonOpts);
        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(),
            NodeId = identity.NodeId,
            LamportTs = clock.Tick(),
            EventType = EventTypes.NodeState,
            EntityId = EventEntityId.Derive(EventTypes.NodeState, null, payloadJson),
            Payload = payloadJson,
            Signature = [],
            ProtocolVersion = protocolPolicy.Declared,
            CreatedAt = DateTime.UtcNow,
            ActorType = "system",
            ActorName = identity.DisplayName,
        };
        evt.Signature = authSigner.SignChallenge(identity, EventSignature.BuildPayload(evt));

        if (EventInvariants.Violation(evt) is { } violation)
            throw new InvalidOperationException(violation);

        await eventLogRepo.AppendAsync(evt);
        await applier.ApplyOwnNodeStateAsync(evt);
        syncTrigger.Signal();
        return evt;
    }
}

/// <summary>
/// Periodically (and on <see cref="RequestImmediateCheck"/>, such as <see cref="VaultGate"/> open) emits this node's
/// <c>node_state</c> event whenever any contributed field changes or after <see cref="KeepAlive"/>.
/// </summary>
public sealed class NodeStateAnnouncer(
    IServiceScopeFactory scopes,
    ILogger<NodeStateAnnouncer> logger,
    IVaultGate? gate = null,
    IHostEnvironment? environment = null) : BackgroundService
{
    public static TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(15);
    public static TimeSpan Debounce { get; set; } = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan KeepAlive = TimeSpan.FromDays(7);

    /// <summary>Stops the background loop from emitting spontaneous announcements (e.g. under <c>Testing</c>).</summary>
    public bool PauseBackground { get; set; } = environment?.IsEnvironment("Testing") == true;

    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private readonly object _stateLock = new();
    private string? _pending;
    private DateTime _pendingSince;
    private volatile bool _immediateRequested;

    public void RequestImmediateCheck()
    {
        _immediateRequested = true;
        if (_wake.CurrentCount == 0)
        {
            try { _wake.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await VaultGateWait.WaitUsableAsync(gate, stoppingToken);
                if (!PauseBackground)
                    await TickAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (VaultSealedException) { }
            catch (SessionLockedException) { }
            catch (Exception ex) { logger.LogWarning(ex, "Announcing this node's state failed; retrying shortly"); }

            try { await _wake.WaitAsync(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Checks whether this node's state changed since its last published <c>node_state</c>, and emits when ready.</summary>
    public async Task<bool> TickAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        if (gate is not null && !gate.IsUsable) return false;
        await _tickGate.WaitAsync(ct);
        try
        {
            return await TickCoreAsync(nowUtc, ct);
        }
        finally
        {
            _tickGate.Release();
        }
    }

    private async Task<bool> TickCoreAsync(DateTime nowUtc, CancellationToken ct)
    {
        if (gate is not null && !gate.IsUsable) return false;

        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var nodeRepo = sp.GetRequiredService<INodeIdentityRepository>();
        if (await nodeRepo.GetAsync() is not { } identity) return false;

        var publisher = sp.GetRequiredService<NodeStatePublisher>();
        var draft = await publisher.BuildDraftAsync(ct);
        if (draft is null) return false;

        var store = sp.GetRequiredService<NodeStateStore>();
        var current = await store.GetAsync(identity.NodeId);
        var wanted = draft.ComputeDigest();
        var announced = current is { LamportTs: > 0 } ? NodeStateDraft.DigestOf(current) : null;

        lock (_stateLock)
        {
            if (wanted == announced)
            {
                _pending = null;
                _immediateRequested = false;
                if (current is not null && nowUtc - current.UpdatedAt < KeepAlive)
                    return false;
            }
            else if (!_immediateRequested && current is not null)
            {
                if (_pending != wanted)
                {
                    _pending = wanted;
                    _pendingSince = nowUtc;
                    return false;
                }
                if (nowUtc - _pendingSince < Debounce) return false;
            }
            _pending = null;
            _immediateRequested = false;
        }

        try
        {
            await publisher.PublishAsync(draft, ct);
            return true;
        }
        catch (SessionLockedException)
        {
            return false;
        }
        catch (VaultSealedException)
        {
            return false;
        }
    }
}

/// <summary>Open step (after migrations): nudges <see cref="NodeStateAnnouncer"/> as soon as a sealed vault unlocks/opens.</summary>
public sealed class NodeStateOpenStep(NodeStateAnnouncer announcer) : IVaultOpenStep
{
    public int Order => 650;

    public Task RunAsync(bool firstOpenInProcess, CancellationToken ct)
    {
        announcer.RequestImmediateCheck();
        return Task.CompletedTask;
    }
}
