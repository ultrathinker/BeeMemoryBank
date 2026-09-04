using BeeMemoryBank.Api.Endpoints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// <c>POST /api/sync/probe-relay</c> asks this node to fetch a URL a PEER supplied, so another node
/// can learn whether its own address is reachable from the outside. The only thing standing between
/// that and an SSRF is <c>IPublicHostValidator</c>, which validates the host we were GIVEN — and the
/// default HttpClient handler follows redirects, so a host that passes the check can 302 the request
/// onto loopback, an RFC1918 address or a cloud metadata endpoint, and the resulting status code
/// goes back to whoever asked. Verified against the real DI wiring rather than a handler the test
/// builds itself, which would only prove the test's own code works.
/// </summary>
public class ProbeRelayEgressHardeningTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public void ProbeRelayClient_HasAutoRedirectDisabled()
    {
        var handlerFactory = _factory.Services.GetRequiredService<IHttpMessageHandlerFactory>();

        var handler = handlerFactory.CreateHandler(SyncEndpoints.NoRedirectClientName);

        // Walk past any DelegatingHandlers the factory pipeline adds down to the primary handler.
        while (handler is DelegatingHandler delegating && delegating.InnerHandler is not null)
            handler = delegating.InnerHandler;

        handler.Should().BeOfType<SocketsHttpHandler>()
            .Which.AllowAutoRedirect.Should().BeFalse(
                "the relay target comes from a peer; following its redirect would let a host that " +
                "passed the public-address check bounce us onto an internal one");
    }

    [Fact]
    public void TheDefaultClient_IsUnaffected()
    {
        // The hardening must be a dedicated client, not a global change: plenty of outbound calls
        // (peer sync, snapshot download, update feed) legitimately follow redirects, and silently
        // turning that off for all of them would break them in ways no test here would catch.
        var handlerFactory = _factory.Services.GetRequiredService<IHttpMessageHandlerFactory>();

        var handler = handlerFactory.CreateHandler(string.Empty);
        while (handler is DelegatingHandler delegating && delegating.InnerHandler is not null)
            handler = delegating.InnerHandler;

        (handler as SocketsHttpHandler)?.AllowAutoRedirect.Should().BeTrue();
    }
}
