using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Sync;
using BeeMemoryBank.Sync.DekRotation;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Vault.Metadata;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// SA-1 on a real node (docs/design/step-a-spec.md 4.4; owner decision 5, "the metadata key rotates with every DEK rotation"):
/// the next metadata key generation is created in the very transaction that swaps the DEK, together with a durable marker; its
/// <c>metadata_key_set</c> event is appended and the marker deleted in one more transaction, retried until it holds; until then
/// the rotation is not complete and this node writes no sealed metadata (it must not seal with the generation a node the rotation
/// left out can still open).
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public class MetadataKeyRotationTests : IAsyncLifetime
{
    private const string MasterPassword = "metadataRotation1";
    private const string MarkerKey = "mek_publish_pending";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly FaultyFactory _a = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _a.InitializeNodeAsync("NodeA", MasterPassword);
        _client = _a.CreateClient();
        await UnlockAsync();
        (await _client.PostAsJsonAsync("/api/sync/activation", new { password = MasterPassword })).StatusCode
            .Should().Be(HttpStatusCode.OK, "the control: the network is switched on, so content is written sealed");
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _a.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Rotation_inserts_next_generation_in_the_dek_transaction()
    {
        _a.Fault.Mode = FaultMode.BeforeAppend; // the publication cannot be logged
        var oldDek = Dek();
        try
        {
            await RotateAsync();
            await WaitForStepAsync("Completing", "Completed", "Failed");

            // The key row is there right after the DEK commit although the publisher failed: the transaction put it there.
            var keys = await KeyRowsAsync();
            keys.Select(k => k.Generation).Should().Equal(1, 2);
            var newDek = Dek();
            try
            {
                MetadataKeyCrypto.TryUnwrap(keys[1], [newDek]).Should().NotBeNull("generation 2 is wrapped under the new DEK");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(newDek);
            }
            MetadataKeyCrypto.TryUnwrap(keys[1], [oldDek]).Should().BeNull("a node the rotation left out cannot open it");
            (await MarkerAsync()).Should().Be(keys[1].KeyId, "the durable marker names the key whose publication is outstanding");
            (await EventCountAsync(EventTypes.MetadataKeySet, keys[1].KeyId)).Should().Be(0, "nothing was logged yet");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldDek);
        }
    }

    [Fact]
    public async Task A_failure_to_create_the_key_row_rolls_the_whole_rotation_back()
    {
        // The same transaction swaps the DEK and creates the key: when the key cannot be written there is no new DEK.
        await ExecAsync("CREATE TRIGGER fail_mek BEFORE INSERT ON tbl_metadata_key WHEN NEW.generation = 2 BEGIN SELECT RAISE(ABORT, 'injected'); END");
        var before = Dek();
        var epochBefore = await EpochAsync();
        try
        {
            await RotateAsync();
            var step = await WaitForStepAsync("Completing", "Completed", "Failed");

            step.Should().Be("Failed", "a rotation without its metadata key is not a rotation");
            CryptographicOperations.FixedTimeEquals(before, Dek()).Should().BeTrue("the session still holds the old DEK");
            (await EpochAsync()).Should().Be(epochBefore, "no committed state with a new DEK");
            (await KeyRowsAsync()).Select(k => k.Generation).Should().Equal(1);
            (await MarkerAsync()).Should().BeNull();
            (await AuditCountAsync("dek_rotation_completed")).Should().Be(0);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(before);
        }
    }

    [Fact]
    public async Task Publisher_failure_keeps_rotation_completing_and_refuses_authoring()
    {
        _a.Fault.Mode = FaultMode.BeforeAppend;
        await RotateAsync();

        (await WaitForStepAsync("Completing", "Completed", "Failed")).Should().Be("Completing",
            "the rotation is not complete until the new metadata key is published");
        (await AuditCountAsync("dek_rotation_completed")).Should().Be(0, "the completion record waits for the publication");
        var sealedBefore = await EventCountAsync(EventTypes.Sealed, null);

        var refused = await _client.PostAsJsonAsync("/api/articles", new { title = "Written while pending", treePath = "/Pending", content = "body" });
        refused.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        refused.Headers.RetryAfter.Should().NotBeNull("the refusal is deferrable: the caller retries");
        (await refused.Content.ReadAsStringAsync()).Should().Contain("metadata_key_publish_pending");
        (await EventCountAsync(EventTypes.Sealed, null)).Should().Be(sealedBefore, "nothing was sealed with the previous generation");
    }

    [Fact]
    public async Task Retry_publishes_and_clears_marker_in_one_transaction()
    {
        // The event is appended, then the transaction fails: the append is undone with it and the marker stays.
        _a.Fault.Mode = FaultMode.AfterAppend;
        await RotateAsync();
        await WaitForStepAsync("Completing", "Completed", "Failed");
        var key = (await KeyRowsAsync())[^1];
        (await MarkerAsync()).Should().Be(key.KeyId);
        (await EventCountAsync(EventTypes.MetadataKeySet, key.KeyId)).Should().Be(0, "an event whose marker was not cleared does not exist");

        // The fault is gone; the next unlock repeats the step.
        _a.Fault.Mode = FaultMode.None;
        (await _client.PostAsJsonAsync("/api/session/lock", new { })).EnsureSuccessStatusCode();
        await UnlockAsync();
        await WaitUntilAsync(async () => await MarkerAsync() is null);

        (await EventCountAsync(EventTypes.MetadataKeySet, key.KeyId)).Should().Be(1);
        (await WaitForStepAsync("Completed", "Failed")).Should().Be("Completed", "the retry finishes the rotation it held back");
        (await AuditCountAsync("dek_rotation_completed")).Should().Be(1);

        var created = await _client.PostAsJsonAsync("/api/articles", new { title = "Written after the retry", treePath = "/Pending", content = "body" });
        created.StatusCode.Should().Be(HttpStatusCode.Created, "authoring resumes");
        (await NewestSealedKeyAsync()).Should().Be(key.KeyId, "sealed with the new generation");
    }

    [Fact]
    public async Task Left_out_peer_cannot_open_events_after_completion()
    {
        _a.Fault.Mode = FaultMode.BeforeAppend;
        var oldDek = Dek();
        try
        {
            await RotateAsync();
            await WaitForStepAsync("Completing", "Completed", "Failed");
            _a.Fault.Mode = FaultMode.None;
            (await _client.PostAsJsonAsync("/api/session/lock", new { })).EnsureSuccessStatusCode();
            await UnlockAsync();
            await WaitUntilAsync(async () => await MarkerAsync() is null);
            (await _client.PostAsJsonAsync("/api/articles", new { title = "After", treePath = "/Pending", content = "body" }))
                .StatusCode.Should().Be(HttpStatusCode.Created);

            // The key travels before anything sealed with it: a peer is never served an event ahead of the key it names.
            var newKey = (await KeyRowsAsync())[^1];
            var keySetAt = await SequenceOfAsync(EventTypes.MetadataKeySet, newKey.KeyId, first: true);
            keySetAt.Should().NotBeNull("the generation was published");
            (await SequenceOfAsync(EventTypes.Sealed, newKey.KeyId, first: true)).Should().BeGreaterThan(keySetAt!.Value);

            // A peer the rotation left out: the rows as synced to it, and only the old DEK.
            var leftOut = new SessionService(null!);
            leftOut.UnlockWithDek((byte[])oldDek.Clone());
            var ring = new MetadataKeyRing(_a.Services.GetRequiredService<IDbConnectionFactory>(), leftOut);
            var opener = new VaultEnvelopeOpener(leftOut, ring);
            (await KeyRowsAsync())[^1].Generation.Should().Be(2);

            var sealedAfter = await SealedEventsAsync(afterGeneration: 2);
            sealedAfter.Should().NotBeEmpty();
            foreach (var evt in sealedAfter)
            {
                var outcome = await opener.OpenAsync(evt, null);
                outcome.Disposition.Should().Be(EnvelopeDisposition.Hold, "it holds a key it can never open");
                outcome.Reason.Should().Be(HoldReasons.Key);
            }
            leftOut.Lock();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldDek);
        }
    }

    [Fact]
    public async Task Every_sealed_authoring_route_is_refused_with_503_while_pending_and_changes_nothing()
    {
        // State to author against, written while publication still works.
        await PostOkAsync("/api/folders", new { path = "/Work" });
        await PostOkAsync("/api/folders", new { path = "/Work/Sub" });
        var article = await CreateArticleAsync("Seed", "/Work", ["alpha", "beta"]);
        var comment = await (await _client.PostAsJsonAsync("/api/comments", new { articleId = article, text = "first" }))
            .Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        var commentId = comment.GetProperty("id").GetInt32();
        var plain = await CreateArticleAsync("Plain", "/Work", ["alpha"]); // no attachment: protection is a valid request for it
        var folderId = await FolderIdAsync("/Work/Sub");
        using (var upload = await UploadAsync(article, "control.txt"))
            upload.StatusCode.Should().Be(HttpStatusCode.Created, "the control: the upload payload below is a valid one");

        _a.Fault.Mode = FaultMode.BeforeAppend;
        await RotateAsync();
        (await WaitForStepAsync("Completing", "Completed", "Failed")).Should().Be("Completing");
        var before = await AuthoringStateAsync();

        var routes = new (string Name, Func<Task<HttpResponseMessage>> Call)[]
        {
            ("POST /api/folders", () => _client.PostAsJsonAsync("/api/folders", new { path = "/Created" })),
            ("PATCH /api/folders", () => _client.PatchAsJsonAsync("/api/folders?path=%2FWork", new { newPath = "/Work2" })),
            ("POST /api/folders/move", () => _client.PostAsJsonAsync("/api/folders/move?path=%2FWork%2FSub", new { newParentPath = "/" })),
            ("DELETE /api/folders", () => _client.DeleteAsync("/api/folders?path=%2FWork")),
            ("POST /api/folders/{id}/copy", () => _client.PostAsJsonAsync($"/api/folders/{folderId}/copy", new { targetParentPath = "/Work" })),
            ("PUT /api/concept-tags/{name}", () => _client.PutAsJsonAsync("/api/concept-tags/alpha", new { newName = "gamma" })),
            ("POST /api/concept-tags/merge", () => _client.PostAsJsonAsync("/api/concept-tags/merge", new { source = "alpha", target = "beta" })),
            ("DELETE /api/concept-tags/{name}", () => _client.DeleteAsync("/api/concept-tags/alpha")),
            ("PUT /api/articles/{id}/concept-tags", () => _client.PutAsJsonAsync($"/api/articles/{article}/concept-tags", new { conceptTags = new[] { "alpha", "delta" } })),
            ("POST /api/media", () => UploadAsync(article, "refused.txt")),
            ("POST /api/articles", () => _client.PostAsJsonAsync("/api/articles", new { title = "New", treePath = "/Work", content = "body" })),
            ("PUT /api/articles/{id}", () => _client.PutAsJsonAsync($"/api/articles/{article}", new { title = "Renamed" })),
            ("POST /api/articles/{id}/move", () => _client.PostAsJsonAsync($"/api/articles/{article}/move", new { newPath = "/Elsewhere" })),
            ("POST /api/articles/{id}/protect", () => _client.PostAsJsonAsync($"/api/articles/{plain}/protect", new { passphrase = "second-layer-1" })),
            ("POST /api/articles/{id}/copy", () => _client.PostAsJsonAsync($"/api/articles/{article}/copy", new { targetFolderPath = "/Work/Sub" })),
            ("DELETE /api/articles/{id}", () => _client.DeleteAsync($"/api/articles/{article}")),
            ("POST /api/comments", () => _client.PostAsJsonAsync("/api/comments", new { articleId = article, text = "refused" })),
            ("DELETE /api/comments/{id}", () => _client.DeleteAsync($"/api/comments/{commentId}")),
            ("POST /api/hard-delete/article/{id}", () => _client.PostAsync($"/api/hard-delete/article/{article}", null)),
            ("POST /api/hard-delete/folder", () => _client.PostAsJsonAsync("/api/hard-delete/folder", new { path = "/Work" })),
        };

        var wrong = new List<string>();
        foreach (var (name, call) in routes)
        {
            using var response = await call();
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable || response.Headers.RetryAfter is null || !body.Contains("metadata_key_publish_pending"))
                wrong.Add($"{name}: {(int)response.StatusCode} retry-after={(response.Headers.RetryAfter is null ? "no" : "yes")} {body}");
        }

        string.Join(Environment.NewLine, wrong).Should().BeEmpty("a deferrable refusal is a 503 with Retry-After and its code on every route that authors sealed content");
        (await AuthoringStateAsync()).Should().Be(before, "a refused write changes nothing: no folder, tag, article, comment, media or purge is left behind without its event");
    }

    [Fact]
    public async Task A_folder_delete_keeps_one_authoring_scope_across_article_and_folder_mutations()
    {
        await PostOkAsync("/api/folders", new { path = "/Composite" });
        var article = await CreateArticleAsync("Composite article", "/Composite", []);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        _a.Fault.HoldSealedAppend = async _ =>
        {
            if (Interlocked.Increment(ref held) == 1)
            {
                entered.TrySetResult();
                await release.Task;
            }
        };

        var delete = _client.DeleteAsync("/api/folders?path=%2FComposite");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // This is the rotation's exclusive side, deliberately entered while the article event is
        // suspended. A composite route must keep its outer shared scope through the folder write,
        // so the nested service call can finish while this waits for the request's scope to leave.
        var exclusive = _a.Services.GetRequiredService<AuthoringBarrier>()
            .EnterExclusiveAsync(TimeSpan.FromSeconds(10));
        try
        {
            release.TrySetResult();
            (await Task.WhenAny(delete, Task.Delay(TimeSpan.FromSeconds(5))))
                .Should().Be(delete, "the folder mutation must stay inside the article delete's authoring scope");

            using var response = await delete;
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            release.TrySetResult();
            (await exclusive.WaitAsync(TimeSpan.FromSeconds(15))).Dispose();
            await delete.WaitAsync(TimeSpan.FromSeconds(15));
            _a.Fault.HoldSealedAppend = null;
        }

        using var conn = Db();
        (await conn.ExecuteScalarAsync<string>("SELECT status FROM tbl_folder WHERE path = '/Composite'")).Should().Be("D");
        (await conn.ExecuteScalarAsync<string>("SELECT status FROM tbl_article WHERE id = @article", new { article })).Should().Be("D");
    }

    [Fact]
    public async Task A_protected_article_change_keeps_one_authoring_scope_across_article_and_comment_mutations()
    {
        var article = await CreateArticleAsync("Protected composite", "/Composite", []);
        using (var comment = await _client.PostAsJsonAsync("/api/comments", new { articleId = article, text = "legacy comment" }))
            comment.StatusCode.Should().Be(HttpStatusCode.OK);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        _a.Fault.HoldSealedAppend = async _ =>
        {
            if (Interlocked.Increment(ref held) == 1)
            {
                entered.TrySetResult();
                await release.Task;
            }
        };

        var protect = _client.PostAsJsonAsync($"/api/articles/{article}/protect", new { passphrase = "second-layer-1" });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var exclusive = _a.Services.GetRequiredService<AuthoringBarrier>()
            .EnterExclusiveAsync(TimeSpan.FromSeconds(10));
        try
        {
            release.TrySetResult();
            (await Task.WhenAny(protect, Task.Delay(TimeSpan.FromSeconds(5))))
                .Should().Be(protect, "comment re-sealing must stay inside the article protection scope");

            using var response = await protect;
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            release.TrySetResult();
            (await exclusive.WaitAsync(TimeSpan.FromSeconds(15))).Dispose();
            await protect.WaitAsync(TimeSpan.FromSeconds(15));
            _a.Fault.HoldSealedAppend = null;
        }

        using var conn = Db();
        (await conn.ExecuteScalarAsync<long>("SELECT protected FROM tbl_article WHERE id = @article", new { article })).Should().Be(1);
        var comments = (await conn.QueryAsync<(long Encrypted, string Text)>(
            "SELECT encrypted, text FROM tbl_comment WHERE article_id = @article AND deleted_at IS NULL", new { article })).ToList();
        comments.Should().ContainSingle();
        comments[0].Encrypted.Should().Be(1, "the comment side effect completed under the same authoring scope");
    }

    [Fact]
    public async Task Protected_article_unlock_propagates_pending_marker_from_legacy_comment_reseal()
    {
        var article = await CreateArticleAsync("Legacy comment", "/Legacy", []);
        using (var protect = await _client.PostAsJsonAsync($"/api/articles/{article}/protect", new { passphrase = "second-layer-1" }))
            protect.StatusCode.Should().Be(HttpStatusCode.OK);

        var commentId = await SeedLegacyCommentAsync(article, "legacy plaintext");
        _a.Fault.Mode = FaultMode.BeforeAppend;
        await RotateAsync();
        (await WaitForStepAsync("Completing", "Completed", "Failed")).Should().Be("Completing");
        var sealedBefore = await EventCountAsync(EventTypes.Sealed, null);

        using var response = await _client.PostAsJsonAsync($"/api/articles/{article}/unlock", new { passphrase = "second-layer-1" });
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter.Should().NotBeNull("pending metadata publication is retryable");
        (await response.Content.ReadAsStringAsync()).Should().Contain("metadata_key_publish_pending");
        (await EventCountAsync(EventTypes.Sealed, null)).Should().Be(sealedBefore, "the legacy comment was not re-sealed while authoring was pending");

        using var conn = Db();
        var legacy = await conn.QuerySingleAsync<(long Encrypted, string Text)>(
            "SELECT encrypted, text FROM tbl_comment WHERE id = @commentId", new { commentId });
        legacy.Encrypted.Should().Be(0);
        legacy.Text.Should().Be("legacy plaintext");
    }

    [Fact]
    public async Task A_write_in_flight_when_the_rotation_starts_is_logged_before_the_rotation_commits()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        _a.Fault.HoldSealedAppend = async _ =>
        {
            if (Interlocked.Increment(ref held) != 1) return;
            entered.TrySetResult();
            await release.Task;
        };
        var generation1 = (await KeyRowsAsync())[0].KeyId;

        // A folder request that sealed its event with generation 1 and is suspended before the event is appended.
        var folder = _client.PostAsJsonAsync("/api/folders", new { path = "/InFlight" });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var epochBefore = await EpochAsync();

        await RotateAsync();
        await Task.Delay(2500);
        (await EpochAsync()).Should().Be(epochBefore, "the DEK swap waits for the write in flight: nothing may be appended under the old key after it");

        release.SetResult();
        (await folder).StatusCode.Should().Be(HttpStatusCode.OK);
        (await WaitForStepAsync("Completed", "Failed", "Completing")).Should().Be("Completed");
        (await _client.PostAsJsonAsync("/api/articles", new { title = "After", treePath = "/InFlight", content = "body" }))
            .StatusCode.Should().Be(HttpStatusCode.Created, "authoring resumes once the key is published");

        var keys = await KeyRowsAsync();
        keys.Select(k => k.Generation).Should().Equal(1, 2);
        var keySetAt = await SequenceOfAsync(EventTypes.MetadataKeySet, keys[1].KeyId, first: true);
        keySetAt.Should().NotBeNull();
        using var conn = Db();
        var sealedRows = (await conn.QueryAsync<(long Seq, string Payload)>(
            "SELECT sequence_num, payload FROM tbl_event WHERE event_type = 'sealed' ORDER BY sequence_num")).ToList();
        var inFlight = sealedRows.Single(r => r.Seq < keySetAt!.Value);
        SealedEnvelope.Parse(inFlight.Payload).KeyId.Should().Be(generation1, "the write in flight finished under the key it was sealed with, before the rotation");
        sealedRows.Where(r => r.Seq > keySetAt!.Value).Select(r => SealedEnvelope.Parse(r.Payload).KeyId)
            .Should().NotBeEmpty().And.OnlyContain(id => id == keys[1].KeyId, "nothing is appended under the old generation after the new key is announced");
    }

    [Fact]
    public async Task A_rotation_whose_process_died_after_the_commit_is_completed_by_a_fresh_service_once_the_key_is_published()
    {
        _a.Fault.Mode = FaultMode.BeforeAppend;
        await RotateAsync();
        (await WaitForStepAsync("Completing", "Completed", "Failed")).Should().Be("Completing");
        (await CompletionRecordAsync()).Should().NotBeNull("what is left to finish is durable: it was written in the DEK transaction");
        (await AuditCountAsync("dek_rotation_completed")).Should().Be(0);

        // The process dies here: no memory of the accepting service survives, and nobody is subscribed to the publication any more.
        var publisher = _a.Services.GetRequiredService<MetadataKeyPublishService>();
        ForgetSubscribers(publisher);
        var restarted = ActivatorUtilities.CreateInstance<BeeMemoryBank.Api.Services.DekRotationService>(_a.Services, _a.DataPath);

        await restarted.FinishPendingCompletionAsync();
        (await AuditCountAsync("dek_rotation_completed")).Should().Be(0, "the key is not published yet: the rotation is not complete");
        (await CompletionRecordAsync()).Should().NotBeNull();

        _a.Fault.Mode = FaultMode.None;
        (await publisher.TryPublishAsync()).Should().BeTrue();
        (await MarkerAsync()).Should().BeNull();

        // The completion record is applied by the unlock chain, the 60 s timer or the publication; here the fresh service does it.
        await ExecAsync("CREATE TRIGGER fail_audit BEFORE INSERT ON tbl_audit_log WHEN NEW.action = 'dek_rotation_completed' BEGIN SELECT RAISE(ABORT, 'injected'); END");
        await restarted.FinishPendingCompletionAsync();
        (await CompletionRecordAsync()).Should().NotBeNull("a finish that failed is kept and repeated");
        await ExecAsync("DROP TRIGGER fail_audit");

        await restarted.FinishPendingCompletionAsync();
        (await AuditCountAsync("dek_rotation_completed")).Should().Be(1);
        (await CompletionRecordAsync()).Should().BeNull();
        restarted.GetProgress().CurrentStep.ToString().Should().Be("Completed");

        await restarted.FinishPendingCompletionAsync();
        (await AuditCountAsync("dek_rotation_completed")).Should().Be(1, "finishing twice writes one completion record");
    }

    [Fact]
    public async Task The_services_own_timer_repeats_a_finish_that_failed()
    {
        _a.Fault.Mode = FaultMode.BeforeAppend;
        await RotateAsync();
        (await WaitForStepAsync("Completing", "Completed", "Failed")).Should().Be("Completing");
        var publisher = _a.Services.GetRequiredService<MetadataKeyPublishService>();
        ForgetSubscribers(publisher);
        _a.Fault.Mode = FaultMode.None;
        (await publisher.TryPublishAsync()).Should().BeTrue();

        var time = new ManualTime();
        var restarted = ActivatorUtilities.CreateInstance<BeeMemoryBank.Api.Services.DekRotationService>(_a.Services, _a.DataPath, time);
        time.Period.Should().Be(TimeSpan.FromSeconds(60), "the retry period of a finish that did not hold");
        await ExecAsync("CREATE TRIGGER fail_audit BEFORE INSERT ON tbl_audit_log WHEN NEW.action = 'dek_rotation_completed' BEGIN SELECT RAISE(ABORT, 'injected'); END");

        time.Fire();
        await Task.Delay(1500);
        (await CompletionRecordAsync()).Should().NotBeNull("the finish failed and is kept");
        await ExecAsync("DROP TRIGGER fail_audit");

        time.Fire();
        await WaitUntilAsync(async () => await CompletionRecordAsync() is null);
        (await AuditCountAsync("dek_rotation_completed")).Should().Be(1);
        restarted.GetProgress().CurrentStep.ToString().Should().Be("Completed");
        restarted.Dispose();
    }

    /// <summary>A clock that never ticks by itself: the test fires the one timer a service creates.</summary>
    private sealed class ManualTime : TimeProvider
    {
        public TimeSpan? Period { get; private set; }
        private TimerCallback? _callback;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            Period = period;
            return new NoTimer();
        }

        public void Fire() => _callback!(null);

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private async Task PostOkAsync(string url, object body)
    {
        using var response = await _client.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
    }

    private async Task<Guid> CreateArticleAsync(string title, string treePath, string[] tags)
    {
        using var response = await _client.PostAsJsonAsync("/api/articles", new { title, treePath, content = "body of " + title, conceptTags = tags });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> FolderIdAsync(string path)
    {
        using var conn = Db();
        return Guid.Parse(await conn.ExecuteScalarAsync<string>("SELECT id FROM tbl_folder WHERE path = @path", new { path }) ?? throw new InvalidOperationException(path));
    }

    private async Task<int> SeedLegacyCommentAsync(Guid articleId, string text)
    {
        using var conn = Db();
        return await conn.ExecuteScalarAsync<int>(
            @"INSERT INTO tbl_comment (comment_id, article_id, text, created_at, encrypted)
              VALUES (@commentId, @articleId, @text, @createdAt, 0);
              SELECT last_insert_rowid();",
            new
            {
                commentId = Guid.NewGuid(),
                articleId,
                text,
                createdAt = DateTime.UtcNow.ToString("O")
            });
    }

    private Task<HttpResponseMessage> UploadAsync(Guid articleId, string fileName)
    {
        var form = new MultipartFormDataContent
        {
            { new ByteArrayContent("some text"u8.ToArray()) { Headers = { ContentType = new("text/plain") } }, "file", fileName },
            { new StringContent(articleId.ToString()), "articleId" },
            { new StringContent("true"), "attachment" },
        };
        return _client.PostAsync("/api/media", form);
    }

    /// <summary>Everything an authoring route could leave behind, as one comparable string.</summary>
    private async Task<string> AuthoringStateAsync()
    {
        using var conn = Db();
        var folders = string.Join(",", await conn.QueryAsync<string>("SELECT path || ':' || status FROM tbl_folder ORDER BY path"));
        var tags = string.Join(",", await conn.QueryAsync<string>("SELECT name FROM tbl_concept_tag ORDER BY name"));
        var articles = string.Join(",", await conn.QueryAsync<string>("SELECT id || ':' || status || ':' || title || ':' || tree_path FROM tbl_article ORDER BY id"));
        var counts = await conn.QuerySingleAsync<(long Comments, long Media, long Events, long Audit)>(
            "SELECT (SELECT COUNT(*) FROM tbl_comment), (SELECT COUNT(*) FROM tbl_media), (SELECT COUNT(*) FROM tbl_event), (SELECT COUNT(*) FROM tbl_hard_delete_audit)");
        return $"folders[{folders}] tags[{tags}] articles[{articles}] comments={counts.Comments} media={counts.Media} events={counts.Events} purges={counts.Audit}";
    }

    private async Task<string?> CompletionRecordAsync()
    {
        using var conn = Db();
        return await conn.ExecuteScalarAsync<string?>("SELECT value FROM tbl_migration_marker WHERE key = @Key", new { Key = DekRotationCompletionRecord.Key });
    }

    /// <summary>Removes every handler of <see cref="MetadataKeyPublishService.Published"/>: the accepting process is gone.</summary>
    private static void ForgetSubscribers(MetadataKeyPublishService publisher) =>
        typeof(MetadataKeyPublishService).GetField(nameof(MetadataKeyPublishService.Published), BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(publisher, null);

    private byte[] Dek() => _a.Services.GetRequiredService<SessionService>().GetMasterDek();

    private async Task UnlockAsync() =>
        (await _client.PostAsJsonAsync("/api/session/unlock", new { password = MasterPassword })).EnsureSuccessStatusCode();

    private async Task RotateAsync()
    {
        var propose = await _client.PostAsJsonAsync("/api/dek-rotation/propose", new { masterPassword = MasterPassword });
        propose.StatusCode.Should().Be(HttpStatusCode.OK, await propose.Content.ReadAsStringAsync());
        var commitEventId = (await propose.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("commitEventId").GetGuid().ToString();
        (await _client.PostAsJsonAsync("/api/dek-rotation/accept", new { commitEventId, masterPassword = MasterPassword }))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    private async Task<string?> WaitForStepAsync(params string[] steps)
    {
        string? step = null;
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var progress = await _client.GetFromJsonAsync<JsonElement>("/api/dek-rotation/progress", JsonOpts);
            step = progress.GetProperty("currentStep").GetString();
            if (steps.Contains(step)) return step;
            await Task.Delay(200);
        }
        return step;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition() && DateTime.UtcNow < deadline) await Task.Delay(100);
    }

    private System.Data.IDbConnection Db() => _a.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection();

    private async Task ExecAsync(string sql)
    {
        using var conn = Db();
        await conn.ExecuteAsync(sql);
    }

    private async Task<List<MetadataKeyRow>> KeyRowsAsync()
    {
        using var conn = Db();
        return (await conn.QueryAsync<(string KeyId, long Generation, string Fp, byte[] Wrapped, byte[] Iv)>(
                "SELECT key_id, generation, dek_fingerprint, wrapped, iv FROM tbl_metadata_key ORDER BY generation"))
            .Select(r => new MetadataKeyRow(r.KeyId, (int)r.Generation, r.Fp, r.Wrapped, r.Iv)).ToList();
    }

    private async Task<string?> MarkerAsync()
    {
        using var conn = Db();
        return await conn.ExecuteScalarAsync<string?>("SELECT value FROM tbl_migration_marker WHERE key = @MarkerKey", new { MarkerKey });
    }

    private async Task<long> EventCountAsync(string type, string? keyId)
    {
        using var conn = Db();
        return keyId is null
            ? await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_event WHERE event_type = @type", new { type })
            : await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM tbl_event WHERE event_type = @type AND payload LIKE '%' || @keyId || '%'", new { type, keyId });
    }

    private async Task<long?> SequenceOfAsync(string type, string keyId, bool first)
    {
        using var conn = Db();
        return await conn.ExecuteScalarAsync<long?>(
            $"SELECT {(first ? "MIN" : "MAX")}(sequence_num) FROM tbl_event WHERE event_type = @type AND payload LIKE '%' || @keyId || '%'",
            new { type, keyId });
    }

    private async Task<long> AuditCountAsync(string action)
    {
        using var conn = Db();
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tbl_audit_log WHERE action = @action", new { action });
    }

    private async Task<long> EpochAsync()
    {
        using var conn = Db();
        return await conn.ExecuteScalarAsync<long>("SELECT dek_epoch FROM tbl_node_identity LIMIT 1");
    }

    private async Task<string> NewestSealedKeyAsync()
    {
        using var conn = Db();
        var payload = await conn.QuerySingleAsync<string>(
            "SELECT payload FROM tbl_event WHERE event_type = 'sealed' ORDER BY sequence_num DESC LIMIT 1");
        return SealedEnvelope.Parse(payload).KeyId;
    }

    /// <summary>The sealed rows whose key is of a generation above <paramref name="afterGeneration"/> minus one, as a peer would be served them.</summary>
    private async Task<List<SyncEvent>> SealedEventsAsync(int afterGeneration)
    {
        var keys = (await KeyRowsAsync()).Where(k => k.Generation >= afterGeneration).Select(k => k.KeyId).ToHashSet();
        using var scope = _a.Services.CreateScope();
        var all = await scope.ServiceProvider.GetRequiredService<IEventLogRepository>().GetAfterSequenceAsync(0, 10_000);
        return all.Where(e => e.EventType == EventTypes.Sealed && keys.Contains(SealedEnvelope.Parse(e.Payload).KeyId)).ToList();
    }

    // ─── fault injection ────────────────────────────────────────────────────

    public enum FaultMode { None, BeforeAppend, AfterAppend }

    public sealed class FaultSwitch
    {
        public volatile FaultMode Mode;

        /// <summary>Awaited before a sealed event is appended (a request suspended between sealing and appending), or null.</summary>
        public volatile Func<SyncEvent, Task>? HoldSealedAppend;
    }

    /// <summary>A node whose event log fails on <c>metadata_key_set</c> when told to: before the row is written, or after it was (then the surrounding transaction must undo it).</summary>
    private sealed class FaultyFactory : BmbWebApplicationFactory
    {
        public FaultSwitch Fault { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                var original = services.Single(d => d.ServiceType == typeof(IEventLogRepository));
                services.Remove(original);
                services.Add(ServiceDescriptor.Describe(typeof(IEventLogRepository), sp =>
                {
                    var inner = (IEventLogRepository)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!);
                    var proxy = DispatchProxy.Create<IEventLogRepository, FaultyEventLog>();
                    var faulty = (FaultyEventLog)(object)proxy;
                    faulty.Inner = inner;
                    faulty.Fault = Fault;
                    return proxy;
                }, original.Lifetime));
            });
        }
    }

    public class FaultyEventLog : DispatchProxy
    {
        public IEventLogRepository Inner { get; set; } = null!;
        public FaultSwitch Fault { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var keySet = targetMethod!.Name == nameof(IEventLogRepository.AppendAsync)
                         && args![0] is SyncEvent { EventType: EventTypes.MetadataKeySet };
            if (keySet && Fault.Mode == FaultMode.BeforeAppend) throw new IOException("injected: the event cannot be logged");
            if (targetMethod.Name == nameof(IEventLogRepository.AppendAsync) && args![0] is SyncEvent { EventType: EventTypes.Sealed } held
                && Fault.HoldSealedAppend is { } hold)
                return HoldThenAppendAsync(hold, held, targetMethod, args);
            object? result;
            try
            {
                result = targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException ex)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
                throw;
            }
            if (keySet && Fault.Mode == FaultMode.AfterAppend && result is Task appended)
                return ThenFailAsync(appended);
            return result;
        }

        private async Task HoldThenAppendAsync(Func<SyncEvent, Task> hold, SyncEvent evt, MethodInfo method, object?[] args)
        {
            await hold(evt);
            try
            {
                await (Task)method.Invoke(Inner, args)!;
            }
            catch (TargetInvocationException ex)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
            }
        }

        private static async Task ThenFailAsync(Task appended)
        {
            await appended;
            throw new IOException("injected: the transaction fails after the event was appended");
        }
    }
}
