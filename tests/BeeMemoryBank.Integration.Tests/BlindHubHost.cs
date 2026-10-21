using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Startup;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// A full node on real Kestrel, listening on https on the loopback interface with a certificate a test chooses — a hub as a
/// blind copy reaches it (ADR 0007): over a real TLS handshake, with no internal key. A second, plain-http listener is the
/// node's own front for the test (login, unlock, join), as the Web layer is in production. The certificate is read on every
/// handshake, so a test can swap it between calls.
/// </summary>
internal sealed class BlindHubHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _dataDir;

    /// <summary>The certificate the https listener presents at the next handshake.</summary>
    public X509Certificate2 Certificate { get; set; }

    public int HttpPort { get; }
    public int HttpsPort { get; }
    public IServiceProvider Services => _app.Services;

    /// <summary>Plain http, with the internal key and the superadmin role: what the Web layer is to a node.</summary>
    public HttpClient Admin { get; }

    /// <summary>The hub's address as a blind copy is told it, by a name the test certificates are issued for.</summary>
    public string Origin => $"https://localhost:{HttpsPort}";

    private BlindHubHost(WebApplication app, string dataDir, X509Certificate2 certificate, int httpPort, int httpsPort)
    {
        _app = app;
        _dataDir = dataDir;
        Certificate = certificate;
        HttpPort = httpPort;
        HttpsPort = httpsPort;
        Admin = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{httpPort}") };
        Admin.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
        Admin.DefaultRequestHeaders.Add("X-User-Role", "superadmin");
    }

    /// <param name="configure">Registrations a test adds after the node's own (the last one wins): its own probe, clock or trust anchors.</param>
    public static async Task<BlindHubHost> StartAsync(X509Certificate2 certificate, string displayName, string password,
        Action<IServiceCollection>? configure = null)
    {
        // Same key as the factory-based tests: BMB_INTERNAL_KEY is process-wide.
        Environment.SetEnvironmentVariable("BMB_INTERNAL_KEY", BmbWebApplicationFactory.InternalKeyForTests);
        var dataDir = Path.Combine(Path.GetTempPath(), "bmb_hub_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        BlindHubHost? host = null;
        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = "Testing";
        builder.WebHost.UseSetting("BeeMemoryBank:DataPath", dataDir);
        builder.WebHost.UseKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, 0);
            o.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(https =>
                https.ServerCertificateSelector = (_, _) => host!.Certificate));
        });
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        builder.Logging.AddProvider(new ErrorCapture(errors));
        builder.AddBeeApiServices(dataDir);
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        await app.RunBeeApiStartupTasksAsync(dataDir);
        app.UseBeeApiPipeline();
        app.MapBeeApiEndpoints();
        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var http = new Uri(addresses.Single(a => a.StartsWith("http://", StringComparison.Ordinal)));
        var https = new Uri(addresses.Single(a => a.StartsWith("https://", StringComparison.Ordinal)));
        host = new BlindHubHost(app, dataDir, certificate, http.Port, https.Port);

        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<InitializationService>().InitializeAsync("admin", displayName, password);
        using (var unlocked = await host.Admin.PostAsJsonAsync("/api/session/unlock", new { password }))
        {
            if (!unlocked.IsSuccessStatusCode)
                throw new InvalidOperationException($"The hub's unlock answered {(int)unlocked.StatusCode}: {await unlocked.Content.ReadAsStringAsync()}"
                    + " | " + string.Join(" | ", errors.TakeLast(5)));
        }
        return host;
    }

    /// <summary>The errors the hub logged, kept so that a failed start can say why.</summary>
    private sealed class ErrorCapture(System.Collections.Concurrent.ConcurrentQueue<string> sink) : Microsoft.Extensions.Logging.ILoggerProvider
    {
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Capture(sink, categoryName);

        public void Dispose() { }

        private sealed class Capture(System.Collections.Concurrent.ConcurrentQueue<string> sink, string category) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Error;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) sink.Enqueue($"{category}: {formatter(state, exception)} {exception}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Admin.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        // The data folder is not removed: test artifacts are never cleaned up by code.
        _ = _dataDir;
    }
}
