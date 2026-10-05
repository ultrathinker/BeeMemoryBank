using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Platforms.Apple.Keychain;

namespace BeeMemoryBank.Infrastructure.Secrets;

/// <summary>
/// The full macOS app's <see cref="IUserSecretStore"/>: one Keychain generic-password item per secret, over the neutral Apple platform
/// layer (<see cref="IKeychainBackend"/>; the real backend is Security.framework's SecItem calls). Service
/// <see cref="DefaultService"/>, Keychain account <c>&lt;purpose&gt;/&lt;account&gt;</c>, or <c>&lt;scope&gt;/&lt;purpose&gt;/&lt;account&gt;</c>
/// when the store is scoped to one vault. The factory (<see cref="UserSecretStores.CreateDefault"/>) always scopes, with the random id kept
/// in the data folder itself (<see cref="ScopeForDataPath"/>, file <see cref="ScopeFileName"/>): two vaults on one Mac never share an item
/// the way two data folders never share a DPAPI file on Windows, and the id travels with the folder, so renaming, moving or copying the
/// folder, or reaching it through a symbolic link, keeps its secrets reachable. The id is read or created at the first use of the store,
/// never when it is constructed.
///
/// <para>Each item holds <c>version(1) | SHA-256(domain | scope | purpose | account | data)(32) | data</c>. That is an integrity and binding
/// check, not authentication: an item that was edited, truncated, replaced by a plain string, or copied from another purpose, account or
/// scope fails loudly as <see cref="UserSecretStoreFailureKind.Malformed"/> and is never returned as a secret. Who may read an item at all
/// is the Keychain's access list: the item is created by the calling process, so only that application (as signed) reads it without a
/// prompt - what happens across a re-signed or replaced binary is the operating system's decision and is not verified by this class.</para>
///
/// <para>Failure contract (the same as <see cref="WindowsDpapiUserSecretStore"/>): <see cref="Read"/> returns <c>null</c> ONLY for the real
/// "item not found" status; a refusal that is not "not found" (locked Keychain, a prompt that may not be shown, user canceled) is
/// <see cref="UserSecretStoreFailureKind.Locked"/> or <see cref="UserSecretStoreFailureKind.Denied"/>, a damaged envelope is
/// <see cref="UserSecretStoreFailureKind.Malformed"/>, anything else is <see cref="UserSecretStoreFailureKind.Unavailable"/>. Reporting a
/// transient refusal as "no secret" would let a caller mint a replacement key over a secret that still exists. The same rule covers the
/// scope id: a damaged scope file is <c>Malformed</c> and an unreadable or unwritable data folder is <c>Unavailable</c>; a scope is never
/// regenerated over an existing file, because a new id would orphan every secret kept under the old one.</para>
///
/// <para>No secret, no key and no account name is ever put into an exception message; the message carries the action and the numeric
/// OSStatus (with the system's own text for it). Every buffer made here is zeroed after use.</para>
/// </summary>
public sealed class MacOsKeychainUserSecretStore : IUserSecretStore, IDisposable
{
    /// <summary>The Keychain service name of the full app's secrets (never the blind app's).</summary>
    public const string DefaultService = "com.beememorybank.desktop.secrets.v1";

    /// <summary>
    /// The file in a data folder that holds the folder's scope id: 16 lowercase hexadecimal characters (8 random bytes), nothing else. It
    /// moves with the folder. Anything that rebuilds a data folder has to carry it over (the re-key swap does).
    /// </summary>
    public const string ScopeFileName = ".secret-scope";

    private const int ScopeLength = 16;
    private const byte EnvelopeVersion = 1;
    private const int DigestLength = 32;
    private static readonly byte[] Domain = "bmb-desktop-keychain-item-v1\0"u8.ToArray();

    /// <summary>How long a reader waits for a scope file that another process has created but not finished writing.</summary>
    private static readonly TimeSpan DefaultScopePatience = TimeSpan.FromSeconds(2);

