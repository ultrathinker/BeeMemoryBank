using System.Runtime.InteropServices;
using System.Text;

namespace BeeMemoryBank.Platforms.Apple.Interop;

/// <summary>
/// The system libraries the Apple adapters of the apps call. The declarations below are resolved by the runtime the first time a method is
/// invoked, so this assembly loads and compiles on any OS; <see cref="RequireMacOS"/> turns a call on another OS into a clear exception
/// instead of a DllNotFoundException. These are low-level declarations shared by the adapters of the blind app and of the full app (and by
/// their tests); they are public only so that those assemblies can use them, and are not meant as an API for anything else.
/// </summary>
public static class NativeLibraries
{
    public const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    public const string Security = "/System/Library/Frameworks/Security.framework/Security";
    public const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    public const string LibC = "libc";

    public static void RequireMacOS()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("This adapter calls macOS system frameworks and runs only on macOS.");
    }

    private static readonly Dictionary<string, IntPtr> Handles = new(StringComparer.Ordinal);

    private static IntPtr Handle(string library)
    {
        lock (Handles)
        {
            if (!Handles.TryGetValue(library, out var handle))
                Handles[library] = handle = NativeLibrary.Load(library);
            return handle;
        }
    }

    /// <summary>The address of an exported symbol (for a struct such as <c>kCFTypeDictionaryKeyCallBacks</c>).</summary>
    public static IntPtr ExportAddress(string library, string symbol) => NativeLibrary.GetExport(Handle(library), symbol);

    /// <summary>
    /// The value of an exported constant such as <c>extern const CFStringRef kSecClass</c>: the symbol is the address of a variable that
    /// holds the object pointer.
    /// </summary>
    public static IntPtr ReadConstant(string library, string symbol) => Marshal.ReadIntPtr(ExportAddress(library, symbol));
}

/// <summary>The few CoreFoundation calls the adapters need.</summary>
public static class CF
{
    public const uint Utf8 = 0x08000100;

    [DllImport(NativeLibraries.CoreFoundation)] public static extern void CFRelease(IntPtr cf);
    [DllImport(NativeLibraries.CoreFoundation)] public static extern nuint CFGetTypeID(IntPtr cf);
    [DllImport(NativeLibraries.CoreFoundation)] public static extern nuint CFDataGetTypeID();

