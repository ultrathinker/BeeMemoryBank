using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindDesktop.Windows;

/// <summary>
/// Gives each account its own DPAPI secrets, under the NAMING CONTRACT (<see cref="BlindAccountNames"/>): the legacy account (the 2.6 install) keeps
/// <c>&lt;root&gt;\secrets\*.dpapi</c> and the entropy <c>BeeMemoryBankBlind/v1/&lt;role&gt;</c> unchanged; any other account has <c>&lt;root&gt;\accounts\&lt;id&gt;\secrets\*.dpapi</c>
/// and the entropy <c>BeeMemoryBankBlind/v1/&lt;id&gt;/&lt;role&gt;</c>, so a blob copied into another account's slot does not open there. Removing an account clears
/// its blobs (<see cref="IBlindSecretStore.Clear"/>) and the empty folder with them; the account host then deletes the account's folder.
/// </summary>
public sealed class WindowsSecretStoreFactory(string root) : IBlindSecretStoreFactory, IBlindSecretBlobAccess
{
    public IBlindSecretStore For(BlindAccountEntry account) => Store(account);

    /// <summary>The account's <c>secrets</c> folder.</summary>
    public string SecretsDirectory(BlindAccountEntry account) => Path.Combine(BlindAccountNames.DataDirectory(root, account), "secrets");

    public byte[]? ReadBlob(BlindAccountEntry account, BlindAccountNames.Role role) => Store(account).ReadBlob(role);

    public void WriteBlob(BlindAccountEntry account, BlindAccountNames.Role role, byte[] blob) => Store(account).WriteBlob(role, blob);

    private DpapiSecretStore Store(BlindAccountEntry account) => new(SecretsDirectory(account), account);
}

/// <summary>
/// Gives each account its own state file, <c>state.json</c> in the account's folder (the root itself for the legacy account: the file the 2.6 app has always written),
/// through the same atomic, damage-tolerant store as before. One store object per file for as long as anyone holds it: the store keeps the file's content in memory, and two objects
/// over one file would each hold their own copy. The factory holds the objects WEAKLY: the account's runtime owns its store, and when the runtime is gone (the account was removed) so is the
/// store, instead of one object for every account that was ever made staying for the life of the process. Whoever asks again after that gets a new object that reads the file afresh.
/// </summary>
public sealed class WindowsStateStoreFactory(string root) : IBlindStateStoreFactory
{
    /// <summary>The dead entries are swept out of the table when it holds this many (a device has a handful of accounts).</summary>
    private const int SweepAbove = 32;

    private readonly object _gate = new();
    private readonly Dictionary<string, WeakReference<AtomicJsonStateStore>> _stores = new(StringComparer.OrdinalIgnoreCase);

    public IBlindStateStore For(BlindAccountEntry account)
    {
        var path = StatePath(account);
        lock (_gate)
        {
            if (_stores.TryGetValue(path, out var held) && held.TryGetTarget(out var live)) return live;
            if (_stores.Count >= SweepAbove)
                foreach (var gone in _stores.Where(pair => !pair.Value.TryGetTarget(out _)).Select(pair => pair.Key).ToList()) _stores.Remove(gone);
            var made = new AtomicJsonStateStore(path);
            _stores[path] = new WeakReference<AtomicJsonStateStore>(made);
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
        Path.GetFullPath(Path.Combine(BlindAccountNames.DataDirectory(root, account), BlindAccountNames.StateFile));
}
