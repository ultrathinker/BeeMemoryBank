using System.Net;
using BeeMemoryBank.Hosting.AspNetCore;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Hosting.AspNetCore.Tests;

/// <summary>
/// BMB_TRUSTED_PROXIES decides whose word this node takes for the client IP, and every per-IP rate
/// limit in the product rests on that answer. Both failure directions are silent: parse too little
/// and all clients share one bucket (a single anonymous caller can exhaust the sync-challenge or
/// login budget for everyone), parse too much and anyone can forge their way out of their own
/// limit. Hence the parser is pure and pinned here rather than only exercised through startup.
/// </summary>
public class TrustedProxyParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_YieldsNothingAndNoComplaints(string? value)
    {
        var entries = TrustedProxyParser.Parse(value, out var invalid);

        entries.Should().BeEmpty();
        invalid.Should().BeEmpty();
    }

    [Fact]
    public void SingleAddress_IsParsedAsAnAddress()
    {
        var entries = TrustedProxyParser.Parse("10.1.2.3", out var invalid);

        invalid.Should().BeEmpty();
        entries.Should().ContainSingle();
        entries[0].Address.Should().Be(IPAddress.Parse("10.1.2.3"));
        entries[0].Network.Should().BeNull();
    }

    [Fact]
    public void Cidr_IsParsedAsANetwork()
    {
        var entries = TrustedProxyParser.Parse("172.16.0.0/12", out var invalid);

        invalid.Should().BeEmpty();
        entries.Should().ContainSingle();
        entries[0].Network!.Value.BaseAddress.Should().Be(IPAddress.Parse("172.16.0.0"));
        entries[0].Network!.Value.PrefixLength.Should().Be(12);
    }

    [Fact]
    public void HostAddressWithAPrefix_IsNormalizedToItsNetwork()
    {
        // "172.17.0.5/16" is how people actually write "the network this container sits on".
        // IPNetwork's own constructor throws on host bits below the prefix, and dropping the entry
        // would silently leave the deployment with no trusted proxy at all.
        var entries = TrustedProxyParser.Parse("172.17.0.5/16", out var invalid);

        invalid.Should().BeEmpty();
        entries[0].Network!.Value.BaseAddress.Should().Be(IPAddress.Parse("172.17.0.0"));
        entries[0].Network!.Value.PrefixLength.Should().Be(16);
    }

    [Fact]
    public void IPv6_AddressesAndNetworks_AreSupported()
    {
        var entries = TrustedProxyParser.Parse("fd00::1, fd00::/8", out var invalid);

        invalid.Should().BeEmpty();
        entries.Should().HaveCount(2);
        entries[0].Address.Should().Be(IPAddress.Parse("fd00::1"));
        entries[1].Network!.Value.PrefixLength.Should().Be(8);
    }

    [Theory]
    [InlineData("10.0.0.1,172.16.0.0/12")]
    [InlineData("10.0.0.1, 172.16.0.0/12")]
    [InlineData("10.0.0.1 172.16.0.0/12")]
    [InlineData("  10.0.0.1 ;\t172.16.0.0/12  ")]
    public void SeparatorsAndWhitespace_AreTolerated(string value)
    {
        // Whichever separator an operator reaches for (a compose file, a shell export, a copied
        // doc line), the same two hops must end up trusted — a list that silently parses as one
        // entry is how a deployment ends up trusting less than it declared.
        var entries = TrustedProxyParser.Parse(value, out var invalid);

        invalid.Should().BeEmpty();
        entries.Should().HaveCount(2);
        entries[0].Address.Should().Be(IPAddress.Parse("10.0.0.1"));
        entries[1].Network!.Value.PrefixLength.Should().Be(12);
    }

    [Fact]
    public void MixedList_KeepsTheGoodEntriesAndReportsTheBadOnes()
    {
        var entries = TrustedProxyParser.Parse("10.0.0.1, not-an-ip, 172.16.0.0/12, 10.0.0.0/99", out var invalid);

        entries.Should().HaveCount(2, "a typo must not discard the valid neighbours");
        invalid.Should().BeEquivalentTo(["not-an-ip", "10.0.0.0/99"]);
    }

    [Fact]
    public void PrefixOutOfRangeForTheFamily_IsRejected()
    {
        // /33 on IPv4 is not a narrower rule, it is a mistake — and silently accepting it as
        // something else would be worse than ignoring it loudly.
        TrustedProxyParser.Parse("10.0.0.0/33", out var invalid);

        invalid.Should().ContainSingle().Which.Should().Be("10.0.0.0/33");
    }

    // ─── IPv4-mapped IPv6 ───────────────────────────────────────────────────
    //
    // The dangerous direction is trusting MORE than was declared, and an IPv4-mapped base address
    // is exactly where that happens: masking "::ffff:172.16.0.0" with the IPv4-style /12 an
    // operator naturally writes zeroes the 0xffff marker itself, collapsing the entry to ::/12 —
    // which contains every IPv4-mapped address AND a large slice of public IPv6. That would hand
    // "trusted proxy" to the internet by way of a plausible typo.

    [Fact]
    public void MappedCidr_WithAnIPv4StylePrefix_IsRejectedRatherThanCollapsedToEverything()
    {
        var entries = TrustedProxyParser.Parse("::ffff:172.16.0.0/12", out var invalid);

        entries.Should().BeEmpty();
        invalid.Should().ContainSingle().Which.Should().Be("::ffff:172.16.0.0/12");
    }

    [Fact]
    public void MappedCidr_WithTheCorrectIPv6Prefix_NormalizesToItsIPv4Network()
    {
        // 96 + 12: the correct IPv6 spelling of 172.16.0.0/12, and it converts exactly.
        var entries = TrustedProxyParser.Parse("::ffff:172.16.0.0/108", out var invalid);

        invalid.Should().BeEmpty();
        entries.Should().ContainSingle();
        entries[0].Network!.Value.BaseAddress.Should().Be(IPAddress.Parse("172.16.0.0"));
        entries[0].Network!.Value.PrefixLength.Should().Be(12);
    }

    [Fact]
    public void MappedSingleAddress_NormalizesToIPv4()
    {
        var entries = TrustedProxyParser.Parse("::ffff:10.0.0.1", out var invalid);

        invalid.Should().BeEmpty();
        entries[0].Address.Should().Be(IPAddress.Parse("10.0.0.1"));
        entries[0].Address!.AddressFamily.Should().Be(System.Net.Sockets.AddressFamily.InterNetwork);
    }

    [Fact]
    public void GenuineIPv6Networks_AreNotMistakenForMappedOnes()
    {
        var entries = TrustedProxyParser.Parse("2001:db8::/32", out var invalid);

        invalid.Should().BeEmpty();
        entries[0].Network!.Value.BaseAddress.Should().Be(IPAddress.Parse("2001:db8::"));
        entries[0].Network!.Value.PrefixLength.Should().Be(32);
    }
}
