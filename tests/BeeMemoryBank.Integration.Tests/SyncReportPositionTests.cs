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