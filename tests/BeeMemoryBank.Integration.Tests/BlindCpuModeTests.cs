using BeeMemoryBank.Api.Services.BlindBackup;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// How a CPU mode (plan §8) shapes the restic process: Economy gets one Go thread and the idle
/// I/O class plus lowest CPU priority, Fast about 90 % of the cores; memory is always capped.
/// </summary>
public class BlindCpuModeTests
{
    private static readonly BlindBackupSettings Settings = new()
    {
        RepoFolder = "/backups/restic",
        ResticPassword = "pw",
    };

    private static readonly string[] Args = ["backup", "--json", "/app/data/blind/stage/beememorybank.db"];

    [Fact]
    public void Economy_OneThread_IdleIo_LowestPriority()
    {
        var psi = ResticRunner.BuildStartInfo(new ResticCall(Settings, Args), BlindCpuMode.Economy, "/cache", processorCount: 8,
            lowPriorityWrapper: true);

        psi.Environment["GOMAXPROCS"].Should().Be("1");
        psi.FileName.Should().Be("/usr/bin/ionice");
        psi.ArgumentList.Take(6).Should().Equal("-c", "3", "/usr/bin/nice", "-n", "19", "restic");
        psi.ArgumentList.Skip(6).Should().Equal(Args, "restic's own arguments follow the wrappers unchanged");
    }

    [Fact]
    public void Fast_NinetyPercentOfCores_NormalPriority()
    {
        var psi = ResticRunner.BuildStartInfo(new ResticCall(Settings, Args), BlindCpuMode.Fast, "/cache", processorCount: 8,
            lowPriorityWrapper: true);

        psi.Environment["GOMAXPROCS"].Should().Be("7");
        psi.FileName.Should().Be("restic", "Fast is not niced");
        psi.ArgumentList.Should().Equal(Args);
    }

    [Fact]
    public void WithoutTheTools_EconomyStillRunsRestic_OnOneThread()
    {
        var psi = ResticRunner.BuildStartInfo(new ResticCall(Settings, Args), BlindCpuMode.Economy, "/cache", processorCount: 8,
            lowPriorityWrapper: false);

        psi.FileName.Should().Be("restic");
        psi.Environment["GOMAXPROCS"].Should().Be("1");
    }

    [Fact]
    public void MemoryLimit_AndRepository_AlwaysSet()
    {
        var psi = ResticRunner.BuildStartInfo(new ResticCall(Settings, Args), BlindCpuMode.Fast, "/cache", processorCount: 1,
            lowPriorityWrapper: false);

        psi.Environment["GOMEMLIMIT"].Should().Be("512MiB");
        psi.Environment["GOMAXPROCS"].Should().Be("1", "a one-core box still gets one thread in Fast");
        psi.Environment["RESTIC_REPOSITORY"].Should().Be(Path.GetFullPath("/backups/restic"));
        psi.Environment["RESTIC_CACHE_DIR"].Should().Be("/cache");
    }
}
