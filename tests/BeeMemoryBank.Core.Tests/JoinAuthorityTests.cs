using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// The rule every join path records (BMB-42): whoever proved the master password is a superadmin, an
/// inherited peer keeps what the host reports, and a blind node never is one.
/// </summary>
public class JoinAuthorityTests
{
    [Fact]
    public void PasswordPeer_IsSuperadmin() =>
        JoinAuthority.ForPasswordPeer(Guid.NewGuid()).Should().BeTrue();

    [Fact]
    public void PasswordPeer_WithBlindId_IsNot() =>
        JoinAuthority.ForPasswordPeer(BlindNodeId.NewId()).Should().BeFalse();

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void InheritedPeer_KeepsWhatTheHostReports(bool reported, bool expected) =>
        JoinAuthority.ForInheritedPeer(Guid.NewGuid(), reported).Should().Be(expected);

    [Fact]
    public void InheritedPeer_WithBlindId_IsNot_EvenIfTheHostSaysSo() =>
        JoinAuthority.ForInheritedPeer(BlindNodeId.NewId(), reportedByHost: true).Should().BeFalse();
}