    [DllImport(NativeLibraries.CoreFoundation)]
    public static extern IntPtr CFStringCreateWithBytes(IntPtr allocator, byte[] bytes, nint numBytes, uint encoding,
        [MarshalAs(UnmanagedType.U1)] bool isExternalRepresentation);
    [DllImport(NativeLibraries.CoreFoundation)] public static extern nint CFStringGetLength(IntPtr theString);
    [DllImport(NativeLibraries.CoreFoundation)] public static extern nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);
    [DllImport(NativeLibraries.CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool CFStringGetCString(IntPtr theString, byte[] buffer, nint bufferSize, uint encoding);

    [DllImport(NativeLibraries.CoreFoundation)]
    public static extern IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keyCallBacks, IntPtr valueCallBacks);
    [DllImport(NativeLibraries.CoreFoundation)] public static extern void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);

    [DllImport(NativeLibraries.CoreFoundation)] public static extern IntPtr CFArrayCreateMutable(IntPtr allocator, nint capacity, IntPtr callBacks);
    [DllImport(NativeLibraries.CoreFoundation)] public static extern void CFArrayAppendValue(IntPtr array, IntPtr value);

    [DllImport(NativeLibraries.CoreFoundation)] public static extern IntPtr CFDataCreateMutable(IntPtr allocator, nint capacity);
    [DllImport(NativeLibraries.CoreFoundation)] public static extern void CFDataAppendBytes(IntPtr data, byte[] bytes, nint length);
    [DllImport(NativeLibraries.CoreFoundation)] public static extern nint CFDataGetLength(IntPtr data);
    [DllImport(NativeLibraries.CoreFoundation)] public static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(NativeLibraries.CoreFoundation)] public static extern IntPtr CFDataGetMutableBytePtr(IntPtr data);

    private static readonly Lazy<IntPtr> TrueValue = new(() => NativeLibraries.ReadConstant(NativeLibraries.CoreFoundation, "kCFBooleanTrue"));
    private static readonly Lazy<IntPtr> DictKeyCallbacks = new(() => NativeLibraries.ExportAddress(NativeLibraries.CoreFoundation, "kCFTypeDictionaryKeyCallBacks"));
    private static readonly Lazy<IntPtr> DictValueCallbacks = new(() => NativeLibraries.ExportAddress(NativeLibraries.CoreFoundation, "kCFTypeDictionaryValueCallBacks"));
    private static readonly Lazy<IntPtr> ArrayCallbacks = new(() => NativeLibraries.ExportAddress(NativeLibraries.CoreFoundation, "kCFTypeArrayCallBacks"));

    public static IntPtr True => TrueValue.Value;
    public static IntPtr DictionaryKeyCallbacks => DictKeyCallbacks.Value;
    public static IntPtr DictionaryValueCallbacks => DictValueCallbacks.Value;
    public static IntPtr ArrayValueCallbacks => ArrayCallbacks.Value;

    /// <summary>A new CFString (the caller releases it) from a non-empty managed string.</summary>
    public static IntPtr CreateString(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        var result = CFStringCreateWithBytes(IntPtr.Zero, bytes, bytes.Length, Utf8, false);
        if (result == IntPtr.Zero) throw new InvalidOperationException("CoreFoundation could not create a string.");
        return result;
    }

    /// <summary>A managed copy of a CFString; null for a null reference or an unconvertible string.</summary>
    public static string? ToManaged(IntPtr cfString)
    {
        if (cfString == IntPtr.Zero) return null;
        var size = CFStringGetMaximumSizeForEncoding(CFStringGetLength(cfString), Utf8) + 1;
        var buffer = new byte[size];
        if (!CFStringGetCString(cfString, buffer, size, Utf8)) return null;
        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }
}

/// <summary>Owns the CoreFoundation objects created for one native call and releases them together.</summary>
public sealed class CfScope : IDisposable
{
    private readonly List<IntPtr> _owned = [];

    /// <summary>Takes ownership of an object that was returned by a Create/Copy function.</summary>
    public IntPtr Own(IntPtr cf)
    {
        if (cf != IntPtr.Zero) _owned.Add(cf);
        return cf;
    }

    public IntPtr NewString(string value) => Own(CF.CreateString(value));

    public IntPtr NewDictionary() =>
        Own(CF.CFDictionaryCreateMutable(IntPtr.Zero, 0, CF.DictionaryKeyCallbacks, CF.DictionaryValueCallbacks));

    public IntPtr NewArray(params IntPtr[] values)
    {
        var array = Own(CF.CFArrayCreateMutable(IntPtr.Zero, 0, CF.ArrayValueCallbacks));
        foreach (var value in values) CF.CFArrayAppendValue(array, value);
        return array;
    }

    /// <summary>A mutable CFData holding a copy of <paramref name="bytes"/>; <see cref="ZeroData"/> wipes it before it is released.</summary>
    public IntPtr NewData(byte[] bytes)
    {
        var data = Own(CF.CFDataCreateMutable(IntPtr.Zero, bytes.Length));
        CF.CFDataAppendBytes(data, bytes, bytes.Length);
        return data;
    }

    /// <summary>Overwrites the bytes of a mutable CFData made by <see cref="NewData"/>.</summary>
    public static void ZeroData(IntPtr mutableData)
    {
        var length = CF.CFDataGetLength(mutableData);
        if (length <= 0) return;
        var pointer = CF.CFDataGetMutableBytePtr(mutableData);
        if (pointer != IntPtr.Zero) Marshal.Copy(new byte[length], 0, pointer, (int)length);
    }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) CF.CFRelease(_owned[i]);
        _owned.Clear();
    }
}

