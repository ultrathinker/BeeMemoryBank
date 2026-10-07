using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Blind;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The trust mode of a callable node is the authority over its pin (ADR 0007, review INT-03): wherever a row is written from a
/// manifest, a package or a record, or read back for a screen, a pin that the mode does not call for is a leftover and never
/// outlives the mode — a stale key must not override the chosen policy or reject a legitimate certificate after a rotation.
/// </summary>
public sealed class BlindTrustAuthorityTests
{
    private const string OldPin = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string NewPin = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    private static WhitelistEntry ProducerRow(string? trust, string? spki) => new()
    {
        NodeId = Guid.NewGuid(), DisplayName = "PC", Ed25519PublicKey = new byte[32], ApiAddress = "https://bmb.example.org",
        TlsTrust = trust, TlsSpki = spki, Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };

    private static BlindManifestPeer Claim(string? trust, string? spki) => new(
        Guid.NewGuid(), "PC", Convert.ToBase64String(new byte[32]), "https://bmb.example.org", true, spki, 1, null, trust);

    /// <summary>The seeding computer's row is moved to the normal certificate by its manifest: the old key does not stay behind.</summary>
    [Fact]
    public void AManifestThatSaysPublicCa_DropsTheOldPinOfTheProducersRow()
    {
        var row = ProducerRow(BlindTrust.Pin, OldPin);

        BlindSeedService.ApplyClaimedTrust(row, Claim(BlindTrust.PublicCa, null));

        (row.TlsTrust, row.TlsSpki).Should().Be((BlindTrust.PublicCa, null));
    }

    [Fact]
    public void AManifestThatSaysPublicCa_AndStillCarriesAPin_KeepsNoPin()
    {
        var row = ProducerRow(null, null);

        BlindSeedService.ApplyClaimedTrust(row, Claim(BlindTrust.PublicCa, NewPin));

        (row.TlsTrust, row.TlsSpki).Should().Be((BlindTrust.PublicCa, null));
    }

    [Theory]
    [InlineData("future-mode")]
    [InlineData("PIN")]
    public void AManifestWithAModeThisBuildDoesNotKnow_IsNotAPin_AndKeepsNoPin(string mode)
    {
        var row = ProducerRow(BlindTrust.Pin, OldPin);

        BlindSeedService.ApplyClaimedTrust(row, Claim(mode, NewPin));

        (row.TlsTrust, row.TlsSpki).Should().Be((mode, null));
        BlindTrust.IsKnown(row.EffectiveTlsTrust).Should().BeFalse();
    }

    [Fact]
    public void AManifestPin_ReplacesThePin()
    {
        var row = ProducerRow(BlindTrust.Pin, OldPin);

        BlindSeedService.ApplyClaimedTrust(row, Claim(BlindTrust.Pin, NewPin));

        (row.TlsTrust, row.TlsSpki).Should().Be((BlindTrust.Pin, NewPin));
    }

    /// <summary>A package of an older build says only the pin: that is a pinned row, as it always was.</summary>
    [Fact]
    public void AManifestFromAnOlderBuild_WithOnlyAPin_IsAPinnedRow()
    {
        var row = ProducerRow(null, null);

        BlindSeedService.ApplyClaimedTrust(row, Claim(null, NewPin));

        (row.TlsTrust, row.TlsSpki).Should().Be((BlindTrust.Pin, NewPin));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(BlindTrust.Pin, null)] // "pin" with nothing to pin says nothing usable
    public void AManifestThatSaysNothingUsable_LeavesTheRowsOwnTrust(string? trust, string? spki)
    {
        var row = ProducerRow(BlindTrust.Pin, OldPin);

        BlindSeedService.ApplyClaimedTrust(row, Claim(trust, spki));

        (row.TlsTrust, row.TlsSpki).Should().Be((BlindTrust.Pin, OldPin));
    }

    /// <summary>What the Admin screens are told about a row's key follows the mode too.</summary>
    [Theory]
    [InlineData(BlindTrust.PublicCa, null)]
    [InlineData("future-mode", null)]
    [InlineData(BlindTrust.Pin, OldPin)]
    [InlineData(null, OldPin)]
    public void TheKeyAnAdminScreenIsTold_IsTheOneTheModeCallsFor(string? trust, string? shown)
    {
        var response = WhitelistEntryResponse.From(ProducerRow(trust, OldPin));

        response.TlsSpki.Should().Be(shown);
    }
}
