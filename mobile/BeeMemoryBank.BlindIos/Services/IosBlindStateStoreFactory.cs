using System.Collections.Concurrent;
using BeeMemoryBank.BlindDesktop.MacOS;
using BeeMemoryBank.BlindMobile.Services.Blind;

namespace BeeMemoryBank.BlindIos.Services;

/// <summary>
/// Gives each account its own state file: <c>blind-state.json</c> in the account's data folder, written by <see cref="MacOsBlindStateStore"/> exactly as the 2.6 app
/// wrote it. Account 1 (the 2.6 install) is the app's root folder, so its file is the very file it always had - same name, same format, nothing moved or rewritten on
/// upgrade; a new account keeps the same name inside <c>accounts/&lt;id&gt;/</c> (the shared <c>FileBlindStateStoreFactory</c> of AppCore would use another name and another
/// layout, and so could not read a 2.6 install's state). The store of a file is made once and handed out again, because it keeps the values it read in memory: two objects over one file
/// would hold two truths. The contract every head runs (<c>BlindStateStoreContract</c>) is proved over this class.
/// </summary>
public sealed class IosBlindStateStoreFactory(string root) : IBlindStateStoreFactory
{
    private readonly ConcurrentDictionary<string, MacOsBlindStateStore> _stores = new(StringComparer.Ordinal);

    public IBlindStateStore For(BlindAccountEntry account) =>
        _stores.GetOrAdd(PathOf(root, account), path => new MacOsBlindStateStore(path));

    /// <summary>The state file of the account: <c>blind-state.json</c> in the account's data folder (<see cref="BlindAccountNames.DataDirectory"/>).</summary>
    public static string PathOf(string root, BlindAccountEntry account) =>
        Path.Combine(BlindAccountNames.DataDirectory(root, account), MacOsBlindStateStore.FileName);
}
