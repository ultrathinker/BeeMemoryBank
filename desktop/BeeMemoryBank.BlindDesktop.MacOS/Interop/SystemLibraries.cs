using System.Runtime.InteropServices;
using BeeMemoryBank.Platforms.Apple.Interop;

namespace BeeMemoryBank.BlindDesktop.MacOS.Interop;

internal static class LibC
{
    [DllImport(NativeLibraries.LibC)] internal static extern uint getuid();
}
