using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Infrastructure.OsAutoUnlock;
using BeeMemoryBank.Infrastructure.Secrets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// A secret store that cannot answer (a locked macOS Keychain, a refused or damaged item) must not turn the two STATUS endpoints into an HTTP 500:
/// <c>GET /api/keys/auto-unlock/status</c> answers 503 with a plain text that names no purpose and no secret, and
/// <c>GET /api/session/lock-impact</c> still answers - auto-unlock "not enabled" (the state it can prove), supported - and logs one warning
/// line. The host is the ordinary Integration host with the auto-unlock service rebuilt over an in-memory store that refuses on demand, on every
/// operating system; no real keychain or DPAPI is involved.
/// </summary>
public class OsSecretStoreUnavailableEndpointTests : IAsyncLifetime
{
    private const string Password = "unavailableStorePassword";
    private readonly RefusingStoreFactory _factory = new();
    private int _adminUserId;

    private sealed class RefusingStoreFactory : BmbWebApplicationFactory
    {
        public InMemoryUserSecretStore Store { get; } = new();
        public CapturingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureLogging(logging => logging.AddProvider(Logs));
            builder.ConfigureTestServices(services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IUserSecretStore>(Store));
                services.Replace(ServiceDescriptor.Singleton(sp => new OsAutoUnlockService(
                    sp.GetRequiredService<IKeySlotRepository>(), sp.GetRequiredService<SessionService>(), DataPath, Store)));
            });
        }
    }

    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, Entries);
        public void Dispose() { }

        private sealed class Capture(string category, ConcurrentQueue<(string, LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }

    public async Task InitializeAsync()
    {
        using var client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
        (await client.PostAsJsonAsync("/api/session/unlock", new { password = Password })).EnsureSuccessStatusCode();
        using var scope = _factory.Services.CreateScope();
        _adminUserId = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByUsernameAsync("admin"))!.Id;

        using var admin = Admin();
        (await admin.PostAsync("/api/keys/auto-unlock/enable", null)).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    private HttpClient Admin()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove("X-User-Role");
        client.DefaultRequestHeaders.Add("X-User-Role", "superadmin");
        client.DefaultRequestHeaders.Add("X-User-Id", _adminUserId.ToString());
        return client;
    }

    public static IEnumerable<object[]> Refusals() =>
        Enum.GetValues<UserSecretStoreFailureKind>().Select(kind => new object[] { kind });

    private void Refuse(UserSecretStoreFailureKind kind) =>
        _factory.Store.ReadFailure = new UserSecretStoreException(kind, "refused by the test");

    [Fact]
    public async Task WhileTheStoreAnswers_BothEndpointsReportTheEnabledSlot()
    {
        using var client = Admin();

        var status = await client.GetAsync("/api/keys/auto-unlock/status");
        var impact = await client.GetAsync("/api/session/lock-impact");

        status.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await status.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("enabled").GetBoolean().Should().BeTrue();
        body.GetProperty("supported").GetBoolean().Should().BeTrue();
        impact.StatusCode.Should().Be(HttpStatusCode.OK);
        (await impact.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("osAutoUnlockEnabled").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Status_Is503_WithAPlainText_WhenTheStoreRefuses_AndRecovers(UserSecretStoreFailureKind kind)
    {
        using var client = Admin();
        Refuse(kind);

        var response = await client.GetAsync("/api/keys/auto-unlock/status");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        var error = json.RootElement.EnumerateObject().Single(p => p.NameEquals("error") || p.NameEquals("Error")).Value.GetString();
        error.Should().Be("The OS secret store is locked or unavailable.");
        text.Should().NotContain("os-auto-unlock").And.NotContain("default").And.NotContain("refused by the test").And.NotContain(kind.ToString());

        _factory.Store.ReadFailure = null;
        (await client.GetAsync("/api/keys/auto-unlock/status")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task LockImpact_StillAnswers_NotEnabledAndSupported_AndLogsOneWarning_WhenTheStoreRefuses(UserSecretStoreFailureKind kind)
    {
        using var client = Admin();
        Refuse(kind);

        var response = await client.GetAsync("/api/session/lock-impact");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var impact = await response.Content.ReadFromJsonAsync<JsonElement>();
        impact.GetProperty("osAutoUnlockEnabled").GetBoolean().Should().BeFalse("the state that can be proved is 'not enabled'");
        impact.GetProperty("osAutoUnlockSupported").GetBoolean().Should().BeTrue("the platform does support it; its store is merely unavailable");
        impact.GetProperty("agents").GetArrayLength().Should().Be(0);
        var warnings = _factory.Logs.Entries
            .Where(e => e.Category == "BeeMemoryBank.Api.Endpoints.SessionEndpoints" && e.Level == LogLevel.Warning).ToList();
        warnings.Should().ContainSingle("one warning line per request");
        warnings[0].Message.Should().Contain(kind.ToString()).And.NotContain("os-auto-unlock").And.NotContain("refused by the test");
    }

    [Fact]
    public async Task AnUnsupportedStore_IsStillTheNotSupportedAnswer_NotA503()
    {
        // 503 is for a store that supports the platform but cannot answer now; "not supported" keeps its existing answer.
        using var client = Admin();
        _factory.Store.ReadFailure = null;
        _factory.Store.IsSupported = false;

        var response = await client.GetAsync("/api/keys/auto-unlock/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "an unsupported store is the existing 'not supported' answer, not a 503");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("supported").GetBoolean().Should().BeFalse();
        _factory.Store.IsSupported = true;
    }
}
