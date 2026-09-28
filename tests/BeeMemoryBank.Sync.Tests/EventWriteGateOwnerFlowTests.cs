using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;

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
/// scope that restores what was there before), so the flow that merely <i>called</i> the apply is an
/// ordinary writer again — the state this test pins.
/// </para>
/// </summary>
public class EventWriteGateOwnerFlowTests : IAsyncLifetime
{
    private SyncTestFixture _node = null!;

    public async Task InitializeAsync()
    {
        _node = new ConcreteFixture();
        await _node.InitializeAsync();
    }

    public Task DisposeAsync() => _node.DisposeAsync();

    /// <summary>
    /// The mark is a privilege for the duration of one apply, and this pins both of its edges: while
    /// it is held the flow writes through a cutover (which is why it exists at all — the reseed's
    /// replay writes while the gate is held), and once the apply is done the flow waits again.
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

    private sealed class ConcreteFixture : SyncTestFixture { }
}