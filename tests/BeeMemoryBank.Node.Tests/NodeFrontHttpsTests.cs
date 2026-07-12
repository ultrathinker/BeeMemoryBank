using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;
using FluentAssertions;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Hosting;
using BeeMemoryBank.Node;

namespace BeeMemoryBank.Node.Tests;

/// <summary>
/// Load-bearing proof (DoD #3) that the opt-in HTTPS front actually serves TLS presenting the
/// <see cref="LocalCaService"/>-issued leaf, that a client trusting the CA can complete a real
/// HTTPS request, AND that the plain-HTTP listener keeps working alongside it (coexistence,
/// DoD #2). Windows-only: LocalCaService (DPAPI) is Windows-only. The HTTPS listener binds the
/// fixed <see cref="NodeFront.HttpsPort"/> (5311); the test assumes that port is free on the
/// machine running it.
/// </summary>
/// <summary>
/// Shared xUnit collection for every test that binds the fixed <see cref="NodeFront.HttpsPort"/>
/// (5311), forcing them to run sequentially rather than in parallel (xUnit's default across test
/// classes) — otherwise two tests binding the same fixed port race and one fails with
/// <c>SocketException: address already in use</c>.
/// </summary>
[CollectionDefinition("NodeFrontHttpsPort", DisableParallelization = true)]
public class NodeFrontHttpsPortCollection { }

[SupportedOSPlatform("windows")]
[Collection("NodeFrontHttpsPort")]
public class NodeFrontHttpsTests : IAsyncLifetime
{
    private readonly List<WebApplication> _apps = new();
    private readonly List<string> _tempDirs = new();

