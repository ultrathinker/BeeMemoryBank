namespace BeeMemoryBank.Api.Services.BlindBackup;

/// <summary>
/// How much of the machine the blind node's own work may take (plan §8). The console/CLI names
/// map 1:1: "Economy" = <see cref="Economy"/> (default: low priority, restic GOMAXPROCS=1, one
/// job at a time, ~10 % CPU), "Fast" = <see cref="Fast"/> (~90 % of cores), "Pause" =
/// <see cref="Pause"/> (the running job is frozen — SIGSTOP on the restic child, a gate in the
/// node's own phases — and nothing new starts until the operator picks another mode).
/// </summary>
public enum BlindCpuMode
{
    Economy,
    Fast,
    Pause,
}

public static class BlindCpuModeExtensions
{
    /// <summary>Parses the wire name (status JSON, console, CLI); null when unknown.</summary>
    public static BlindCpuMode? FromName(string? name) => name switch
    {
        "economy" => BlindCpuMode.Economy,
        "fast" => BlindCpuMode.Fast,
        "pause" => BlindCpuMode.Pause,
        _ => null,
    };

    public static string Name(this BlindCpuMode mode) => mode switch
    {
        BlindCpuMode.Economy => "economy",
        BlindCpuMode.Fast => "fast",
        BlindCpuMode.Pause => "pause",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };
}