/// <summary>Security.framework: generic-password Keychain items and the file-based keychains the tests use.</summary>
public static class Sec
{
    [DllImport(NativeLibraries.Security)] public static extern int SecItemAdd(IntPtr attributes, IntPtr result);
    [DllImport(NativeLibraries.Security)] public static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
    [DllImport(NativeLibraries.Security)] public static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);
    [DllImport(NativeLibraries.Security)] public static extern int SecItemDelete(IntPtr query);

    [DllImport(NativeLibraries.Security)] public static extern int SecKeychainOpen(byte[] pathName, out IntPtr keychain);
    [DllImport(NativeLibraries.Security)]
    public static extern int SecKeychainCreate(byte[] pathName, uint passwordLength, byte[]? password,
        [MarshalAs(UnmanagedType.U1)] bool promptUser, IntPtr initialAccess, out IntPtr keychain);
    [DllImport(NativeLibraries.Security)] public static extern int SecKeychainDelete(IntPtr keychain);
    [DllImport(NativeLibraries.Security)] public static extern int SecKeychainLock(IntPtr keychain);
    [DllImport(NativeLibraries.Security)]
    public static extern int SecKeychainUnlock(IntPtr keychain, uint passwordLength, byte[]? password,
        [MarshalAs(UnmanagedType.U1)] bool usePassword);
    [DllImport(NativeLibraries.Security)]
    public static extern int SecKeychainSetUserInteractionAllowed([MarshalAs(UnmanagedType.U1)] bool state);
    [DllImport(NativeLibraries.Security)] public static extern IntPtr SecCopyErrorMessageString(int status, IntPtr reserved);

    private static IntPtr Constant(string name) => NativeLibraries.ReadConstant(NativeLibraries.Security, name);

    private static readonly Lazy<IntPtr> Class = new(() => Constant("kSecClass"));
    private static readonly Lazy<IntPtr> ClassGenericPassword = new(() => Constant("kSecClassGenericPassword"));
    private static readonly Lazy<IntPtr> AttrService = new(() => Constant("kSecAttrService"));
    private static readonly Lazy<IntPtr> AttrAccount = new(() => Constant("kSecAttrAccount"));
    private static readonly Lazy<IntPtr> AttrLabel = new(() => Constant("kSecAttrLabel"));
    private static readonly Lazy<IntPtr> ValueData = new(() => Constant("kSecValueData"));
    private static readonly Lazy<IntPtr> ReturnData = new(() => Constant("kSecReturnData"));
    private static readonly Lazy<IntPtr> MatchLimit = new(() => Constant("kSecMatchLimit"));
    private static readonly Lazy<IntPtr> MatchLimitOne = new(() => Constant("kSecMatchLimitOne"));
    private static readonly Lazy<IntPtr> MatchSearchList = new(() => Constant("kSecMatchSearchList"));
    private static readonly Lazy<IntPtr> UseKeychain = new(() => Constant("kSecUseKeychain"));

    public static IntPtr kSecClass => Class.Value;
    public static IntPtr kSecClassGenericPassword => ClassGenericPassword.Value;
    public static IntPtr kSecAttrService => AttrService.Value;
    public static IntPtr kSecAttrAccount => AttrAccount.Value;
    public static IntPtr kSecAttrLabel => AttrLabel.Value;
    public static IntPtr kSecValueData => ValueData.Value;
    public static IntPtr kSecReturnData => ReturnData.Value;
    public static IntPtr kSecMatchLimit => MatchLimit.Value;
    public static IntPtr kSecMatchLimitOne => MatchLimitOne.Value;
    public static IntPtr kSecMatchSearchList => MatchSearchList.Value;
    public static IntPtr kSecUseKeychain => UseKeychain.Value;

    /// <summary>The system's text for an OSStatus (empty when it has none).</summary>
    public static string Describe(int status)
    {
        try
        {
            var message = SecCopyErrorMessageString(status, IntPtr.Zero);
            if (message == IntPtr.Zero) return "";
            try { return CF.ToManaged(message) ?? ""; }
            finally { CF.CFRelease(message); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            return "";
        }
    }

    /// <summary>A NUL-terminated UTF-8 path for the C functions that take <c>const char*</c>.</summary>
    public static byte[] CPath(string path) => Encoding.UTF8.GetBytes(path + "\0");
}
