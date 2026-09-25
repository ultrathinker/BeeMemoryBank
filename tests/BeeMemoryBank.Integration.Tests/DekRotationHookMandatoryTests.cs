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
        /// <summary>When set, the hook signals <see cref="Entered"/> and then waits for this gate.</summary>
        public volatile TaskCompletionSource? Gate;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task BeforeRewrapAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Gate is { } gate)
            {
                Entered.TrySetResult();
                await gate.Task;
            }
            if (Fail) throw new IOException("simulated chat.db I/O failure");
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
        // The unlock fires a background sweep of pending auto-accepts. Left running, a slow machine
        // finishes it after a test has staged its peer COMMIT, and it runs that rotation once more
        // behind the test's back (a fifth hook call where four are counted).
        await _factory.Services.GetRequiredService<SessionService>().PostUnlockCatchUp;
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

    private string _lastTerminal = "";

    /// <summary>
    /// Waits for the accept that was just fired (it runs in the background) to reach a terminal
    /// step. A terminal progress identical to the one the previous accept ended on is ignored, so a
    /// second accept is never mistaken for the first one's leftover result.
    /// </summary>
    private async Task<JsonElement> WaitForTerminalProgressAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            await Task.Delay(50);
            var progress = await (await _client.GetAsync("/api/dek-rotation/progress")).Content.ReadFromJsonAsync<JsonElement>();
            var step = Enum.Parse<DekRotationFlowStep>(progress.GetProperty("currentStep").GetString()!);
            var raw = progress.GetRawText();
            if (step is DekRotationFlowStep.Completed or DekRotationFlowStep.Failed && raw != _lastTerminal)
            {
                _lastTerminal = raw;
                return progress;
            }
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("accept did not reach a new terminal step: " + raw);
        }
    }

    private static DekRotationFlowStep StepOf(JsonElement progress)
        => Enum.Parse<DekRotationFlowStep>(progress.GetProperty("currentStep").GetString()!);

    private async Task<string> ProposeAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/dek-rotation/propose", new { masterPassword = Password });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("commitEventId").GetGuid().ToString();
    }

    /// <summary>
    /// Accepts, retrying while the node answers 409. A finished accept publishes its terminal step
    /// before it releases the rotation lock, so an accept sent the moment that step is seen can
    /// find the lock still held — a client is expected to try again, and so does this one.
    /// </summary>
    private async Task AcceptAsync(string commitEventId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var resp = await _client.PostAsJsonAsync("/api/dek-rotation/accept", new { commitEventId, masterPassword = Password });
            if (resp.StatusCode != HttpStatusCode.Conflict || DateTime.UtcNow > deadline)
            {
                resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
                return;
            }
            await Task.Delay(50);
        }
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
        await AcceptAsync(await ProposeAsync());
        StepOf(await WaitForTerminalProgressAsync()).Should().Be(DekRotationFlowStep.Completed);
    }

    [Fact]
    public async Task Initiator_HookPassesAtProposeButFailsAtAccept_CommitStaysPending_AndTheSameCommitIsRetried()
    {
        _hook.Fail = false;
        var commitEventId = await ProposeAsync(); // hooks pass; the COMMIT is now public
        (await CountAsync("SELECT COUNT(*) FROM tbl_event WHERE event_type = 'dek_rotation_commit'")).Should().Be(1);
        var epoch = await CountAsync("SELECT dek_epoch FROM tbl_node_identity");

        _hook.Fail = true; // e.g. chat.db briefly locked by the time the admin accepts
        await AcceptAsync(commitEventId);
        var failed = await WaitForTerminalProgressAsync();
        StepOf(failed).Should().Be(DekRotationFlowStep.Failed);
        failed.GetProperty("errorMessage").GetString().Should().Contain("not started");

        var stateRepo = _factory.Services.GetRequiredService<IDekRotationStateRepository>();
        (await stateRepo.GetAsync(commitEventId))!.State.Should().Be(DekRotationState.Committing,
            "the COMMIT is already public; marking it Failed would split this node from peers that applied it");
        (await CountAsync("SELECT dek_epoch FROM tbl_node_identity")).Should().Be(epoch, "nothing was changed");

        // The same commit, accepted again with the password once the cause is gone.
        _hook.Fail = false;
        await AcceptAsync(commitEventId);
        StepOf(await WaitForTerminalProgressAsync()).Should().Be(DekRotationFlowStep.Completed);
        (await stateRepo.GetAsync(commitEventId))!.State.Should().Be(DekRotationState.Applied);
        (await CountAsync("SELECT dek_epoch FROM tbl_node_identity")).Should().Be(epoch + 1);

        // Once applied it cannot be run a second time (that would treat the new DEK as the old one).
        await AcceptAsync(commitEventId);
        var refused = await WaitForTerminalProgressAsync();
        StepOf(refused).Should().Be(DekRotationFlowStep.Failed);
        refused.GetProperty("errorMessage").GetString().Should().Contain("already applied");
        (await CountAsync("SELECT dek_epoch FROM tbl_node_identity")).Should().Be(epoch + 1);
    }

    [Fact]
    public async Task Initiator_AcceptWhileAnotherAcceptRuns_IsRefusedWith409_NotDroppedInTheBackground()
    {
        _hook.Fail = false;
        var commitEventId = await ProposeAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _hook.Gate = gate; // the next accept parks inside its pre-rewrap hook, holding the rotation lock

        (await _client.PostAsJsonAsync("/api/dek-rotation/accept", new { commitEventId, masterPassword = Password }))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);
        await _hook.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var second = await _client.PostAsJsonAsync("/api/dek-rotation/accept", new { commitEventId, masterPassword = Password });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "a 202 here would be a promise nobody keeps: the background accept cannot get the lock and nothing reports it");
        (await second.Content.ReadAsStringAsync()).Should().Contain("in progress");

        _hook.Gate = null;
        gate.SetResult();
        StepOf(await WaitForTerminalProgressAsync()).Should().Be(DekRotationFlowStep.Completed);
    }

    [Fact]
    public async Task Initiator_Completed_IsOnlyReportedOnceMaintenanceModeHasEnded()
    {
        _hook.Fail = false;
        var maintenance = _factory.Services.GetRequiredService<MaintenanceModeService>();
        await AcceptAsync(await ProposeAsync());

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var progress = await (await _client.GetAsync("/api/dek-rotation/progress")).Content.ReadFromJsonAsync<JsonElement>();
            // Sampled right after reading Completed: maintenance must already be off, i.e. a caller
            // acting on Completed never gets a 503.
            var inMaintenance = maintenance.IsInMaintenance;
            if (StepOf(progress) == DekRotationFlowStep.Completed)
            {
                inMaintenance.Should().BeFalse("Completed must not be observable while the node still answers 503");
                (await _client.GetAsync("/api/session/status")).StatusCode.Should().NotBe(HttpStatusCode.ServiceUnavailable);
                return;
            }
            StepOf(progress).Should().NotBe(DekRotationFlowStep.Failed);
            await Task.Delay(5);
        }
        throw new TimeoutException("rotation did not complete");
    }

    // ───── Peer ─────────────────────────────────────────────────────────────────────────────

    private async Task<(SyncEvent Commit, byte[] OldDek, byte[] NewDek)> ArrivePeerCommitAsync()
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

        var oldDek = _factory.Services.GetRequiredService<SessionService>().GetMasterDek();
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
        return (commit, oldDek, newDek);
    }

    private async Task<DekRotationState> StateAsync(SyncEvent commit)
        => (await _factory.Services.GetRequiredService<IDekRotationStateRepository>().GetAsync(commit.EventId.ToString()))!.State;

    [Fact]
    public async Task Peer_FailingHook_StaysPending_RetriesWithBoundedBackoff_NoStorm_AndAppliesOnUnlockRetry()
    {
        var rotation = _factory.Services.GetRequiredService<DekRotationService>();
        rotation.DeferredRetry.BaseDelay = TimeSpan.FromMilliseconds(100);
        rotation.DeferredRetry.MaxAttempts = 3;
        var (commit, oldDek, newDek) = await ArrivePeerCommitAsync();

        var act = () => rotation.AutoAcceptCommitAsync(commit);
        await act.Should().ThrowAsync<DekRotationPreconditionException>();
        (await StateAsync(commit)).Should().Be(DekRotationState.Committing,
            "nothing was changed, so the rotation must stay retryable rather than terminally Failed");
        var session = _factory.Services.GetRequiredService<SessionService>();
        session.GetMasterDek().Should().Equal(oldDek, "the node must still be on its old DEK");

        // Backoff 100 + 200 + 400 ms, then the budget is spent: 1 initial attempt + 3 retries, no more.
        await Task.Delay(2500);
        _hook.Calls.Should().Be(4, "automatic retries are bounded — no storm, no endless loop");
        (await StateAsync(commit)).Should().Be(DekRotationState.Committing);

        // An unlock (or a manual apply) still retries it after the automatic budget is spent.
        _hook.Fail = false;
        await rotation.RetryPendingAutoAcceptsAsync();
        (await StateAsync(commit)).Should().Be(DekRotationState.Applied);
        session.GetMasterDek().Should().Equal(newDek, "the retried rotation applied");
        Array.Clear(oldDek);
    }

    [Fact]
    public async Task Peer_TransientHookFailure_AppliesByItself_WhileTheNodeStaysUnlocked()
    {
        var rotation = _factory.Services.GetRequiredService<DekRotationService>();
        rotation.DeferredRetry.BaseDelay = TimeSpan.FromMilliseconds(200);
        var (commit, oldDek, newDek) = await ArrivePeerCommitAsync();

        var act = () => rotation.AutoAcceptCommitAsync(commit);
        await act.Should().ThrowAsync<DekRotationPreconditionException>();
        _hook.Fail = false; // the transient cause clears; nobody unlocks, nobody clicks Apply

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (await StateAsync(commit) != DekRotationState.Applied && DateTime.UtcNow < deadline)
            await Task.Delay(100);

        (await StateAsync(commit)).Should().Be(DekRotationState.Applied, "the scheduled retry must apply it on its own");
        _factory.Services.GetRequiredService<SessionService>().GetMasterDek().Should().Equal(newDek);
        Array.Clear(oldDek);
    }

    [Fact]
    public async Task Peer_RedeliveredCommit_ForAnAppliedRotation_IsANoOp()
    {
        _hook.Fail = false;
        var rotation = _factory.Services.GetRequiredService<DekRotationService>();
        var (commit, oldDek, newDek) = await ArrivePeerCommitAsync();
        Array.Clear(oldDek);

        await rotation.AutoAcceptCommitAsync(commit);
        (await StateAsync(commit)).Should().Be(DekRotationState.Applied);
        var epoch = await CountAsync("SELECT dek_epoch FROM tbl_node_identity");
        var calls = _hook.Calls;

        // Sync re-delivers the same COMMIT after it was applied.
        var redelivery = () => rotation.AutoAcceptCommitAsync(commit);
        await redelivery.Should().NotThrowAsync("a settled commit is skipped, not re-run or failed");

        (await StateAsync(commit)).Should().Be(DekRotationState.Applied);
        (await CountAsync("SELECT dek_epoch FROM tbl_node_identity")).Should().Be(epoch, "the epoch must not move again");
        _factory.Services.GetRequiredService<SessionService>().GetMasterDek().Should().Equal(newDek, "the DEK must not change again");
        _hook.Calls.Should().Be(calls, "a skipped commit does not even run the pre-rewrap hooks");
    }
}
