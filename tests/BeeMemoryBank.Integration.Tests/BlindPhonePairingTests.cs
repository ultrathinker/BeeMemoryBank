extern alias WebApp;

using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Core.Services.BlindPhone;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Recovery;
using Microsoft.Extensions.DependencyInjection;
using AndroidBlindCopyModel = WebApp::BeeMemoryBank.Web.Pages.AndroidBlindCopyModel;
using ApiClient = WebApp::BeeMemoryBank.Web.Services.ApiClient;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Pairing an Android blind node on Windows (plan section 10): the phone's code becomes a plain whitelist
/// row (never a superadmin) told to the mesh, its backup key is sealed under the DEK as
/// <c>android-backup:&lt;id&gt;</c>, and the phone gets a "where to call" code built from the LISTENING
/// node's own whitelist row — its address, id, pin and key — authenticated with the phone's one-time secret.
/// A phone cannot be told to call a node the network has not pinned.
/// </summary>
public class BlindPhonePairingTests : IAsyncLifetime
{
    private const string Password = "blindPhonePairing1";
    private const string ListenerAddress = "https://blind.test:5610";

    private BmbWebApplicationFactory _api = null!;
    private HttpClient _client = null!;
    private readonly Guid _listener = BlindNodeId.NewId();
    private readonly string _listenerPin = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
    private readonly byte[] _listenerKey = Ed25519Signer.GenerateKeyPair().publicKey;

    public async Task InitializeAsync()
    {
        _api = new BmbWebApplicationFactory();
        _client = _api.CreateClient();
        await _api.InitializeNodeAsync("Host", Password);
        (await _client.PostAsJsonAsync("/api/session/unlock", new { Password })).EnsureSuccessStatusCode();
        await AddRowAsync(_listener, ListenerAddress, _listenerPin, _listenerKey);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _api.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task APhone_IsAddedAsAPlainPeer_ItsKeySealed_AndToldToCallTheListenerAsTheNetworkPinnedIt()
    {
        var phone = PhoneCode();

        var resp = await _client.PostAsJsonAsync("/api/blind-nodes/android/", new { code = phone.ToString(), listenerId = _listener });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = (await RowAsync(phone.NodeId))!;
        row.IsSuperadmin.Should().BeFalse("a blind node is never a superadmin");
        row.Ed25519PublicKey.Should().Equal(phone.PublicKey);
        (await EventsAsync(EventTypes.WhitelistAdd)).Should().ContainSingle(e => e.GetProperty("node_id").GetGuid() == phone.NodeId,
            "the mesh — the listener included — must hear of the phone to take its calls");

        var sealedSet = (await EventsAsync(EventTypes.SealedSecretSet))
            .Single(e => e.GetProperty("name").GetString() == $"android-backup:{phone.NodeId}");
        var dek = _api.Services.GetRequiredService<SessionService>().GetMasterDek();
        var opened = SealedSecretCrypto.TryOpen(sealedSet.GetProperty("name").GetString()!,
                Convert.FromBase64String(sealedSet.GetProperty("wrapped").GetString()!),
                Convert.FromBase64String(sealedSet.GetProperty("iv").GetString()!), dek);
        BlindPhoneBackupSeal.TryDecode(opened, out var seal).Should().BeTrue("the pairing record is sealed, not a bare key");
        seal!.BackupKey.Should().Equal(phone.BackupKey, "a restore from the phone's backup opens it with this seal");
        seal.ProducerNodeId.Should().Be(_listener, "a restore takes the package's producer from here, not from the file");
        seal.ProducerPublicKey.Should().Equal(_listenerKey);
        (seal.ProducerAddress, seal.ProducerTlsSpki).Should().Be((ListenerAddress, _listenerPin));
        var me = (await _api.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!;
        seal.PairedBy.Should().Be(me.NodeId, "the pairing node signs the binding a restore will check");
        Ed25519Signer.Verify(me.Ed25519PublicKey, seal.PairingStatement(phone.NodeId), seal.PairingSignature).Should().BeTrue();

        var paired = await resp.Content.ReadFromJsonAsync<JsonElement>();
        BlindCallCode.TryParse(paired.GetProperty("callCode").GetString(), out var call).Should().BeTrue();
        call!.Address.Should().Be(ListenerAddress);
        call.NodeId.Should().Be(_listener);
        call.SpkiPin.Should().Be(_listenerPin);
        call.PublicKey.Should().Equal(_listenerKey);
        call.IsAuthenticBy(phone.Secret).Should().BeTrue("the phone checks the code came from whoever read its own code");
    }

    [Fact]
    public async Task TheSameCodeTwice_KeepsOneRow()
    {
        var phone = PhoneCode();

        for (var i = 0; i < 2; i++)
            (await _client.PostAsJsonAsync("/api/blind-nodes/android/", new { code = phone.ToString(), listenerId = _listener }))
                .StatusCode.Should().Be(HttpStatusCode.OK);

        (await EventsAsync(EventTypes.WhitelistAdd)).Count(e => e.GetProperty("node_id").GetGuid() == phone.NodeId).Should().Be(1);
    }

    [Fact]
    public async Task NotAPhoneCode_IsRefused_AndNothingIsWritten()
    {
        var before = await EventCountAsync();

        var resp = await _client.PostAsJsonAsync("/api/blind-nodes/android/", new { code = "bmb-blind-phone:?n=nonsense", listenerId = _listener });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await EventCountAsync()).Should().Be(before);
    }

    /// <summary>
    /// Only a row the call code can carry is a listener — an https origin and a 32-byte base64url pin, as
    /// <see cref="BlindCallCode"/> requires: anything else is neither listed nor paired to (a controlled 409,
    /// not a call code the phone would refuse or an unhandled error).
    /// </summary>
    [Theory]
    [InlineData("http://hub.test:5300", "pin")]        // plain http: nothing to pin
    [InlineData("https://hub.test:5300", null)]        // https, but the network has no pin for it
    [InlineData("https://hub.test:5300/sync", "pin")]  // not an origin: a path
    [InlineData("https://u:p@hub.test:5300", "pin")]   // not an origin: user info
    [InlineData("https://hub.test:5300", "pin-1")]     // a pin that is not a SHA-256 in base64url
    [InlineData("not an address", "pin")]
    public async Task AListenerThePhoneCannotPin_IsRefused_AndNothingIsWritten(string address, string? pin)
    {
        var hub = Guid.NewGuid();
        await AddRowAsync(hub, address, pin == "pin" ? _listenerPin : pin, Ed25519Signer.GenerateKeyPair().publicKey);
        var phone = PhoneCode();
        var before = await EventCountAsync();

        var resp = await _client.PostAsJsonAsync("/api/blind-nodes/android/", new { code = phone.ToString(), listenerId = hub });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await RowAsync(phone.NodeId)).Should().BeNull();
        (await EventCountAsync()).Should().Be(before, "neither the row nor the seal may be published");
        (await ListenersAsync()).Should().NotContain(hub);
    }

