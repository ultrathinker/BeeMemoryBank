using System.Security.Cryptography;
using BeeMemoryBank.Storage.Sealed;
using Xunit.Abstractions;

namespace BeeMemoryBank.Storage.Tests.Sealed;

/// <summary>Runs only inside a memory-limited container, on purpose (see <see cref="CgroupContainerRunTests"/>).</summary>
public sealed class ContainerRunFactAttribute : FactAttribute
{
    public ContainerRunFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BMB_CGROUP_RUN") != "1")
            Skip = "A run inside a memory-limited container: set BMB_CGROUP_RUN=1, BMB_CGROUP_NEED_MB and BMB_CGROUP_EXPECT=fits|refuse|unchecked (see CgroupContainerRunTests).";
    }
}

/// <summary>
/// BMB-304 fix2 (a): the memory budget against a REAL cgroup limit. It is not part of the normal run (a unit test cannot put itself in a container): the T480 run that
/// records it starts it with <c>docker run --memory=...</c>, optionally with a tmpfs that a sibling process of the container fills (what the resident set of ONE
/// process does not see and the cgroup charges: the Node, the Api and the Web are three processes in one container).
/// <list type="bullet">
/// <item><c>BMB_CGROUP_EXPECT=fits</c>: the job is asked for, taken and touched page by page, and the container is still alive;</item>
/// <item><c>refuse</c>: the job is refused in words (<see cref="NotEnoughMemoryException"/>) that name a limit larger than this container's, and nothing is allocated;</item>
/// <item><c>unchecked</c>: the same job taken WITHOUT asking - what a model that passes it would have done: the container's OOM killer ends the test host (exit 137).</item>
/// </list>
/// </summary>
public sealed class CgroupContainerRunTests(ITestOutputHelper output)
{
    private const long Mb = 1024 * 1024;

    [ContainerRunFact]
    public void The_budget_of_a_job_follows_what_the_container_has_left()
    {
        var need = long.Parse(Environment.GetEnvironmentVariable("BMB_CGROUP_NEED_MB")!) * Mb;
        var expect = Environment.GetEnvironmentVariable("BMB_CGROUP_EXPECT")!;
        var reading = MemoryBudget.Read();
        var committed = GC.GetGCMemoryInfo().TotalCommittedBytes;
        // What the budget said before the cgroup's usage was part of it: the resident set of this process, less the managed heap, and nothing of the siblings.
        var residentOnly = reading with { NativeBytes = Math.Max(0, Environment.WorkingSet - committed) };
        output.WriteLine(
            $"limit={reading.ProcessLimitBytes / Mb} MB, heap limit={reading.HeapLimitBytes / Mb} MB, managed={reading.ManagedBytes / Mb} MB, native={reading.NativeBytes / Mb} MB, " +
            $"cgroup usage={(MemoryLimits.ProcessUsage() is { } u ? (u / Mb).ToString() : "unknown")} MB, resident set={Environment.WorkingSet / Mb} MB");
        output.WriteLine($"free now={MemoryBudget.Free(reading) / Mb} MB (by the resident set alone it would be {MemoryBudget.Free(residentOnly) / Mb} MB); the job needs {need / Mb} MB + {MemoryBudget.Slack / Mb} MB slack");

        switch (expect)
        {
            case "refuse":
                var act = () => MemoryBudget.RequireFree(need + MemoryBudget.Slack, "Opening the database of a snapshot");
                var refusal = act.Should().Throw<NotEnoughMemoryException>().Which;
                output.WriteLine("refused: " + refusal.Message);
                refusal.RecommendedLimitBytes.Should().BeGreaterThan(reading.ProcessLimitBytes, "the limit the sentence names would be enough, this one is not");
                break;
            case "fits":
                MemoryBudget.RequireFree(need + MemoryBudget.Slack, "Opening the database of a snapshot");
                var buffer = MemoryBudget.Allocate(need, "Opening the database of a snapshot");
                RandomNumberGenerator.Fill(buffer);   // every page is touched: the container really holds it
                output.WriteLine($"taken and touched: {need / Mb} MB; the container is alive");
                GC.KeepAlive(buffer);
                break;
            case "unchecked":
                var raw = new byte[need];
                RandomNumberGenerator.Fill(raw);
                output.WriteLine("survived without asking (the container had the room after all)");
                GC.KeepAlive(raw);
                break;
            default:
                throw new InvalidOperationException("BMB_CGROUP_EXPECT is fits, refuse or unchecked.");
        }
    }
}
