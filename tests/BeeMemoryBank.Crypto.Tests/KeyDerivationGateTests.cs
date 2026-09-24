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
