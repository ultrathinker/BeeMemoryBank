using System.Runtime.InteropServices;

namespace BeeMemoryBank.BlindDesktop.MacOS.Interop;

/// <summary>
/// The few Network.framework, libdispatch and Objective-C runtime calls the device-conditions adapter needs, declared like <see cref="LibC"/>: resolved by the runtime the first time a
/// method is called, so this assembly loads and compiles on any OS, and every entry point that is used goes through <see cref="BeeMemoryBank.Platforms.Apple.Interop.NativeLibraries.RequireMacOS"/>.
/// Nothing here is exercised off a Mac: the adapter's logic is tested through <see cref="IMacOsConditionsSource"/>, the native part by a person on a Mac (see the report of this head).
/// </summary>
internal static class NetworkFramework
{
    private const string Library = "/System/Library/Frameworks/Network.framework/Network";

    internal const int PathStatusSatisfied = 1;

    [DllImport(Library)] internal static extern IntPtr nw_path_monitor_create();
    [DllImport(Library)] internal static extern void nw_path_monitor_set_queue(IntPtr monitor, IntPtr queue);
    [DllImport(Library)] internal static extern void nw_path_monitor_set_update_handler(IntPtr monitor, IntPtr block);
    [DllImport(Library)] internal static extern void nw_path_monitor_start(IntPtr monitor);
    [DllImport(Library)] internal static extern void nw_path_monitor_cancel(IntPtr monitor);

    [DllImport(Library)] internal static extern int nw_path_get_status(IntPtr path);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool nw_path_is_expensive(IntPtr path);

    [DllImport(Library)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool nw_path_is_constrained(IntPtr path);
}

internal static class Dispatch
{
    internal const string Library = "/usr/lib/libSystem.dylib";

    [DllImport(Library)] internal static extern IntPtr dispatch_queue_create([MarshalAs(UnmanagedType.LPUTF8Str)] string label, IntPtr attributes);
}

internal static class ObjectiveC
{
    internal const string Library = "/usr/lib/libobjc.A.dylib";
    private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";

    [DllImport(Library)] internal static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Library)] internal static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Library, EntryPoint = "objc_msgSend")] internal static extern IntPtr SendForObject(IntPtr receiver, IntPtr selector);

    // BOOL is a signed char on Intel and a bool on Apple silicon; a byte reads both (the value is 0 or 1).
    [DllImport(Library, EntryPoint = "objc_msgSend")] internal static extern byte SendForBool(IntPtr receiver, IntPtr selector);
    [DllImport(Library, EntryPoint = "objc_msgSend")] internal static extern byte SendForBoolWithSelector(IntPtr receiver, IntPtr selector, IntPtr argument);

    /// <summary>Makes sure Foundation (and with it <c>NSProcessInfo</c>) is loaded in this process.</summary>
    internal static void LoadFoundation() => NativeLibrary.Load(Foundation);
}

/// <summary>
/// An Objective-C block (<c>void (^)(nw_path_t)</c>) that captures nothing, built by hand: a "global block" is a small struct that the runtime accepts anywhere a block is expected and
/// never copies or frees (<c>_NSConcreteGlobalBlock</c>, flag <c>BLOCK_IS_GLOBAL</c>, a descriptor with no copy or dispose helpers). It is allocated once and kept for the life of the
/// process, together with the function it calls, so nothing can be freed under the system. The function cannot carry state, so the caller keeps it in a static.
/// </summary>
internal sealed class GlobalBlock
{
    private const int BlockIsGlobal = 1 << 28;

    // struct Block_literal { void* isa; int flags; int reserved; void* invoke; Block_descriptor* descriptor; }  (32 bytes)
    // struct Block_descriptor { unsigned long reserved; unsigned long size; }                                      (16 bytes)
    private const int LiteralSize = 32;
    private const int DescriptorSize = 16;

    private readonly Delegate _keepAlive;

    /// <param name="invoke">A delegate whose native signature is <c>void(IntPtr block, IntPtr argument)</c>; it is kept referenced as long as this object is.</param>
    internal GlobalBlock(Delegate invoke) : this(invoke, GlobalBlockClass())
    {
    }

    /// <param name="isa">The address of <c>_NSConcreteGlobalBlock</c> (a test passes any address: the layout is what it looks at).</param>
    internal GlobalBlock(Delegate invoke, IntPtr isa)
    {
        _keepAlive = invoke;

        var descriptor = Marshal.AllocHGlobal(DescriptorSize);
        Marshal.WriteInt64(descriptor, 0, 0);
        Marshal.WriteInt64(descriptor, 8, LiteralSize);

        Pointer = Marshal.AllocHGlobal(LiteralSize);
        Marshal.WriteIntPtr(Pointer, 0, isa);
        Marshal.WriteInt32(Pointer, 8, BlockIsGlobal);
        Marshal.WriteInt32(Pointer, 12, 0);
        Marshal.WriteIntPtr(Pointer, 16, Marshal.GetFunctionPointerForDelegate(invoke));
        Marshal.WriteIntPtr(Pointer, 24, descriptor);
    }

    /// <summary>The block, to hand to a function that takes a block.</summary>
    internal IntPtr Pointer { get; }

    private static IntPtr GlobalBlockClass() => NativeLibrary.GetExport(NativeLibrary.Load(Dispatch.Library), "_NSConcreteGlobalBlock");
}
