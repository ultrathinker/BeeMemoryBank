using BeeMemoryBank.Platforms.Apple.Interop;

namespace BeeMemoryBank.Platforms.Apple.Keychain;

/// <summary>The OSStatus values of Security.framework that the secret stores react to.</summary>
public static class KeychainStatus
{
    public const int Success = 0;
    public const int UserCanceled = -128;
    public const int Param = -50;
    public const int AuthFailed = -25293;
    public const int NoSuchKeychain = -25294;
    public const int InvalidKeychain = -25295;
    public const int DuplicateItem = -25299;
    public const int ItemNotFound = -25300;
    public const int InteractionNotAllowed = -25308;
}

/// <summary>
/// The four Keychain operations a secret store needs, on generic-password items identified by (service, account). Returns the OSStatus
/// of the system call: the store decides what each status means. A seam so that a store's logic is tested on any OS with a fake, and the
/// real Security.framework calls are tested on a Mac against a throwaway keychain.
/// </summary>
public interface IKeychainBackend
{
    int Add(string service, string account, string label, byte[] data);
    int Update(string service, string account, byte[] data);
    /// <summary>The item's data, or null with <see cref="KeychainStatus.ItemNotFound"/>.</summary>
    int Copy(string service, string account, out byte[]? data);
    int Delete(string service, string account);
    string Describe(int status);
}

/// <summary>
/// Security.framework through P/Invoke: <c>SecItemAdd</c>, <c>SecItemCopyMatching</c>, <c>SecItemUpdate</c>, <c>SecItemDelete</c> on
/// generic-password items of the legacy (file-based) keychain. With no path the user's default keychain is used (production); with a path
/// every call is confined to that keychain file (<c>kSecUseKeychain</c> on add, <c>kSecMatchSearchList</c> on the queries), so a test never
/// reaches the login keychain.
/// </summary>
public sealed class SecurityFrameworkKeychain : IKeychainBackend, IDisposable
{
    private readonly string? _keychainPath;
    private readonly object _gate = new();
    private IntPtr _keychain;

    /// <param name="keychainPath">A keychain file to confine every call to, or null for the default keychain.</param>
    /// <param name="allowUserInteraction">
    /// False makes a call that would need a password prompt fail with <see cref="KeychainStatus.InteractionNotAllowed"/> instead of waiting
    /// for a person. The setting is process-wide (the system has no per-call switch for file-based keychains), so only tests that confine
    /// themselves to a throwaway keychain turn it off.
    /// </param>
    public SecurityFrameworkKeychain(string? keychainPath = null, bool allowUserInteraction = true)
    {
        NativeLibraries.RequireMacOS();
        _keychainPath = keychainPath;
        if (!allowUserInteraction) Sec.SecKeychainSetUserInteractionAllowed(false);
    }

    public int Add(string service, string account, string label, byte[] data)
    {
        using var scope = new CfScope();
        var keychain = OpenKeychain(out var openStatus);
        if (openStatus != KeychainStatus.Success) return openStatus;
        var attributes = scope.NewDictionary();
        CF.CFDictionarySetValue(attributes, Sec.kSecClass, Sec.kSecClassGenericPassword);
        CF.CFDictionarySetValue(attributes, Sec.kSecAttrService, scope.NewString(service));
        CF.CFDictionarySetValue(attributes, Sec.kSecAttrAccount, scope.NewString(account));
        CF.CFDictionarySetValue(attributes, Sec.kSecAttrLabel, scope.NewString(label));
        var value = scope.NewData(data);
        CF.CFDictionarySetValue(attributes, Sec.kSecValueData, value);
        if (keychain != IntPtr.Zero) CF.CFDictionarySetValue(attributes, Sec.kSecUseKeychain, keychain);
        try { return Sec.SecItemAdd(attributes, IntPtr.Zero); }
        finally { CfScope.ZeroData(value); }
    }

    public int Update(string service, string account, byte[] data)
    {
        using var scope = new CfScope();
        var query = Query(scope, service, account, out var status);
        if (status != KeychainStatus.Success) return status;
        var changes = scope.NewDictionary();
        var value = scope.NewData(data);
        CF.CFDictionarySetValue(changes, Sec.kSecValueData, value);
        try { return Sec.SecItemUpdate(query, changes); }
        finally { CfScope.ZeroData(value); }
    }

    public int Copy(string service, string account, out byte[]? data)
    {
        data = null;
        using var scope = new CfScope();
        var query = Query(scope, service, account, out var status);
        if (status != KeychainStatus.Success) return status;
        CF.CFDictionarySetValue(query, Sec.kSecReturnData, CF.True);
        CF.CFDictionarySetValue(query, Sec.kSecMatchLimit, Sec.kSecMatchLimitOne);
        status = Sec.SecItemCopyMatching(query, out var result);
        if (status != KeychainStatus.Success) return status;
        scope.Own(result);
        if (result == IntPtr.Zero || CF.CFGetTypeID(result) != CF.CFDataGetTypeID()) return KeychainStatus.Param;
        var length = (int)CF.CFDataGetLength(result);
        var copy = new byte[length];
        if (length > 0) System.Runtime.InteropServices.Marshal.Copy(CF.CFDataGetBytePtr(result), copy, 0, length);
        data = copy;
        return KeychainStatus.Success;
    }

    public int Delete(string service, string account)
    {
        using var scope = new CfScope();
        var query = Query(scope, service, account, out var status);
        return status != KeychainStatus.Success ? status : Sec.SecItemDelete(query);
    }

    public string Describe(int status) => Sec.Describe(status);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_keychain != IntPtr.Zero) CF.CFRelease(_keychain);
            _keychain = IntPtr.Zero;
        }
    }

    private IntPtr Query(CfScope scope, string service, string account, out int status)
    {
        var keychain = OpenKeychain(out status);
        var query = scope.NewDictionary();
        CF.CFDictionarySetValue(query, Sec.kSecClass, Sec.kSecClassGenericPassword);
        CF.CFDictionarySetValue(query, Sec.kSecAttrService, scope.NewString(service));
        CF.CFDictionarySetValue(query, Sec.kSecAttrAccount, scope.NewString(account));
        if (keychain != IntPtr.Zero) CF.CFDictionarySetValue(query, Sec.kSecMatchSearchList, scope.NewArray(keychain));
        return query;
    }

    /// <summary>The confining keychain (opened once), or zero for the default keychain. A path that does not exist is an error.</summary>
    private IntPtr OpenKeychain(out int status)
    {
        status = KeychainStatus.Success;
        if (_keychainPath is null) return IntPtr.Zero;
        lock (_gate)
        {
            if (_keychain != IntPtr.Zero) return _keychain;
            // SecKeychainOpen succeeds for a path that does not exist and the first item call then fails in a confusing way.
            if (!File.Exists(_keychainPath))
            {
                status = KeychainStatus.NoSuchKeychain;
                return IntPtr.Zero;
            }
            status = Sec.SecKeychainOpen(Sec.CPath(_keychainPath), out _keychain);
            if (status != KeychainStatus.Success) _keychain = IntPtr.Zero;
            return _keychain;
        }
    }
}
