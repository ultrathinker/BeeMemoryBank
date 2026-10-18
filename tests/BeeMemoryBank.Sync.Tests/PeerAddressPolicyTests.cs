using BeeMemoryBank.Sync;
using FluentAssertions;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// BMB-307 fix round (F5, F6): the one policy for a peer's address. What is stored is canonical, what is refused is refused with a sentence that does not
/// quote the input (a pasted address may carry a secret), and loopback and LAN addresses are NOT refused: this product's nodes live on LANs.
/// </summary>
public class PeerAddressPolicyTests
{
    [Theory]
    [InlineData("peer.local", "https://peer.local")]
    [InlineData("  https://peer.local  ", "https://peer.local")]
    [InlineData("HTTP://Peer.Local:80/", "http://peer.local")]
    [InlineData("https://peer.local:443", "https://peer.local")]
    [InlineData("https://peer.local///", "https://peer.local")]
    [InlineData("http://192.168.1.20:5300/", "http://192.168.1.20:5300")]
    [InlineData("http://127.0.0.1:5300", "http://127.0.0.1:5300")]
    [InlineData("http://localhost:5300", "http://localhost:5300")]
    [InlineData("https://[::1]:5300", "https://[::1]:5300")]
    [InlineData("http://10.0.0.5", "http://10.0.0.5")]
    [InlineData("http://peer.local/bmb/", "http://peer.local/bmb")]
    public void An_address_that_is_a_place_is_accepted_in_one_canonical_form(string typed, string stored)
    {
        var result = PeerAddressPolicy.Canonicalize(typed);

        result.Ok.Should().BeTrue(result.Refusal);
        result.Address.Should().Be(stored);
    }

    [Theory]
    [InlineData("http://peer.local")]
    [InlineData("HTTP://PEER.LOCAL/")]
    [InlineData(" http://peer.local:80/ ")]
    public void The_same_place_always_gives_the_same_text(string typed)
    {
        PeerAddressPolicy.Canonicalize(typed).Address.Should().Be("http://peer.local");
    }

    [Theory]
    // a user name or a password in the address
    [InlineData("http://user:s3cret@peer.local")]
    [InlineData("https://s3cret@peer.local")]
    [InlineData("http://peer.local@evil.local")]
    [InlineData("s3cret@peer.local")]
    // a query or a fragment: a token rides there, and the address is extended with /api/sync/...
    [InlineData("https://peer.local/?token=s3cret")]
    [InlineData("https://peer.local/path?x=s3cret")]
    [InlineData("https://peer.local/#s3cret")]
    // another scheme
    [InlineData("ftp://peer.local/s3cret")]
    [InlineData("file:///etc/s3cret")]
    [InlineData("javascript:alert(s3cret)")]
    [InlineData("gopher://peer.local")]
    // unspecified
    [InlineData("http://0.0.0.0:5300")]
    [InlineData("http://[::]:5300")]
    [InlineData("http://0.1.2.3")]
    // link-local and the cloud metadata services
    [InlineData("http://169.254.169.254/latest/meta-data/s3cret")]
    [InlineData("http://169.254.1.1")]
    [InlineData("http://[fe80::1]")]
    [InlineData("http://[::ffff:169.254.169.254]")]
    [InlineData("http://[fd00:ec2::254]")]
    [InlineData("http://100.100.100.200")]
    [InlineData("http://metadata.google.internal/s3cret")]
    [InlineData("http://metadata.google.internal./")]
    [InlineData("http://METADATA.GOOGLE.INTERNAL")]
    // the same addresses written as numbers
    [InlineData("http://2852039166")]
    [InlineData("http://0xA9FEA9FE")]
    [InlineData("http://0251.0376.0251.0376")]
    // not an address at all
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://")]
    [InlineData("http://peer .local")]
    [InlineData("http://peer.local/s3cret\u0007")]
    public void An_address_that_is_not_just_where_a_device_is_is_refused_and_the_sentence_does_not_quote_it(string typed)
    {
        var result = PeerAddressPolicy.Canonicalize(typed);

        result.Ok.Should().BeFalse($"'{typed}' must be refused");
        result.Address.Should().BeNull();
        result.Refusal.Should().NotBeNullOrWhiteSpace();
        result.Refusal.Should().NotContain("s3cret").And.NotContain("peer.local").And.NotContain("evil.local").And.NotContain("2852039166");
    }

    [Fact]
    public void A_very_long_address_is_refused()
    {
        PeerAddressPolicy.Canonicalize("https://peer.local/" + new string('a', PeerAddressPolicy.MaxLength)).Ok.Should().BeFalse();
    }

    [Fact]
    public void Loopback_and_private_addresses_are_not_refused_because_nodes_live_on_lans()
    {
        foreach (var lan in new[] { "http://127.0.0.1:5300", "http://[::1]:5300", "http://localhost", "http://192.168.0.7", "http://172.16.4.2", "http://10.1.2.3:5300", "http://fileserver" })
            PeerAddressPolicy.Canonicalize(lan).Ok.Should().BeTrue($"{lan} is a normal place for a node");
    }

    [Fact]
    public void A_fingerprint_tells_hosts_apart_without_carrying_the_host_or_the_path()
    {
        var a = PeerAddressPolicy.Fingerprint("https://peer.local/bmb");
        var samePlace = PeerAddressPolicy.Fingerprint("HTTPS://PEER.LOCAL:443/other");
        var other = PeerAddressPolicy.Fingerprint("https://other.local");
        var otherPort = PeerAddressPolicy.Fingerprint("https://peer.local:8443");

        a.Should().HaveLength(12).And.MatchRegex("^[0-9a-f]{12}$");
        a.Should().NotContain("peer").And.NotContain("local");
        samePlace.Should().Be(a, "the path does not tell a device from itself");
        other.Should().NotBe(a);
        otherPort.Should().NotBe(a);
        PeerAddressPolicy.Fingerprint(null).Should().Be("none");
        PeerAddressPolicy.Fingerprint("").Should().Be("none");
    }
}
