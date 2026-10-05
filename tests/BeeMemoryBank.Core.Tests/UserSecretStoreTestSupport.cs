using System.Security.Cryptography;
using BeeMemoryBank.Infrastructure.Secrets;
using BeeMemoryBank.Platforms.Apple.Keychain;
using BeeMemoryBank.Platforms.Apple.TestSupport;

namespace BeeMemoryBank.Core.Tests;

/// <summary>A test that needs a real Mac (Security.framework). Skipped, not failed, everywhere else.</summary>
public sealed class MacOnlyFactAttribute : FactAttribute
{
    public MacOnlyFactAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "needs macOS";
    }
}

/// <summary>A theory that needs a real Mac (Security.framework). Skipped, not failed, everywhere else.</summary>
public sealed class MacOnlyTheoryAttribute : TheoryAttribute
{
    public MacOnlyTheoryAttribute()
    {
        if (!OperatingSystem.IsMacOS()) Skip = "needs macOS";
    }
}

/// <summary>A test of the "this is not a Mac" behavior. Skipped on a Mac.</summary>
public sealed class NotMacFactAttribute : FactAttribute
{
    public NotMacFactAttribute()
    {
        if (OperatingSystem.IsMacOS()) Skip = "needs an operating system other than macOS";
    }
}

/// <summary>
/// An in-memory Keychain: the four operations of <see cref="IKeychainBackend"/> with the real statuses (duplicate on a second add, not found
/// on update/copy/delete of a missing item), able to answer any scripted status instead of acting, to record every array it was given or
/// handed out (so a test can prove the store zeroed them), and to let a test tamper with items the way a person with the Keychain Access
/// app could.
/// </summary>
internal sealed class FakeKeychainBackend : IKeychainBackend, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Service, string Account), byte[]> _items = new();

    /// <summary>When set, the operation answers this status and changes nothing.</summary>
    public int? AddStatus { get; set; }
    public int? UpdateStatus { get; set; }
    public int? CopyStatus { get; set; }
    public int? DeleteStatus { get; set; }
    /// <summary>Copy answers Success but gives no data (a status/data mismatch the store must not trust).</summary>
    public bool CopyGivesNoData { get; set; }
    /// <summary>Runs at the start of every Update, before it looks for the item: another process doing something in between.</summary>
    public Action<string, string>? BeforeUpdate { get; set; }
    /// <summary>Throws instead of answering (a native library that cannot be loaded).</summary>
    public Exception? ThrowOnEveryCall { get; set; }

    public List<string> Calls { get; } = [];
    public List<string> Labels { get; } = [];
    /// <summary>The very arrays the store passed to Add and Update.</summary>
    public List<byte[]> PassedIn { get; } = [];
    /// <summary>The very arrays Copy handed to the store.</summary>
    public List<byte[]> HandedOut { get; } = [];
    public bool Disposed { get; private set; }

    public IReadOnlyList<(string Service, string Account)> Keys
    {
        get { lock (_gate) return _items.Keys.ToList(); }
    }

    public int Add(string service, string account, string label, byte[] data)
    {
        Record($"add {service} {account}");
        lock (_gate)
        {
            PassedIn.Add(data);
            Labels.Add(label);
            if (AddStatus is { } status) return status;
            if (_items.ContainsKey((service, account))) return KeychainStatus.DuplicateItem;
            _items[(service, account)] = data.ToArray();
            return KeychainStatus.Success;
        }
    }

    public int Update(string service, string account, byte[] data)
    {
        Record($"update {service} {account}");
        BeforeUpdate?.Invoke(service, account);
        lock (_gate)
        {
            PassedIn.Add(data);
            if (UpdateStatus is { } status) return status;
            if (!_items.ContainsKey((service, account))) return KeychainStatus.ItemNotFound;
            _items[(service, account)] = data.ToArray();
            return KeychainStatus.Success;
        }
    }

    public int Copy(string service, string account, out byte[]? data)
    {
        Record($"copy {service} {account}");
        data = null;
        lock (_gate)
        {
            if (CopyStatus is { } status) return status;
            if (CopyGivesNoData) return KeychainStatus.Success;
            if (!_items.TryGetValue((service, account), out var item)) return KeychainStatus.ItemNotFound;
            data = item.ToArray();
            HandedOut.Add(data);
            return KeychainStatus.Success;
        }
    }

    public int Delete(string service, string account)
    {
        Record($"delete {service} {account}");
        lock (_gate)
        {
            if (DeleteStatus is { } status) return status;
            return _items.Remove((service, account)) ? KeychainStatus.Success : KeychainStatus.ItemNotFound;
        }
    }

    public string Describe(int status) => $"fake text for {status}";

    public void Dispose() => Disposed = true;

    public byte[]? GetRaw(string service, string account)
    {
        lock (_gate) return _items.TryGetValue((service, account), out var item) ? item.ToArray() : null;
    }

    public void SetRaw(string service, string account, byte[] item)
    {
        lock (_gate) _items[(service, account)] = item.ToArray();
    }

    public void RemoveRaw(string service, string account)
    {
        lock (_gate) _items.Remove((service, account));
    }

    private void Record(string call)
    {
        lock (_gate) Calls.Add(call);
        if (ThrowOnEveryCall is not null) throw ThrowOnEveryCall;
    }
}

