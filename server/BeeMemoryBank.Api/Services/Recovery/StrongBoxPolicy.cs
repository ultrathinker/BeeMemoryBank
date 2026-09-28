using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Api.Services.Recovery;

/// <summary>What kind of machine is about to build a strong box (plan 6.2, 6.6).</summary>
public enum RecoveryHostKind
{
    /// <summary>The Windows app: builds a 1 GiB box in the background while the password is in memory.</summary>
    Pc,
    /// <summary>A listening server node: builds one only with room to spare.</summary>
    Hub,
    /// <summary>A phone never builds one — it publishes its device box and nothing heavier.</summary>
    Phone,
}

/// <summary>
/// Which strong-box preset this machine can afford right now. 1 GiB on a PC; on a hub 1 GiB only with
/// more than 3 GiB available, otherwise the 512 MiB preset; nothing when even that would squeeze the
/// machine, and nothing on a phone. "Available" is physical memory minus what is in use — the
/// derivation commits its whole memory cost for seconds, so it must fit next to everything else.
/// </summary>
public static class StrongBoxPolicy
{
    private const long GiB = 1L << 30;

    public static string? ChoosePreset(RecoveryHostKind host, long availableBytes) => host switch
    {
        RecoveryHostKind.Phone => null,
        RecoveryHostKind.Pc when availableBytes > 2 * GiB => RecoveryBoxKdf.Strong1024,
        RecoveryHostKind.Hub when availableBytes > 3 * GiB => RecoveryBoxKdf.Strong1024,
        _ when availableBytes > GiB + GiB / 2 => RecoveryBoxKdf.Strong512,
        _ => null,
    };

    public static RecoveryHostKind CurrentHost() =>
        OperatingSystem.IsAndroid() ? RecoveryHostKind.Phone
        : OperatingSystem.IsWindows() ? RecoveryHostKind.Pc
        : RecoveryHostKind.Hub;

    public static long AvailableMemoryBytes()
    {
        var info = GC.GetGCMemoryInfo();
        return Math.Max(0, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes);
    }
}
