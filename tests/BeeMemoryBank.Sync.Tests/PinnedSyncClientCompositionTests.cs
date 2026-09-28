using BeeMemoryBank.Sync.Blind;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Review L-stage1 #6: every client a node syncs through carries the pinned handler — the
/// scheduler's on any host that calls AddSync (the phone's foreground service included), and a
/// default client composed the way the phone's MauiProgram does it, next to its own handlers.
/// </summary>
public class PinnedSyncClientCompositionTests
{
    [Fact]
    public void AddSync_PinsTheSchedulersClient()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddHttpClient();
        services.AddSync();

        var chain = Chain(services.BuildServiceProvider(), SyncScheduler.HttpClientName);

        chain.Should().Contain(h => h is SpkiPinGuardHandler);
    }

    [Fact]
    public void ADefaultClient_WithUsePinnedSyncHandler_IsPinned_AndKeepsItsOwnHandlers()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSync();
        services.AddTransient<MarkerHandler>();
        services.AddHttpClient(string.Empty).UsePinnedSyncHandler().AddHttpMessageHandler<MarkerHandler>();

        var chain = Chain(services.BuildServiceProvider(), Microsoft.Extensions.Options.Options.DefaultName);

        chain.Should().Contain(h => h is SpkiPinGuardHandler).And.Contain(h => h is MarkerHandler);
    }

    private static List<HttpMessageHandler> Chain(IServiceProvider sp, string name)
    {
        var chain = new List<HttpMessageHandler>();
        HttpMessageHandler? handler = sp.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);
        while (handler != null)
        {
            chain.Add(handler);
            handler = (handler as DelegatingHandler)?.InnerHandler;
        }
        return chain;
    }

    private sealed class MarkerHandler : DelegatingHandler;
}
