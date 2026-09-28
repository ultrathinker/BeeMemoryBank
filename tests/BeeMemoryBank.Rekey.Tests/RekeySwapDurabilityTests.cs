using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Rekey;

namespace BeeMemoryBank.Rekey.Tests;

/// <summary>
/// Review release-b R1-5 (sec#2). The journal and the directory renames are made durable: each journal write and each
/// rename flushes the parent directory, write-through on Windows. A power cut that loses the journal's directory entry
/// anyway still leaves a startable D: the resolver recognises the swap from the siblings.
/// </summary>
[Collection(nameof(DurableFsEvents))]
public sealed class RekeySwapDurabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb-durable-" + Guid.NewGuid().ToString("N"));
    private readonly string _d;
    private readonly List<string> _flushed = [];
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 21, 0, 0, TimeSpan.Zero);

    public RekeySwapDurabilityTests()
    {
        _d = Path.Combine(_root, "vault");
        Directory.CreateDirectory(_d);
        File.WriteAllText(Path.Combine(_d, "beememorybank.db"), "old-db");
        Directory.CreateDirectory(RekeySwapJournal.NewDirFor(_d));
        File.WriteAllText(Path.Combine(RekeySwapJournal.NewDirFor(_d), "beememorybank.db"), "new-db");
        DurableFs.DirectoryFlushed += OnFlushed;
    }

    private void OnFlushed(string dir)
    {
        lock (_flushed) _flushed.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)));
    }

    public void Dispose()
    {
        DurableFs.DirectoryFlushed -= OnFlushed;
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Parent => Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root));

    [Fact]
    public void EveryJournalWrite_FlushesTheDirectoryItsEntryLivesIn()
    {
        RekeySwapJournal.Write(_d, new RekeySwapJournal(RekeySwapJournal.NewDirFor(_d), RekeySwapJournal.OldDirFor(_d, Now), RekeySwapJournal.Prepared));

        _flushed.Should().Contain(Parent);
    }

    [Fact]
    public void TheSwap_FlushesTheParentAfterEachRenameAndEachJournalUpdate()
    {
        RekeySwap.Swap(_d, Now);

        _flushed.Count(f => f == Parent).Should().BeGreaterThanOrEqualTo(5, "three journal writes and two renames, each flushed");
        File.ReadAllText(Path.Combine(_d, "beememorybank.db")).Should().Be("new-db");
    }

    /// <summary>The journal's entry was lost between the renames: the verified new vault is recognised and goes to D.</summary>
    [Fact]
    public void ALostJournal_BetweenTheRenames_PromotesTheVerifiedNewVault()
    {
        File.WriteAllText(Path.Combine(RekeySwapJournal.NewDirFor(_d), RekeySwapJournal.VerifiedMarker), "{}");
        var swap = () => RekeySwap.Swap(_d, Now, p => { if (p == RekeySwap.FaultBetweenRenames) throw new IOException("power cut"); });
        swap.Should().Throw<IOException>();
        File.Delete(RekeySwapJournal.PathFor(_d)); // what the power cut took

        var resolved = RekeySwapResolver.Resolve(_d);

        resolved.FirstStartAfterSwap.Should().BeTrue();
        File.ReadAllText(Path.Combine(_d, "beememorybank.db")).Should().Be("new-db");
        File.ReadAllText(Path.Combine(RekeySwapJournal.OldDirFor(_d, Now), "beememorybank.db")).Should().Be("old-db");
        RekeySwapJournal.Read(_d)!.Phase.Should().Be(RekeySwapJournal.Swapped, "so the first start clears it like any swap");
    }

    /// <summary>The same loss, with a new directory that was never verified: the old vault goes back to D.</summary>
    [Fact]
    public void ALostJournal_WithAnUnverifiedNewDir_RestoresTheOldVault()
    {
        var swap = () => RekeySwap.Swap(_d, Now, p => { if (p == RekeySwap.FaultBetweenRenames) throw new IOException("power cut"); });
        swap.Should().Throw<IOException>();
        File.Delete(RekeySwapJournal.PathFor(_d));

        var resolved = RekeySwapResolver.Resolve(_d);

        resolved.FirstStartAfterSwap.Should().BeFalse();
        File.ReadAllText(Path.Combine(_d, "beememorybank.db")).Should().Be("old-db");
    }

    [Fact]
    public void FlushDirectory_WorksOnThisPlatform()
    {
        var flush = () => DurableFs.FlushDirectory(_root);

        flush.Should().NotThrow();
    }
}

/// <summary>DurableFs.DirectoryFlushed is process-wide: these tests must not overlap with other swaps.</summary>
[CollectionDefinition(nameof(DurableFsEvents), DisableParallelization = true)]
public sealed class DurableFsEvents;
