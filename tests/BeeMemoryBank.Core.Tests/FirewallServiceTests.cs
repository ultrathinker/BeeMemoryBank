using System;
using System.Runtime.Versioning;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Side-effect-free tests for <see cref="FirewallService"/>. The real add/remove path shells out
/// to <c>netsh</c> and requires administrator elevation (inbound firewall rules have no
/// CurrentUser escape hatch), so it is NOT exercised here — only the input-validation / guard
/// behavior that is safe to run unattended.
/// </summary>
[SupportedOSPlatform("windows")]
public class FirewallServiceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    [InlineData(int.MinValue)]
    public void EnsureInboundTcpRule_RejectsInvalidPorts(int port)
    {
        if (!OperatingSystem.IsWindows()) return;

        var svc = new FirewallService("BeeMemoryBank Unit Test (do not create)");
        svc.EnsureInboundTcpRule(port).Should().BeFalse();
    }

    [Fact]
    public void RemoveRule_NeverThrows_RegardlessOfWhetherRuleExists()
    {
        if (!OperatingSystem.IsWindows()) return;

        var svc = new FirewallService("BeeMemoryBank Unit Test (do not create)");
        Action act = () => svc.RemoveRule(5311);
        act.Should().NotThrow();
    }
}
