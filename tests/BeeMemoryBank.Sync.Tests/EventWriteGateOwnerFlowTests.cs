using System.Text.Json;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Codex security #4: an apply marks its flow as the write gate's owner, so the reseed's replay —
/// which runs while the gate is held — can still write. That mark is a static
/// <see cref="AsyncLocal{T}"/>, and it used to be set and never restored: afterwards every write made
/// on the <i>same</i> flow — and every task the apply spawned — skipped <see cref="EventWriteGate.EnterAsync"/>
/// entirely. A cutover replacing the database file could then have an apply land in the file being
/// replaced, which is the one thing the gate exists to prevent.
///
/// <para>
/// The mark is now scoped to the one apply (<see cref="EventWriteGate.EnterOwnerFlow"/> returns a
/// scope that restores what was there before), and the two writes an apply starts in the background
/// rather than awaiting are detached from its flow, so they do not inherit it. Both halves are pinned
/// here.
/// </para>
///
/// <para>
/// The gate is a process-wide singleton and nothing else in this assembly uses it, so these tests own
/// it while they run — and they are two facts of one class so they cannot run side by side.
/// </para>
/// </summary>
public class EventWriteGateOwnerFlowTests : IAsyncLifetime
{
    private readonly WatchingInitiator _initiator = new();
    private SyncTestFixture _node = null!;

    public async Task InitializeAsync()
    {
        _node = new OwnerFlowFixture(_initiator);
        await _node.InitializeAsync();
    }

    public Task DisposeAsync() => _node.DisposeAsync();

    /// <summary>
    /// One edge of the mark: while the scope is held the flow writes through a held gate (which is why
    /// the mark exists at all — the reseed's replay writes while the gate is held), and once the scope
    /// is given back the same flow waits again.
    /// </summary>
    [Fact]
    public async Task TheOwnerFlowMark_IsScopedToTheApplyThatSetIt()
    {
        using var quiesced = await EventWriteGate.Instance.QuiesceAsync();

        using (EventWriteGate.EnterOwnerFlow())
        {
            using var inside = await EventWriteGate.Instance.EnterAsync();
            // Reaching here at all is the point: a held gate would have blocked the write.
        }

        var after = EventWriteGate.Instance.EnterAsync();

        after.IsCompleted.Should().BeFalse(
            "the mark belongs to the apply that set it — left behind, every later write on that flow " +
            "skips the gate, and can land in a database file a cutover is replacing right now");

        quiesced.Dispose();
        using var lease = await after.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The other edge, through the real path: an apply that auto-accepts a restore starts a task it
    /// does not await (<c>EventApplier.Restore.cs</c>), and that task used to inherit the apply's
    /// owner mark — so the restore's writes skipped the gate for as long as the restore took, across a
    /// cutover that could be replacing the database file right then.
    /// </summary>
    [Fact]
    public async Task TheDetachedAutoAccept_DoesNotInheritTheApplysOwnerMark()
    {
        var evt = await SignedRestoreEventAsync();

        (await _node.EventApplier.ApplyAsync(evt)).Should().Be(EventApplyResult.Applied);
        await _initiator.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The accept is now inside the initiator, waiting to be let go — with the gate held.
        using var quiesced = await EventWriteGate.Instance.QuiesceAsync();
        _initiator.Go.TrySetResult();
        await _initiator.Tried.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // No waiting needed after this: waved through, the gate hands back a finished lease right
        // here; held, it hands back a task that cannot finish until the quiesce is released.
        _initiator.Entered.IsCompleted.Should().BeFalse(
            "a write the apply started in the background is an ordinary writer: inheriting the " +
            "apply's mark, it would go on skipping the gate for the whole restore");

        quiesced.Dispose();
        using var lease = await _initiator.Entered.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>A restore event from a superadmin peer whose row says auto-accept.</summary>
    private async Task<SyncEvent> SignedRestoreEventAsync()
    {
        var peer = Guid.NewGuid();
        var (publicKey, privateKey) = Ed25519Signer.GenerateKeyPair();
        await _node.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = peer,
            DisplayName = "Peer",
            Ed25519PublicKey = publicKey,
            ApiAddress = "http://127.0.0.1",
            Status = "A",
            IsSuperadmin = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _node.WhitelistRepo.SetAutoAcceptRestoreAsync(peer.ToString(), true);

        var evt = new SyncEvent
        {
            EventId = Guid.NewGuid(),
            NodeId = peer,
            LamportTs = 1,
            EventType = EventTypes.RestoreNetwork,
            Payload = JsonSerializer.Serialize(new RestoreNetworkEventPayload(
                "unused", DateTime.UtcNow.ToString("O"), 4096,
                DateTime.UtcNow.AddDays(1).ToString("O"), "not-a-url", FilterSecrets: true)),
            Signature = new byte[64],
            ProtocolVersion = SyncProtocolVersion.Current,
            CreatedAt = DateTime.UtcNow
        };
        // Signed, because the applier verifies an arriving event against the peer's pinned key.
        evt.Signature = Ed25519Signer.Sign(privateKey, EventSignature.BuildPayload(evt));
        return evt;
    }

    /// <summary>
    /// The auto-accepted restore, watched from the inside: it announces that it is running, waits to
    /// be let go, and then tries to write through the gate like any other background work.
    /// </summary>
    private sealed class WatchingInitiator : IRestoreInitiator
    {
        public readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Go = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Set once <see cref="Entered"/> is assigned — never before, so reading it is safe.</summary>
        public readonly TaskCompletionSource Tried = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IDisposable> Entered { get; private set; } = null!;

        public async Task AcceptRestoreAsync(string eventId, RestoreNetworkEventPayload payload, SyncEvent restoreEvent)
        {
            Reached.TrySetResult();
            await Go.Task;
            Entered = EventWriteGate.Instance.EnterAsync();
            Tried.TrySetResult();
            await Entered;
        }

        public Task RetryPendingRestoresAsync() => Task.CompletedTask;
    }

    private sealed class OwnerFlowFixture(WatchingInitiator initiator) : SyncTestFixture
    {
        protected override IRestoreInitiator CreateRestoreInitiator() => initiator;
    }
}