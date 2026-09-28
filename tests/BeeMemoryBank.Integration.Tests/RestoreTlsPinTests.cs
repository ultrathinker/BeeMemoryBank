using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The restore client's TLS pin: the pin the user typed, in <see cref="Spki"/>'s form,
/// enforced by <see cref="SpkiPinRegistry"/> through the request's own pin — the same check a PC adds a
/// blind node with. That every restore request carries it: RestoreScenarioTests.
/// </summary>
public class RestoreTlsPinTests
{
    private static X509Certificate2 SelfSigned(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static SpkiPinRegistry Registry() =>
        new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());

    private static HttpRequestMessage PinnedTo(string pin)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "https://blind.example/api/blind/restore/package");
        req.Options.Set(SpkiPinRegistry.ExplicitPin, pin);
        return req;
    }

    [Fact]
    public void TheTypedPin_AcceptsThePinnedKey_EvenSelfSigned()
    {
        using var blind = SelfSigned("blind");
        using var req = PinnedTo(Spki.Of(blind));

        RestoreTlsPin.IsWellFormed(Spki.Of(blind)).Should().BeTrue();
        Registry().Validate(req, blind, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeTrue();
    }

    [Fact]
    public void TheTypedPin_RefusesAnyOtherKey()
    {
        using var blind = SelfSigned("blind");
        using var impostor = SelfSigned("blind"); // same name, different key
        using var req = PinnedTo(Spki.Of(blind));

        Registry().Validate(req, impostor, SslPolicyErrors.None).Should().BeFalse();
        Registry().Validate(req, null, SslPolicyErrors.RemoteCertificateNotAvailable).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AAAA")]
    [InlineData("!!!!AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void MalformedPin_IsNotAPin(string? pin)
    {
        RestoreTlsPin.IsWellFormed(pin).Should().BeFalse();
    }
}
