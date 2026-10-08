using System.Runtime.InteropServices;

namespace BeeMemoryBank.FullIos.Platforms.iOS;

/// <summary>
/// The app's memory as iOS counts it against its limit (the "physical footprint" jetsam uses, and its peak), from task_info(TASK_VM_INFO).
/// Process.PeakWorkingSet64 is not supported on iOS. Used by the self-check only.
/// </summary>
internal static class IosMemory
{
    private const int TaskVmInfo = 22;
    private const int PhysFootprintOffset = 144;      // task_vm_info rev1
    private const int FootprintPeakOffset = 168;      // task_vm_info rev3: ledger_phys_footprint_peak
    private const int Rev3Count = 176 / 4;

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern uint mach_task_self();

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int task_info(uint task, int flavor, byte[] info, ref int count);

    /// <summary>(footprint, peak) in bytes; zeros if the kernel did not answer.</summary>
    public static (long Footprint, long Peak) Read()
    {
        var buffer = new byte[512];
        var count = buffer.Length / 4;
        if (task_info(mach_task_self(), TaskVmInfo, buffer, ref count) != 0) return (0, 0);
        var footprint = BitConverter.ToInt64(buffer, PhysFootprintOffset);
        var peak = count >= Rev3Count ? BitConverter.ToInt64(buffer, FootprintPeakOffset) : 0;
        return (footprint, peak);
    }
}
