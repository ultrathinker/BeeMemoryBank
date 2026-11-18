namespace BeeMemoryBank.Crypto.Tests;

/// <summary>
/// The check behind the Android ingest key re-enrolment (release 2.12, plan WS-D D4): an enrolled seed that is not the seed of the node's
/// current key (the node re-keyed) is enrolled again.
/// </summary>
public class NodeIdentitySeedOfTests
{
    [Fact]
    public void The_seed_of_the_current_key_matches_and_a_retired_seed_does_not()
    {
        var (current, currentSeed) = Ed25519Signer.GenerateKeyPair();
        var (_, retiredSeed) = Ed25519Signer.GenerateKeyPair();

        NodeIdentityCrypto.IsSeedOf(currentSeed, current).Should().BeTrue();
        NodeIdentityCrypto.IsSeedOf(retiredSeed, current).Should().BeFalse("the enrolled seed is of a key the node retired: enrol again");
    }

    [Fact]
    public void A_seed_or_key_of_the_wrong_size_never_matches_and_never_throws()
    {
        var (current, seed) = Ed25519Signer.GenerateKeyPair();
        NodeIdentityCrypto.IsSeedOf(null, current).Should().BeFalse();
        NodeIdentityCrypto.IsSeedOf(new byte[31], current).Should().BeFalse();
        NodeIdentityCrypto.IsSeedOf(seed, new byte[31]).Should().BeFalse();
        NodeIdentityCrypto.IsSeedOf(seed, null).Should().BeFalse();
    }
}
