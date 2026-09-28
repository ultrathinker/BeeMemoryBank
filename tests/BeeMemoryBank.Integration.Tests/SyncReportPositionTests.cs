using System.Net;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Codex security #5. What a peer reports at <c>POST /api/sync/report-position</c> is a claim about
/// how far it has read OUR log, and that number is what lets the log be cut: a blind node trims up to
/// the position every active peer has reached (<c>BlindLogTrimmer</c> takes the minimum, and a peer
/// that never reported pins the log whole), and ordinary compaction reads the same rows. Nothing
/// checked the claim, so any whitelisted peer could report a position past the end of the log — with
/// one peer, the minimum is that number, and the log is cut ahead of what the peer really holds.
///
/// <para>
/// The rule pinned here: a report may not go above the log's head, and may not go backwards past what
/// is already recorded for that peer. Equal is allowed — a caught-up peer reports the same position
/// every cycle.
/// </para>
/// </summary>
public class SyncReportPositionTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();

    public Task InitializeAsync() => _factory.InitializeNodeAsync(password: "reportPositionPw");

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// <c>long.MaxValue</c> is the claim that made this worth fixing: it is above everything the log
    /// can ever hold, and the trimmer would take it as read-by-all.
    /// </summary>
    [Fact]
    public async Task ReportPosition_AboveTheLogHead_IsRefused_AndRecordsNothing()
    {
        var (peerId, http) = await PeerAsync();

        (await ReportAsync(http, long.MaxValue)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await Positions.GetAsync(peerId)).Should().BeNull(
            "a position past the end of the log is a claim the sender cannot have, and recording it " +
            "would let the log be trimmed ahead of what the peer really holds");
    }

    /// <summary>
    /// The honest case, and the boundary the rule turns on: a peer caught up with the head reports the
    /// head, twice, and both are accepted. One below it is not — the log must never be trimmed further
    /// on the strength of a number that goes backwards.
    /// </summary>
    [Fact]
    public async Task ReportPosition_AtTheHead_IsAccepted_AndOneBelowIt_IsNot()
    {
        var (peerId, http) = await PeerAsync();
        long head = await Events.GetMaxSequenceAsync();

        (await ReportAsync(http, head)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReportAsync(http, head)).StatusCode.Should().Be(HttpStatusCode.OK,
            "a caught-up peer reports the same position on every cycle");

        (await ReportAsync(http, head - 1)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Positions.GetAsync(peerId))!.LastPushedSeq.Should().Be(head,
            "the refused report must not move the recorded position");
    }

    /// <summary>
    /// Codex round 2, security #3. Serving a page is not the peer having read it: <c>/api/sync/events</c>
    /// records how far it has been *sent* the moment it answers, and the report is the peer's own word
    /// about what it applied. The two are kept apart — only the reported one may delete events — and
    /// this is the case that tells them apart: an honest peer that has applied less than it was served
    /// says so, and saying so must not be refused.
    /// </summary>
    [Fact]
    public async Task ReportPosition_BelowWhatThePeerWasServed_IsAccepted_AndIsWhatTheLogIsCutBy()
    {
        var (peerId, http) = await PeerAsync();
        for (var i = 0; i < 3; i++)
            await Events.AppendIfNotExistsAsync(new SyncEvent
            {
                EventId = Guid.NewGuid(), NodeId = peerId, LamportTs = i + 1,
                EventType = EventTypes.FolderCreate, Payload = "{}", Signature = new byte[64],
                ProtocolVersion = BeeMemoryBank.Sync.SyncProtocolVersion.Current, CreatedAt = DateTime.UtcNow
            });

        // The peer pulls a page and is served all three — the delivery watermark moves to 3.
        using var _ = await http.GetAsync("/api/sync/events?afterSequence=0");
        (await Positions.GetAsync(peerId))!.LastPushedSeq.Should().Be(3);

        // It applied only two of them, and says so.
        (await ReportAsync(http, 2)).StatusCode.Should().Be(HttpStatusCode.OK,
            "a peer that has applied less than it was served may say so — refusing it would leave the " +
            "delivery watermark as the only number the log could be cut by");

        var row = (await Positions.GetAsync(peerId))!;
        row.ReportedSeq.Should().Be(2, "the acknowledged position is what a blind node's trimmer cuts at");
        row.LastPushedSeq.Should().Be(3, "and the delivery watermark still says what it was sent");
    }

    private IEventLogRepository Events => _factory.Services.GetRequiredService<IEventLogRepository>();

    private ISyncPushPositionRepository Positions => _factory.Services.GetRequiredService<ISyncPushPositionRepository>();

    private async Task<(Guid PeerId, HttpClient Http)> PeerAsync()
    {
        var (publicKey, _) = Ed25519Signer.GenerateKeyPair();
        var peerId = Guid.NewGuid();
        await _factory.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = peerId,
            DisplayName = "peer",
            Ed25519PublicKey = publicKey,
            Status = "A",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            LamportTs = 1,
            SourceNodeId = peerId,
        });

        var token = _factory.Services.GetRequiredService<SyncTokenStore>()
            .IssueToken(peerId, BeeMemoryBank.Sync.SyncProtocolVersion.Current);
        var http = _factory.Server.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return (peerId, http);
    }

    private static Task<HttpResponseMessage> ReportAsync(HttpClient http, long sequence) =>
        http.PostAsync($"/api/sync/report-position?sequence={sequence}", null);
}