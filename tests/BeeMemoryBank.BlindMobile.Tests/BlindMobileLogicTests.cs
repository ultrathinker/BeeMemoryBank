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
            var recorder = new SqliteBlindIdentityRecorder(dbFactory);

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
        public byte[]? LoadIdentitySeed() => IdentitySeed != null ? (byte[])IdentitySeed.Clone() : null;
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

    [Fact]
    public async Task BlindHttpClientProvider_DisposingOneClient_AllowsSubsequentClientToSucceed()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var certReq = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=127.0.0.1", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var ephemeralCert = certReq.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
        using var serverCert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
            ephemeralCert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null, System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.Exportable);

        var serverPin = BeeMemoryBank.Crypto.SpkiPin.Of(serverCert);
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource();

        _ = Task.Run(async () =>
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
                            catch { }
                        }
                    }, cts.Token);
                }
            }
            catch when (cts.IsCancellationRequested) { }
            catch (Exception) { }
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

            var sp = services.BuildServiceProvider();
            var factory = sp.GetRequiredService<IHttpClientFactory>();

            // Factory caller 1 disposes client
            using (var client1 = factory.CreateClient())
            {
                var res1 = await client1.GetAsync($"https://127.0.0.1:{port}/");
                res1.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
            }

            // Factory caller 2 gets a client under same pin: must succeed, NOT fail with ObjectDisposedException
            using (var client2 = factory.CreateClient())
            {
                var res2 = await client2.GetAsync($"https://127.0.0.1:{port}/");
                res2.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
            }
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
        }
    }

    [Fact]
    public async Task SqliteBlindIdentityRecorder_ConcurrentInitialization_EnforcesSingleIdentity()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bmb-concurrent-id-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "beememorybank.db");

        try
        {
            BeeMemoryBank.Storage.Sqlite.DapperConfig.Configure();
            var dbFactory = new BeeMemoryBank.Storage.Sqlite.DbConnectionFactory(dbPath);
            var runner = new BeeMemoryBank.Storage.Sqlite.MigrationRunner(dbFactory);
            await runner.RunMigrationsAsync();

            var nodeRepo = new BeeMemoryBank.Storage.Sqlite.NodeIdentityRepository(dbFactory);
            var recorder = new SqliteBlindIdentityRecorder(dbFactory);

            var tasks = Enumerable.Range(0, 10).Select(async i =>
            {
                var nodeId = BlindNodeId.NewId();
                var (publicKey, _) = BeeMemoryBank.Crypto.Ed25519Signer.GenerateKeyPair();
                try
                {
                    await recorder.RecordAsync(nodeId, publicKey, $"Phone {i}");
                    return (Success: true, NodeId: nodeId, Exception: (Exception?)null);
                }
                catch (Exception ex)
                {
                    return (Success: false, NodeId: nodeId, Exception: (Exception?)ex);
                }
            }).ToList();

            var results = await Task.WhenAll(tasks);

            // Exactly ONE identity row must exist in the database!
            using var conn = dbFactory.CreateConnection();
            conn.Open();
            var count = await Dapper.SqlMapper.ExecuteScalarAsync<long>(conn, "SELECT COUNT(*) FROM tbl_node_identity");
            count.Should().Be(1, "exactly one identity row may ever be created in tbl_node_identity");

            // Successful callers must agree on the recorded node ID
            var recordedIdentity = await nodeRepo.GetAsync();
            recordedIdentity.Should().NotBeNull();
            var successfulResults = results.Where(r => r.Success).ToList();
            successfulResults.Should().HaveCount(1);
            successfulResults[0].NodeId.Should().Be(recordedIdentity!.NodeId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SqliteBlindIdentityRecorder_RecordAsync_EnforcesSingleIdentityInvariantAtomically()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bmb-node-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "beememorybank.db");

        try
        {
            BeeMemoryBank.Storage.Sqlite.DapperConfig.Configure();
            var dbFactory = new BeeMemoryBank.Storage.Sqlite.DbConnectionFactory(dbPath);
            var runner = new BeeMemoryBank.Storage.Sqlite.MigrationRunner(dbFactory);
            await runner.RunMigrationsAsync();

            var recorder = new SqliteBlindIdentityRecorder(dbFactory);

            var id1 = BlindNodeId.NewId();
            var key1 = new byte[32];
            await recorder.RecordAsync(id1, key1, "Node 1");

            // Calling with same identity is idempotent:
            await recorder.RecordAsync(id1, key1, "Node 1");

            // Calling with a different identity must be rejected atomically:
            var id2 = BlindNodeId.NewId();
            var key2 = new byte[32];
            var act = async () => await recorder.RecordAsync(id2, key2, "Node 2");
            await act.Should().ThrowAsync<InvalidOperationException>();

            using var conn = dbFactory.CreateConnection();
            conn.Open();
            var count = await Dapper.SqlMapper.ExecuteScalarAsync<long>(conn, "SELECT COUNT(*) FROM tbl_node_identity");
            count.Should().Be(1);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task CreateIdentityAsync_WhenCrashBetweenDbAndState_RecoversMatchingIdentity()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bmb-crash-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "beememorybank.db");
        var logPath = Path.Combine(tempDir, "blind.log");

        try
        {
            BeeMemoryBank.Storage.Sqlite.DapperConfig.Configure();
            var dbFactory = new BeeMemoryBank.Storage.Sqlite.DbConnectionFactory(dbPath);
            var runner = new BeeMemoryBank.Storage.Sqlite.MigrationRunner(dbFactory);
            await runner.RunMigrationsAsync();

            var nodeRepo = new BeeMemoryBank.Storage.Sqlite.NodeIdentityRepository(dbFactory);
            var recorder = new SqliteBlindIdentityRecorder(dbFactory);
            var keys = new InMemoryBlindPhoneKeys();
            var store = new InMemoryBlindPhoneStore();
            var state = new BlindPhoneState(store);
            var log = new BlindPhoneLog(logPath, TimeProvider.System);

            // Simulate the crash:
            // 1. Keystore saved seed and keys
            var (origPubKey, origSeed) = BeeMemoryBank.Crypto.Ed25519Signer.GenerateKeyPair();
            keys.SaveIdentitySeed(origSeed);
            var origBackupKey = new byte[32];
            keys.SaveBackupKey(origBackupKey);
            var origSecret = BeeMemoryBank.Crypto.BlindPairingSecret.New();
            keys.SavePairingSecret(origSecret);

            // 2. SQLite wrote the v=2 identity row
            var origNodeId = BlindNodeId.NewId();
            await recorder.RecordAsync(origNodeId, origPubKey, "Original Name");

            // 3. BUT process crashed before state.NodeId was saved!
            state.NodeId.Should().BeNull();

            // Next launch: pairing service is instantiated fresh
            var freshPairing = new BlindPhonePairing(state, keys, recorder, log);
            freshPairing.HasIdentity.Should().BeFalse();

            // CreateIdentityAsync is invoked (as done in BlindHomePage.OnAppearing)
            await freshPairing.CreateIdentityAsync("Recovered Name");

            // Must NOT throw, must recover the existing identity and matching keys!
            freshPairing.HasIdentity.Should().BeTrue();
            state.NodeId.Should().Be(origNodeId);
            state.PublicKey.Should().Equal(origPubKey);
            var phoneCode = freshPairing.PhoneCode();
            phoneCode.Should().NotBeNull();
            phoneCode!.NodeId.Should().Be(origNodeId);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task AcceptCallCode_WhenInterruptedOrRetried_RecoversDurableConnectionAndSpendsSecretAtomically()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bmb-accept-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "beememorybank.db");
        var logPath = Path.Combine(tempDir, "blind.log");

        try
        {
            BeeMemoryBank.Storage.Sqlite.DapperConfig.Configure();
            var dbFactory = new BeeMemoryBank.Storage.Sqlite.DbConnectionFactory(dbPath);
            var runner = new BeeMemoryBank.Storage.Sqlite.MigrationRunner(dbFactory);
            await runner.RunMigrationsAsync();

            var nodeRepo = new BeeMemoryBank.Storage.Sqlite.NodeIdentityRepository(dbFactory);
            var recorder = new SqliteBlindIdentityRecorder(dbFactory);
            var keys = new InMemoryBlindPhoneKeys();
            var store = new InMemoryBlindPhoneStore();
            var state = new BlindPhoneState(store);
            var log = new BlindPhoneLog(logPath, TimeProvider.System);
            var pairing = new BlindPhonePairing(state, keys, recorder, log);

            await pairing.CreateIdentityAsync("Phone");
            var phoneCode = pairing.PhoneCode()!;

            // Prepare a valid call code from the listening node
            var listenerNodeId = BlindNodeId.NewId();
            var (listenerPubKey, _) = BeeMemoryBank.Crypto.Ed25519Signer.GenerateKeyPair();
            var validPin = System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var enrollment = BlindPhoneEnrollment.Prepare(
                phoneCode.ToString(),
                "https://127.0.0.1:5301",
                listenerNodeId,
                validPin,
                listenerPubKey,
                DateTime.UtcNow,
                out var prepareErr);
            prepareErr.Should().BeNull();
            var validCodeStr = enrollment!.CallCode.ToString();

            // Test concurrent acceptance: multiple tasks try to accept codes
            var otherPin = System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var tasks = Enumerable.Range(0, 5).Select(async i =>
            {
                var codeToTry = (i == 0) ? validCodeStr : validCodeStr.Replace(validPin, otherPin);
                return await Task.Run(() => pairing.AcceptCallCode(codeToTry));
            }).ToList();

            var results = await Task.WhenAll(tasks);

            // Exactly ONE acceptance must succeed (null result)
            results.Count(r => r == null).Should().Be(1);
            pairing.IsPaired.Should().BeTrue();
            state.CallCode!.SpkiPin.Should().Be(validPin);

            // Secret is atomically spent
            keys.LoadPairingSecret().Should().BeNull();

            // Replay-safe: once paired and secret spent, replaying the code is rejected with already paired
            var replayResult = pairing.AcceptCallCode(validCodeStr);
            replayResult.Should().Contain("already paired");

            // Test crash recovery: simulate process death after state.CallCode was saved but before keys.ClearPairingSecret()
            var secret2 = BeeMemoryBank.Crypto.BlindPairingSecret.New();
            keys.SavePairingSecret(secret2);
            var (listenerPubKey2, _) = BeeMemoryBank.Crypto.Ed25519Signer.GenerateKeyPair();
            var pin2 = System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var code2 = BlindCallCode.Create("https://127.0.0.1:5302", BlindNodeId.NewId(), pin2, listenerPubKey2, secret2);
            state.CallCode = code2; // CallCode is committed in state, but secret2 was not cleared due to crash

            // On next start/access:
            var recoveredPairing = new BlindPhonePairing(state, keys, recorder, log);
            recoveredPairing.IsPaired.Should().BeTrue();
            recoveredPairing.AwaitingAnswer.Should().BeFalse();
            keys.LoadPairingSecret().Should().BeNull("orphaned secret committed in previous session was spent on recovery");
            recoveredPairing.PhoneCode().Should().BeNull();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task TwoCodePairing_WithFakeListener_ExecutesFullLifecycle_AndEnforcesMandatorySpkiPin()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bmb-twocode-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "beememorybank.db");
        var logPath = Path.Combine(tempDir, "blind-log.jsonl");

        using var rsa1 = System.Security.Cryptography.RSA.Create(2048);
        var req1 = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=127.0.0.1", rsa1, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert1 = req1.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
        using var serverCert1 = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
            cert1.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null, System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.Exportable);

        using var rsa2 = System.Security.Cryptography.RSA.Create(2048);
        var req2 = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=127.0.0.1", rsa2, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert2 = req2.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
        using var serverCert2 = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(
            cert2.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx), null, System.Security.Cryptography.X509Certificates.X509KeyStorageFlags.Exportable);

        var listenerSpkiPin = BeeMemoryBank.Crypto.SpkiPin.Of(serverCert1);
        var otherSpkiPin = BeeMemoryBank.Crypto.SpkiPin.Of(serverCert2);

        var listener1 = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener1.Start();
        var port1 = ((System.Net.IPEndPoint)listener1.LocalEndpoint).Port;

        var listener2 = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener2.Start();
        var port2 = ((System.Net.IPEndPoint)listener2.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource();

        void StartEchoServer(System.Net.Sockets.TcpListener tcpListener, System.Security.Cryptography.X509Certificates.X509Certificate2 cert)
        {
            Task.Run(async () =>
            {
                try
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        var client = await tcpListener.AcceptTcpClientAsync(cts.Token);
                        _ = Task.Run(async () =>
                        {
                            using (client)
                            using (var ssl = new System.Net.Security.SslStream(client.GetStream(), false))
                            {
                                try
                                {
                                    await ssl.AuthenticateAsServerAsync(cert);
                                    var buffer = new byte[4096];
                                    while (!cts.Token.IsCancellationRequested)
                                    {
                                        var read = await ssl.ReadAsync(buffer, cts.Token);
                                        if (read == 0) break;
                                        var reqText = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
                                        if (reqText.Contains("\r\n\r\n"))
                                        {
                                            var response = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"u8.ToArray();
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
                catch when (cts.IsCancellationRequested) { }
                catch (Exception) { }
            });
        }

        StartEchoServer(listener1, serverCert1);
        StartEchoServer(listener2, serverCert2);

        try
        {
            BeeMemoryBank.Storage.Sqlite.DapperConfig.Configure();
            var dbFactory = new BeeMemoryBank.Storage.Sqlite.DbConnectionFactory(dbPath);
            var runner = new BeeMemoryBank.Storage.Sqlite.MigrationRunner(dbFactory);
            await runner.RunMigrationsAsync();

            var nodeRepo = new BeeMemoryBank.Storage.Sqlite.NodeIdentityRepository(dbFactory);
            var recorder = new SqliteBlindIdentityRecorder(dbFactory);
            var keys = new InMemoryBlindPhoneKeys();
            var store = new InMemoryBlindPhoneStore();
            var state = new BlindPhoneState(store);
            var log = new BlindPhoneLog(logPath, TimeProvider.System);
            var pairing = new BlindPhonePairing(state, keys, recorder, log);

            // 1. Initial State: no identity, not paired, not awaiting answer
            pairing.HasIdentity.Should().BeFalse();
            pairing.IsPaired.Should().BeFalse();
            pairing.AwaitingAnswer.Should().BeFalse();
            pairing.PhoneCode().Should().BeNull();

            // 2. Create Identity
            await pairing.CreateIdentityAsync("Pixel Blind Phone");
            pairing.HasIdentity.Should().BeTrue();
            pairing.IsPaired.Should().BeFalse();
            pairing.AwaitingAnswer.Should().BeTrue();

            var phoneCode = pairing.PhoneCode();
            phoneCode.Should().NotBeNull();
            phoneCode!.DisplayName.Should().Be("Pixel Blind Phone");
            BlindNodeId.IsBlind(phoneCode.NodeId).Should().BeTrue();

            // Check DB identity row is v=2
            var dbIdentity = await nodeRepo.GetAsync();
            dbIdentity.Should().NotBeNull();
            dbIdentity!.Ed25519PrivateKeyV.Should().Be(BeeMemoryBank.Crypto.NodeIdentityCrypto.ExternalKeyVersion);
            dbIdentity.Ed25519PrivateKey.Should().BeEmpty();

            // 3. Windows Hub side prepares enrollment from phone code
            var listenerNodeId = BlindNodeId.NewId();
            var (listenerPubKey, _) = BeeMemoryBank.Crypto.Ed25519Signer.GenerateKeyPair();
            var enrollment = BlindPhoneEnrollment.Prepare(
                phoneCode.ToString(),
                $"https://127.0.0.1:{port1}",
                listenerNodeId,
                listenerSpkiPin,
                listenerPubKey,
                DateTime.UtcNow,
                out var prepareError);

            prepareError.Should().BeNull();
            enrollment.Should().NotBeNull();
            enrollment!.Entry.NodeId.Should().Be(phoneCode.NodeId);
            enrollment.Entry.IsSuperadmin.Should().BeFalse();
            enrollment.SealedSecretName.Should().Be($"android-backup:{phoneCode.NodeId}");
            enrollment.CallCode.SpkiPin.Should().Be(listenerSpkiPin);

            // 4. Verify state before accepting call code
            pairing.IsPaired.Should().BeFalse();
            state.CallCode.Should().BeNull();

            // 5. Phone accepts call code from Windows Hub
            var acceptErr = pairing.AcceptCallCode(enrollment.CallCode.ToString());
            acceptErr.Should().BeNull();
            pairing.IsPaired.Should().BeTrue();
            pairing.AwaitingAnswer.Should().BeFalse();
            state.CallCode.Should().NotBeNull();
            state.CallCode!.Address.Should().Be($"https://127.0.0.1:{port1}");
            state.CallCode.SpkiPin.Should().Be(listenerSpkiPin);

            // Secret is spent immediately upon accepting call code
            keys.LoadPairingSecret().Should().BeNull();
            pairing.PhoneCode().Should().BeNull();

            // 6. Connect to Fake Listener using BlindHttpClientProvider
            var services = new ServiceCollection();
            services.AddSingleton(state);
            services.AddTransient<MaintenanceDetectingHandler>();
            services.AddTransient<BlindHttpHandler>();
            services.AddSingleton<BlindHttpClientProvider>();
            services.AddSingleton<IHttpClientFactory>(sp => sp.GetRequiredService<BlindHttpClientProvider>());

            var sp = services.BuildServiceProvider();
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = factory.CreateClient();

            // Connecting to the paired listener (port 1, matching SPKI pin) succeeds:
            var res = await httpClient.GetAsync($"https://127.0.0.1:{port1}/");
            res.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

            // Connecting to another server (port 2, mismatched SPKI pin) fails due to mandatory SPKI pin enforcement:
            var actWrong = async () => await httpClient.GetAsync($"https://127.0.0.1:{port2}/");
            await actWrong.Should().ThrowAsync<HttpRequestException>();

            // 7. Replay resistance: re-submitting call code fails because secret was spent
            var replayErr = pairing.AcceptCallCode(enrollment.CallCode.ToString());
            replayErr.Should().Contain("already paired");

            // 8. Re-pair flow generates a fresh one-time secret and new phone code
            pairing.StartRePair();
            pairing.AwaitingAnswer.Should().BeTrue();
            var rePairCode = pairing.PhoneCode();
            rePairCode.Should().NotBeNull();
            rePairCode!.Secret.Should().NotEqual(phoneCode.Secret);
            rePairCode.NodeId.Should().Be(phoneCode.NodeId, "NodeId remains stable across re-pairs");
            rePairCode.BackupKey.Should().Equal(phoneCode.BackupKey, "Backup key remains stable across re-pairs");
        }
        finally
        {
            cts.Cancel();
            listener1.Stop();
            listener2.Stop();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task CreateIdentityAsync_WhenMissingSeed_FailsClosed_AndPreservesRowAndKeys()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bmb-missing-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "beememorybank.db");
        var logPath = Path.Combine(tempDir, "blind.log");

        try
        {
            BeeMemoryBank.Storage.Sqlite.DapperConfig.Configure();
            var dbFactory = new BeeMemoryBank.Storage.Sqlite.DbConnectionFactory(dbPath);
            var runner = new BeeMemoryBank.Storage.Sqlite.MigrationRunner(dbFactory);
            await runner.RunMigrationsAsync();

            var nodeRepo = new BeeMemoryBank.Storage.Sqlite.NodeIdentityRepository(dbFactory);
            var recorder = new SqliteBlindIdentityRecorder(dbFactory);
            var keys = new InMemoryBlindPhoneKeys();
            var store = new InMemoryBlindPhoneStore();
            var state = new BlindPhoneState(store);
            var log = new BlindPhoneLog(logPath, TimeProvider.System);

            // Setup valid blind v=2 row in database
            var (origPubKey, _) = BeeMemoryBank.Crypto.Ed25519Signer.GenerateKeyPair();
            var origNodeId = BlindNodeId.NewId();
            await recorder.RecordAsync(origNodeId, origPubKey, "Original Name");

            // Keys: backup key is present, but identity seed is MISSING (null)
            var backupKey = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32 };
            keys.SaveBackupKey(backupKey);
            keys.LoadIdentitySeed().Should().BeNull();

            var pairing = new BlindPhonePairing(state, keys, recorder, log);

            // Act: Must fail closed!
            var act = async () => await pairing.CreateIdentityAsync("Attempted Overwrite");
            await act.Should().ThrowAsync<InvalidOperationException>();

            // Assert: Database row must be preserved, NOT deleted or overwritten!
            using (var conn = dbFactory.CreateConnection())
            {
                conn.Open();
                var count = await Dapper.SqlMapper.ExecuteScalarAsync<long>(conn, "SELECT COUNT(*) FROM tbl_node_identity");
                count.Should().Be(1, "tbl_node_identity row must not be deleted on missing seed");
                var currentId = await Dapper.SqlMapper.ExecuteScalarAsync<Guid>(conn, "SELECT node_id FROM tbl_node_identity LIMIT 1");
                currentId.Should().Be(origNodeId, "existing identity must remain unchanged");
            }

            // Assert: Phone keys must be preserved, NOT cleared!
            keys.LoadBackupKey().Should().Equal(backupKey, "backup key must remain intact");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task CreateIdentityAsync_WhenMismatchedSeed_FailsClosed_AndPreservesRowAndKeys()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bmb-mismatched-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "beememorybank.db");
        var logPath = Path.Combine(tempDir, "blind.log");

        try
        {
            BeeMemoryBank.Storage.Sqlite.DapperConfig.Configure();
            var dbFactory = new BeeMemoryBank.Storage.Sqlite.DbConnectionFactory(dbPath);
            var runner = new BeeMemoryBank.Storage.Sqlite.MigrationRunner(dbFactory);
            await runner.RunMigrationsAsync();

            var nodeRepo = new BeeMemoryBank.Storage.Sqlite.NodeIdentityRepository(dbFactory);
            var recorder = new SqliteBlindIdentityRecorder(dbFactory);
            var keys = new InMemoryBlindPhoneKeys();
            var store = new InMemoryBlindPhoneStore();
            var state = new BlindPhoneState(store);
            var log = new BlindPhoneLog(logPath, TimeProvider.System);

            // Setup valid blind v=2 row in database
            var (origPubKey, _) = BeeMemoryBank.Crypto.Ed25519Signer.GenerateKeyPair();
            var origNodeId = BlindNodeId.NewId();
            await recorder.RecordAsync(origNodeId, origPubKey, "Original Name");

            // Keys: backup key is present, and identity seed has a DIFFERENT key
            var backupKey = new byte[32];
            backupKey[0] = 42;
            keys.SaveBackupKey(backupKey);
            var (_, differentSeed) = BeeMemoryBank.Crypto.Ed25519Signer.GenerateKeyPair();
            keys.SaveIdentitySeed(differentSeed);

            var pairing = new BlindPhonePairing(state, keys, recorder, log);

            // Act: Must fail closed!
            var act = async () => await pairing.CreateIdentityAsync("Attempted Overwrite");
            await act.Should().ThrowAsync<InvalidOperationException>();

            // Assert: Database row must be preserved, NOT deleted or overwritten!
            using (var conn = dbFactory.CreateConnection())
            {
                conn.Open();
                var count = await Dapper.SqlMapper.ExecuteScalarAsync<long>(conn, "SELECT COUNT(*) FROM tbl_node_identity");
                count.Should().Be(1, "tbl_node_identity row must not be deleted on mismatched seed");
                var currentId = await Dapper.SqlMapper.ExecuteScalarAsync<Guid>(conn, "SELECT node_id FROM tbl_node_identity LIMIT 1");
                currentId.Should().Be(origNodeId, "existing identity must remain unchanged");
            }

            // Assert: Phone keys must be preserved, NOT cleared!
            keys.LoadBackupKey().Should().Equal(backupKey, "backup key must remain intact");
            keys.LoadIdentitySeed().Should().Equal(differentSeed, "existing seed must remain intact");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SqliteBlindIdentityRecorder_GetRecordedAsync_WhenNonBlindNodeId_FailsClosed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bmb-nonblind-id-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "beememorybank.db");

        try
        {
            BeeMemoryBank.Storage.Sqlite.DapperConfig.Configure();
            var dbFactory = new BeeMemoryBank.Storage.Sqlite.DbConnectionFactory(dbPath);
            var runner = new BeeMemoryBank.Storage.Sqlite.MigrationRunner(dbFactory);
            await runner.RunMigrationsAsync();

            // Insert a row with a normal non-blind Guid
            var normalNodeId = Guid.NewGuid();
            while (BlindNodeId.IsBlind(normalNodeId)) normalNodeId = Guid.NewGuid();

            using (var conn = dbFactory.CreateConnection())
            {
                conn.Open();
                await Dapper.SqlMapper.ExecuteAsync(conn,
                    @"INSERT INTO tbl_node_identity (node_id, display_name, ed25519_public_key, ed25519_private_key, ed25519_private_key_v, created_at)
                      VALUES (@normalNodeId, 'Normal Node', @pubKey, @privKey, 2, @now)",
                    new { normalNodeId, pubKey = new byte[32], privKey = Array.Empty<byte>(), now = DateTime.UtcNow });
            }

            var nodeRepo = new BeeMemoryBank.Storage.Sqlite.NodeIdentityRepository(dbFactory);
            var recorder = new SqliteBlindIdentityRecorder(dbFactory);

            // Act & Assert: Must fail closed!
            var act = async () => await recorder.GetRecordedAsync();
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*not a blind node ID*");

            // Row in DB must NOT be cleared!
            using (var conn = dbFactory.CreateConnection())
            {
                conn.Open();
                var count = await Dapper.SqlMapper.ExecuteScalarAsync<long>(conn, "SELECT COUNT(*) FROM tbl_node_identity");
                count.Should().Be(1);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SqliteBlindIdentityRecorder_GetRecordedAsync_WhenV1PrivateKey_FailsClosed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bmb-v1-id-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "beememorybank.db");

        try
        {
            BeeMemoryBank.Storage.Sqlite.DapperConfig.Configure();
            var dbFactory = new BeeMemoryBank.Storage.Sqlite.DbConnectionFactory(dbPath);
            var runner = new BeeMemoryBank.Storage.Sqlite.MigrationRunner(dbFactory);
            await runner.RunMigrationsAsync();

            var blindNodeId = BlindNodeId.NewId();

            using (var conn = dbFactory.CreateConnection())
            {
                conn.Open();
                await Dapper.SqlMapper.ExecuteAsync(conn,
                    @"INSERT INTO tbl_node_identity (node_id, display_name, ed25519_public_key, ed25519_private_key, ed25519_private_key_v, created_at)
                      VALUES (@blindNodeId, 'V1 Node', @pubKey, @privKey, 1, @now)",
                    new { blindNodeId, pubKey = new byte[32], privKey = Array.Empty<byte>(), now = DateTime.UtcNow });
            }

            var nodeRepo = new BeeMemoryBank.Storage.Sqlite.NodeIdentityRepository(dbFactory);
            var recorder = new SqliteBlindIdentityRecorder(dbFactory);

            // Act & Assert: Must fail closed!
            var act = async () => await recorder.GetRecordedAsync();
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*key version 1*");

            // Row in DB must NOT be cleared!
            using (var conn = dbFactory.CreateConnection())
            {
                conn.Open();
                var count = await Dapper.SqlMapper.ExecuteScalarAsync<long>(conn, "SELECT COUNT(*) FROM tbl_node_identity");
                count.Should().Be(1);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SqliteBlindIdentityRecorder_GetRecordedAsync_WhenNonEmptyPrivateKey_FailsClosed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bmb-nonempty-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "beememorybank.db");

        try
        {
            BeeMemoryBank.Storage.Sqlite.DapperConfig.Configure();
            var dbFactory = new BeeMemoryBank.Storage.Sqlite.DbConnectionFactory(dbPath);
            var runner = new BeeMemoryBank.Storage.Sqlite.MigrationRunner(dbFactory);
            await runner.RunMigrationsAsync();

            var blindNodeId = BlindNodeId.NewId();

            using (var conn = dbFactory.CreateConnection())
            {
                conn.Open();
                await Dapper.SqlMapper.ExecuteAsync(conn,
                    @"INSERT INTO tbl_node_identity (node_id, display_name, ed25519_public_key, ed25519_private_key, ed25519_private_key_v, created_at)
                      VALUES (@blindNodeId, 'Key In DB', @pubKey, @privKey, 2, @now)",
                    new { blindNodeId, pubKey = new byte[32], privKey = new byte[] { 1, 2, 3 }, now = DateTime.UtcNow });
            }

            var nodeRepo = new BeeMemoryBank.Storage.Sqlite.NodeIdentityRepository(dbFactory);
            var recorder = new SqliteBlindIdentityRecorder(dbFactory);

            // Act & Assert: Must fail closed!
            var act = async () => await recorder.GetRecordedAsync();
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*private key material*");

            // Row in DB must NOT be cleared!
            using (var conn = dbFactory.CreateConnection())
            {
                conn.Open();
                var count = await Dapper.SqlMapper.ExecuteScalarAsync<long>(conn, "SELECT COUNT(*) FROM tbl_node_identity");
                count.Should().Be(1);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task CreateIdentityAsync_DoesNotClearKeys_OnNewRegistration_PreservingOrdinaryIngestKey()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "bmb-ingest-preserve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "beememorybank.db");
        var logPath = Path.Combine(tempDir, "blind.log");

        try
        {
            BeeMemoryBank.Storage.Sqlite.DapperConfig.Configure();
            var dbFactory = new BeeMemoryBank.Storage.Sqlite.DbConnectionFactory(dbPath);
            var runner = new BeeMemoryBank.Storage.Sqlite.MigrationRunner(dbFactory);
            await runner.RunMigrationsAsync();

            var nodeRepo = new BeeMemoryBank.Storage.Sqlite.NodeIdentityRepository(dbFactory);
            var recorder = new SqliteBlindIdentityRecorder(dbFactory);
            var mockKeys = new MockOrdinaryAppKeys();
            var initialIngestKey = new byte[] { 10, 20, 30, 40 };
            mockKeys.SetSimulatedIngestKey(initialIngestKey);

            var store = new InMemoryBlindPhoneStore();
            var state = new BlindPhoneState(store);
            var log = new BlindPhoneLog(logPath, TimeProvider.System);
            var pairing = new BlindPhonePairing(state, mockKeys, recorder, log);

            // Registration from scratch (empty DB)
            await pairing.CreateIdentityAsync("Phone");

            // Assert: Registration succeeded
            pairing.HasIdentity.Should().BeTrue();
            // Assert: mockKeys.Clear() was NEVER called, and the ingest store is intact!
            mockKeys.ClearCalled.Should().BeFalse("Clear() must not be called as a side effect of creating identity");
            mockKeys.IngestCleared.Should().BeFalse("ingest store must not be erased during registration");
            mockKeys.SimulatedIngestKey.Should().NotBeNull();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }
}

internal sealed class MockOrdinaryAppKeys : IBlindPhoneKeys
{
    public bool ClearCalled { get; private set; }
    public bool IngestCleared { get; private set; }
    public byte[]? SimulatedIngestKey { get; private set; }
    private byte[]? _backup;
    private byte[]? _pairing;

    public void SetSimulatedIngestKey(byte[] key) => SimulatedIngestKey = key;

    public void SaveIdentitySeed(byte[] seed)
    {
        SimulatedIngestKey = seed.ToArray();
    }

    public byte[]? LoadIdentitySeed() => SimulatedIngestKey;
    public void SaveBackupKey(byte[] key) => _backup = key.ToArray();
    public byte[]? LoadBackupKey() => _backup;
    public void SavePairingSecret(byte[] secret) => _pairing = secret.ToArray();
    public byte[]? LoadPairingSecret() => _pairing;
    public void ClearPairingSecret() => _pairing = null;

    public void Clear()
    {
        ClearCalled = true;
        IngestCleared = true;
        SimulatedIngestKey = null;
        _backup = null;
        _pairing = null;
    }
}

internal sealed class TestHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        sendAsync(request, cancellationToken);
}
