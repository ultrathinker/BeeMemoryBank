using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>Which host registers the baseline producer side (hook H4, docs/design/step-a-spec.md 15.2): a full node does, a blind host never does.</summary>
public class BaselineCompositionTests
{
    [Fact]
    public async Task A_full_node_registers_the_producer_the_scheduler_the_source_and_the_activation_observer()
    {
        using var node = new StepAFactory();
        await node.InitializeNodeAsync();
        var sp = node.Services;

        sp.GetRequiredService<IBaselineSource>().Should().BeOfType<FullNodeBaselineSource>();
        sp.GetRequiredService<IBaselineScheduler>().Should().BeSameAs(sp.GetRequiredService<BaselineScheduler>());
        sp.GetServices<IHostedService>().Should().ContainSingle(s => s is BaselineScheduler);
        sp.GetRequiredService<IBaselineProducer>().Should().BeOfType<BaselineProducerRunner>();
        sp.GetServices<IMetadataActivationObserver>().Should().ContainSingle(o => o is FullNodeActivationObserver);
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetService<BaselineProducer>().Should().NotBeNull("the producer resolves with every dependency (the held-event status is the test host's)");
    }

    [Fact]
    public async Task A_blind_role_host_has_none_of_it()
    {
        using var blind = new BlindNodeFactory();
        await blind.InitializeNodeAsync();
        var sp = blind.Services;

        sp.GetService<BaselineScheduler>().Should().BeNull();
        sp.GetService<IBaselineScheduler>().Should().BeNull();
        sp.GetService<IBaselineProducer>().Should().BeNull();
        sp.GetService<FullNodeBaselineSource>().Should().BeNull();
        sp.GetServices<IMetadataActivationObserver>().Should().NotContain(o => o is FullNodeActivationObserver);
    }
}
