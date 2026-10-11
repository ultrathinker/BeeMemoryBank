using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// Step A hook H1 (docs/design/step-a-spec.md 15.2) in AppCore: a blind copy (phone, desktop) has the blind store under its
/// data folder, the format-2 reader, the store as the baseline it serves and summarises. It writes no package.
/// </summary>
public sealed class BlindAppCoreFormat2CompositionTests
{
    [Fact]
    public void TheBlindCopy_HasTheBlindStoreAndTheReader()
    {
        var services = new ServiceCollection();
        var dir = Path.Combine(Path.GetTempPath(), "bmb-appcore-f2-" + Guid.NewGuid().ToString("N"));
        BlindMobileServices.AddBlindAppCore(services, new BlindAppOptions(dir, Path.Combine(dir, "beememorybank.db")));
        using var sp = services.BuildServiceProvider();

        var store = sp.GetRequiredService<IBaselineStore>().Should().BeOfType<BaselineStore>().Subject;
        store.CurrentDirectory.Should().Be(Path.Combine(dir, "baselines", "current"));
        sp.GetRequiredService<IBlindPackageV2Reader>().Should().BeOfType<BlindPackageV2Reader>();
        sp.GetRequiredService<IBaselineSource>().Should().BeOfType<BlindBaselineSource>();
        sp.GetRequiredService<IBlindStoreSummarySource>().Should().BeOfType<BlindStoreSummarySource>();
        sp.GetService<IBlindPackageV2Writer>().Should().BeNull("a blind copy pulls packages, it serves none");
    }
}
