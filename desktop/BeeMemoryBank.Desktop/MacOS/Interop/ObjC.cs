using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BeeMemoryBank.Platforms.Apple.Interop;

namespace BeeMemoryBank.Desktop.MacOS.Interop;

// The Objective-C runtime, as far as UserNotifications needs it: send a message, make a class with a few methods (the notification centre's
// delegate), make and call a block (the completion handlers). Resolved by the runtime the first time a method is called, so this assembly
// loads and compiles on every OS; the caller checks for macOS first. Internal on purpose, like NativeCalls.

/// <summary>libobjc: classes, selectors, messages, autorelease pools.</summary>
internal static class ObjC
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";

    [DllImport(Lib)] internal static extern IntPtr objc_getClass(string name);
    [DllImport(Lib)] internal static extern IntPtr objc_getProtocol(string name);
    [DllImport(Lib)] internal static extern IntPtr sel_registerName(string name);
    [DllImport(Lib)] internal static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nuint extraBytes);
    [DllImport(Lib)] internal static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool class_addMethod(IntPtr cls, IntPtr name, IntPtr imp, string types);
    [DllImport(Lib)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool class_addProtocol(IntPtr cls, IntPtr protocol);
    [DllImport(Lib)] internal static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(Lib)] internal static extern void objc_autoreleasePoolPop(IntPtr pool);

    // objc_msgSend is one function; each declaration below is the call shape of a message (the arguments are all pointers or integers of a
    // pointer's size, so the same registers carry them on x86-64 and arm64).
    [DllImport(Lib, EntryPoint = "objc_msgSend")] internal static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] internal static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] internal static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] internal static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, IntPtr c);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] internal static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, nuint c);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] internal static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, IntPtr c, nuint d);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] internal static extern IntPtr Send(IntPtr receiver, IntPtr selector, nuint a, IntPtr b);

    internal static IntPtr Class(string name) => objc_getClass(name);

    internal static IntPtr Sel(string name) => sel_registerName(name);

    /// <summary>A managed copy of an NSString (an NSString is a CFString); null for nil.</summary>
    internal static string? ToManaged(IntPtr nsString) => CF.ToManaged(nsString);

    /// <summary>Loads a framework so its classes can be found by name.</summary>
    internal static bool LoadFramework(string path) => NativeLibrary.TryLoad(path, out _);
}

/// <summary>An autorelease pool for a stretch of messages sent from a thread that has none (a thread-pool thread). Dispose pops it.</summary>
internal readonly struct AutoreleasePool : IDisposable
{
    private readonly IntPtr _pool;

    private AutoreleasePool(IntPtr pool) => _pool = pool;

    internal static AutoreleasePool Push() => new(ObjC.objc_autoreleasePoolPush());

    public void Dispose() => ObjC.objc_autoreleasePoolPop(_pool);
}

/// <summary>
/// Objective-C blocks, both directions. A block <b>made here</b> is a global block (the runtime never copies or frees it) that carries one
/// GCHandle after the fixed fields; its function is a static thunk that takes the handle, frees it and calls the managed handler, so it
/// answers once (a completion handler is called once). The 40 bytes of the block itself are not freed: the system may still look at the block
/// after it called it. A block <b>received</b> (the completion handler of a delegate method) is called through the function pointer in its
/// header (<c>Block_layout.invoke</c>, offset 16).
/// </summary>
internal static class ObjCBlocks
{
    // struct Block_layout { void *isa; int flags; int reserved; void (*invoke)(void *, ...); struct Block_descriptor *descriptor; } + our handle
    private const int BlockIsGlobal = 1 << 28;
    private const int InvokeOffset = 16;
    private const int HandleOffset = 32;
    private const int BlockSize = 40;

    // The functions the system calls when it runs a block of ours. Static methods called through a function pointer: nothing to keep alive.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ObjectThunk(IntPtr block, IntPtr a) => Call<Action<IntPtr>>(block, h => h(a));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void BoolObjectThunk(IntPtr block, byte a, IntPtr b) => Call<Action<bool, IntPtr>>(block, h => h(a != 0, b));

