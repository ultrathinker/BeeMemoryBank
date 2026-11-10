using BeeMemoryBank.Api.Dream;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using static BeeMemoryBank.Integration.Tests.Dream.TreeTestContext;

namespace BeeMemoryBank.Integration.Tests.Dream;

/// <summary>
/// tree_review on a real node (package M2, fix round): the task the container makes is bound to the node's examined store,
/// and the seven-day limit holds against the real dream_examined table. The unit tests in TreeReviewTaskTests use a lookup
/// of their own; this is the production wiring.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class DreamTreeReviewWiringTests : IAsyncLifetime
{
    private const string Password = "dreamTreeWiringPw1!";
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly DreamEngineTestNode _node = new(keepRealServices: true);

    public async Task InitializeAsync()
    {
        await _node.InitializeNodeAsync(password: Password);
        (await _node.Services.GetRequiredService<SessionService>().UnlockAsync(Password)).Should().BeTrue();
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _node.Dispose();
        return Task.CompletedTask;
    }

    private static TreeTestCorpus Tree()
    {
        var c = new TreeTestCorpus();
        c.AddNotes("/Work/Alpha", 6, "Alpha");
        c.AddNotes("/Work/Beta", 4, "Beta");
        c.AddNotes("/Personal/Recipes", 3, "Recipe");
        c.AddNotes("/Archive", 1, "Old");
        return c;
    }

    [Fact]
    public async Task TheTaskTheContainerMakes_AsksTheNodesStore_AndTheSevenDayLimitHoldsAgainstTheRealTable()
    {
        using var scope = _node.Services.CreateScope();
        var task = scope.ServiceProvider.GetServices<IDreamModelTask>().OfType<TreeReviewTask>().Single();
        var store = scope.ServiceProvider.GetRequiredService<DreamStore>();
        task.Examined.Should().BeSameAs(store, "the dependency is the scope's own store, not an optional extra");

        var corpus = Tree();
        async Task<int> OfferedAt(DateTimeOffset when, DreamRunMode mode = DreamRunMode.Normal) =>
            (await ToList(task.CandidatesAsync(corpus, Make(mode: mode, clock: new TreeTestClock(when)), 1, CancellationToken.None))).Count;

        (await OfferedAt(Now)).Should().Be(1, "nothing was ever examined");

        await store.RecordExaminedAsync("some-tree-key", DreamNames.TreeReviewTask, "marker", "test/model", DreamNames.TreePromptVersion,
            DreamExaminedVerdict.Advice, Guid.NewGuid(), Now.UtcDateTime);

        (await OfferedAt(Now.AddDays(3))).Should().Be(0, "looked at three days ago");
        (await OfferedAt(Now.AddDays(6.9))).Should().Be(0);
        (await OfferedAt(Now.AddDays(7.1))).Should().Be(1);
        (await OfferedAt(Now.AddDays(3), DreamRunMode.Trial)).Should().Be(1, "a trial run does not read the registry");
    }

    [Fact]
    public async Task AnotherTasksLook_DoesNotHoldTheTreeBack()
    {
        using var scope = _node.Services.CreateScope();
        var task = scope.ServiceProvider.GetServices<IDreamModelTask>().OfType<TreeReviewTask>().Single();
        var store = scope.ServiceProvider.GetRequiredService<DreamStore>();
        await store.RecordExaminedAsync("a-pair-key", DreamNames.PairVerdictTask, "marker", "test/model", DreamNames.PairPromptVersion,
            DreamExaminedVerdict.Nothing, Guid.NewGuid(), Now.UtcDateTime);

        var list = await ToList(task.CandidatesAsync(Tree(), Make(clock: new TreeTestClock(Now.AddDays(1))), 1, CancellationToken.None));

        list.Should().ContainSingle();
    }
}
