using BeeMemoryBank.BlindMobile.Services;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services.BlindPhone;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.BlindMobile.Tests;

public class BlindMobileLogicTests
{
    [Fact]
    public void BlindPaths_FormatPathsCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bmb_test_" + Guid.NewGuid().ToString("N"));

        BlindPaths.Backups(tempDir).Should().Be(Path.Combine(tempDir, "blind-backups"));
        BlindPaths.Replica(tempDir).Should().Be(Path.Combine(tempDir, "blind-replica"));
        BlindPaths.Log(tempDir).Should().Be(Path.Combine(tempDir, "blind-log.jsonl"));
    }

    [Fact]
    public async Task PendingBlindIdentityRecorder_DocumentsSkeletonBehavior_CompletesWithoutDatabaseWrites()
    {
        // In Stage 1 skeleton, PendingBlindIdentityRecorder is a deliberate in-memory placeholder
        // that completes successfully so BlindPhonePairing.CreateIdentityAsync can initialize
        // ephemeral pairing state and generate codes before the v=2 SQLite row is implemented in Stage 2.
        var recorder = new PendingBlindIdentityRecorder();
        var nodeId = Guid.NewGuid();
        var pubKey = new byte[32];

        var task = recorder.RecordAsync(nodeId, pubKey, "TestPhone", CancellationToken.None);
        await task;

        task.IsCompletedSuccessfully.Should().BeTrue();

        // Pair with in-memory state using this stand-in:
        var store = new InMemoryBlindPhoneStore();
        var state = new BlindPhoneState(store);
        var keys = new InMemoryBlindPhoneKeys();
        var log = new BlindPhoneLog(Path.Combine(Path.GetTempPath(), "test-log-" + Guid.NewGuid().ToString("N") + ".jsonl"), TimeProvider.System);
        var pairing = new BlindPhonePairing(state, keys, recorder, log);

        await pairing.CreateIdentityAsync("TestPhone");
        state.NodeId.Should().NotBeNull();
        state.DisplayName.Should().Be("TestPhone");
    }

    [Fact]
    public async Task SqliteBlindIdentityRecorder_WritesV2IdentityRow_ToDatabase()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bmb-identity-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "beememorybank.db");

        try
        {
            BeeMemoryBank.Storage.Sqlite.DapperConfig.Configure();
            var dbFactory = new BeeMemoryBank.Storage.Sqlite.DbConnectionFactory(dbPath);
            var runner = new BeeMemoryBank.Storage.Sqlite.MigrationRunner(dbFactory);
            await runner.RunMigrationsAsync();

            var nodeRepo = new BeeMemoryBank.Storage.Sqlite.NodeIdentityRepository(dbFactory);
            var recorder = new SqliteBlindIdentityRecorder(nodeRepo);

            var nodeId = BlindNodeId.NewId();
            var (pubKey, seed) = BeeMemoryBank.Crypto.Ed25519Signer.GenerateKeyPair();
            var displayName = "Test Blind Phone";

            await recorder.RecordAsync(nodeId, pubKey, displayName, CancellationToken.None);

            var identity = await nodeRepo.GetAsync();
            identity.Should().NotBeNull();
            identity!.NodeId.Should().Be(nodeId);
            identity.DisplayName.Should().Be(displayName);
            identity.Ed25519PublicKey.Should().Equal(pubKey);
            identity.Ed25519PrivateKeyV.Should().Be(BeeMemoryBank.Crypto.NodeIdentityCrypto.ExternalKeyVersion); // 2
            identity.Ed25519PrivateKey.Should().BeEmpty();
            identity.Ed25519PrivateKeyIV.Should().BeNull();
            identity.CanGenerateEmbeddings.Should().BeFalse();
            identity.InitialSyncCompleted.Should().BeFalse();

            // Calling RecordAsync again with same nodeId is idempotent
            await recorder.RecordAsync(nodeId, pubKey, displayName, CancellationToken.None);

            // Calling RecordAsync with different nodeId throws
            var diffAct = async () => await recorder.RecordAsync(BlindNodeId.NewId(), pubKey, "Another Phone", CancellationToken.None);
            await diffAct.Should().ThrowAsync<InvalidOperationException>();

            // Attempting to decrypt the private key using NodeIdentityCrypto throws with external key notice
            var decryptAct = () => BeeMemoryBank.Crypto.NodeIdentityCrypto.GetDecryptedPrivateKey(
                identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV, identity.NodeId, new byte[32]);
            var ex = decryptAct.Should().Throw<InvalidOperationException>();
            ex.Which.Message.Should().Contain("outside the database (v=2)");

            // Signing using NodeIdentityCrypto with external seed callback succeeds
            var payload = "test-payload"u8.ToArray();
            var signature = BeeMemoryBank.Crypto.NodeIdentityCrypto.SignWithIdentityOrGetDek(
                identity.Ed25519PrivateKey, identity.Ed25519PrivateKeyIV, identity.Ed25519PrivateKeyV, identity.NodeId,
                getMasterDek: () => throw new Exception("DEK should never be requested"),
                getExternalSeed: () => seed,
                payload);

            BeeMemoryBank.Crypto.Ed25519Signer.Verify(identity.Ed25519PublicKey, payload, signature).Should().BeTrue();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task PendingBlindReplicaSource_ThrowsBlindFeaturePendingException()
    {
        var source = new PendingBlindReplicaSource();
        var callCode = new BlindCallCode("https://127.0.0.1:5300", Guid.NewGuid(), "test_pin", new byte[32], new byte[32]);

        var act = () => source.FetchAndInstallAsync(callCode, "workDir", null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<BlindFeaturePendingException>();
        ex.Which.ContractItem.Should().Contain("GET /api/blind/replica");
    }

    [Fact]
    public async Task PendingBlindPhoneSync_ThrowsBlindFeaturePendingException()
    {
        var sync = new PendingBlindPhoneSync();
        var callCode = new BlindCallCode("https://127.0.0.1:5300", Guid.NewGuid(), "test_pin", new byte[32], new byte[32]);

        var act = () => sync.SyncOnceAsync(callCode, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<BlindFeaturePendingException>();
        ex.Which.ContractItem.Should().Contain("v=2 identity signer");
    }

    [Fact]
    public async Task PendingBlindPackageSource_ThrowsBlindFeaturePendingException()
    {
        var pkg = new PendingBlindPackageSource();

        var act = () => pkg.CreateAsync("dest.tar.gz", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<BlindFeaturePendingException>();
        ex.Which.ContractItem.Should().Contain("building the blind package");
    }

    [Fact]
    public async Task PendingRecoverySetSource_ThrowsBlindFeaturePendingException()
    {
        var recovery = new PendingRecoverySetSource();

        var act = () => recovery.BuildJsonAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<BlindFeaturePendingException>();
        ex.Which.ContractItem.Should().Contain("the recovery set of the phone's data");
    }

    private sealed class InMemoryBlindPhoneStore : IBlindPhoneStore
    {
        private readonly Dictionary<string, string> _store = new();
        public string? Get(string key) => _store.TryGetValue(key, out var val) ? val : null;
        public void Set(string key, string? value)
        {
            if (value is null) _store.Remove(key);
            else _store[key] = value;
        }
    }

    private sealed class InMemoryBlindPhoneKeys : IBlindPhoneKeys
    {
        public byte[]? IdentitySeed { get; private set; }
        public byte[]? BackupKey { get; private set; }
        public byte[]? PairingSecret { get; private set; }

        public void SaveIdentitySeed(byte[] seed) => IdentitySeed = (byte[])seed.Clone();
        public void SaveBackupKey(byte[] key) => BackupKey = (byte[])key.Clone();
        public byte[]? LoadBackupKey() => BackupKey != null ? (byte[])BackupKey.Clone() : null;
        public void SavePairingSecret(byte[] secret) => PairingSecret = (byte[])secret.Clone();
        public byte[]? LoadPairingSecret() => PairingSecret != null ? (byte[])PairingSecret.Clone() : null;
        public void ClearPairingSecret() => PairingSecret = null;
        public void Clear()
        {
            IdentitySeed = null;
            BackupKey = null;
            PairingSecret = null;
        }
    }

    [Fact]
    public void BlindPhoneReset_Wipe_CleansTestOwnedDirectoryAndClearsKeys()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bmb-wipe-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbFile = Path.Combine(tempDir, "beememorybank.db");
        var walFile = Path.Combine(tempDir, "beememorybank.db-wal");
        var logFile = BlindPaths.Log(tempDir);
        var backupsDir = BlindPaths.Backups(tempDir);
        var replicaDir = BlindPaths.Replica(tempDir);

        File.WriteAllText(dbFile, "dummy-db");
        File.WriteAllText(walFile, "dummy-wal");
        File.WriteAllText(logFile, "dummy-log");
        Directory.CreateDirectory(backupsDir);
        Directory.CreateDirectory(replicaDir);

        var services = new ServiceCollection();
        var keys = new InMemoryBlindPhoneKeys();
        var store = new InMemoryBlindPhoneStore();
        var state = new BlindPhoneState(store);

        keys.SaveBackupKey(new byte[] { 1, 2, 3 });
        keys.SavePairingSecret(new byte[] { 4, 5, 6 });
        state.NodeId = Guid.NewGuid();
        state.DisplayName = "WipeTestPhone";

        services.AddSingleton<IBlindPhoneKeys>(keys);
        services.AddSingleton(state);

        var provider = services.BuildServiceProvider();

        // Wipe must use the passed test directory rather than process LocalApplicationData
        BlindPhoneReset.Wipe(provider, tempDir);

        keys.LoadBackupKey().Should().BeNull();
        keys.LoadPairingSecret().Should().BeNull();
        state.NodeId.Should().BeNull();
        state.DisplayName.Should().BeNull();

        File.Exists(dbFile).Should().BeFalse();
        File.Exists(walFile).Should().BeFalse();
        File.Exists(logFile).Should().BeFalse();
        Directory.Exists(backupsDir).Should().BeFalse();
        Directory.Exists(replicaDir).Should().BeFalse();
    }

    [Fact]
    public void PreferencesBlindStore_RoundTripsAndRemovesValues()
    {
        var dict = new Dictionary<string, string>();
        var store = new PreferencesBlindStore(
            dict.GetValueOrDefault,
            (k, v) => dict[k] = v,
            k => dict.Remove(k));

        store.Get("foo").Should().BeNull();

        store.Set("foo", "bar");
        store.Get("foo").Should().Be("bar");

        store.Set("foo", null);
        store.Get("foo").Should().BeNull();
        dict.ContainsKey("foo").Should().BeFalse();
    }

    [Fact]
    public async Task MaintenanceDetectingHandler_PassesThroughNon503Responses()
    {
        var inner = new TestHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"ok\"}")
            }));

        var client = new HttpClient(new MaintenanceDetectingHandler { InnerHandler = inner });
        var res = await client.GetAsync("https://example.com/api/test");

        res.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Be("{\"status\":\"ok\"}");
    }

    [Fact]
    public async Task MaintenanceDetectingHandler_Rewrites503WithReasonJson()
    {
        var inner = new TestHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"reason\":\"snapshot restore in progress\"}")
            }));

        var client = new HttpClient(new MaintenanceDetectingHandler { InnerHandler = inner });
        var res = await client.GetAsync("https://example.com/api/test");

        res.StatusCode.Should().Be(System.Net.HttpStatusCode.ServiceUnavailable);
        res.ReasonPhrase.Should().Be("Node maintenance: snapshot restore in progress");
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("Node maintenance: snapshot restore in progress");
    }

    [Fact]
    public async Task MaintenanceDetectingHandler_Rewrites503WithDefaultWhenMalformed()
    {
        var inner = new TestHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("not-a-json-string")
            }));

        var client = new HttpClient(new MaintenanceDetectingHandler { InnerHandler = inner });
        var res = await client.GetAsync("https://example.com/api/test");

        res.StatusCode.Should().Be(System.Net.HttpStatusCode.ServiceUnavailable);
        res.ReasonPhrase.Should().Be("Node is being maintained. Try again in a minute.");
        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("Node is being maintained. Try again in a minute.");
    }

    [Fact]
    public async Task BlindHttpHandler_RejectsCleartextHttp()
    {
        var inner = new TestHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)));

        var client = new HttpClient(new BlindHttpHandler(inner));
        var act = () => client.GetAsync("http://example.com/api/test");

        var ex = await act.Should().ThrowAsync<HttpRequestException>();
        ex.Which.Message.Should().Contain("Cleartext HTTP is forbidden");
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task BlindHttpHandler_RejectsRedirects(int statusCode)
    {
        var inner = new TestHttpMessageHandler((req, ct) =>
        {
            var resp = new HttpResponseMessage((System.Net.HttpStatusCode)statusCode);
            resp.Headers.Location = new Uri("https://redirected.example.com/target");
            return Task.FromResult(resp);
        });

        var client = new HttpClient(new BlindHttpHandler(inner));
        var act = () => client.GetAsync("https://example.com/api/test");

        var ex = await act.Should().ThrowAsync<HttpRequestException>();
        ex.Which.Message.Should().Contain("redirects are not followed");
    }

    [Fact]
    public async Task BlindHttpHandler_AllowsHttpsSuccess()
    {
        var inner = new TestHttpMessageHandler((req, ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("success")
            }));

        var client = new HttpClient(new BlindHttpHandler(inner));
        var res = await client.GetAsync("https://example.com/api/test");

        res.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync()).Should().Be("success");
    }

    [Fact]
    public void BlindHttpHandler_CreatePrimaryHandler_DisablesAutoRedirect()
    {
        var handler = BlindHttpHandler.CreatePrimaryHandler();
        handler.AllowAutoRedirect.Should().BeFalse();
    }

    [Fact]
    public void BlindHttpHandler_ValidateServerCertificate_RejectsWhenNoPinAvailable()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=test", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));

        var state = new BlindPhoneState(new InMemoryBlindPhoneStore());
        var httpReq = new HttpRequestMessage(HttpMethod.Get, "https://example.com");

        // When neither request options nor state CallCode specifies a pin, validation must reject
        // even if ordinary CA policy validation reports SslPolicyErrors.None
        var result = BlindHttpHandler.ValidateServerCertificate(httpReq, cert, System.Net.Security.SslPolicyErrors.None, state);
        result.Should().BeFalse();
    }

    [Fact]
    public void BlindHttpHandler_ValidateServerCertificate_ValidatesExpectedSpkiPin()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=test", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));

        var expectedPin = BeeMemoryBank.Crypto.SpkiPin.Of(cert);
        var state = new BlindPhoneState(new InMemoryBlindPhoneStore());

        // Request with matching explicit pin -> true (even with RemoteCertificateChainErrors)
        var pinnedReq = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        pinnedReq.Options.Set(BlindHttpHandler.ExplicitPin, expectedPin);
        BlindHttpHandler.ValidateServerCertificate(pinnedReq, cert, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, state).Should().BeTrue();

        // Request with mismatched explicit pin -> false
        var badPinnedReq = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        badPinnedReq.Options.Set(BlindHttpHandler.ExplicitPin, "mismatched-pin");
        BlindHttpHandler.ValidateServerCertificate(badPinnedReq, cert, System.Net.Security.SslPolicyErrors.None, state).Should().BeFalse();

        // State with matching CallCode pin -> true
        var store = new InMemoryBlindPhoneStore();
        var pairedState = new BlindPhoneState(store);
        pairedState.CallCode = new BeeMemoryBank.Core.Models.BlindCallCode(
            "https://example.com", Guid.NewGuid(), expectedPin, new byte[32], new byte[32]);
        var stateReq = new HttpRequestMessage(HttpMethod.Get, "https://example.com");
        BlindHttpHandler.ValidateServerCertificate(stateReq, cert, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, pairedState).Should().BeTrue();

        // State with mismatched CallCode pin -> false
        var otherPin = System.Buffers.Text.Base64Url.EncodeToString(new byte[32]);
        var badStore = new InMemoryBlindPhoneStore();
        var badPairedState = new BlindPhoneState(badStore);
        badPairedState.CallCode = new BeeMemoryBank.Core.Models.BlindCallCode(
            "https://example.com", Guid.NewGuid(), otherPin, new byte[32], new byte[32]);
        BlindHttpHandler.ValidateServerCertificate(stateReq, cert, System.Net.Security.SslPolicyErrors.None, badPairedState).Should().BeFalse();
    }

    [Fact]
    public async Task RePair_WithNewPinToSameHost_DoesNotReuseOldPooledConnection()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var certReq = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=127.0.0.1", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var ephemeralCert = certReq.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
        using var serverCert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
            ephemeralCert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null, System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.Exportable);

        var serverPin = BeeMemoryBank.Crypto.SpkiPin.Of(serverCert);
        var wrongPin = System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource();

        var serverTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cts.Token);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        using (var ssl = new System.Net.Security.SslStream(client.GetStream(), false))
                        {
                            try
                            {
                                await ssl.AuthenticateAsServerAsync(serverCert);
                                var buffer = new byte[4096];
                                while (!cts.Token.IsCancellationRequested)
                                {
                                    var read = await ssl.ReadAsync(buffer, cts.Token);
                                    if (read == 0) break;
                                    var reqText = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
                                    if (reqText.Contains("\r\n\r\n"))
                                    {
                                        var response = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: keep-alive\r\n\r\nOK"u8.ToArray();
                                        await ssl.WriteAsync(response, cts.Token);
                                        await ssl.FlushAsync(cts.Token);
                                    }
                                }
                            }
                            catch
                            {
                            }
                        }
                    }, cts.Token);
                }
            }
            catch when (cts.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
            }
        });

        try
        {
            var store = new InMemoryBlindPhoneStore();
            var state = new BlindPhoneState(store);
            state.CallCode = new BlindCallCode($"https://127.0.0.1:{port}", Guid.NewGuid(), serverPin, new byte[32], new byte[32]);

            var services = new ServiceCollection();
            services.AddSingleton(state);
            services.AddTransient<MaintenanceDetectingHandler>();
            services.AddTransient<BlindHttpHandler>();
            services.AddSingleton<BlindHttpClientProvider>();
            services.AddSingleton<IHttpClientFactory>(sp => sp.GetRequiredService<BlindHttpClientProvider>());
            services.AddTransient<HttpClient>(sp => sp.GetRequiredService<BlindHttpClientProvider>().GetClient());

            var sp = services.BuildServiceProvider();
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient1 = factory.CreateClient();

            // Request 1: uses serverPin -> succeeds! Connection is established under serverPin.
            var res1 = await httpClient1.GetAsync($"https://127.0.0.1:{port}/");
            res1.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

            // Re-pair to the same origin with a new/different pin (wrongPin)
            state.CallCode = new BlindCallCode($"https://127.0.0.1:{port}", Guid.NewGuid(), wrongPin, new byte[32], new byte[32]);

            // Request 2: with BlindHttpClientProvider, changing pin disposes the old handler and its pooled connections.
            // A new TLS handshake takes place, checking wrongPin against the server cert and throwing HttpRequestException.
            var httpClient2 = factory.CreateClient();
            var act2 = async () => await httpClient2.GetAsync($"https://127.0.0.1:{port}/");
            await act2.Should().ThrowAsync<HttpRequestException>();
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
        }
    }
}

internal sealed class TestHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        sendAsync(request, cancellationToken);
}
