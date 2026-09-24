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
