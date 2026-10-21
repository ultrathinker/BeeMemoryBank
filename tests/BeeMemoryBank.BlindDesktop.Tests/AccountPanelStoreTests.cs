using BeeMemoryBank.BlindDesktop.ViewModels;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>
/// The Windows and macOS copies' store line (docs/design/step-a-spec.md 10.2): the restore point's age, the changes held since and the bytes stored,
/// asked for off the UI thread, and never a note count.
/// </summary>
public sealed class AccountPanelStoreTests
{
    private static BlindStoreInfo Summary() => new(DateTime.UtcNow - TimeSpan.FromDays(2), ChangesSince: 7, StoredBytes: 2 * 1024 * 1024);

    private static async Task<string> SettledAsync(AccountPanelViewModel vm, Func<string, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done(vm.StoreText) && DateTime.UtcNow < deadline) await Task.Delay(20);
        return vm.StoreText;
    }

    [Fact]
    public async Task BeforeTheFirstLoad_TheLineSaysNoneYet_AndTheSourceIsNotAsked()
    {
        var asked = 0;
        var app = new FakeApp();
        var vm = new AccountPanelViewModel(app, new FakeWorkRequests(), _ => [],
            store: _ => { asked++; return Task.FromResult<BlindStoreInfo?>(Summary()); });
        app.Status = FakeApp.StatusOf(paired: true, loaded: false);

        await vm.RefreshAsync();

        vm.StoreText.Should().Contain("none yet").And.Contain("first load");
        asked.Should().Be(0);
    }

    [Fact]
    public async Task AfterTheFirstLoad_TheLineTellsTheRestorePointTheChangesAndTheBytes_ButNoNotes()
    {
        var app = new FakeApp();
        var vm = new AccountPanelViewModel(app, new FakeWorkRequests(), _ => [],
            store: _ => Task.FromResult<BlindStoreInfo?>(Summary()));
        app.Status = FakeApp.StatusOf(paired: true, loaded: true);

        await vm.RefreshAsync();
        var text = await SettledAsync(vm, t => t.Contains("made"));

        text.Should().Be("Restore point: made 2 d ago. Changes since: 7. Stored: 2 MiB.");
        text.Should().NotContainEquivalentOf("note");
    }

    [Fact]
    public async Task ASourceThatCannotRead_LeavesTheLineUnknown_NotZero()
    {
        var app = new FakeApp();
        var vm = new AccountPanelViewModel(app, new FakeWorkRequests(), _ => [],
            store: _ => Task.FromResult<BlindStoreInfo?>(null));
        app.Status = FakeApp.StatusOf(paired: true, loaded: true);

        await vm.RefreshAsync();
        await Task.Delay(100);

        vm.StoreText.Should().Contain("unknown").And.NotContain("0");
    }

    [Fact]
    public async Task WithoutASource_TheLineIsUnknown()
    {
        var app = new FakeApp();
        var vm = new AccountPanelViewModel(app, new FakeWorkRequests(), _ => []);
        app.Status = FakeApp.StatusOf(paired: true, loaded: true);

        await vm.RefreshAsync();

        vm.StoreText.Should().Contain("unknown");
    }
}
