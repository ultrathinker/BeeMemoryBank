using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.ViewModels;

/// <summary>
/// The tray / menu-bar tooltip, which sums up all the accounts in one line ("3 copies - 1 paused"). Worked out from the same <see cref="BlindAccountsView"/> the window shows (its cards
/// say which account is paused or needs a person), so the tooltip and the window never disagree. A running long job leads the line ("First load: 42%").
/// </summary>
public static class AccountsTooltip
{
    public const string Name = "Bee Memory Bank - blind copy";

    public static string For(BlindAccountsView view, string? jobLine)
    {
        if (view.Problem is not null) return $"{Name}: needs attention - open to see";

        var cards = view.Cards;
        var parts = new List<string>();
        if (jobLine is not null) parts.Add(jobLine);
        if (cards.Count == 0)
        {
            parts.Add("no copies yet - open to add one");
            return $"{Name}: {string.Join(" - ", parts)}";
        }

        parts.Add(cards.Count == 1 ? "1 copy" : $"{cards.Count} copies");
        var paused = cards.Count(c => c.IsPaused);
        if (paused > 0) parts.Add($"{paused} paused");
        var problems = cards.Count(c => c.Kind == BlindCardKind.Problem);
        if (problems > 0) parts.Add(problems == 1 ? "1 needs attention" : $"{problems} need attention");
        return $"{Name}: {string.Join(" - ", parts)}";
    }
}
