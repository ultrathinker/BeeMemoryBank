using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The exception handler must sit at the TOP of the API pipeline, not after the identity gates:
/// it only sees exceptions thrown by middleware AFTER it, so a failure inside one of the gates
/// (public surface, rate limit, maintenance, agent auth, caller scope, MCP guards) has to come
/// back as the mapped JSON error response — never as the server default page or a raw rethrow.
/// </summary>
public class ExceptionHandlerPipelineOrderTests
{
    [Fact]
    public async Task FailureInsideCallerScopeGate_YieldsMappedJson500()
    {
        using var factory = new ExplodingScopeFactory();
        var client = factory.CreateClient();
        // A non-superadmin web caller is what sends CallerScopeMiddleware into
        // FolderAccessService.GetFullAccessInfoAsync — the dependency this test replaces.
        client.DefaultRequestHeaders.Remove("X-User-Role");
        client.DefaultRequestHeaders.Add("X-User-Role", "user");
        client.DefaultRequestHeaders.Add("X-User-Id", "2");

        var resp = await client.GetAsync("/api/articles");

        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("Internal server error");
        // The mapped generic message, not the exception internals.
        (await resp.Content.ReadAsStringAsync()).Should().NotContain("Exploded");
    }

    /// <summary>Replaces FolderAccessService with a resolution-time fault of an unmapped exception
    /// type (not one of the typed exceptions ExceptionStatusMap translates), so the response proves
    /// the handler's default 500 arm is what answered — reachable only if the handler is above
    /// CallerScopeMiddleware in the chain.</summary>
    private sealed class ExplodingScopeFactory : BmbWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            // Test services run after the app's own registrations, so this replaces the real
            // FolderAccessService for every request in this factory.
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<FolderAccessService>();
                services.AddScoped<FolderAccessService>(_ =>
                    throw new ExplodedScopeException("FolderAccessService exploded"));
            });
        }
    }

    private sealed class ExplodedScopeException(string message) : Exception(message);
}
