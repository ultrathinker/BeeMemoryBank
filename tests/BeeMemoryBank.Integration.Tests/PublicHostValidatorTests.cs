using BeeMemoryBank.Api.Services;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Unit tests for the SSRF guard used by <c>POST /api/sync/probe-relay</c>. Uses literal IP
/// addresses (not hostnames) so <c>Dns.GetHostAddressesAsync</c> resolves them without any real
/// network/DNS lookup — deterministic and fast.
/// </summary>
public class PublicHostValidatorTests
{
    private readonly DnsPublicHostValidator _validator = new();

    [Theory]
    [InlineData("127.0.0.1")]       // loopback
    [InlineData("10.0.0.5")]        // RFC1918
    [InlineData("172.16.0.1")]      // RFC1918
    [InlineData("192.168.1.1")]     // RFC1918
    [InlineData("169.254.169.254")] // link-local — cloud metadata endpoint
    [InlineData("0.0.0.0")]
    [InlineData("::1")]             // IPv6 loopback
    [InlineData("fe80::1")]         // IPv6 link-local
    [InlineData("fd00::1")]         // IPv6 unique local
    public async Task IsPublicHostAsync_RejectsNonPublicAddresses(string ip)
    {
        (await _validator.IsPublicHostAsync(ip, CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    public async Task IsPublicHostAsync_AcceptsPublicAddresses(string ip)
    {
        (await _validator.IsPublicHostAsync(ip, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task IsPublicHostAsync_RejectsUnresolvableHost()
    {
        (await _validator.IsPublicHostAsync(
            "this-host-does-not-exist.invalid", CancellationToken.None)).Should().BeFalse();
    }
}
