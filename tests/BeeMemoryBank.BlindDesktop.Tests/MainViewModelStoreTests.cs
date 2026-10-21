using BeeMemoryBank.BlindDesktop.ViewModels;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>
/// The store line of each account's panel (docs/design/step-a-spec.md 10.2) comes from THAT account's copy: the window hands the panel a function bound to the account's id, so two accounts
/// never show each other's restore point or size. (The line's own wording is <see cref="AccountPanelStoreTests"/>.)
/// </summary>
public sealed class MainViewModelStoreTests
{
    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        condition().Should().BeTrue("the condition should have become true");
    }

    [Fact]
    public async Task EachAccountsPanel_AsksForItsOwnCopy()
    {
        var accounts = new FakeAccounts();
        var (first, _) = accounts.AddAccount("Berlin office");
        var (second, _) = accounts.AddAccount("Paris");
        var asked = new List<string>();
        var vm = new MainViewModel(accounts, _ => new FakeWorkRequests(), new FakeAutostart(), _ => [], zone: TimeZoneInfo.Utc,
            store: (id, _) =>
            {
                lock (asked) asked.Add(id);
                return Task.FromResult<BlindStoreInfo?>(new BlindStoreInfo(DateTime.UtcNow - TimeSpan.FromDays(2), id == first.Id ? 7 : 3, id == first.Id ? 2 << 20 : 5 << 20));
            });

        await vm.RefreshAsync();
        await WaitFor(() => vm.Detail!.Panel!.StoreText.Contains("Changes since: 7", StringComparison.Ordinal));
        lock (asked) asked.Should().OnlyContain(id => id == first.Id);

        vm.SelectedRow = vm.Rows[1];
        await WaitFor(() => vm.Detail?.Id == second.Id);
        await WaitFor(() => { lock (asked) return asked.Contains(second.Id); });
        await WaitFor(() => vm.Detail!.Panel!.StoreText.Contains("Changes since: 3", StringComparison.Ordinal));
        vm.Dispose();
    }
}