    [Fact]
    public async Task WhileLocked_NothingIsWritten()
    {
        _api.Services.GetRequiredService<SessionService>().Lock();
        var phone = PhoneCode();
        var before = await EventCountAsync();

        var resp = await _client.PostAsJsonAsync("/api/blind-nodes/android/", new { code = phone.ToString(), listenerId = _listener });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await RowAsync(phone.NodeId)).Should().BeNull("a phone without its sealed key would wait for it forever");
        (await EventCountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task APhoneIdAlreadyInTheNetworkUnderAnotherKey_IsRefused()
    {
        var phone = PhoneCode();
        await AddRowAsync(phone.NodeId, apiAddress: null, tlsSpki: null, Ed25519Signer.GenerateKeyPair().publicKey);

        var resp = await _client.PostAsJsonAsync("/api/blind-nodes/android/", new { code = phone.ToString(), listenerId = _listener });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await RowAsync(phone.NodeId))!.Ed25519PublicKey.Should().NotEqual(phone.PublicKey);
    }

    [Fact]
    public async Task AnOrdinaryUser_GetsNowhere()
    {
        using var user = _api.Server.CreateClient();
        user.DefaultRequestHeaders.Add("X-Internal-Key", BmbWebApplicationFactory.InternalKeyForTests);
        user.DefaultRequestHeaders.Add("X-User-Role", "user");

        (await user.PostAsJsonAsync("/api/blind-nodes/android/", new { code = PhoneCode().ToString(), listenerId = _listener }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await user.GetAsync("/api/blind-nodes/android/listeners")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Listeners_AreTheNodesWithAPinnedHttpsAddress()
    {
        (await ListenersAsync()).Should().Equal([_listener]);
    }

    /// <summary>The Windows page: pick the listener, paste the phone's code, get the call code as text and QR.</summary>
    [Fact]
    public async Task ThePage_OffersTheListener_AndShowsTheCallCode()
    {
        var page = new AndroidBlindCopyModel(new ApiClient(_client));
        await page.OnGetAsync();
        page.Listeners!.Select(l => l.NodeId).Should().Equal([_listener]);

        var phone = PhoneCode();
        await page.OnPostAsync(phone.ToString(), _listener);

        page.ErrorMessage.Should().BeNull();
        BlindCallCode.TryParse(page.Paired!.CallCode, out var call).Should().BeTrue();
        call!.IsAuthenticBy(phone.Secret).Should().BeTrue();
        page.CallCodeQr.Should().StartWith("data:image/png;base64,");
    }

    [Fact]
    public async Task ThePage_ShowsWhyAPairingWasRefused()
    {
        var page = new AndroidBlindCopyModel(new ApiClient(_client));

        await page.OnPostAsync("not a phone code", _listener);

        page.Paired.Should().BeNull();
        page.ErrorMessage.Should().Contain("not the code of an Android blind copy");
        page.Listeners.Should().NotBeNull("the form is shown again");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static BlindPhoneCode PhoneCode() => new(BlindNodeId.NewId(), Ed25519Signer.GenerateKeyPair().publicKey,
        BlindPairingSecret.New(), RandomNumberGenerator.GetBytes(32), "Pixel");

    private async Task AddRowAsync(Guid nodeId, string? apiAddress, string? tlsSpki, byte[] publicKey)
    {
        using var scope = _api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = nodeId, DisplayName = nodeId.ToString("N")[..6], Ed25519PublicKey = publicKey,
            ApiAddress = apiAddress, TlsSpki = tlsSpki, Status = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
    }

    private async Task<WhitelistEntry?> RowAsync(Guid nodeId)
    {
        using var scope = _api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWhitelistRepository>().GetByNodeIdAsync(nodeId, includeDeleted: true);
    }

    private async Task<List<Guid>> ListenersAsync()
    {
        var list = await _client.GetFromJsonAsync<JsonElement>("/api/blind-nodes/android/listeners");
        return list.EnumerateArray().Select(l => l.GetProperty("nodeId").GetGuid()).ToList();
    }

    private async Task<List<JsonElement>> EventsAsync(string type)
    {
        using var scope = _api.Services.CreateScope();
        var events = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(0, 10_000);
        return events.Where(e => e.EventType == type).Select(e => JsonDocument.Parse(e.Payload).RootElement.Clone()).ToList();
    }

    private async Task<int> EventCountAsync()
    {
        using var scope = _api.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(0, 10_000)).Count;
    }
}