/// <summary>
/// One store under the shared behavioral suite (<see cref="UserSecretStoreContract"/>), with what the suite needs besides the store: a
/// second store instance over the same data (what another process or another service object would be) and a way to tamper with the raw
/// stored item, for the stores that keep an envelope.
/// </summary>
internal interface IStoreHarness : IDisposable
{
    IUserSecretStore Store { get; }

    /// <summary>Another store object over the very same underlying data.</summary>
    IUserSecretStore OpenAnother();

    /// <summary>Whether the store keeps a raw item that <see cref="Transplant"/> can copy under another name.</summary>
    bool CanTransplant { get; }

    /// <summary>Copies the raw stored item of (fromPurpose, fromAccount) under (toPurpose, toAccount), bypassing the store.</summary>
    void Transplant(string fromPurpose, string fromAccount, string toPurpose, string toAccount);
}

internal sealed class InMemoryStoreHarness : IStoreHarness
{
    private readonly InMemoryUserSecretStore _store = new();

    public IUserSecretStore Store => _store;
    public IUserSecretStore OpenAnother() => _store;
    public bool CanTransplant => false;
    public void Transplant(string fromPurpose, string fromAccount, string toPurpose, string toAccount) =>
        throw new NotSupportedException();
    public void Dispose() => _store.DisposeValues();
}

internal sealed class FakeKeychainStoreHarness : IStoreHarness
{
    public const string Service = "test.bmb.desktop.secrets.fake";

    public FakeKeychainBackend Backend { get; } = new();
    private readonly MacOsKeychainUserSecretStore _store;

    public FakeKeychainStoreHarness(string? scope = null)
    {
        Scope = scope;
        _store = Open();
    }

    public string? Scope { get; }
    public IUserSecretStore Store => _store;
    public IUserSecretStore OpenAnother() => Open();
    public bool CanTransplant => true;

    public void Transplant(string fromPurpose, string fromAccount, string toPurpose, string toAccount)
    {
        var item = Backend.GetRaw(Service, KeychainAccount(fromPurpose, fromAccount))
            ?? throw new InvalidOperationException("nothing to transplant");
        Backend.SetRaw(Service, KeychainAccount(toPurpose, toAccount), item);
    }

    public string KeychainAccount(string purpose, string account) =>
        Scope is null ? purpose + "/" + account : Scope + "/" + purpose + "/" + account;

    private MacOsKeychainUserSecretStore Open() => new(Backend, Service, Scope, isSupported: true);

    public void Dispose() => _store.Dispose();
}

