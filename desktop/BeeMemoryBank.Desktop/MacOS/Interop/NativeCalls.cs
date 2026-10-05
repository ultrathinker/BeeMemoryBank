using System;
using System.Runtime.InteropServices;
using BeeMemoryBank.Platforms.Apple.Interop;

namespace BeeMemoryBank.Desktop.MacOS.Interop;

// Native declarations of the shell's macOS adapters. They are resolved by the runtime the first time a method is called, so this assembly
// loads and compiles on every OS; each caller checks for macOS (NativeLibraries.RequireMacOS) before it gets here. The library names are
// the shared ones of the Apple platform layer. They are internal on purpose: a public type here would be a new public type of an
// assembly that other products share.

/// <summary>IOKit: system power notifications (sleep) and power assertions (keep awake).</summary>
internal static class IOKitPower
{
    /// <summary>kIOReturnSuccess.</summary>
    internal const int Success = 0;

    // IOMessage.h: iokit_common_msg(x) = sys_iokit | sub_iokit_common | x = 0xE0000000 | x
    internal const uint MessageCanSystemSleep = 0xE0000270;
    internal const uint MessageSystemWillSleep = 0xE0000280;
    internal const uint MessageSystemWillNotSleep = 0xE0000290;
    internal const uint MessageSystemHasPoweredOn = 0xE0000300;

    /// <summary>kIOPMAssertionLevelOn.</summary>
    internal const uint AssertionLevelOn = 255;

    /// <summary>The assertion type that keeps the system from idle sleep (the display may still turn off): what <c>caffeinate -i</c> takes.</summary>
    internal const string PreventUserIdleSystemSleep = "PreventUserIdleSystemSleep";

    // void (*IOServiceInterestCallback)(void *refcon, io_service_t service, uint32_t messageType, void *messageArgument)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ServiceInterestCallback(IntPtr refcon, uint service, uint messageType, IntPtr messageArgument);

    // io_connect_t IORegisterForSystemPower(void *refcon, IONotificationPortRef *thePortRef, IOServiceInterestCallback callback, io_object_t *notifier)
    [DllImport(NativeLibraries.IOKit)]
    internal static extern uint IORegisterForSystemPower(IntPtr refcon, out IntPtr notificationPort, ServiceInterestCallback callback, out uint notifier);

    [DllImport(NativeLibraries.IOKit)] internal static extern int IODeregisterForSystemPower(ref uint notifier);
    [DllImport(NativeLibraries.IOKit)] internal static extern int IOAllowPowerChange(uint kernelPort, nint notificationId);
    [DllImport(NativeLibraries.IOKit)] internal static extern int IOServiceClose(uint connect);
    [DllImport(NativeLibraries.IOKit)] internal static extern IntPtr IONotificationPortGetRunLoopSource(IntPtr notificationPort);
    [DllImport(NativeLibraries.IOKit)] internal static extern void IONotificationPortDestroy(IntPtr notificationPort);

    [DllImport(NativeLibraries.IOKit)]
    internal static extern int IOPMAssertionCreateWithName(IntPtr assertionType, uint assertionLevel, IntPtr assertionName, out uint assertionId);

    [DllImport(NativeLibraries.IOKit)] internal static extern int IOPMAssertionRelease(uint assertionId);
}

/// <summary>The few CoreFoundation run-loop calls the sleep monitor needs.</summary>
internal static class RunLoop
{
    /// <summary>kCFRunLoopRunTimedOut / kCFRunLoopRunStopped / kCFRunLoopRunFinished are 3 / 2 / 1; any of them just ends one turn.</summary>
    internal const int Finished = 1;

    [DllImport(NativeLibraries.CoreFoundation)] internal static extern IntPtr CFRunLoopGetCurrent();
    [DllImport(NativeLibraries.CoreFoundation)] internal static extern void CFRunLoopAddSource(IntPtr runLoop, IntPtr source, IntPtr mode);
    [DllImport(NativeLibraries.CoreFoundation)] internal static extern void CFRunLoopRemoveSource(IntPtr runLoop, IntPtr source, IntPtr mode);

    [DllImport(NativeLibraries.CoreFoundation)]
    internal static extern int CFRunLoopRunInMode(IntPtr mode, double seconds, [MarshalAs(UnmanagedType.U1)] bool returnAfterSourceHandled);

    [DllImport(NativeLibraries.CoreFoundation)] internal static extern void CFRunLoopStop(IntPtr runLoop);

    private static readonly Lazy<IntPtr> CommonModesValue = new(() => NativeLibraries.ReadConstant(NativeLibraries.CoreFoundation, "kCFRunLoopCommonModes"));
    private static readonly Lazy<IntPtr> DefaultModeValue = new(() => NativeLibraries.ReadConstant(NativeLibraries.CoreFoundation, "kCFRunLoopDefaultMode"));

    internal static IntPtr CommonModes => CommonModesValue.Value;
    internal static IntPtr DefaultMode => DefaultModeValue.Value;
}

internal static class LibC
{
    [DllImport(NativeLibraries.LibC)] internal static extern uint getuid();
}
