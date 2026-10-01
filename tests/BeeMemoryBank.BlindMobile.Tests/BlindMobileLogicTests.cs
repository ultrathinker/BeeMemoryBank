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
        // Document: In Stage 2, this must be replaced by a real SQLite test asserting the v=2 row in tbl_node_identity.
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
}

internal sealed class TestHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        sendAsync(request, cancellationToken);
}
