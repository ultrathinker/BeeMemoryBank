using System.Collections.Concurrent;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.Services;

/// <summary>
/// The tray lines of the accounts: one <see cref="TrayNotifications"/> per account (its runtime reports the progress of its own long job to it), and the first line among them for the
/// tooltip. A quiet app does not pop up notices; the tooltip shows what is running ("First load: 42 %").
/// </summary>
public sealed class AccountNotifications
{
    private readonly ConcurrentDictionary<string, TrayNotifications> _byAccount = new(StringComparer.Ordinal);

    /// <summary>The notifications of one account; the same object for the life of the account.</summary>
    public TrayNotifications For(string accountId) => _byAccount.GetOrAdd(accountId, _ => new TrayNotifications());

    /// <summary>The line of an account that runs a long job, or null when none does. With several, the first by account id (a tooltip has room for one).</summary>
    public string? Line => _byAccount.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value.Line).FirstOrDefault(line => line is not null);

    /// <summary>Forgets an account that is gone.</summary>
    public void Forget(string accountId) => _byAccount.TryRemove(accountId, out _);

    /// <summary>
    /// Puts the lines in step with the accounts the host lists now: an account with a long job shows its line, one without clears it (so does one that is being removed: its job is being stopped),
    /// and an account that is not listed any more is forgotten - the line of a job that ended with its account must not stay in the tooltip. The accounts are read here, AFTER the ids this knows
    /// are taken: an account whose runtime asked for its line since then is not among those ids, so a read that was made before it was listed cannot make this forget a live account.
    /// Returns the snapshot it used.
    /// </summary>
    public BlindAccountsSnapshot Reconcile(Func<BlindAccountsSnapshot> read)
    {
        var known = _byAccount.Keys.ToList();
        var snapshot = read();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var account in snapshot.Accounts)
        {
            listed.Add(account.Entry.Id);
            var notifications = For(account.Entry.Id);
            if (account.Entry.Phase != BlindAccountPhases.Deleting && account.Status?.ActiveJob is { } job) notifications.Show(job, account.Status.JobProgress ?? 0);
            else notifications.Clear();
        }
        foreach (var id in known.Where(id => !listed.Contains(id))) Forget(id);
        return snapshot;
    }
}
