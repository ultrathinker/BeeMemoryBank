using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Xunit;
using FluentAssertions;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Core.Services.Acme;
using BeeMemoryBank.Hosting;
using Certes;

namespace BeeMemoryBank.Node.Tests;

/// <summary>
/// Load-bearing proof that Node's live HTTPS listener actually serves an ACME TLS-ALPN-01
/// challenge certificate for the matching SNI when one is active (via the cross-process
/// <see cref="AcmeChallengePersister"/> file hand-off), and falls back to the normal
/// <see cref="LocalCaService"/> leaf for every other SNI and once the challenge is cleared.
/// Windows-only (matches <see cref="NodeFrontHttpsTests"/>'s own guard — LocalCaService/DPAPI
/// are Windows-only, and this test starts the same real HTTPS listener that class exercises).
/// </summary>
[SupportedOSPlatform("windows")]
[Collection("NodeFrontHttpsPort")]
public class NodeFrontAcmeChallengeTests : IAsyncLifetime
{
    private readonly List<WebApplication> _apps = new();
    private readonly List<string> _tempDirs = new();

    // See NodeFrontHttpsTests for why this is IAsyncLifetime, not bare IAsyncDisposable.
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

    [Fact]
    public async Task HttpsFront_ServesChallengeCert_ForMatchingSni_AndFallsBackOtherwise()
    {
        if (!OperatingSystem.IsWindows()) return;

        const string challengeDomain = "acme-challenge-test.example.com";

        var dataDir = Path.Combine(Path.GetTempPath(), "bmb-acme-challenge-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        _tempDirs.Add(dataDir);

        var caService = new LocalCaService(dataDir);
        using var ca = caService.GetOrCreateCaCertificate()!;
        using var leaf = caService.GetOrCreateLeafCertificate()!;
        ca.Should().NotBeNull();
        leaf.Should().NotBeNull();

        var children = new Dictionary<string, ReadyFileInfo>
        {
            { "Api", new ReadyFileInfo(111, new[] { "http://127.0.0.1:1" }, "BeeMemoryBank.Api", "1.0.0", DateTime.UtcNow) },
            { "Web", new ReadyFileInfo(222, new[] { "http://127.0.0.1:2" }, "BeeMemoryBank.Web", "1.0.0", DateTime.UtcNow) }
        };

        // NodeFront.HttpsPort is a fixed port shared with NodeFrontHttpsTests (same xUnit
        // collection, so never run concurrently — but Kestrel's socket release after a prior
        // test's DisposeAsync can lag the OS by a few hundred ms on Windows). Retry the bind a
        // few times rather than fail on that transient race.
        WebApplication? frontApp = null;
        for (var attempt = 1; ; attempt++)
        {
            var frontBuilder = WebApplication.CreateBuilder();
            frontBuilder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            var front = new NodeFront("http://127.0.0.1:1", "http://127.0.0.1:2", children);
            front.RegisterServices(frontBuilder.Services, enableHttps: true, dataPath: dataDir);
            frontApp = frontBuilder.Build();
            front.MapEndpoints(frontApp);
            try
            {
                await frontApp.StartAsync();
                break;
            }
            catch (IOException) when (attempt < 5)
            {
                await frontApp.DisposeAsync();
                await Task.Delay(300);
            }
        }
        _apps.Add(frontApp);

        // ── Before any challenge: any SNI should get the normal leaf. ──────────────────────
        var thumbprintNoChallenge = await ConnectAndGetThumbprintAsync(challengeDomain);
        thumbprintNoChallenge.Should().Be(leaf.Thumbprint,
            "with no active challenge, every SNI must be served the normal LocalCa leaf");

        // ── Write a challenge cert for challengeDomain via the shared-file hand-off. ───────
        var accountKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
        var keyAuthz = accountKey.KeyAuthorization("dummy-token-for-test");
        using var challengeCert = TlsAlpn01CertificateBuilder.Build(challengeDomain, keyAuthz);
        var persister = new AcmeChallengePersister(dataDir);
        persister.Write(challengeDomain, challengeCert);

        try
        {
            // ── Matching SNI now gets the challenge cert, not the leaf. ────────────────────
            var thumbprintDuringChallenge = await ConnectAndGetThumbprintAsync(challengeDomain);
            thumbprintDuringChallenge.Should().Be(challengeCert.Thumbprint,
                "the SNI matching the active challenge must be served the challenge cert");
            thumbprintDuringChallenge.Should().NotBe(leaf.Thumbprint);

            // ── A DIFFERENT SNI must still get the normal leaf — the challenge must not leak
            //    to unrelated hostnames sharing the same listener. ───────────────────────────
            var thumbprintOtherSni = await ConnectAndGetThumbprintAsync("some-other-host.example.org");
            thumbprintOtherSni.Should().Be(leaf.Thumbprint,
                "an unrelated SNI must never receive the challenge cert for a different domain");
        }
        finally
        {
            persister.Delete();
        }

        // ── After the challenge is cleared, the original domain falls back to the leaf again. ──
        var thumbprintAfterDelete = await ConnectAndGetThumbprintAsync(challengeDomain);
        thumbprintAfterDelete.Should().Be(leaf.Thumbprint,
            "once the challenge file is deleted, the domain must fall back to the normal leaf");

        async Task<string> ConnectAndGetThumbprintAsync(string sni)
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, NodeFront.HttpsPort);
            await using var stream = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);

            string? presentedThumbprint = null;
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = sni,
                ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 },
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    // Only checking WHICH cert was served (by thumbprint) — chain trust for the
                    // normal leaf is already proven by NodeFrontHttpsTests; the ad-hoc challenge
                    // cert here is intentionally self-signed and unrelated to the CA.
                    if (cert is X509Certificate2 c) presentedThumbprint = c.Thumbprint;
                    return true;
                },
            });

            return presentedThumbprint ?? throw new InvalidOperationException("No certificate presented.");
        }
    }
}