    private readonly IKeychainBackend? _backend;
    private readonly bool _ownsBackend;
    private readonly string _service;
    private readonly string? _fixedScope;
    private readonly Func<string>? _scopeProvider;
    private readonly bool _isSupported;
    /// <summary>
    /// Every Keychain call of this process goes through this one lock, whichever store object, backend or keychain it is for. Several
    /// threads writing ONE item through several keychain references of the legacy file-keychain API failed once in a stress run on macOS 26.5
    /// (a write answered errSecInvalidRecord -67701, and a read that followed the same thread's own write did not return an intact value);
    /// it did not recur in about 20 000 further write/read pairs, also in parallel runs under CPU load, so the cause is not established - serializing the
    /// process's calls costs nothing that matters and removes the in-process share of the risk. Other PROCESSES of the app (the Api, bmbd,
    /// the Web app) cannot be serialized this way.
    /// </summary>
    private static readonly object ProcessGate = new();
    private readonly object _scopeGate = new();
    private string? _resolvedScope;

    /// <summary>The seam: any backend (a fake in tests, a throwaway-keychain backend). The caller keeps ownership of the backend.</summary>
    /// <param name="backend">The Keychain operations.</param>
    /// <param name="service">The Keychain service name; tests override it so they never share items with a real install.</param>
    /// <param name="scope">An explicit scope (16-hex ids are what <see cref="ScopeForDataPath"/> gives; tests pass any valid name), or null for none.</param>
    /// <param name="isSupported">Overrides <see cref="IsSupported"/>; by default it is whether this process runs on macOS.</param>
    public MacOsKeychainUserSecretStore(IKeychainBackend backend, string service = DefaultService, string? scope = null, bool? isSupported = null)
        : this(backend ?? throw new ArgumentNullException(nameof(backend)), service, scope, scopeProvider: null, isSupported, ownsBackend: false) { }