    private static unsafe IntPtr ObjectThunkPointer => (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&ObjectThunk;

    private static unsafe IntPtr BoolObjectThunkPointer => (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte, IntPtr, void>)&BoolObjectThunk;

    private static readonly Lazy<IntPtr> GlobalBlockClass = new(() => NativeLibraries.ExportAddress("/usr/lib/libSystem.B.dylib", "_NSConcreteGlobalBlock"));
    private static readonly Lazy<IntPtr> Descriptor = new(() =>
    {
        var descriptor = Marshal.AllocHGlobal(16);
        Marshal.WriteInt64(descriptor, 0, 0);        // reserved
        Marshal.WriteInt64(descriptor, 8, BlockSize); // size
        return descriptor;
    });

    /// <summary>A block <c>void (^)(id)</c>.</summary>
    internal static IntPtr Create(Action<IntPtr> handler) => Make(ObjectThunkPointer, handler);

    /// <summary>A block <c>void (^)(BOOL, id)</c>.</summary>
    internal static IntPtr Create(Action<bool, IntPtr> handler) => Make(BoolObjectThunkPointer, handler);

    private static IntPtr Make(IntPtr invoke, object handler)
    {
        var block = Marshal.AllocHGlobal(BlockSize);
        Marshal.WriteIntPtr(block, 0, GlobalBlockClass.Value);
        Marshal.WriteInt32(block, 8, BlockIsGlobal);
        Marshal.WriteInt32(block, 12, 0);
        Marshal.WriteIntPtr(block, InvokeOffset, invoke);
        Marshal.WriteIntPtr(block, 24, Descriptor.Value);
        Marshal.WriteIntPtr(block, HandleOffset, GCHandle.ToIntPtr(GCHandle.Alloc(handler)));
        return block;
    }

    private static void Call<THandler>(IntPtr block, Action<THandler> call) where THandler : class
    {
        try
        {
            var raw = Marshal.ReadIntPtr(block, HandleOffset);
            if (raw == IntPtr.Zero) return; // called twice: the second call has nothing to do
            Marshal.WriteIntPtr(block, HandleOffset, IntPtr.Zero);
            var handle = GCHandle.FromIntPtr(raw);
            var handler = handle.Target as THandler;
            handle.Free();
            if (handler != null) call(handler);
        }
        catch (Exception)
        {
            // An exception must not unwind into the system's code.
        }
    }

    /// <summary>Calls a received block <c>void (^)(void)</c>.</summary>
    internal static unsafe void Invoke(IntPtr block)
    {
        if (block == IntPtr.Zero) return;
        ((delegate* unmanaged[Cdecl]<IntPtr, void>)Marshal.ReadIntPtr(block, InvokeOffset))(block);
    }

    /// <summary>Calls a received block <c>void (^)(NSUInteger)</c>.</summary>
    internal static unsafe void Invoke(IntPtr block, nuint argument)
    {
        if (block == IntPtr.Zero) return;
        ((delegate* unmanaged[Cdecl]<IntPtr, nuint, void>)Marshal.ReadIntPtr(block, InvokeOffset))(block, argument);
    }
}

/// <summary>An Objective-C class made at run time: a subclass of NSObject with methods whose bodies are managed delegates.</summary>
internal sealed class ObjCClass
{
    private readonly IntPtr _class;
    private readonly List<Delegate> _keepAlive = [];

    private ObjCClass(IntPtr cls) => _class = cls;

    /// <summary>A new class <paramref name="name"/>, which must not exist yet. Null when the runtime refused.</summary>
    internal static ObjCClass? Create(string name, string? protocol)
    {
        var cls = ObjC.objc_allocateClassPair(ObjC.Class("NSObject"), name, 0);
        if (cls == IntPtr.Zero) return null;
        if (protocol != null && ObjC.objc_getProtocol(protocol) is var p && p != IntPtr.Zero) ObjC.class_addProtocol(cls, p);
        return new ObjCClass(cls);
    }

    /// <summary>Adds an instance method; <paramref name="body"/> is called with the receiver and the selector first. Before <see cref="Register"/>.</summary>
    internal bool AddMethod(string selector, string typeEncoding, Delegate body)
    {
        _keepAlive.Add(body);
        return ObjC.class_addMethod(_class, ObjC.Sel(selector), Marshal.GetFunctionPointerForDelegate(body), typeEncoding);
    }

    internal void Register() => ObjC.objc_registerClassPair(_class);

    /// <summary>A new instance (<c>[[Class alloc] init]</c>), kept for the life of the process.</summary>
    internal IntPtr NewInstance() => ObjC.Send(ObjC.Send(_class, ObjC.Sel("alloc")), ObjC.Sel("init"));
}
