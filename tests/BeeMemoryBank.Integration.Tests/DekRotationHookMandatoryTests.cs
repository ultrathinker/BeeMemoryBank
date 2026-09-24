using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Exceptions;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Pre-rewrap hooks are mandatory. A hook that cannot finish (chat.db rows it could not move off
/// the outgoing master DEK, an I/O error) must stop the rotation BEFORE anything changes — on the
/// initiator before the proposal is even published, on a peer without marking the rotation Failed
/// (which nothing retries) — and the rotation must go through once the cause is gone. Letting it
/// commit would leave that data sealed under a DEK that is gone after the next restart.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class DekRotationHookMandatoryTests : IAsyncLifetime
{
    private sealed class ToggleHook : IDekRotationHook
    {
        public volatile bool Fail = true;
        public int Calls;

        public Task BeforeRewrapAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Fail ? Task.FromException(new IOException("simulated chat.db I/O failure")) : Task.CompletedTask;
        }
    }

    private sealed class HookedFactory(ToggleHook hook) : BmbWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => s.AddSingleton<IDekRotationHook>(hook));
        }
    }

    private readonly ToggleHook _hook = new();
    private readonly HookedFactory _factory;
    private HttpClient _client = null!;
    private const string Password = "hookMandatoryPwd1!";

    public DekRotationHookMandatoryTests() => _factory = new HookedFactory(_hook);

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        await _factory.InitializeNodeAsync(password: Password);
        (await _client.PostAsJsonAsync("/api/session/login", new { username = "admin", password = Password }))
            .EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task<int> CountAsync(string sql)
    {
        using var conn = _factory.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        return await conn.ExecuteScalarAsync<int>(sql);
    }

    [Fact]
    public async Task Initiator_FailingHook_RefusesThePropose_PublishesNothing_AndSucceedsOnceFixed()
    {
        var propose = await _client.PostAsJsonAsync("/api/dek-rotation/propose", new { masterPassword = Password });
        propose.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await propose.Content.ReadAsStringAsync()).Should().Contain("not started");

        (await CountAsync("SELECT COUNT(*) FROM tbl_event WHERE event_type LIKE 'dek_rotation%'"))
            .Should().Be(0, "a refused proposal must not publish anything a peer could act on");
        (await CountAsync("SELECT COUNT(*) FROM tbl_dek_rotation_state")).Should().Be(0);

        _hook.Fail = false;
        var retry = await _client.PostAsJsonAsync("/api/dek-rotation/propose", new { masterPassword = Password });
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        var commitEventId = (await retry.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("commitEventId").GetGuid().ToString();
        (await _client.PostAsJsonAsync("/api/dek-rotation/accept", new { commitEventId, masterPassword = Password }))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        DekRotationFlowStep step;
        do
        {
            await Task.Delay(200);
            var progress = await (await _client.GetAsync("/api/dek-rotation/progress")).Content.ReadFromJsonAsync<JsonElement>();
            step = Enum.Parse<DekRotationFlowStep>(progress.GetProperty("currentStep").GetString()!);
        } while (step is not (DekRotationFlowStep.Completed or DekRotationFlowStep.Failed) && DateTime.UtcNow < deadline);
        step.Should().Be(DekRotationFlowStep.Completed);
    }

    [Fact]
    public async Task Peer_FailingHook_LeavesTheRotationPending_WithoutARetryStorm_AndItAppliesOnRetry()
    {
        var (peerPub, peerSeed) = Ed25519Signer.GenerateKeyPair();
        var peerNodeId = Guid.NewGuid();
        var whitelist = _factory.Services.GetRequiredService<IWhitelistRepository>();
        await whitelist.CreateAsync(new WhitelistEntry
        {
            NodeId = peerNodeId, DisplayName = "Initiator", Ed25519PublicKey = peerPub, Status = "A",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await whitelist.SetAutoAcceptDekRotationAsync(peerNodeId.ToString(), true);

        var session = _factory.Services.GetRequiredService<SessionService>();
        var oldDek = session.GetMasterDek();
        var newDek = RandomNumberGenerator.GetBytes(32);
        var (enc, iv) = MasterKeyManager.WrapMasterDek(newDek, oldDek);
        var epoch = await CountAsync("SELECT dek_epoch FROM tbl_node_identity");
        var payload = new DekRotationCommitPayload(
            ProposedEventId: Guid.NewGuid().ToString(), NewDekEpoch: epoch + 1, RotationTs: DateTime.UtcNow.ToString("O"),
            OriginatorNodeId: peerNodeId.ToString(), EncryptedNewDek: Convert.ToBase64String(enc), Iv: Convert.ToBase64String(iv));
        var commit = new SyncEvent
        {
            EventId = Guid.NewGuid(), NodeId = peerNodeId, LamportTs = 1_000_000, EventType = EventTypes.DekRotationCommit,
            Payload = JsonSerializer.Serialize(payload), ProtocolVersion = 1,
            CreatedAt = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc),
        };
        commit.Signature = Ed25519Signer.Sign(peerSeed, EventSignature.BuildPayload(commit));
        // What EventApplier does when the COMMIT arrives: the event is logged and the state is Committing.
        await _factory.Services.GetRequiredService<IEventLogRepository>().AppendAsync(commit);
        await _factory.Services.GetRequiredService<IDekRotationStateRepository>().UpsertAsync(new DekRotationStateRow(
            commit.EventId.ToString(), DekRotationState.Committing, payload.ProposedEventId, payload.RotationTs,
            null, null, null, null, null, null, null, DateTime.UtcNow.ToString("O"), DateTime.UtcNow.ToString("O")));

        var rotation = _factory.Services.GetRequiredService<DekRotationService>();
        var act = () => rotation.AutoAcceptCommitAsync(commit);
        await act.Should().ThrowAsync<DekRotationPreconditionException>();

        var stateRepo = _factory.Services.GetRequiredService<IDekRotationStateRepository>();
        (await stateRepo.GetAsync(commit.EventId.ToString()))!.State.Should().Be(DekRotationState.Committing,
            "nothing was changed, so the rotation must stay retryable rather than terminally Failed");
        session.GetMasterDek().Should().Equal(oldDek, "the node must still be on its old DEK");

        // The post-apply sweep must not immediately re-dispatch the deferred row in a loop.
        await Task.Delay(1500);
        _hook.Calls.Should().Be(1, "a deferred rotation waits for the next unlock instead of retrying itself forever");

        _hook.Fail = false;
        await rotation.RetryPendingAutoAcceptsAsync();

        (await stateRepo.GetAsync(commit.EventId.ToString()))!.State.Should().Be(DekRotationState.Applied);
        session.GetMasterDek().Should().Equal(newDek, "the retried rotation applied");
        Array.Clear(oldDek);
    }
}