    // IAsyncDisposable alone is NOT an xUnit lifecycle hook — xUnit v2 only recognizes
    // IAsyncLifetime (InitializeAsync/DisposeAsync returning Task). Without this, DisposeAsync
    // below was never actually called, leaking this test's WebApplication (and its bound
    // NodeFront.HttpsPort socket) for the rest of the test process — which made every
    // subsequent test binding that same fixed port fail with "address already in use".
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var app in _apps)
        {
            try { await app.StopAsync(); await app.DisposeAsync(); }
            catch { /* suppress shutdown errors */ }
        }
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private async Task<WebApplication> StartStubAsync(Action<WebApplication> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        configure(app);
        await app.StartAsync();
        _apps.Add(app);
        return app;
    }

    private static bool ChainBuildsAgainst(X509Certificate2 leaf, X509Certificate2 ca)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        return chain.Build(leaf);
    }

    [Fact]
    public async Task HttpsFront_PresentsLocalCaLeaf_AndCoexistsWithHttpListener()
    {
        if (!OperatingSystem.IsWindows()) return;

        // ── Arrange: data dir with a pre-created CA + leaf (so the selector has a cert to serve) ──
        var dataDir = Path.Combine(Path.GetTempPath(), "bmb-https-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        _tempDirs.Add(dataDir);

        var caService = new LocalCaService(dataDir);
        using var ca = caService.GetOrCreateCaCertificate()!;
        using var leaf = caService.GetOrCreateLeafCertificate()!;
        ca.Should().NotBeNull();
        leaf.Should().NotBeNull();
        leaf.HasPrivateKey.Should().BeTrue("the leaf served by Kestrel must carry its private key");
        // Sanity: the leaf we generated really does chain to the CA.
        ChainBuildsAgainst(leaf, ca).Should().BeTrue();

        // ── Arrange: stub Api + Web behind the front ──────────────────────────────────────────
        var apiStub = await StartStubAsync(app =>
        {
            app.MapGet("/health", () => Results.Ok(new { server = "api" }));
        });
        var webStub = await StartStubAsync(app =>
        {
            app.MapGet("/connect", () => Results.Ok("connect-page"));
            app.MapGet("/connect/ca.crt", () => Results.Bytes(new byte[] { 1, 2, 3 }, "application/x-x509-ca-cert"));
        });
        var children = new Dictionary<string, ReadyFileInfo>
        {
            { "Api", new ReadyFileInfo(111, apiStub.Urls.ToList(), "BeeMemoryBank.Api", "1.0.0", DateTime.UtcNow) },
            { "Web", new ReadyFileInfo(222, webStub.Urls.ToList(), "BeeMemoryBank.Web", "1.0.0", DateTime.UtcNow) }
        };

        // ── Arrange: the front WITH HTTPS enabled (HTTP listener stays on a random loopback port) ──
        var frontBuilder = WebApplication.CreateBuilder();
        frontBuilder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var front = new NodeFront(apiStub.Urls.First(), webStub.Urls.First(), children);
        front.RegisterServices(frontBuilder.Services, enableHttps: true, dataPath: dataDir);
        var frontApp = frontBuilder.Build();
        front.MapEndpoints(frontApp);
        await frontApp.StartAsync();
        _apps.Add(frontApp);

        var httpUrl = frontApp.Urls.First();

        // ── Act/Assert 1: plain-HTTP listener is STILL up (coexistence — the load-bearing
        //    guarantee that enabling HTTPS never disturbs the existing request path) ──────────
        using var http = new HttpClient();
        var httpRes = await http.GetAsync($"{httpUrl}/health");
        httpRes.IsSuccessStatusCode.Should().BeTrue("the plain-HTTP listener must keep working when HTTPS is enabled");

        // ── Act/Assert 2: HTTPS listener on NodeFront.HttpsPort presents the LocalCaService leaf
        //    and a CA-trusting client completes a real request. Mirrors LocalCaServiceTests'
        //    chain-validation approach (CustomRootTrust against the CA). ────────────────────────
        string? presentedThumbprint = null;
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            {
                using var presented = new X509Certificate2(cert!);
                presentedThumbprint = presented.Thumbprint;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(ca);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                return chain.Build(presented);
            }
        };
        using var https = new HttpClient(handler);
        var httpsRes = await https.GetAsync($"https://127.0.0.1:{NodeFront.HttpsPort}/health");

        httpsRes.IsSuccessStatusCode.Should().BeTrue("a CA-trusting client must complete the TLS request");
        presentedThumbprint.Should().Be(leaf.Thumbprint,
            "the server must present the exact LocalCaService-issued leaf, not some other cert");
        (await httpsRes.Content.ReadAsStringAsync()).Should().Contain("api");

        // ── Act/Assert 3: the /connect path (proxied to Web) is reachable over HTTPS too ───────
        var connectRes = await https.GetAsync($"https://127.0.0.1:{NodeFront.HttpsPort}/connect");
        connectRes.IsSuccessStatusCode.Should().BeTrue();
        (await connectRes.Content.ReadAsStringAsync()).Should().Contain("connect-page");

        // ── Act/Assert 4: the ca.crt endpoint is proxied to Web over HTTPS ─────────────────────
        var caCrtRes = await https.GetAsync($"https://127.0.0.1:{NodeFront.HttpsPort}/connect/ca.crt");
        caCrtRes.IsSuccessStatusCode.Should().BeTrue();
        caCrtRes.Content.Headers.ContentType!.MediaType.Should().Be("application/x-x509-ca-cert");
    }

    [Fact]
    public void RegisterServices_WithHttpsDisabled_DoesNotThrowAndNeedsNoDataPath()
    {
        // DoD #2: with HTTPS off (the default), RegisterServices must behave exactly as before —
        // no dataPath required, no exception, identical service registration.
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var front = new NodeFront("http://127.0.0.1:9", "http://127.0.0.1:8",
            new Dictionary<string, ReadyFileInfo>
            {
                { "Api", new ReadyFileInfo(1, new[] { "http://127.0.0.1:9" }, "BeeMemoryBank.Api", "1.0.0", DateTime.UtcNow) },
                { "Web", new ReadyFileInfo(2, new[] { "http://127.0.0.1:8" }, "BeeMemoryBank.Web", "1.0.0", DateTime.UtcNow) }
            });

        Action act = () => front.RegisterServices(services);

        act.Should().NotThrow();
        services.Should().Contain(d =>
            d.ServiceType == typeof(Microsoft.Extensions.Options.IConfigureOptions<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>),
            "the body-size Kestrel override must still be registered in the default (HTTPS-off) path");
    }
}
