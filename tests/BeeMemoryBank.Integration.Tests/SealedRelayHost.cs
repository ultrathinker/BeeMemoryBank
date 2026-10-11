using System.Data;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using BeeMemoryBank.Api.Endpoints;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Sealed;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Boot;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.Metadata;
using SealedEnvelope = BeeMemoryBank.Sync.Metadata.SealedEnvelope;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>A node the relay knows as a peer: its id and Ed25519 keys.</summary>
internal sealed record RelayPeer(Guid Id, byte[] PublicKey, byte[] PrivateKey)
{
    public static RelayPeer New()
    {
        var (pub, priv) = Ed25519Signer.GenerateKeyPair();
        return new RelayPeer(Guid.NewGuid(), pub, priv);
    }
}

/// <summary>
/// The relay of a closed node on its own (docs/design/step-b-spec.md 6): a boot store with an identity and a whitelist mirror, the in-memory
/// token store, a gate the test sets, and the one piece of the sealed-vault middleware (track 3) that matters here - a request of the relay
/// set goes to <see cref="ISealedRelay"/> while the gate is Closed, Opening or Importing. No vault database exists in this host at all, so
/// nothing the relay answers can have come from one.
/// </summary>
internal sealed class SealedRelayHost : IAsyncDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "bmb_relay_host_" + Guid.NewGuid().ToString("N"));
    public FakeGate Gate { get; } = new(VaultState.Closed);
    public BootStore Boot { get; private set; } = null!;
    public RelayInbox Inbox { get; private set; } = null!;
    public RelayWriteLock Lock { get; private set; } = null!;
    public SealedRelayLimits Limits { get; private set; } = null!;
    public WebApplication App { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public Guid NodeId { get; } = Guid.NewGuid();
    public byte[] NodePublicKey { get; private set; } = [];
    public long FreeBytes { get; set; } = long.MaxValue;

    public string BootPath => Path.Combine(Dir, "beememorybank.boot.db");

    public static async Task<SealedRelayHost> StartAsync(VaultState state = VaultState.Closed)
    {
        var host = new SealedRelayHost();
        host.Gate.State = state;
        Directory.CreateDirectory(host.Dir);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddBootStore(host.Dir);
        builder.Services.AddSingleton<SyncTokenStore>();
        builder.Services.AddSingleton<SyncEndpoints.SyncChallengeRateLimiter>();
        builder.Services.AddSingleton<RelayWriteLock>();
        builder.Services.AddSingleton(new SealedRelayLimits(() => host.FreeBytes));
        builder.Services.AddSingleton<IVaultGate>(host.Gate);
        builder.Services.AddSingleton<InvisibleModeService>();
        builder.Services.AddSingleton<ISealedRelay, SealedRelay>();
        host.App = builder.Build();

        // What the sealed-vault middleware does with the relay set (spec 5.6); every other request is answered 503 here.
        host.App.Run(async context =>
        {
            var relay = context.RequestServices.GetRequiredService<ISealedRelay>();
            if (host.Gate.State is VaultState.Closed or VaultState.Opening or VaultState.Importing && relay.Handles(context.Request.Path, context.Request.Method))
                await relay.HandleAsync(context);
            else
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        });

        host.Boot = host.App.Services.GetRequiredService<BootStore>();
        host.Inbox = host.App.Services.GetRequiredService<RelayInbox>();
        host.Lock = host.App.Services.GetRequiredService<RelayWriteLock>();
        host.Limits = host.App.Services.GetRequiredService<SealedRelayLimits>();

        await host.Boot.EnsureCreatedAsync();
        await host.Boot.SetMetaAsync(BootMetaKeys.NodeId, host.NodeId.ToString("D"));
        await host.Boot.SetMetaAsync(BootMetaKeys.SeqFloor, "101");
        await host.Boot.SetMetaAsync(BootMetaKeys.SealedHead, "100");
        await host.Boot.SetMetaAsync(BootMetaKeys.CleanClose, "1");
        var (pub, _) = Ed25519Signer.GenerateKeyPair();
        host.NodePublicKey = pub;
        using (var c = host.Boot.Connections.Open())
            await c.ExecuteAsync(
                "INSERT INTO tbl_boot_identity (node_id, display_name, ed25519_public_key, updated_at) VALUES (@id, 'Closed node', @pub, @now)",
                new { id = host.NodeId.ToString("D"), pub, now = DateTime.UtcNow.ToString("o") });

        await host.App.StartAsync();
        host.Client = host.App.GetTestClient();
        return host;
    }

    /// <summary>A peer in the whitelist mirror (status 'A').</summary>
    public async Task<RelayPeer> AddPeerAsync(bool superadmin = false, string status = "A", Guid? id = null)
    {
        var peer = RelayPeer.New();
        if (id is { } fixedId) peer = peer with { Id = fixedId };
        using var c = Boot.Connections.Open();
        await c.ExecuteAsync(
            @"INSERT INTO tbl_whitelist (node_id, display_name, ed25519_public_key, status, is_superadmin, created_at, updated_at)
              VALUES (@id, @name, @pub, @status, @sa, @now, @now)",
            new { id = peer.Id.ToString("D"), name = "Peer " + peer.Id.ToString("N")[..6], pub = peer.PublicKey, status, sa = superadmin ? 1 : 0, now = DateTime.UtcNow.ToString("o") });
        return peer;
    }

    public async Task<string?> MirrorStatusAsync(Guid node)
    {
        using var c = Boot.Connections.Open();
        return await c.ExecuteScalarAsync<string?>("SELECT status FROM tbl_whitelist WHERE node_id = @id", new { id = node.ToString("D") });
    }

    // ---- requests ------------------------------------------------------------------------------------------------------------------

    public async Task<string> TokenAsync(RelayPeer peer, int protocol = 4, IReadOnlyList<string>? capabilities = null)
    {
        var response = await AuthenticateAsync(peer, protocol, capabilities ?? MetadataCapabilities.Advertised);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SyncAuthResponse>())!.Token;
    }

    public async Task<HttpResponseMessage> AuthenticateAsync(RelayPeer peer, int protocol = 4, IReadOnlyList<string>? capabilities = null,
        byte[]? signWith = null)
    {
        var challenge = (await (await Client.PostAsync("/api/sync/challenge", null)).Content.ReadFromJsonAsync<SyncChallengeResponse>())!;
        var payload = "BMB-CHALLENGE-V2\0"u8.ToArray().Concat(challenge.ServerNodeId.ToByteArray()).Concat(Convert.FromBase64String(challenge.Challenge)).ToArray();
        var signature = Ed25519Signer.Sign(signWith ?? peer.PrivateKey, payload);
        return await Client.PostAsJsonAsync("/api/sync/authenticate",
            new SyncAuthRequest(peer.Id, challenge.Challenge, Convert.ToBase64String(signature), protocol, capabilities, "2.6.0"));
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        return Client.SendAsync(request);
    }

    public Task<HttpResponseMessage> PushAsync(string token, params SyncEvent[] events) => SendAsync(HttpMethod.Post, "/api/sync/events", token, events);

    public async Task<List<SyncEvent>> PullAsync(string token, long after)
    {
        var response = await SendAsync(HttpMethod.Get, $"/api/sync/events?afterSequence={after}", token);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<SyncEvent>>())!;
    }

    // ---- events --------------------------------------------------------------------------------------------------------------------

    private static long _lamport = 1000;

    /// <summary>A well-formed sealed event of <paramref name="author"/>: its envelope carries <paramref name="secretText"/> under a key nobody here has.</summary>
    public static SyncEvent SealedEvent(RelayPeer author, string secretText = "canary", long originSequence = 1)
    {
        var inner = Encoding.UTF8.GetBytes("{\"type\":\"article_create\",\"title\":\"" + secretText + "\"}");
        var padded = new byte[(inner.Length / SealedEnvelope.PadBlock + 1) * SealedEnvelope.PadBlock];
        inner.CopyTo(padded, 0);
        var nonce = RandomNumberGenerator.GetBytes(SealedEnvelope.NonceBytes);
        var ciphertext = new byte[padded.Length];
        var tag = new byte[SealedEnvelope.TagBytes];
        using (var aes = new AesGcm(RandomNumberGenerator.GetBytes(32), SealedEnvelope.TagBytes))
            aes.Encrypt(nonce, padded, ciphertext, tag);
        var envelope = new SealedEnvelope(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), nonce, [], ciphertext.Concat(tag).ToArray());
        return Sign(author, new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = author.Id, LamportTs = Interlocked.Increment(ref _lamport), EventType = EventClasses.Sealed,
            Payload = envelope.ToPayload(), ProtocolVersion = EventClasses.SealedProtocol, CreatedAt = DateTime.UtcNow, SequenceNum = originSequence,
        });
    }

    /// <summary>A control event (a whitelist change) of <paramref name="author"/>, protocol 3 as a 2.6 node writes it.</summary>
    public static SyncEvent ControlEvent(RelayPeer author, string type, object payload, long originSequence = 1) =>
        Sign(author, new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = author.Id, LamportTs = Interlocked.Increment(ref _lamport), EventType = type,
            Payload = System.Text.Json.JsonSerializer.Serialize(payload), ProtocolVersion = SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow,
            SequenceNum = originSequence,
        });

    /// <summary>A protocol-3 content event: what a node that has not applied the activation writes, and what a closed node must not keep.</summary>
    public static SyncEvent ClearContentEvent(RelayPeer author, long originSequence = 1) =>
        Sign(author, new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = author.Id, LamportTs = Interlocked.Increment(ref _lamport), EventType = EventTypes.ArticleCreate,
            Payload = "{\"title\":\"clear title\"}", ProtocolVersion = 3, CreatedAt = DateTime.UtcNow, SequenceNum = originSequence,
        });

    public static SyncEvent Sign(RelayPeer author, SyncEvent evt)
    {
        evt.Signature = Ed25519Signer.Sign(author.PrivateKey, EventSignature.BuildPayload(evt));
        return evt;
    }

    // ---- the boot file ---------------------------------------------------------------------------------------------------------------

    /// <summary>A digest of the boot store file and its write-ahead log: a write by anyone changes it.</summary>
    public string BootFilesDigest()
    {
        using var sha = SHA256.Create();
        foreach (var path in new[] { BootPath, BootPath + "-wal" })
        {
            if (!File.Exists(path)) { sha.TransformBlock([0], 0, 1, null, 0); continue; }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            sha.TransformBlock([1], 0, 1, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    /// <summary>True when <paramref name="text"/> is anywhere in the boot store file or its log, as UTF-8 or UTF-16.</summary>
    public bool BootFilesContain(string text)
    {
        foreach (var path in new[] { BootPath, BootPath + "-wal" })
        {
            if (!File.Exists(path)) continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            if (bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0 || bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0) return true;
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}
