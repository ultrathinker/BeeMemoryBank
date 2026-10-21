using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;

namespace BeeMemoryBank.BlindDesktop.Tests;

/// <summary>
/// A full node that answers the pull protocol in memory (challenge, authenticate, events, report-position), standing at the bottom of an account's real HTTP pipeline
/// (<see cref="BlindAccountHostOptions.PrimaryHandlerFactory"/>): the account's own pull client, signer, applier and database run for real. Each hub has its own node id, key, pin and a canary
/// (the name of the folder its events create) that must reach its own account's database and no other's. It records every request it gets.
/// </summary>
internal sealed class DesktopFakeHub : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly byte[] _privateKey;
    private readonly object _gate = new();
    private long _lamport = 100;
    private readonly List<SyncEvent> _events = [];
    private readonly List<string> _seen = [];

    public DesktopFakeHub(string host)
    {
        Origin = $"https://{host}:5610";
        (PublicKey, _privateKey) = Ed25519Signer.GenerateKeyPair();
        Pin = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        Canary = "cnry" + host.Replace(".", "") + Guid.NewGuid().ToString("N");
    }

    public Guid NodeId { get; } = Guid.NewGuid();
    public byte[] PublicKey { get; }
    public string Origin { get; }
    public string Pin { get; }

    /// <summary>The folder name this hub's events carry.</summary>
    public string Canary { get; }

    /// <summary>This hub's signature over <paramref name="payload"/> (an invitation it issues is signed this way).</summary>
    public byte[] Sign(byte[] payload) => Ed25519Signer.Sign(_privateKey, payload);

    public IReadOnlyList<string> Requests { get { lock (_gate) return [.. _seen]; } }

    /// <summary>The node ids that authenticated here.</summary>
    public IReadOnlyList<Guid> Callers() => Requests
        .Where(r => r.StartsWith("POST /api/sync/authenticate\n", StringComparison.Ordinal))
        .Select(r => JsonDocument.Parse(r[(r.IndexOf('\n') + 1)..]).RootElement.GetProperty("nodeId").GetGuid()).ToList();

    /// <summary>The whitelist row an account needs to accept this hub's events: the hub is a superadmin author with this key.</summary>
    public WhitelistEntry WhitelistRow() => new()
    {
        NodeId = NodeId, DisplayName = "Hub", Ed25519PublicKey = PublicKey, Status = "A", IsSuperadmin = true,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, LamportTs = 1, SourceNodeId = NodeId,
    };

    /// <summary>The call code this hub gives an account whose pairing secret is <paramref name="pairingSecret"/>.</summary>
    public string CallCodeFor(byte[] pairingSecret) => BlindCallCode.Create(Origin, NodeId, Pin, PublicKey, pairingSecret).ToString();

    /// <summary>Adds a signed folder_create event named <paramref name="name"/> (this hub's canary by default).</summary>
    public SyncEvent AddFolder(string? name = null)
    {
        name ??= Canary;
        var evt = new SyncEvent
        {
            EventType = EventTypes.FolderCreate, ProtocolVersion = 3,
            Payload = JsonSerializer.Serialize(new FolderCreatePayload(Guid.NewGuid(), "/" + name, name, "/", DateTime.UtcNow, DateTime.UtcNow)),
            EventId = Guid.NewGuid(), NodeId = NodeId, CreatedAt = DateTime.UtcNow,
        };
        lock (_gate)
        {
            evt.LamportTs = ++_lamport;
            evt.SequenceNum = _events.Count + 1;
            evt.Signature = Ed25519Signer.Sign(_privateKey, EventSignature.BuildPayload(evt));
            _events.Add(evt);
        }
        return evt;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (_gate) _seen.Add($"{request.Method.Method} {url.AbsolutePath}\n{body}");

        switch (url.AbsolutePath)
        {
            case "/api/sync/challenge":
                return Json(new { challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), serverNodeId = NodeId });
            case "/api/sync/authenticate":
                return Json(new { token = "token-" + Guid.NewGuid().ToString("N") });
            case "/api/sync/events":
            {
                var after = long.Parse(System.Web.HttpUtility.ParseQueryString(url.Query)["afterSequence"] ?? "0");
                List<SyncEvent> page;
                lock (_gate) page = _events.Where(e => e.SequenceNum > after).ToList();
                return Json(page);
            }
            case "/api/sync/report-position":
                return Json(new { ok = true });
            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, Web), Encoding.UTF8, "application/json"),
    };
}
