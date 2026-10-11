using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// Step A (docs/design/step-a-spec.md 9.4): an AppCore blind copy (phone, desktop) stores and relays sealed events and never
/// opens one; nothing is ever held there. The composition says so.
/// </summary>
public sealed class BlindAppCoreEnvelopeOpenerTests
{
    [Fact]
    public void TheBlindCopy_ResolvesTheRelayOpener_AndHoldsNothing()
    {
        var services = new ServiceCollection();
        var dir = Path.Combine(Path.GetTempPath(), "bmb-appcore-opener-" + Guid.NewGuid().ToString("N"));
        BlindMobileServices.AddBlindAppCore(services, new BlindAppOptions(dir, Path.Combine(dir, "beememorybank.db")));
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IEnvelopeOpener>().Should().BeOfType<RelayEnvelopeOpener>();
        sp.GetRequiredService<IHeldEventDrain>().Should().BeOfType<NoHeldEventDrain>();
        sp.GetRequiredService<ISyncProtocolPolicy>().Should().BeOfType<SyncProtocolPolicy>();
        sp.GetRequiredService<IHeldEventStatus>().Should().BeOfType<HeldEventStatus>();
        services.Count(d => d.ServiceType == typeof(IEnvelopeOpener)).Should().Be(1);
    }
}