/// <summary>The store over a real Keychain FILE made for one test (never the login keychain). macOS only.</summary>
internal sealed class RealKeychainStoreHarness : IStoreHarness
{
    private readonly ThrowawayKeychain _keychain = new();
    private readonly List<MacOsKeychainUserSecretStore> _opened = [];
    private readonly SecurityFrameworkKeychain _raw;

    public RealKeychainStoreHarness(string? scope = null)
    {
        Scope = scope;
        Service = "test.bmb.desktop.secrets." + Guid.NewGuid().ToString("N")[..12];
        _raw = new SecurityFrameworkKeychain(_keychain.Path, allowUserInteraction: false);
        Store = OpenStore();
    }

    public string Service { get; }
    public string? Scope { get; }
    public ThrowawayKeychain Keychain => _keychain;
    public SecurityFrameworkKeychain Raw => _raw;
    public IUserSecretStore Store { get; }
    public bool CanTransplant => true;

    public IUserSecretStore OpenAnother() => OpenStore();

    public string KeychainAccount(string purpose, string account) =>
        Scope is null ? purpose + "/" + account : Scope + "/" + purpose + "/" + account;

    public void Transplant(string fromPurpose, string fromAccount, string toPurpose, string toAccount)
    {
        _raw.Copy(Service, KeychainAccount(fromPurpose, fromAccount), out var item).Should().Be(KeychainStatus.Success);
        _raw.Delete(Service, KeychainAccount(toPurpose, toAccount));
        _raw.Add(Service, KeychainAccount(toPurpose, toAccount), "transplanted", item!).Should().Be(KeychainStatus.Success);
        CryptographicOperations.ZeroMemory(item);
    }

    public MacOsKeychainUserSecretStore OpenStore(string? scope = null, string? service = null)
    {
        var store = MacOsKeychainUserSecretStore.ForKeychainFile(_keychain.Path, service ?? Service, scope ?? Scope);
        lock (_opened) _opened.Add(store);
        return store;
    }

    /// <summary>A store on this keychain file scoped to the vault in <paramref name="dataPath"/> the way production scopes it (the id kept in that folder).</summary>
    public MacOsKeychainUserSecretStore OpenStoreForDataPath(string dataPath, string? service = null)
    {
        var store = MacOsKeychainUserSecretStore.ForKeychainFileAndDataPath(_keychain.Path, dataPath, service ?? Service);
        lock (_opened) _opened.Add(store);
        return store;
    }

    public void Dispose()
    {
        lock (_opened) foreach (var store in _opened) store.Dispose();
        _raw.Dispose();
        _keychain.Dispose();
    }
}

/// <summary>
/// Data folders made for one test, all under one root of its own in the temp folder, removed with the test (only that root). The helpers a
/// scope test needs: a plain folder, a copy of a folder (what moving or backing up a vault gives), a symbolic-link alias of a folder, and
/// whether the file system under the root tells names that differ only by case apart.
/// </summary>
internal sealed class TempDataFolders : IDisposable
{
    public TempDataFolders()
    {
        Root = Path.Combine(Path.GetTempPath(), "bmb-scope-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Make(string? name = null)
    {
        var dir = Path.Combine(Root, name ?? "vault-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public string CopyOf(string source)
    {
        var target = Path.Combine(Root, "copy-" + Guid.NewGuid().ToString("N")[..8]);
        CopyDirectory(source, target);
        return target;
    }

    /// <summary>A symbolic link to <paramref name="target"/>, or null where this process cannot make one (Windows without the privilege).</summary>
    public string? TryLinkTo(string target)
    {
        var link = Path.Combine(Root, "alias-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return Directory.Exists(link) ? link : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    public bool IsCaseSensitive()
    {
        var probe = Make("CaseProbe");
        File.WriteAllText(Path.Combine(probe, "a"), "1");
        return !File.Exists(Path.Combine(probe, "A"));
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source)) CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}