    private MacOsKeychainUserSecretStore(IKeychainBackend? backend, string service, string? scope, Func<string>? scopeProvider, bool? isSupported, bool ownsBackend)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        if (scope is not null) ValidateScope(scope);
        _backend = backend;
        _ownsBackend = ownsBackend;
        _service = service;
        _fixedScope = scope;
        _scopeProvider = scopeProvider;
        _isSupported = isSupported ?? OperatingSystem.IsMacOS();
    }

    /// <summary>
    /// The user's default (login) Keychain, with an explicit scope or none: production code uses <see cref="ForDataPath"/>. Off macOS the
    /// store is created unsupported (every operation says "unavailable"), it never throws at construction.
    /// </summary>
    public static MacOsKeychainUserSecretStore ForDefaultKeychain(string? scope = null, string service = DefaultService) =>
        new(OperatingSystem.IsMacOS() ? new SecurityFrameworkKeychain() : null, service, scope, scopeProvider: null, isSupported: null, ownsBackend: true);

    /// <summary>
    /// Production: the user's default (login) Keychain, scoped to the vault in <paramref name="dataPath"/>. The scope id is read from (or
    /// created in) the data folder at the first use, so constructing the store cannot fail or touch the disk. Off macOS the store is
    /// created unsupported.
    /// </summary>
    public static MacOsKeychainUserSecretStore ForDataPath(string dataPath, string service = DefaultService) =>
        new(OperatingSystem.IsMacOS() ? new SecurityFrameworkKeychain() : null, service, scope: null,
            scopeProvider: () => ScopeForDataPath(dataPath), isSupported: null, ownsBackend: true);

    /// <summary>
    /// A Keychain FILE: every call is confined to it and user interaction is disabled (a locked file fails at once instead of waiting for a
    /// person). For tests and tools; never the login Keychain. Off macOS the store is created unsupported.
    /// </summary>
    public static MacOsKeychainUserSecretStore ForKeychainFile(string keychainFilePath, string service = DefaultService, string? scope = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keychainFilePath);
        return new(OperatingSystem.IsMacOS() ? new SecurityFrameworkKeychain(keychainFilePath, allowUserInteraction: false) : null,
            service, scope, scopeProvider: null, isSupported: null, ownsBackend: true);
    }

    /// <summary>A Keychain FILE (as <see cref="ForKeychainFile"/>) scoped to the vault in <paramref name="dataPath"/> like <see cref="ForDataPath"/>.</summary>
    public static MacOsKeychainUserSecretStore ForKeychainFileAndDataPath(string keychainFilePath, string dataPath, string service = DefaultService)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keychainFilePath);
        return new(OperatingSystem.IsMacOS() ? new SecurityFrameworkKeychain(keychainFilePath, allowUserInteraction: false) : null,
            service, scope: null, scopeProvider: () => ScopeForDataPath(dataPath), isSupported: null, ownsBackend: true);
    }

    /// <summary>Any backend, scoped to the vault in <paramref name="dataPath"/> like <see cref="ForDataPath"/>. The caller keeps ownership of the backend.</summary>
    public static MacOsKeychainUserSecretStore WithDataPathScope(IKeychainBackend backend, string dataPath, string service = DefaultService, bool? isSupported = null) =>
        new(backend ?? throw new ArgumentNullException(nameof(backend)), service, scope: null,
            scopeProvider: () => ScopeForDataPath(dataPath), isSupported, ownsBackend: false);

    /// <summary>
    /// The scope id of the vault in <paramref name="dataPath"/>: the content of <c>&lt;dataPath&gt;/<see cref="ScopeFileName"/></c>, 16
    /// lowercase hexadecimal characters made from 8 random bytes. A folder without the file gets one, created with
    /// <see cref="FileMode.CreateNew"/> so that two processes or threads starting at once end with ONE id (the loser of the race reads the
    /// winner's file; a reader that finds the file still being written waits for it). The id belongs to the folder, not to its path: a
    /// renamed, moved or copied folder keeps it and so keeps its secrets, a symbolic link to the folder gives the same id, and two folders
    /// differing only by case on a case-sensitive volume have two ids. The Windows store gets this isolation from its per-folder files;
    /// the Keychain has one namespace per user, so without an id two profiles on one Mac would overwrite each other's auto-unlock key, CA
    /// key and update handoff.
    ///
    /// <para>A file with any other content is NOT replaced: that would orphan every secret kept under the old id. It throws
    /// <see cref="UserSecretStoreException"/> <see cref="UserSecretStoreFailureKind.Malformed"/>. A folder that cannot be created, read or
    /// written is <see cref="UserSecretStoreFailureKind.Unavailable"/>. (The only file this method ever deletes is the one it has just
    /// created itself and failed to finish writing.)</para>
    /// </summary>
    public static string ScopeForDataPath(string dataPath) => ScopeForDataPath(dataPath, DefaultScopePatience);

    internal static string ScopeForDataPath(string dataPath, TimeSpan patience)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        string file;
        try
        {
            Directory.CreateDirectory(dataPath);
            file = Path.Combine(dataPath, ScopeFileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw ScopeUnavailable(ex);
        }

        var waited = Stopwatch.StartNew();
        while (true)
        {
            switch (ReadScopeFile(file, out var existing))
            {
                case ScopeFileState.Valid:
                    return existing!;
                case ScopeFileState.Invalid:
                    throw ScopeMalformed();
                case ScopeFileState.Unfinished:
                    // Another process has created the file and is writing it. After the patience runs out it is damaged, not "being written".
                    if (waited.Elapsed >= patience) throw ScopeMalformed();
                    Thread.Sleep(5);
                    continue;
                case ScopeFileState.Busy:
                    if (waited.Elapsed >= patience) throw ScopeUnavailable(null);
                    Thread.Sleep(5);
                    continue;
            }

            // Missing: create it. CreateNew fails if somebody else did it first; then the loop reads theirs.
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(ScopeLength / 2)).ToLowerInvariant();
            FileStream? created = null;
            try
            {
                created = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }
            catch (IOException) when (File.Exists(file))
            {
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw ScopeUnavailable(ex);
            }

            try
            {
                using (created)
                {
                    created.Write(Encoding.ASCII.GetBytes(id));
                    created.Flush(flushToDisk: true);
                }
                return id;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Our own half-written file would read as damaged for ever; it is the one file this method may remove.
                try { File.Delete(file); } catch { /* best effort */ }
                throw ScopeUnavailable(ex);
            }
        }
    }

    private enum ScopeFileState { Missing, Valid, Invalid, Unfinished, Busy }

    private static ScopeFileState ReadScopeFile(string file, out string? id)
    {
        id = null;
        byte[] bytes;
        int length;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bytes = new byte[ScopeLength + 1 + 64];
            length = stream.Read(bytes, 0, bytes.Length);
        }
        catch (FileNotFoundException)
        {
            return ScopeFileState.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return ScopeFileState.Missing;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw ScopeUnavailable(ex);
        }
        catch (IOException)
        {
            // A sharing violation while the creator still has the file open; the loop retries until the patience is over.
            return ScopeFileState.Busy;
        }

        // Empty, or a hexadecimal beginning: the creator may not have finished writing it. Anything else that is not exactly 16 lowercase
        // hexadecimal characters is damaged.
        if (length < ScopeLength && IsLowerHex(bytes.AsSpan(0, length))) return ScopeFileState.Unfinished;
        if (length != ScopeLength || !IsLowerHex(bytes.AsSpan(0, length))) return ScopeFileState.Invalid;
        id = Encoding.ASCII.GetString(bytes, 0, length);
        return ScopeFileState.Valid;
    }

    private static bool IsLowerHex(ReadOnlySpan<byte> text)
    {
        foreach (var b in text)
            if (!(b is >= (byte)'0' and <= (byte)'9' || b is >= (byte)'a' and <= (byte)'f')) return false;
        return true;
    }

    private static UserSecretStoreException ScopeMalformed() =>
        new(UserSecretStoreFailureKind.Malformed,
            "The secret scope file of this data folder is damaged and is not replaced: a new scope would orphan the secrets kept under the old one.");

    private static UserSecretStoreException ScopeUnavailable(Exception? inner) =>
        new(UserSecretStoreFailureKind.Unavailable, "The secret scope of this data folder cannot be read or created.", inner);

    public bool IsSupported => _isSupported;

    public byte[]? Read(string purpose, string account)
    {
        Validate(purpose, account);
        var backend = Require();
        var scope = ResolveScope();
        var keychainAccount = KeychainAccount(scope, purpose, account);
        byte[]? item;
        int status;
        try
        {
            lock (ProcessGate) status = backend.Copy(_service, keychainAccount, out item);
        }
        catch (Exception ex) when (IsNativeLoadFailure(ex))
        {
            throw NativeUnavailable("read", ex);
        }

        if (status == KeychainStatus.ItemNotFound)
        {
            if (item is not null) CryptographicOperations.ZeroMemory(item);
            return null;
        }
        if (status != KeychainStatus.Success || item is null)
        {
            if (item is not null) CryptographicOperations.ZeroMemory(item);
            throw Failure("read", status);
        }
        try
        {
            return Open(scope, purpose, account, item);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(item);
        }
    }

    public void Write(string purpose, string account, ReadOnlySpan<byte> value)
    {
        Validate(purpose, account);
        var backend = Require();
        var scope = ResolveScope();
        var keychainAccount = KeychainAccount(scope, purpose, account);
        var item = Seal(scope, purpose, account, value);
        try
        {
            lock (ProcessGate)
            {
                var label = "Bee Memory Bank: " + purpose;
                var status = backend.Add(_service, keychainAccount, label, item);
                if (status == KeychainStatus.DuplicateItem)
                {
                    // Replace in place: SecItemUpdate swaps the value of the one item, so there is never a half-written item.
                    status = backend.Update(_service, keychainAccount, item);
                    // Another process removed it between the two calls: one more add. A second duplicate is a real failure.
                    if (status == KeychainStatus.ItemNotFound) status = backend.Add(_service, keychainAccount, label, item);
                }
                if (status != KeychainStatus.Success) throw Failure("stored", status);
            }
        }
        catch (Exception ex) when (IsNativeLoadFailure(ex))
        {
            throw NativeUnavailable("stored", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(item);
        }
    }

    public void Delete(string purpose, string account)
    {
        Validate(purpose, account);
        var backend = Require();
        var keychainAccount = KeychainAccount(ResolveScope(), purpose, account);
        int status;
        try
        {
            lock (ProcessGate) status = backend.Delete(_service, keychainAccount);
        }
        catch (Exception ex) when (IsNativeLoadFailure(ex))
        {
            throw NativeUnavailable("deleted", ex);
        }
        if (status is KeychainStatus.Success or KeychainStatus.ItemNotFound) return;
        throw Failure("deleted", status);
    }

    public void Dispose()
    {
        if (_ownsBackend && _backend is IDisposable disposable) disposable.Dispose();
    }

    /// <summary>
    /// The scope of this store: the explicit one, none, or the id of the data folder, read (or created) at the first use. A failure is not
    /// remembered: the next call tries again, so a folder that was unreadable a moment ago is not condemned for the life of the process.
    /// </summary>
    private string? ResolveScope()
    {
        if (_scopeProvider is null) return _fixedScope;
        var known = Volatile.Read(ref _resolvedScope);
        if (known is not null) return known;
        lock (_scopeGate)
        {
            return _resolvedScope ??= _scopeProvider();
        }
    }

    private static string KeychainAccount(string? scope, string purpose, string account) =>
        scope is null ? purpose + "/" + account : scope + "/" + purpose + "/" + account;

    private IKeychainBackend Require() =>
        _isSupported && _backend is not null
            ? _backend
            : throw new UserSecretStoreException(UserSecretStoreFailureKind.Unavailable, "The macOS Keychain is unavailable on this platform.");

    /// <summary>
    /// The status mapping. Locked: the Keychain could not do the call without asking a person (errSecInteractionNotAllowed, and
    /// errSecAuthFailed, which is what a LOCKED file keychain answers on macOS 26.5 when interaction is disabled). Denied: the person (or
    /// the access list) refused (userCanceledErr). Everything else, including "no such keychain", is Unavailable.
    /// </summary>
    private UserSecretStoreException Failure(string action, int status)
    {
        var kind = status switch
        {
            KeychainStatus.InteractionNotAllowed or KeychainStatus.AuthFailed => UserSecretStoreFailureKind.Locked,
            KeychainStatus.UserCanceled => UserSecretStoreFailureKind.Denied,
            _ => UserSecretStoreFailureKind.Unavailable
        };
        var detail = "";
        try { detail = _backend?.Describe(status) ?? ""; }
        catch (Exception ex) when (IsNativeLoadFailure(ex)) { }
        return new UserSecretStoreException(kind,
            $"The user secret cannot be {action} (Keychain status {status}{(detail.Length > 0 ? ": " + detail : "")}).");
    }

    private static bool IsNativeLoadFailure(Exception ex) =>
        ex is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException;

    private static UserSecretStoreException NativeUnavailable(string action, Exception inner) =>
        new(UserSecretStoreFailureKind.Unavailable, $"The user secret cannot be {action}: the macOS Keychain is unavailable.", inner);

    private static byte[] Digest(string? scope, string purpose, string account, ReadOnlySpan<byte> data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Domain);
        AppendName(hash, scope ?? "");
        AppendName(hash, purpose);
        AppendName(hash, account);
        hash.AppendData(data);
        return hash.GetHashAndReset();
    }

    private static void AppendName(IncrementalHash hash, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        try
        {
            hash.AppendData(bytes);
            hash.AppendData(stackalloc byte[] { 0 });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static byte[] Seal(string? scope, string purpose, string account, ReadOnlySpan<byte> secret)
    {
        var item = new byte[1 + DigestLength + secret.Length];
        item[0] = EnvelopeVersion;
        var digest = Digest(scope, purpose, account, secret);
        digest.CopyTo(item, 1);
        CryptographicOperations.ZeroMemory(digest);
        secret.CopyTo(item.AsSpan(1 + DigestLength));
        return item;
    }

    private static byte[] Open(string? scope, string purpose, string account, byte[] item)
    {
        if (item.Length < 1 + DigestLength || item[0] != EnvelopeVersion) throw Malformed();
        var secret = item.AsSpan(1 + DigestLength).ToArray();
        var expected = Digest(scope, purpose, account, secret);
        var intact = CryptographicOperations.FixedTimeEquals(expected, item.AsSpan(1, DigestLength));
        CryptographicOperations.ZeroMemory(expected);
        if (!intact)
        {
            CryptographicOperations.ZeroMemory(secret);
            throw Malformed();
        }
        return secret;
    }

    private static UserSecretStoreException Malformed() =>
        new(UserSecretStoreFailureKind.Malformed, "The user secret was altered outside the app or does not belong to this purpose and is not used.");

    private static void Validate(string purpose, string account)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        // The Keychain account is "purpose/account": a '/' in the purpose would let two different (purpose, account) pairs share one item,
        // and a NUL is the separator of the digest input.
        if (purpose.Contains('/') || purpose.Contains('\0'))
            throw new ArgumentException("The purpose must not contain '/' or NUL.", nameof(purpose));
        if (account.Contains('\0'))
            throw new ArgumentException("The account must not contain NUL.", nameof(account));
    }

    private static void ValidateScope(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        if (scope.Contains('/') || scope.Contains('\0'))
            throw new ArgumentException("The scope must not contain '/' or NUL.", nameof(scope));
    }
}
