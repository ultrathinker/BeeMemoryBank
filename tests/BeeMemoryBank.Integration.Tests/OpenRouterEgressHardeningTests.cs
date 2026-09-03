using System.Net.Http;
using BeeMemoryBank.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// M3 regression test: <c>OpenRouterClient.cs</c> documents its egress as "pinned to
/// https://openrouter.ai ... prevents an SSRF-style redirect of vault content to an attacker
/// host", but that comment described the URL constant only — the client itself used to be
/// resolved from DI as the plain default <c>HttpClient</c> (<c>AddHttpClient()</c> +
/// <c>AddTransient&lt;HttpClient&gt;</c> in Program.cs), whose handler has
/// <c>AllowAutoRedirect = true</c>. A 307/308 from openrouter.ai would silently re-POST the
/// entire conversation (decrypted article bodies included) to wherever the redirect pointed —
/// the Authorization header is stripped cross-origin by HttpClient, but the payload is not.
///
/// This test verifies the ACTUAL production DI wiring (via the same WebApplicationFactory the
/// rest of the integration suite uses) gives <see cref="OpenRouterClient"/> a dedicated typed
/// HttpClient with auto-redirect disabled, rather than re-registering the handler in the test
/// itself (which would only prove the test's own code works, not Program.cs's).
/// </summary>
public class OpenRouterEgressHardeningTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public void OpenRouterClient_TypedHttpClient_HasAutoRedirectDisabled()
    {
        var handlerFactory = _factory.Services.GetRequiredService<IHttpMessageHandlerFactory>();

        // AddHttpClient<TClient>() (no explicit name) registers the typed client's HttpClient
        // under the name typeof(TClient).Name — this is how OpenRouterClient is now registered
        // in Program.cs (replacing a plain AddScoped<OpenRouterClient>() that shared the
        // default/unconfigured HttpClient with everything else in the app).
        var handler = handlerFactory.CreateHandler(nameof(OpenRouterClient));

        // Walk past any DelegatingHandlers (logging, etc.) added by the HttpClientFactory
        // pipeline down to the actual primary handler set via ConfigurePrimaryHttpMessageHandler.
        while (handler is DelegatingHandler delegating && delegating.InnerHandler is not null)
            handler = delegating.InnerHandler;

        handler.Should().BeOfType<SocketsHttpHandler>()
            .Which.AllowAutoRedirect.Should().BeFalse(
                "a redirect from openrouter.ai must never be silently followed with the " +
                "in-flight conversation (decrypted vault content) as the payload");
    }

    [Fact]
    public void OpenRouterClient_ResolvesFromDI_AsATypedClient()
    {
        // Sanity companion to the handler test: OpenRouterClient itself must still resolve
        // (constructor injection of its dedicated HttpClient + ILogger) after the registration
        // change from AddScoped<OpenRouterClient>() to AddHttpClient<OpenRouterClient>().
        using var scope = _factory.Services.CreateScope();
        var client = scope.ServiceProvider.GetService<OpenRouterClient>();
        client.Should().NotBeNull();
    }
}
