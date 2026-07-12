using System.Net;
using BeeMemoryBank.Core.Services;
using Makaretu.Dns;

namespace BeeMemoryBank.Core.Tests.Mdns;

/// <summary>
/// Deterministic, network-free tests for <see cref="MdnsBrowser.TryParse"/> and
/// <see cref="MdnsAnnouncer.BuildProfile"/>. These always pass and genuinely exercise the TXT/SRV/A
/// parsing + profile-building logic that the live round-trip test depends on.
/// See <c>MdnsRoundTripTests</c> for the real multicast announce/browse path.
/// </summary>
public class MdnsParsingTests
{
    private static readonly Guid SampleNodeId = Guid.Parse("12345678-1234-1234-1234-123456789abc");

    [Fact]
    public void TryParse_FullRecord_HostFromAddressKeyedOnTarget()
    {
        var instance = new DomainName("11111111-1111-1111-1111-111111111abc._beememorybank._tcp.local");
        var target = new DomainName("11111111-1111-1111-1111-111111111abc-beememorybank.local");

        var msg = new Message();
        msg.Answers.Add(new PTRRecord { Name = new DomainName(MdnsConstants.QualifiedServiceName), DomainName = instance });
        msg.Answers.Add(new SRVRecord { Name = instance, Port = 5301, Target = target });

        var txt = new TXTRecord { Name = instance };
        txt.Strings.Add("nodeId=" + SampleNodeId);
        txt.Strings.Add("name=My Laptop");
        txt.Strings.Add("ver=1.0.1");
        txt.Strings.Add("https=true");
        msg.Answers.Add(txt);

        msg.Answers.Add(new ARecord { Name = target, Address = IPAddress.Parse("192.168.1.42") });

        var ok = MdnsBrowser.TryParse(msg, instance, out var rec);

        ok.Should().BeTrue();
        rec.Should().NotBeNull();
        rec!.NodeId.Should().Be(SampleNodeId);
        rec.Name.Should().Be("My Laptop");
        rec.Version.Should().Be("1.0.1");
        rec.Https.Should().BeTrue();
        rec.Host.Should().Be("192.168.1.42");
        rec.Port.Should().Be(5301);
        rec.Url.Should().Be("https://192.168.1.42:5301");
    }

    [Fact]
    public void TryParse_HttpsFalse_BuildsHttpUrl()
    {
        var instance = new DomainName("node-x._beememorybank._tcp.local");
        var msg = new Message();
        msg.Answers.Add(new SRVRecord { Name = instance, Port = 5301, Target = new DomainName("node-x.local") });

        var txt = new TXTRecord { Name = instance };
        txt.Strings.Add("nodeId=" + SampleNodeId);
        txt.Strings.Add("name=Node X");
        txt.Strings.Add("https=false");
        msg.Answers.Add(txt);

        // No A record present -> host falls back to the SRV target hostname.
        var ok = MdnsBrowser.TryParse(msg, instance, out var rec);

        ok.Should().BeTrue();
        rec!.Host.Should().Be("node-x.local");
        rec.Https.Should().BeFalse();
        rec.Url.Should().Be("http://node-x.local:5301");
    }

    [Fact]
    public void TryParse_MissingSrv_ReturnsFalse()
    {
        var instance = new DomainName("lonely._beememorybank._tcp.local");
        var msg = new Message();
        // Only a TXT, no SRV -> no port -> not usable.
        var txt = new TXTRecord { Name = instance };
        txt.Strings.Add("nodeId=" + SampleNodeId);
        msg.Answers.Add(txt);

        var ok = MdnsBrowser.TryParse(msg, instance, out var rec);

        ok.Should().BeFalse();
        rec.Should().BeNull();
    }

    [Fact]
    public void TryParse_MalformedTxtValue_IsIgnoredGracefully()
    {
        var instance = new DomainName("badtxt._beememorybank._tcp.local");
        var msg = new Message();
        msg.Answers.Add(new SRVRecord { Name = instance, Port = 5301, Target = new DomainName("badtxt.local") });

        var txt = new TXTRecord { Name = instance };
        txt.Strings.Add("nodeId=not-a-guid");      // unparseable -> Guid.Empty
        txt.Strings.Add("noequalsign");            // malformed entry -> ignored
        txt.Strings.Add("=emptykey");              // empty key -> ignored
        msg.Answers.Add(txt);
        msg.Answers.Add(new ARecord { Name = new DomainName("badtxt.local"), Address = IPAddress.Parse("10.0.0.5") });

        var ok = MdnsBrowser.TryParse(msg, instance, out var rec);

        ok.Should().BeTrue();
        rec!.NodeId.Should().Be(Guid.Empty);
        rec.Host.Should().Be("10.0.0.5");
    }

    [Fact]
    public void BuildProfile_IncludesAllExpectedTxtKeys()
    {
        var profile = MdnsAnnouncer.BuildProfile(SampleNodeId, "Display Name", "2.3.4", https: true, port: 5301);

        profile.ServiceName.ToString().Should().Be(MdnsConstants.ServiceType);
        var txt = profile.Resources.OfType<TXTRecord>().Single();
        var dict = txt.Strings
            .Select(s => s.Split('=', 2))
            .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");

        dict[MdnsConstants.TxtNodeId].Should().Be(SampleNodeId.ToString());
        dict[MdnsConstants.TxtName].Should().Be("Display Name");
        dict[MdnsConstants.TxtVersion].Should().Be("2.3.4");
        dict[MdnsConstants.TxtHttps].Should().Be("true");

        profile.Resources.OfType<SRVRecord>().Single().Port.Should().Be(5301);
    }
}
