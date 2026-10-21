using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.MacOS;

/// <summary>
/// Gives each account its own state file, <c>blind-state.json</c> in the account's folder (the root itself for the legacy account: the file the 2.6 app has always written, so the
/// state of an installed copy is found where it was), through the same atomic, damage-keeping store as before. One store object per file for as long as anyone holds it: the store holds
/// the file's content in memory, and two objects over one file would each have their own copy. The factory holds the objects WEAKLY: the account's runtime owns its store, and when the runtime
/// is gone (the account was removed) so is the store, instead of one object for every account that was ever made staying for the life of the process. Whoever asks again after that gets a new object that
/// reads the file afresh. A store's <see cref="MacOsBlindStateStore.Erase"/> removes only that account's file and its leftovers; nothing else in the folder is touched.
/// </summary>
public sealed class MacOsBlindStateStoreFactory(string root) : IBlindStateStoreFactory
{
    /// <summary>The dead entries are swept out of the table when it holds this many (a device has a handful of accounts).</summary>
    private const int SweepAbove = 32;

    private readonly object _gate = new();
    private readonly Dictionary<string, WeakReference<MacOsBlindStateStore>> _stores = new(StringComparer.Ordinal);

    public IBlindStateStore For(BlindAccountEntry account)
    {
        var path = StatePath(account);
        lock (_gate)
        {
            if (_stores.TryGetValue(path, out var held) && held.TryGetTarget(out var live)) return live;
            if (_stores.Count >= SweepAbove)
                foreach (var gone in _stores.Where(pair => !pair.Value.TryGetTarget(out _)).Select(pair => pair.Key).ToList()) _stores.Remove(gone);
            var made = new MacOsBlindStateStore(path);
            _stores[path] = new WeakReference<MacOsBlindStateStore>(made);
            return made;
        }
    }

    /// <summary>How many files the table has an entry for, live or not yet swept (for the tests).</summary>
    internal int Tracked
    {
        get { lock (_gate) return _stores.Count; }
    }

    /// <summary>The account's state file.</summary>
    public string StatePath(BlindAccountEntry account) =>
        Path.GetFullPath(Path.Combine(BlindAccountNames.DataDirectory(root, account), MacOsBlindStateStore.FileName));
}
