using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Sealed;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Sealed;
using Dapper;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The reset of a sealed node and the way back (docs/design/step-b-spec.md 13.1, 10.7; red tests 15.3.13 and the notice of 15.3.17): seal, reset, set the
/// node up again with the password of the day, restore the pre-encryption snapshot through its key envelope. The snapshot opens whatever happened to
/// the password or the master key in between.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class SealRollbackTests(ActivatedPlainTemplate template) : IClassFixture<ActivatedPlainTemplate>
{
    private const string Password = ActivatedPlainTemplate.Password;

    private async Task<SealedApiNode> StartAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb_seal_back_" + Guid.NewGuid().ToString("N"));
        SealedNode.CopyTree(template.PlainDir, dir, skipDatabases: false);
        var node = SealedApiNode.PlainOver(dir);
        (await node.Session.UnlockAsync(Password)).Should().BeTrue();
        return node;
    }

    private static async Task<string> SealAsync(SealedApiNode node)
    {
        var seal = node.Services.GetRequiredService<SealMigrationService>();
        var result = await seal.RunAsync(Password, new SealOptions());
        result.Phase.Should().Be(SealProgress.Done, result.Error);
        node.Gate.IsSealed.Should().BeTrue();
        return Path.GetFileName(result.Snapshot!.RelativeName);
    }

    // ---------------------------------------------------------------- reset

    [Fact]
    public async Task The_reset_of_a_sealed_node_ends_plain_empty_and_startable_and_leaves_the_way_back()
    {
        using var node = await StartAsync();
        using var client = node.Client();
        var snapshotName = await SealAsync(node);
        // The checkbox on a sealed node, so that its record and its key file exist to be removed.
        (await client.PostAsJsonAsync("/api/keys/server-auto-unlock/enable", new { password = Password, confirmation = "START UNLOCKED" })).EnsureSuccessStatusCode();
        File.Exists(Path.Combine(node.Dir, "server-auto-unlock.enabled")).Should().BeTrue();
        Directory.CreateDirectory(Path.Combine(node.Dir, "snapshots"));
        File.WriteAllText(Path.Combine(node.Dir, "snapshots", "bmb-snapshot-kept.tar.gz"), "kept");

        using var wrong = await client.PostAsJsonAsync("/api/init/reset", new { masterPassword = "wrong" });
        wrong.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        node.Gate.IsSealed.Should().BeTrue("a wrong password resets nothing");

        using var reset = await client.PostAsJsonAsync("/api/init/reset", new { masterPassword = Password });
        reset.StatusCode.Should().Be(HttpStatusCode.OK, await reset.Content.ReadAsStringAsync());

        node.Gate.IsSealed.Should().BeFalse("the node is plain again");
        File.Exists(Path.Combine(node.Dir, "beememorybank.boot.db")).Should().BeFalse();
        File.Exists(Path.Combine(node.Dir, "server-auto-unlock.enabled")).Should().BeFalse();
        File.Exists(Path.Combine(node.Dir, "server-auto-unlock.key")).Should().BeFalse("the node created it");
        File.Exists(Path.Combine(node.Dir, "pre-encryption", snapshotName)).Should().BeTrue("the way back survives a reset");
        File.Exists(Path.Combine(node.Dir, "snapshots", "bmb-snapshot-kept.tar.gz")).Should().BeTrue();
        (await SealedApiNode.GetJsonAsync(client, "/api/vault/status")).GetProperty("mode").GetString().Should().Be("plain");

        // Startable: a restart finds a plain folder with no database, and Setup works.
        node.Restart();
        node.Gate.IsSealed.Should().BeFalse();
        using (var scope = node.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<InitializationService>().InitializeAsync("admin", "AfterReset", "AnotherPassword1!");
        (await node.Session.UnlockAsync("AnotherPassword1!")).Should().BeTrue();
    }

    [Fact]
    public async Task A_closed_sealed_node_is_reset_with_the_password_without_unlocking_the_session()
    {
        using var node = await StartAsync();
        await SealAsync(node);
        node.Restart();
        node.Gate.State.Should().Be(VaultState.Closed);

        using var scope = node.Services.CreateScope();
        var reset = scope.ServiceProvider.GetRequiredService<NodeResetService>();
        (await reset.ResetAsync("wrong", "test")).Outcome.Should().Be(NodeResetOutcome.InvalidPassword);
        node.Gate.State.Should().Be(VaultState.Closed, "a wrong password opens nothing");

        var result = await reset.ResetAsync(Password, "test");

        result.Outcome.Should().Be(NodeResetOutcome.Done);
        result.WasSealed.Should().BeTrue();
        node.Session.IsUnlocked.Should().BeFalse();
        node.Gate.IsSealed.Should().BeFalse();
        File.Exists(Path.Combine(node.Dir, "beememorybank.boot.db")).Should().BeFalse();
        Directory.EnumerateFiles(Path.Combine(node.Dir, "pre-encryption"), "bmb-snapshot-*.tar.gz").Should().HaveCount(1);
    }

    // ---------------------------------------------------------------- rollback (15.3.13)

    [Fact]
    public async Task Rollback_a_seal_reset_set_up_again_with_the_same_password_restore_gives_a_plain_node_with_the_canary_vault()
    {
        using var node = await StartAsync();
        var before = await DumpAsync(node);
        var oldNode = await NodeIdAsync(node);
        var name = await SealAsync(node);

        await ResetAndSetUpAsync(node, Password);
        await RestoreAsync(node, name, Password);

        node.Gate.IsSealed.Should().BeFalse("the rollback ends plain");
        (await node.Session.UnlockAsync(Password)).Should().BeTrue();
        (await DumpAsync(node)).Should().BeEquivalentTo(before, "the canary vault is fully readable");
        (await NodeIdAsync(node)).Should().NotBe(oldNode, "a standalone restore makes a new node");
    }

    [Fact]
    public async Task Rollback_after_a_key_rotation_a_password_change_and_a_restart_still_opens_with_the_password_of_the_snapshots_day()
    {
        using var node = await StartAsync();
        using var client = node.Client();
        var before = await DumpAsync(node);
        var name = await SealAsync(node);

        // The master key rotates (this node is the initiator) and the password changes: the vault now opens with neither the old key nor the old password.
        (await RotateAsync(client)).Should().Be(WayBackNotice.Sentence, "the rotation confirms with the sentence while the snapshot exists");
        var userId = await AdminIdAsync(node);
        using (var change = new HttpRequestMessage(HttpMethod.Post, "/api/users/me/change-password") { Content = JsonContent.Create(new { oldPassword = Password, newPassword = "ChangedPassword1!" }) })
        {
            change.Headers.Add("X-User-Id", userId.ToString());
            using var changed = await client.SendAsync(change);
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
            (await changed.Content.ReadAsStringAsync()).Should().Contain("old password", "the confirmation says the way-back snapshot still opens with it");
        }
        node.Restart();
        (await node.Session.UnlockAsync("ChangedPassword1!")).Should().BeTrue();

        await ResetAndSetUpAsync(node, Password, passwordNow: "ChangedPassword1!");
        await RestoreAsync(node, name, Password);

        (await node.Session.UnlockAsync(Password)).Should().BeTrue();
        (await DumpAsync(node)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Rollback_after_a_key_rotation_in_the_same_second_as_the_way_back_still_opens_with_the_password_of_the_snapshots_day()
    {
        // The rotation makes its own backup in snapshots/, the folder a restore by name looks in first. Snapshot names carry the second they are made in:
        // the test makes the way-back and the rotation's backup in one second, so the restore must still open the way-back, not the backup.
        using var node = await StartAsync();
        using var client = node.Client();
        node.Services.GetRequiredService<SnapshotService>().SnapshotNameClock = () => new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);
        var before = await DumpAsync(node);
        var name = await SealAsync(node);

        (await RotateAsync(client)).Should().Be(WayBackNotice.Sentence, "the rotation confirms with the sentence while the snapshot exists");
        var userId = await AdminIdAsync(node);
        using (var change = new HttpRequestMessage(HttpMethod.Post, "/api/users/me/change-password") { Content = JsonContent.Create(new { oldPassword = Password, newPassword = "ChangedPassword1!" }) })
        {
            change.Headers.Add("X-User-Id", userId.ToString());
            using var changed = await client.SendAsync(change);
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
            (await changed.Content.ReadAsStringAsync()).Should().Contain("old password", "the confirmation says the way-back snapshot still opens with it");
        }
        node.Restart();
        (await node.Session.UnlockAsync("ChangedPassword1!")).Should().BeTrue();

        await ResetAndSetUpAsync(node, Password, passwordNow: "ChangedPassword1!");
        await RestoreAsync(node, name, Password);

        (await node.Session.UnlockAsync(Password)).Should().BeTrue();
        (await DumpAsync(node)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task A_snapshot_made_in_the_same_second_as_the_way_back_takes_another_name()
    {
        using var node = await StartAsync();
        var snapshots = node.Services.GetRequiredService<SnapshotService>();
        snapshots.SnapshotNameClock = () => new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);
        var name = await SealAsync(node);

        var later = await snapshots.CreateAsync(filterSecrets: false, sign: false);

        later.FileName.Should().NotBe(name, "a name belongs to one snapshot: a restore by name looks in both snapshot folders");
        SnapshotNames(node, "snapshots").Intersect(SnapshotNames(node, "pre-encryption")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_name_held_in_both_snapshot_folders_is_refused_and_nothing_is_restored()
    {
        // The state a same-second collision left behind: an ordinary snapshot under the way-back's name in snapshots/, which a restore by name would open first.
        using var node = await StartAsync();
        var name = await SealAsync(node);
        Directory.CreateDirectory(Path.Combine(node.Dir, "snapshots"));
        File.Copy(Path.Combine(node.Dir, "pre-encryption", name), Path.Combine(node.Dir, "snapshots", name));

        var snapshots = node.Services.GetRequiredService<SnapshotService>();
        var restore = async () => await snapshots.RestoreAsync(name, standaloneMode: true, masterPassword: Password);

        var refusal = (await restore.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Two snapshots*")).Which.Message;
        refusal.Should().Contain("Rename or delete the ordinary snapshot with this name in snapshots/").And.Contain("try again", "the message says what to do");
        refusal.Should().Contain($"pre-encryption/{name}", "and how to name the folder instead").And.Contain($"snapshots/{name}");
        refusal.Should().NotContain(node.Dir, "a file name and folder names, never a path");
        File.Exists(Path.Combine(node.Dir, "pre-encryption", name)).Should().BeTrue("the way-back is still there");
        File.Exists(Path.Combine(node.Dir, "snapshots", name)).Should().BeTrue("nothing was moved or replaced");
    }

    [Fact]
    public async Task A_way_back_made_after_a_snapshot_of_the_same_second_takes_another_name_and_the_rollback_works()
    {
        // The other order: an ordinary snapshot already holds the second when the way-back is made.
        using var node = await StartAsync();
        var snapshots = node.Services.GetRequiredService<SnapshotService>();
        snapshots.SnapshotNameClock = () => PinnedSecond;
        var before = await DumpAsync(node);
        var first = await snapshots.CreateAsync(filterSecrets: false, sign: false);

        var name = await SealAsync(node);

        name.Should().NotBe(first.FileName, "the way-back does not take the name of a snapshot in the other folder");
        SnapshotNames(node, "snapshots").Intersect(SnapshotNames(node, "pre-encryption")).Should().BeEmpty();
        await ResetAndSetUpAsync(node, Password);
        await RestoreAsync(node, name, Password);

        (await node.Session.UnlockAsync(Password)).Should().BeTrue();
        (await DumpAsync(node)).Should().BeEquivalentTo(before, "the rollback by the way-back's name gives the way-back");
    }

    [Fact]
    public async Task Restoring_the_way_back_by_name_on_the_same_encrypted_vault_gives_its_content_not_another_snapshots()
    {
        // The silent case before the fix: both archives open with the vault's own key, so a restore that took the ordinary snapshot of the same second
        // by mistake succeeded and gave the wrong content. The way-back holds the vault as it was before the encryption; the ordinary snapshot, made
        // later, holds a folder the way-back does not.
        using var node = await StartAsync();
        node.Services.GetRequiredService<SnapshotService>().SnapshotNameClock = () => PinnedSecond;
        var before = await DumpAsync(node);
        var name = await SealAsync(node);

        node.Restart();
        (await node.Session.UnlockAsync(Password)).Should().BeTrue();
        var snapshots = node.Services.GetRequiredService<SnapshotService>();
        snapshots.SnapshotNameClock = () => PinnedSecond;
        using (var c = node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection())
            await c.ExecuteAsync("INSERT INTO tbl_folder (id, path, name, created_at, updated_at) VALUES (@id, '/made-after-the-way-back', 'made-after-the-way-back', '2026-10-13T00:00:00Z', '2026-10-13T00:00:00Z')",
                new { id = Guid.NewGuid().ToString() });
        (await DumpAsync(node)).Should().NotBeEquivalentTo(before, "the new folder changes the content");
        await snapshots.CreateAsync(filterSecrets: false, sign: false);

        await snapshots.RestoreAsync(name, standaloneMode: true, masterPassword: Password);

        (await node.Session.UnlockAsync(Password)).Should().BeTrue();
        (await DumpAsync(node)).Should().BeEquivalentTo(before, "the restore by the way-back's name gave the way-back, not the snapshot made after it");
    }

    [Fact]
    public async Task A_refused_restore_over_the_route_is_a_clean_400_and_has_done_no_work_yet()
    {
        using var node = await StartAsync();
        using var client = node.Client();
        var name = await SealAsync(node);
        Directory.CreateDirectory(Path.Combine(node.Dir, "snapshots"));
        File.Copy(Path.Combine(node.Dir, "pre-encryption", name), Path.Combine(node.Dir, "snapshots", name));
        var staging = SnapshotStaging.DirIn(node.Dir);
        var stagedBefore = Directory.Exists(staging) ? Directory.GetFileSystemEntries(staging).Length : 0;
        var snapshotsBefore = SnapshotNames(node, "snapshots");
        if (!node.Session.IsUnlocked) (await node.Session.UnlockAsync(Password)).Should().BeTrue();

        // The dialog's own defaults: the safety backup on, the way-back's name.
        using var response = await client.PostAsJsonAsync("/api/snapshots/restore",
            new { fileName = name, masterPassword = Password, createBackupFirst = true, mode = "standalone" });
        var body = await response.Content.ReadAsStringAsync();
        var sessionLocked = !node.Session.IsUnlocked;
        if (sessionLocked) await node.Session.UnlockAsync(Password);   // only to read the audit log below
        var audit = await AuditActionsAsync(node);

        using var all = new AssertionScope();   // every claim is reported, so that a failure says which part of the work was done
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("Two snapshots").And.Contain(name).And.Contain("Rename or delete the ordinary snapshot");
        body.Should().NotContain(node.Dir).And.NotContain("Exception", "a file name and folder names, no path, no .NET type");
        body.Should().NotContain("A backup was saved", "no safety backup was taken for a restore that was refused");
        SnapshotNames(node, "snapshots").Should().BeEquivalentTo(snapshotsBefore, "no safety backup, nothing else written");
        (Directory.Exists(staging) ? Directory.GetFileSystemEntries(staging).Length : 0).Should().Be(stagedBefore, "nothing was staged");
        sessionLocked.Should().BeFalse("the refusal did not lock the session");
        var lockFree = HeavyOperationLock.Instance.Wait(0);
        if (lockFree) HeavyOperationLock.Instance.Release();
        lockFree.Should().BeTrue("the heavy-operation lock is free");
        audit.Should().NotContain("snapshot_restore_started", "no restore was started");
        audit.Should().ContainSingle(a => a == "snapshot_restore_refused", "the refusal is on the record");
    }

    [Fact]
    public async Task The_folder_named_in_front_of_a_name_held_in_both_folders_says_which_snapshot_is_meant()
    {
        // The way out of the refusal that needs no file touched: the same name, with its folder.
        using var node = await StartAsync();
        var before = await DumpAsync(node);
        var name = await SealAsync(node);
        // An ordinary snapshot that holds the way-back's name, as a collision of the old code left it (a different archive under the same name).
        var ordinary = await node.Services.GetRequiredService<SnapshotService>().CreateAsync(filterSecrets: false, sign: false);
        File.Move(Path.Combine(node.Dir, "snapshots", ordinary.FileName), Path.Combine(node.Dir, "snapshots", name));
        await ResetAndSetUpAsync(node, Password);

        var snapshots = node.Services.GetRequiredService<SnapshotService>();
        snapshots.Invoking(s => s.RequireUnambiguousRestoreName(name)).Should().Throw<InvalidOperationException>("the bare name is ambiguous");
        snapshots.Invoking(s => s.RequireUnambiguousRestoreName("pre-encryption/" + name)).Should().NotThrow();
        if (OperatingSystem.IsWindows())
            snapshots.Invoking(s => s.RequireUnambiguousRestoreName("snapshots\\" + name)).Should().NotThrow("on Windows either separator names the folder");
        await snapshots.RestoreAsync("pre-encryption/" + name, standaloneMode: true, masterPassword: Password);

        (await node.Session.UnlockAsync(Password)).Should().BeTrue();
        (await DumpAsync(node)).Should().BeEquivalentTo(before, "the way-back was restored, though an ordinary snapshot holds the same name");
        File.Exists(Path.Combine(node.Dir, "snapshots", name)).Should().BeTrue("no file was touched");
        File.Exists(Path.Combine(node.Dir, "pre-encryption", name)).Should().BeTrue();
    }

    [Fact]
    public async Task A_folder_in_front_of_a_name_looks_in_that_folder_only_and_nothing_else_is_taken_for_a_folder()
    {
        // Two files that are no archives, one in each folder: a restore that finds a file fails on its content, one that finds none says not found.
        using var node = await StartAsync();
        var inSnapshots = "bmb-snapshot-20260101-000001.tar.gz";
        var inPreEncryption = "bmb-snapshot-20260101-000002.tar.gz";
        Directory.CreateDirectory(Path.Combine(node.Dir, "snapshots"));
        Directory.CreateDirectory(Path.Combine(node.Dir, "pre-encryption"));
        File.WriteAllText(Path.Combine(node.Dir, "snapshots", inSnapshots), "not an archive");
        File.WriteAllText(Path.Combine(node.Dir, "pre-encryption", inPreEncryption), "not an archive");
        var snapshots = node.Services.GetRequiredService<SnapshotService>();

        async Task<bool> FoundAsync(string requested)
        {
            try { await snapshots.RestoreAsync(requested, standaloneMode: true, masterPassword: Password); }
            catch (FileNotFoundException) { return false; }
            catch (Exception) { return true; }
            return true;
        }

        (await FoundAsync("snapshots/" + inPreEncryption)).Should().BeFalse("the file is not in snapshots/");
        (await FoundAsync("pre-encryption/" + inSnapshots)).Should().BeFalse("the file is not in pre-encryption/");
        (await FoundAsync("pre-encryption/" + inPreEncryption)).Should().BeTrue();
        (await FoundAsync("snapshots/" + inSnapshots)).Should().BeTrue();
        (await FoundAsync(inPreEncryption)).Should().BeTrue("a bare name looks in both folders, as before");
        (await FoundAsync(inSnapshots)).Should().BeTrue();
        (await FoundAsync("../pre-encryption/" + inSnapshots)).Should().BeTrue("anything but exactly a folder and a name is reduced to the name, as before");
        (await FoundAsync("elsewhere/pre-encryption/" + inSnapshots)).Should().BeTrue();
    }

    [Fact]
    public async Task Rollback_with_a_wrong_password_is_refused_before_anything_is_replaced()
    {
        using var node = await StartAsync();
        var name = await SealAsync(node);
        await ResetAndSetUpAsync(node, Password);
        var fresh = await DumpAsync(node);

        var snapshots = node.Services.GetRequiredService<SnapshotService>();
        var restore = async () => await snapshots.RestoreAsync(name, standaloneMode: true, masterPassword: "not the password of that day");

        await restore.Should().ThrowAsync<InvalidOperationException>();
        (await DumpAsync(node)).Should().BeEquivalentTo(fresh, "the new empty vault is untouched");
        node.Session.IsUnlocked.Should().BeTrue();
        Directory.EnumerateFiles(node.Dir, "*.standalone-staging").Should().BeEmpty();
    }

    [Fact]
    public async Task Rollback_with_the_text_of_the_recovery_key_of_that_day_as_the_password_of_the_new_vault_and_of_the_restore()
    {
        using var node = await StartAsync();
        string recoveryKey;
        using (var scope = node.Services.CreateScope())
            recoveryKey = await scope.ServiceProvider.GetRequiredService<KeyManagementService>().AddRecoveryKeyAsync();
        var before = await DumpAsync(node);
        var name = await SealAsync(node);

        await ResetAndSetUpAsync(node, recoveryKey);
        await RestoreAsync(node, name, recoveryKey);

        node.Gate.IsSealed.Should().BeFalse();
        (await node.Session.UnlockAsync(recoveryKey)).Should().BeTrue("a recovery slot is an Argon2id slot like the others: the string works wherever a password does");
        (await DumpAsync(node)).Should().BeEquivalentTo(before);
    }

    // ---------------------------------------------------------------- 15.3.17 the notice

    [Fact]
    public async Task The_confirmations_of_a_password_change_and_a_rotation_carry_the_way_back_sentence_only_while_the_snapshot_exists()
    {
        using var node = await StartAsync();
        using var client = node.Client();
        var userId = await AdminIdAsync(node);

        using (var plain = new HttpRequestMessage(HttpMethod.Post, "/api/users/me/change-password") { Content = JsonContent.Create(new { oldPassword = Password, newPassword = Password + "x" }) })
        {
            plain.Headers.Add("X-User-Id", userId.ToString());
            using var answer = await client.SendAsync(plain);
            answer.StatusCode.Should().Be(HttpStatusCode.OK);
            (await answer.Content.ReadAsStringAsync()).Should().BeEmpty("without a snapshot the confirmation is what it was");
        }
        using (var propose = await client.PostAsJsonAsync("/api/dek-rotation/propose", new { masterPassword = Password + "x" }))
        {
            propose.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await propose.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("notice").ValueKind.Should().Be(JsonValueKind.Null);
            await client.PostAsync("/api/dek-rotation/cancel/" + body.GetProperty("commitEventId").GetGuid(), null);
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Resets the sealed node through the route, sets it up again with <paramref name="password"/> and unlocks it.</summary>
    private static async Task ResetAndSetUpAsync(SealedApiNode node, string password, string? passwordNow = null)
    {
        using var client = node.Client();
        using var reset = await client.PostAsJsonAsync("/api/init/reset", new { masterPassword = passwordNow ?? Password });
        reset.StatusCode.Should().Be(HttpStatusCode.OK, await reset.Content.ReadAsStringAsync());
        node.Gate.IsSealed.Should().BeFalse();
        using (var scope = node.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<InitializationService>().InitializeAsync("admin", "SetUpAgain", password);
        (await node.Session.UnlockAsync(password)).Should().BeTrue();
    }

    /// <summary>The restore of the pre-encryption snapshot by its file name on a node that is plain (spec 10.7 step 5).</summary>
    private static async Task RestoreAsync(SealedApiNode node, string name, string password)
    {
        var snapshots = node.Services.GetRequiredService<SnapshotService>();
        var outcome = await snapshots.RestoreAsync(name, standaloneMode: true, masterPassword: password);
        outcome.Should().NotBeNull();
    }

    /// <summary>One rotation with this node as the initiator, until it has completed; returns the notice its confirmation carried.</summary>
    private static async Task<string?> RotateAsync(HttpClient client)
    {
        var propose = await client.PostAsJsonAsync("/api/dek-rotation/propose", new { masterPassword = Password });
        propose.EnsureSuccessStatusCode();
        var proposed = await propose.Content.ReadFromJsonAsync<JsonElement>();
        var notice = proposed.GetProperty("notice").GetString();
        var commitEventId = proposed.GetProperty("commitEventId").GetGuid().ToString();
        (await client.PostAsJsonAsync("/api/dek-rotation/accept", new { commitEventId, masterPassword = Password })).EnsureSuccessStatusCode();
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var progress = await SealedApiNode.GetJsonAsync(client, "/api/dek-rotation/progress");
            var step = progress.GetProperty("currentStep").GetString();
            if (step == "Completed") return notice;
            step.Should().NotBe("Failed", progress.ToString());
            await Task.Delay(250);
        }
        throw new TimeoutException("The rotation did not complete.");
    }

    private static readonly DateTime PinnedSecond = new(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>The actions of the audit log, in order.</summary>
    private static async Task<List<string>> AuditActionsAsync(SealedApiNode node)
    {
        using var c = node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        return (await c.QueryAsync<string>("SELECT action FROM tbl_audit_log ORDER BY id")).ToList();
    }

    /// <summary>The file names of the snapshots in one of the node's snapshot folders.</summary>
    private static HashSet<string> SnapshotNames(SealedApiNode node, string folder) =>
        Directory.GetFiles(Path.Combine(node.Dir, folder), "bmb-snapshot-*.tar.gz").Select(f => Path.GetFileName(f)).ToHashSet();

    private static async Task<int> AdminIdAsync(SealedApiNode node)
    {
        using var c = node.Services.GetRequiredService<DbConnectionFactory>().CreateConnection();
        return await c.ExecuteScalarAsync<int>("SELECT id FROM tbl_user WHERE username = 'admin'");
    }

    private static async Task<Guid> NodeIdAsync(SealedApiNode node) =>
        (await node.Services.GetRequiredService<INodeIdentityRepository>().GetAsync())!.NodeId;

    private static readonly HashSet<string> Derived = new(["embedding_projection", "embedding_model_version", "embedding_pending", "index_pending"], StringComparer.Ordinal);

    private static async Task<Dictionary<string, string>> DumpAsync(SealedApiNode node)
    {
        var vault = node.Services.GetRequiredService<DbConnectionFactory>();
        using var c = vault.CreateConnection();
        var result = new Dictionary<string, string>();
        foreach (var table in new[] { "tbl_article", "tbl_folder" })
        {
            if (await c.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table", new { table }) == 0) continue;
            using var command = c.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY 1";
            using var reader = command.ExecuteReader();
            var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var rows = 0;
            while (reader.Read())
            {
                rows++;
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    if (Derived.Contains(reader.GetName(i))) continue;   // caches the background processors write and a restore clears
                    var value = reader.GetValue(i);
                    hash.AppendData(System.Text.Encoding.UTF8.GetBytes(i + ":" + value.GetType().Name + ":"));
                    hash.AppendData(value is byte[] bytes ? bytes : System.Text.Encoding.UTF8.GetBytes(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? ""));
                }
            }
            result[table] = rows + ":" + Convert.ToHexString(hash.GetHashAndReset());
        }
        return result;
    }
}
