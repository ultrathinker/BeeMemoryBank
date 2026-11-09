using System.Buffers.Text;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// BMB-337: the fill is local bookkeeping and has no version, so it never stands in the way of a real whitelist event. An update that carries the TLS setting of
/// a peer still wins over a filled key, whichever came first, exactly as it wins over a row that has none.
/// </summary>
public class PeerTlsBackfillEventOrderTests : IAsyncLifetime
{
    private static readonly string Pin = Base64Url.EncodeToString(Enumerable.Repeat((byte)1, 32).ToArray());
    private static readonly string NewPin = Base64Url.EncodeToString(Enumerable.Repeat((byte)9, 32).ToArray());

    private sealed class Fixture : SyncTestFixture { }

    private Fixture _origin = null!;
    private Fixture _receiver = null!;
    private Guid _originId;
    private readonly Guid _blind = BlindNodeId.NewId();

    public async Task InitializeAsync()
    {
        _origin = new Fixture();
        await _origin.InitializeAsync();
        await _origin.InitService.InitializeAsync("admin", "Origin", "pass");
        await _origin.Session.UnlockAsync("pass");
        _receiver = new Fixture();
        await _receiver.InitializeAsync();
        await _receiver.InitService.InitializeAsync("admin", "Receiver", "pass");
        await _receiver.Session.UnlockAsync("pass");

        var origin = (await _origin.NodeRepo.GetAsync())!;
        _originId = origin.NodeId;
        await _receiver.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = _originId, DisplayName = "Origin", Ed25519PublicKey = origin.Ed25519PublicKey, IsSuperadmin = true,
            Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        foreach (var f in new[] { _origin, _receiver })
            await f.WhitelistRepo.CreateAsync(new WhitelistEntry
            {
                NodeId = _blind, DisplayName = "Blind", Ed25519PublicKey = new byte[32], ApiAddress = "https://blind.example:5610",
                Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
    }

    public async Task DisposeAsync()
    {
        await _origin.DisposeAsync();
        await _receiver.DisposeAsync();
    }

    private async Task FillAsync(string pin)
    {
        var self = (await _receiver.NodeRepo.GetAsync())!.NodeId;
        var fill = new PeerTlsBackfill(_receiver.WhitelistRepo, NullLogger<PeerTlsBackfill>.Instance);
        var standing = new MyStanding(_originId, 4, true, false, [new PeerProtocolSeen(_blind, "Blind", 4, null, DateTime.UtcNow, TlsTrust: "pin", TlsSpki: pin)]);
        await fill.FillAsync(_originId, standing, self);
    }

    private async Task ApplyLastAsync() =>
        await _receiver.ApplyFromAsync(_origin, (await _origin.EventLogRepo.GetAfterSequenceAsync(0)).Last());

    private async Task<WhitelistEntry> RowAsync() => (await _receiver.WhitelistRepo.GetByNodeIdAsync(_blind))!;

    [Fact]
    public async Task AWhitelistUpdate_CarryingAKey_StillWins_OverAFilledOne()
    {
        await FillAsync(Pin);
        (await RowAsync()).TlsSpki.Should().Be(Pin);

        var version = await _origin.EventLogger.LogWhitelistUpdateAsync(_blind, null, null, tlsSpki: NewPin);
        await ApplyLastAsync();

        var row = await RowAsync();
        (row.TlsTrust, row.TlsSpki).Should().Be(("pin", NewPin), "the key a node's owner set later is the key");
        row.VersionOf(WhitelistConcerns.Tls).Should().Be(version, "the event dated the setting; the fill never did");
    }

    [Fact]
    public async Task AWhitelistUpdate_SayingPublicCa_StillWins_OverAFilledKey()
    {
        await FillAsync(Pin);

        await _origin.EventLogger.LogWhitelistUpdateAsync(_blind, null, null, tlsTrust: "public-ca");
        await ApplyLastAsync();

        var row = await RowAsync();
        (row.TlsTrust, row.TlsSpki).Should().Be(("public-ca", (string?)null));
    }

    [Fact]
    public async Task AWhitelistUpdate_RemovingTheSetting_StillWins_OverAFilledKey()
    {
        await FillAsync(Pin);

        await _origin.EventLogger.LogWhitelistUpdateAsync(_blind, null, null, tlsTrust: "none");
        await ApplyLastAsync();

        var row = await RowAsync();
        (row.TlsTrust, row.TlsSpki).Should().Be(((string?)null, (string?)null));

        // The empty columns of an off are not the empty columns of a row nobody filled: the next standing does not put the key back.
        await FillAsync(Pin);
        row = await RowAsync();
        (row.TlsTrust, row.TlsSpki).Should().Be(((string?)null, (string?)null), "an off stays off");
    }

    [Fact]
    public async Task AKeyAnEventBroughtFirst_IsNotReplacedByAFill()
    {
        var version = await _origin.EventLogger.LogWhitelistUpdateAsync(_blind, null, null, tlsSpki: NewPin);
        await ApplyLastAsync();

        await FillAsync(Pin);

        var row = await RowAsync();
        (row.TlsTrust, row.TlsSpki).Should().Be(("pin", NewPin));
        row.VersionOf(WhitelistConcerns.Tls).Should().Be(version);
    }
}
