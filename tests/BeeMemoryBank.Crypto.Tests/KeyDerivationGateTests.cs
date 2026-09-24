using BeeMemoryBank.Crypto;

namespace BeeMemoryBank.Crypto.Tests;

[CollectionDefinition(nameof(KeyDerivationGateCollection), DisableParallelization = true)]
public class KeyDerivationGateCollection;

/// <summary>
/// The process-wide Argon2id gate. Runs alone: saturating the static gate would otherwise stall
/// every other derivation-running test in this assembly.
/// </summary>
[Collection(nameof(KeyDerivationGateCollection))]
public class KeyDerivationGateTests
{
    [Fact]
    public void Saturated_FailsFastWithKdfBusy_InsteadOfQueueingForever()
    {
        using (KeyDerivation.SaturateForTests())
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var act = () => KeyDerivation.DeriveKek("password", KeyDerivation.GenerateSalt());
            act.Should().Throw<KdfBusyException>();
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "a full queue rejects immediately");
        }

        // Released: derivations work again.
        KeyDerivation.DeriveKek("password", KeyDerivation.GenerateSalt()).Should().HaveCount(32);
    }
}

public class KeyDerivationUntrustedParameterTests
{
    [Theory]
    [InlineData(65536, 3, 4)]
    [InlineData(32768, 2, 1)]
    [InlineData(262144, 10, 8)]
    public void AcceptsSaneParameters(int memory, int iterations, int parallelism)
        => KeyDerivation.ValidateUntrustedParameters(memory, iterations, parallelism);

    [Theory]
    [InlineData(1048576, 3, 4)]   // 1 GiB: what a hostile peer's join response or a planted blob might ask for
    [InlineData(int.MaxValue, 3, 4)]
    [InlineData(65536, 11, 4)]
    [InlineData(65536, 3, 0)]
    [InlineData(65536, 3, 9)]
    [InlineData(1024, 3, 4)]      // weakened
    [InlineData(65536, 1, 4)]     // weakened
    public void RejectsHostileParameters(int memory, int iterations, int parallelism)
    {
        var act = () => KeyDerivation.ValidateUntrustedParameters(memory, iterations, parallelism);
        act.Should().Throw<System.Security.Cryptography.CryptographicException>();
    }
}

[Collection(nameof(KeyDerivationGateCollection))]
public class KeyDerivationMemoryBudgetTests
{
    [Fact]
    public void Units_ScaleWithRequestedMemory_AndNeverExceedTheBudget()
    {
        KeyDerivation.UnitsFor(65_536).Should().Be(1);                    // default 64 MiB
        KeyDerivation.UnitsFor(65_537).Should().Be(Math.Min(2, (int)(KeyDerivation.MemoryBudgetKiB / 65_536)));
        KeyDerivation.UnitsFor(int.MaxValue).Should().Be((int)(KeyDerivation.MemoryBudgetKiB / 65_536));
    }

    [Fact]
    public void ConcurrentMaxCostDerivations_NeverCommitMoreThanTheBudget()
    {
        // Eight parallel derivations at the largest cost an untrusted caller may choose (256 MiB):
        // with a count-only gate they would all run at once on a big machine (2 GiB). The memory
        // budget must hold regardless of core count.
        KeyDerivation.ResetPeakForTests();
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            try
            {
                // One iteration keeps the test fast; memory is what is being budgeted.
                KeyDerivation.DeriveKek("p", KeyDerivation.GenerateSalt(), memory: 262_144, iterations: 1, parallelism: 1);
            }
            catch (KdfBusyException) { }
        });

        KeyDerivation.PeakInFlightKiB.Should().BeGreaterThan(0);
        KeyDerivation.PeakInFlightKiB.Should().BeLessThanOrEqualTo(Math.Max(KeyDerivation.MemoryBudgetKiB, 262_144));
    }
}

[Collection(nameof(KeyDerivationGateCollection))]
public class KeyDerivationFairnessTests
{
    [Fact]
    public void LargeRequest_IsNotStarvedBySmallOnes()
    {
        // A stream of default-cost derivations must not keep a larger one waiting until it times out:
        // the gate is FIFO once anyone waits.
        var budgetUnits = (int)(KeyDerivation.MemoryBudgetKiB / 65_536);
        if (budgetUnits < 2) return; // nothing to starve on a 2-unit host with 128 MiB requests clamped

        using var cts = new CancellationTokenSource();
        var small = Enumerable.Range(0, budgetUnits).Select(_ => Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { KeyDerivation.DeriveKek("p", KeyDerivation.GenerateSalt(), memory: 65_536, iterations: 1, parallelism: 1); }
                catch (KdfBusyException) { }
            }
        })).ToArray();

        Thread.Sleep(200);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var act = () => KeyDerivation.DeriveKek("p", KeyDerivation.GenerateSalt(),
            memory: 65_536 * Math.Min(4, budgetUnits), iterations: 1, parallelism: 1);
        act.Should().NotThrow<KdfBusyException>();
        cts.Cancel();
        Task.WaitAll(small);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
    }
}
