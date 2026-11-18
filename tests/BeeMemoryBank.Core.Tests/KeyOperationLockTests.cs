using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The key-operation lock is per vault: several vaults open in one process (a test stand, the tests themselves) must not refuse
/// each other's rotations, and the operations of one vault still exclude each other.
/// </summary>
public class KeyOperationLockTests
{
    [Fact]
    public void TheLockOfOneVault_DoesNotRefuseAnotherVault_AndStillExcludesItsOwnSecondOperation()
    {
        var a = "vault-a-" + Guid.NewGuid().ToString("N");
        var b = "vault-b-" + Guid.NewGuid().ToString("N");

        using var first = KeyOperationLock.TryEnter(a, "an identity re-key");
        first.Should().NotBeNull();

        using (var other = KeyOperationLock.TryEnter(b, "a key rotation"))
            other.Should().NotBeNull("another vault in the same process has its own lock");

        KeyOperationLock.TryEnter(a, "a key rotation").Should().BeNull("the same vault: the second key operation is refused");
        KeyOperationLock.TryEnter(a.ToUpperInvariant(), "a key rotation").Should().BeNull("the same vault named in another case");
        KeyOperationLock.Busy(a, "A key rotation").Should().Contain("an identity re-key");
        KeyOperationLock.Holder(b).Should().BeNull();

        first!.Dispose();
        using var again = KeyOperationLock.TryEnter(a, "a key rotation");
        again.Should().NotBeNull("released");
    }
}
