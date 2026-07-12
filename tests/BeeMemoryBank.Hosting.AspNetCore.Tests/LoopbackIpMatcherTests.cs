using System.Net;
using BeeMemoryBank.Hosting.AspNetCore;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Hosting.AspNetCore.Tests;

public class LoopbackIpMatcherTests
{
    [Theory]
    // Standard IPv4 loopback
    [InlineData("127.0.0.1", true)]
    // Standard IPv6 loopback
    [InlineData("::1", true)]
    // Other addresses in the 127.0.0.0/8 subnet
    [InlineData("127.0.0.2", true)]
    [InlineData("127.255.255.254", true)]
    // IPv4-mapped IPv6 loopback
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("::ffff:127.0.0.2", true)]
    [InlineData("::ffff:127.255.255.254", true)]
    // Standard non-loopback IPv4
    [InlineData("8.8.8.8", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("10.0.0.1", false)]
    // Standard non-loopback IPv6
    [InlineData("2001:4860:4860::8888", false)]
    [InlineData("fe80::1", false)]
    // IPv4-mapped IPv6 non-loopback
    [InlineData("::ffff:8.8.8.8", false)]
    public void IsLoopback_ShouldCorrectlyIdentifyLoopbackAddresses(string ipString, bool expectedResult)
    {
        // Arrange
        var ipAddress = IPAddress.Parse(ipString);

        // Act
        var result = LoopbackIpMatcher.IsLoopback(ipAddress);

        // Assert
        result.Should().Be(expectedResult);
    }

    [Fact]
    public void IsLoopback_ShouldReturnFalse_WhenIpIsNull()
    {
        // Act
        var result = LoopbackIpMatcher.IsLoopback(null);

        // Assert
        result.Should().BeFalse();
    }
}
