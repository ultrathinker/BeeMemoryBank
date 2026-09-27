using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services.BlindBackup;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The blind backup surface: settings masking and the keep-existing update semantics, the
/// backup pipeline against a scripted restic stand-in (repo init, snapshot args, retention,
/// staging cleanup, recovery-set placement), the ×2.5 free-space invariant, and the CPU-mode
/// switch. The real restic binary is exercised on a test host (bmb-blind containers), not in
/// the dotnet suite.
/// </summary>
public class BlindBackupEndpointsTests : IAsyncLifetime
{
    private readonly FakeRestic _restic = new();
    private readonly FakeRecoverySet _recoverySet = new(null);
    private readonly ScriptedResticFactory _factory;
    private readonly string _repoDir =
        Path.Combine(Path.GetTempPath(), "bmb_blind_repo_" + Guid.NewGuid().ToString("N"));

    public BlindBackupEndpointsTests() => _factory = new ScriptedResticFactory(_restic, _recoverySet);

    /// <summary>
    /// Every test in this class runs against the scripted restic — a stray real `restic`
    /// invocation in a suite would be a build machine flake, not a signal. A subclass rather than
    /// WithWebHostBuilder: the latter now wraps the factory in a delegated one, which has neither
    /// InitializeNodeAsync nor the DataPath the staging assertions look at.
    /// </summary>
    private sealed class ScriptedResticFactory(IResticRunner restic, IRecoverySetSource recoverySet)
        : BlindNodeFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s =>
            {
                s.AddSingleton(restic);
                s.AddSingleton(recoverySet);
            });
        }
    }

    public Task InitializeAsync() => _factory.InitializeNodeAsync(password: "blindBackupPw");

    public Task DisposeAsync()
    {
        _factory.Dispose();
        if (Directory.Exists(_repoDir))
            try { Directory.Delete(_repoDir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    private HttpClient Client() => _factory.CreateClient();

    private async Task ConfigureRepoAsync(HttpClient client)
    {
        Directory.CreateDirectory(_repoDir);
        var resp = await client.PutAsJsonAsync("/api/blind/backup/settings", new
        {
            repoType = "folder",
            repoFolder = _repoDir,
            resticPassword = "test-restic-pw",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── settings ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Settings_PasswordIsMasked_OnReadAndUpdate()
    {
        using var client = Client();
        await ConfigureRepoAsync(client);

        var s = await (await client.GetAsync("/api/blind/backup/settings")).Content.ReadFromJsonAsync<JsonElement>();
        s.GetProperty("settings").GetProperty("resticPassword").GetString().Should().Be("••••",
            "the restic password never travels back to a reader");
        s.GetProperty("settings").GetProperty("s3SecretKey").ValueKind.Should().Be(JsonValueKind.Null);
        s.GetProperty("configured").GetBoolean().Should().BeTrue();

        // Round-tripping the mask must not wipe the stored password.
        await client.PutAsJsonAsync("/api/blind/backup/settings", new { resticPassword = "••••" });
        var store = _factory.Services.GetRequiredService<BlindBackupSettingsStore>();
        store.Load().ResticPassword.Should().Be("test-restic-pw",
            "the mask means keep — a console that echoes what it read cannot destroy the password");
    }

    [Fact]
    public async Task Settings_ScheduleCannotBeEnabled_WithoutARepository()
    {
        using var client = Client();
        var resp = await client.PutAsJsonAsync("/api/blind/backup/settings", new { scheduleEnabled = true });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .Should().Contain("password");
    }

    [Fact]
    public async Task Settings_ResticBinary_CannotBeSetThroughTheApi()
    {
        using var client = Client();
        (await client.PutAsJsonAsync("/api/blind/backup/settings", new { resticBinary = "/bin/sh" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.Services.GetRequiredService<BlindBackupSettingsStore>().Load().ResticBinary.Should().BeNull(
            "which executable the node runs is not a console setting — that would be code execution for a password holder");
    }

    [Fact]
    public async Task Settings_MalformedMemoryLimit_CannotBeScheduled()
    {
        using var client = Client();
        await ConfigureRepoAsync(client);

        var resp = await client.PutAsJsonAsync("/api/blind/backup/settings",
            new { resticGoMemLimit = "lots", scheduleEnabled = true });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "restic refuses to start on a malformed GOMEMLIMIT — every scheduled backup would fail at launch");
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()
            .Should().Contain("memory limit");
    }

    [Fact]
    public async Task Settings_WithTheScheduleOn_APartialUpdateCannotBreakIt()
    {
        using var client = Client();
        await ConfigureRepoAsync(client);
        (await client.PutAsJsonAsync("/api/blind/backup/settings", new { scheduleEnabled = true }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var store = _factory.Services.GetRequiredService<BlindBackupSettingsStore>();

        (await client.PutAsJsonAsync("/api/blind/backup/settings", new { resticGoMemLimit = "lots" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest,
                "the request does not mention the schedule, but the stored settings have it on");
        (await client.PutAsJsonAsync("/api/blind/backup/settings", new { resticPassword = "" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var stored = store.Load();
        stored.ResticGoMemLimit.Should().Be("512MiB", "a refused update is not saved");
        stored.ResticPassword.Should().Be("test-restic-pw");
    }

    [Fact]
    public async Task Settings_ADraftMayBeIncomplete_ButNotWrong()
    {
        using var client = Client();
        (await client.PutAsJsonAsync("/api/blind/backup/settings", new { repoType = "folder", repoFolder = _repoDir }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "no password yet is fine while the schedule is off");
        (await client.PutAsJsonAsync("/api/blind/backup/settings",
                new { repoFolder = Path.Combine(_factory.DataPath, "blind", "stage") }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "a repository inside the node's data folder is wrong, draft or not");
    }

    [Fact]
    public async Task Status_ShowsRepoPath_WhenConfigured()
    {
        using var client = Client();
        var before = await (await client.GetAsync("/api/blind/status")).Content.ReadFromJsonAsync<JsonElement>();
        before.GetProperty("backups_path").ValueKind.Should().Be(JsonValueKind.Null);

        await ConfigureRepoAsync(client);
        var after = await (await client.GetAsync("/api/blind/status")).Content.ReadFromJsonAsync<JsonElement>();
        after.GetProperty("backups_path").GetString().Should().Be(_repoDir);
        after.GetProperty("cpu_mode").GetString().Should().Be("economy",
            "the default CPU mode is the quiet one (plan §8)");
    }

    // ── backup pipeline ──────────────────────────────────────────────────────

    [Fact]
    public async Task BackupNow_InitsMissingRepo_SnapshotsStageAndMedia_Retains_CleansStage()
    {
        _restic.InitMissingOnce = true;
        using var client = Client();
        await ConfigureRepoAsync(client);

        var start = await client.PostAsync("/api/blind/backup/now", content: null);
        start.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var job = await WaitUntilDoneAsync(client);
        job.GetProperty("state").GetString().Should().Be("done");
        job.GetProperty("progress").GetDouble().Should().Be(1.0,
            "the summary line of restic's --json stream completes the progress");

        _restic.Calls.Should().Contain(c => c.StartsWith("init"),
            "a repo that answers 10 to `snapshots` is initialized exactly once");
        _restic.Calls.Should().Contain(c => c.Contains("backup") && c.Contains("--host bmb-blind") && c.Contains("--json"));
        _restic.Calls.Should().Contain(c =>
            c.Contains("forget") && c.Contains("--keep-daily 7") && c.Contains("--keep-weekly 4") &&
            c.Contains("--keep-monthly 12") && c.Contains("--keep-yearly 3") && c.Contains("--prune"),
            "the standard retention of plan §7 is applied after every backup");

        _restic.SawStageDb.Should().BeTrue("the snapshot is taken from the VACUUM INTO copy, not the live database");
        var stage = Path.Combine(_factory.DataPath, "blind", "stage", "beememorybank.db");
        File.Exists(stage).Should().BeFalse("the staging copy is removed in a finally — a second copy of the vault must not linger");
    }

    [Fact]
    public async Task SettingsSavedMidBackup_DoNotMoveTheRunningJob()
    {
        _recoverySet.Json = """{"format":"bmb-recovery-set-v1"}""";
        var otherRepo = _repoDir + "-other";
        using var client = Client();
        await ConfigureRepoAsync(client);
        _restic.BlockBackup = true;

        (await client.PostAsync("/api/blind/backup/now", content: null)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        for (var i = 0; i < 100 && !_restic.Calls.Any(c => c.StartsWith("backup")); i++) await Task.Delay(50);
        (await client.PutAsJsonAsync("/api/blind/backup/settings", new { repoFolder = otherRepo }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        _restic.BlockBackup = false;
        (await WaitUntilDoneAsync(client)).GetProperty("state").GetString().Should().Be("done");

        _restic.Repositories.Should().OnlyContain(r => r == Path.GetFullPath(_repoDir),
            "forget/prune after the snapshot must hit the repository the snapshot went to");
        File.Exists(_repoDir + ".recovery-set.json").Should().BeTrue();
        File.Exists(otherRepo + ".recovery-set.json").Should().BeFalse(
            "the recovery-set belongs next to the repository this job wrote, not the one saved meanwhile");
    }

    [Fact]
    public async Task SnapshotList_DuringABackup_IsBusy_AndStartsNoSecondRestic()
    {
        using var client = Client();
        await ConfigureRepoAsync(client);
        _restic.BlockBackup = true;
        (await client.PostAsync("/api/blind/backup/now", content: null)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        for (var i = 0; i < 100 && !_restic.Calls.Any(c => c.StartsWith("backup")); i++) await Task.Delay(50);
        var before = _restic.Calls.Count;

        (await client.GetAsync("/api/blind/backup/list")).StatusCode.Should().Be(HttpStatusCode.Conflict,
            "a second restic mid-backup would race the first and share its Pause/cancel");
        _restic.Calls.Count.Should().Be(before);

        _restic.BlockBackup = false;
        await WaitUntilDoneAsync(client);
        (await client.GetAsync("/api/blind/backup/list")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WeeklyCheck_WaitsOutABusyManager_InsteadOfSkippingTheWeek()
    {
        using var client = Client();
        await ConfigureRepoAsync(client);
        var jobs = _factory.Services.GetRequiredService<BlindJobManager>();
        var schedule = _factory.Services.GetServices<IHostedService>().OfType<BlindBackupScheduleService>().Single();

        // The Sunday backup has just ended and an operator's job took the manager first.
        var release = new TaskCompletionSource();
        jobs.TryStart("backup", (_, ct) => release.Task.WaitAsync(ct)).Should().NotBeNull();
        var check = schedule.StartWeeklyCheckAsync(CancellationToken.None);
        release.SetResult();
        await check.WaitAsync(TimeSpan.FromSeconds(10));

        jobs.LastStartedAt("verify").Should().NotBeNull(
            "the weekly subset check starts once the manager is free, not never");
        (await WaitUntilDoneAsync(client, kind: "verify")).GetProperty("state").GetString().Should().Be("done");
        _restic.Calls.Should().Contain(c => c.StartsWith("check --read-data-subset"));
    }

    [Fact]
    public async Task Wipe_DuringARunningBackup_CancelsItFirst_ThenWipes()
    {
        using var client = Client();
        (await client.PostAsJsonAsync("/api/blind/console/password", new { newPassword = "console-pw-123" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        await ConfigureRepoAsync(client);
        _restic.BlockBackup = true;
        (await client.PostAsync("/api/blind/backup/now", content: null)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        for (var i = 0; i < 100 && !_restic.Calls.Any(c => c.StartsWith("backup")); i++) await Task.Delay(50);

        (await client.PostAsJsonAsync("/api/blind/wipe", new { consolePassword = "console-pw-123", confirmNodeName = "TestNode" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var jobs = _factory.Services.GetRequiredService<BlindJobManager>();
        jobs.IsBusy.Should().BeFalse();
        jobs.Snapshot().First().State.Should().Be("cancelled", "the backup was stopped before anything was erased");
        _restic.Calls.Should().NotContain(c => c.StartsWith("forget"), "a cancelled backup runs no later step");
    }

    [Fact]
    public async Task CopyTo_ANewFolder_IsAResticCopyWithTheSourceChunker_AndGetsTheRecoverySet()
    {
        _recoverySet.Json = """{"format":"bmb-recovery-set-v1"}""";
        var dest = _repoDir + "-usb";
        using var client = Client();
        await ConfigureRepoAsync(client);

        (await client.PostAsJsonAsync("/api/blind/backup/copy", new { destination = dest }))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await WaitUntilDoneAsync(client, kind: "copy")).GetProperty("state").GetString().Should().Be("done");

        var init = _restic.Calls.FindIndex(c => c == "init --copy-chunker-params");
        init.Should().BeGreaterThanOrEqualTo(0, "a new destination becomes a restic repository with the source's chunker");
        _restic.Repositories[init].Should().Be(dest);
        var copy = _restic.Calls.FindIndex(c => c == "copy --host bmb-blind");
        copy.Should().BeGreaterThan(init, "restic copy keeps the copy encrypted and incremental — no plaintext export");
        _restic.Repositories[copy].Should().Be(dest);
        File.Exists(dest + ".recovery-set.json").Should().BeTrue("the copy alone must be enough for a restore");
    }

    [Fact]
    public async Task CopyTo_AfterACancelledCopy_ClearsTheDestinationsStaleLocks()
    {
        var dest = _repoDir + "-usb";
        using var client = Client();
        await ConfigureRepoAsync(client);
        _restic.CopyGate = new TaskCompletionSource();
        (await client.PostAsJsonAsync("/api/blind/backup/copy", new { destination = dest }))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);
        for (var i = 0; i < 100 && !_restic.Calls.Any(c => c.StartsWith("copy")); i++) await Task.Delay(50);

        // The copy dies mid-way (here: cancelled by the wipe gate) and leaves its lock in dest.
        var jobs = _factory.Services.GetRequiredService<BlindJobManager>();
        (await jobs.BeginWipeAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        jobs.EndWipe();
        _restic.CopyGate = null;
        _restic.Calls.Clear(); _restic.Repositories.Clear();

        (await client.PostAsJsonAsync("/api/blind/backup/copy", new { destination = dest }))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await WaitUntilDoneAsync(client, kind: "copy")).GetProperty("state").GetString().Should().Be("done");

        _restic.Calls.Should().NotContain(c => c.StartsWith("init"), "the destination is a repository already");
        var unlockDest = Enumerable.Range(0, _restic.Calls.Count)
            .FirstOrDefault(i => _restic.Calls[i] == "unlock" && _restic.Repositories[i] == dest, -1);
        unlockDest.Should().BeGreaterThanOrEqualTo(0, "the retry must clear the stale lock the killed copy left in the destination")
            .And.BeLessThan(_restic.Calls.FindIndex(c => c.StartsWith("copy")));
    }

    [Fact]
    public async Task CopyTo_DestinationRetargetedIntoTheSource_WhileSettingsMoved_IsRefused()
    {
        var outside = _repoDir + "-usb-real";
        Directory.CreateDirectory(outside);
        var link = _repoDir + "-usb";
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return; } // Windows without the privilege
        using var client = Client();
        await ConfigureRepoAsync(client); // source repository A = _repoDir
        (await client.PostAsJsonAsync("/api/blind/backup/mode", new { mode = "pause" })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.PostAsJsonAsync("/api/blind/backup/copy", new { destination = Path.Combine(link, "copy") }))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);
        await Task.Delay(300); // validated against A, parked on the pause
        Directory.Delete(link);
        Directory.CreateSymbolicLink(link, _repoDir);                 // the destination now lies inside A
        (await client.PutAsJsonAsync("/api/blind/backup/settings", new { repoFolder = _repoDir + "-b" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);                // and settings.json names B
        (await client.PostAsJsonAsync("/api/blind/backup/mode", new { mode = "economy" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var job = await WaitUntilDoneAsync(client, kind: "copy");
        job.GetProperty("state").GetString().Should().Be("failed",
            "the job copies from A, so the destination is checked against A — not against B from the newer settings");
        job.GetProperty("detail").GetString().Should().Contain("overlaps the backup repository");
        _restic.Calls.Should().BeEmpty("nothing may touch A once the destination points into it");
    }

    [Fact]
    public async Task CopyTo_InsideTheNodeOrTheRepository_IsRefused()
    {
        using var client = Client();
        await ConfigureRepoAsync(client);
        foreach (var bad in new[]
                 {
                     Path.Combine(_factory.DataPath, "blind", "restic-cache", "copy"), // the wipe deletes there
                     Path.Combine(_repoDir, "copy"),                                  // pruned with the repository
                     _repoDir,
                     "relative/copy",
                 })
            (await client.PostAsJsonAsync("/api/blind/backup/copy", new { destination = bad }))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest, $"{bad} is not a safe place for a copy");
        _restic.Calls.Should().NotContain(c => c.StartsWith("copy"));
    }

    [Fact]
    public async Task RepositoryLink_RetargetedIntoTheNode_WhileTheJobWaits_IsCaughtBeforeResticOpensIt()
    {
        var outside = _repoDir + "-real";
        Directory.CreateDirectory(outside);
        var link = _repoDir + "-link";
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return; } // Windows without the privilege
        using var client = Client();
        (await client.PutAsJsonAsync("/api/blind/backup/settings", new
        {
            repoType = "folder", repoFolder = Path.Combine(link, "restic"), resticPassword = "test-restic-pw",
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/blind/backup/mode", new { mode = "pause" })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.PostAsync("/api/blind/backup/now", content: null)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await Task.Delay(300); // validated at the start, parked on the pause
        Directory.Delete(link);
        Directory.CreateSymbolicLink(link, Path.Combine(_factory.DataPath, "blind", "stage"));
        (await client.PostAsJsonAsync("/api/blind/backup/mode", new { mode = "economy" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var job = await WaitUntilDoneAsync(client);
        job.GetProperty("state").GetString().Should().Be("failed",
            "the location is checked again right before restic opens the repository");
        job.GetProperty("detail").GetString().Should().Contain("overlaps");
        _restic.Calls.Should().NotContain(c => c.StartsWith("snapshots") || c.StartsWith("init") || c.StartsWith("backup"));
    }

    [Fact]
    public async Task Wipe_WhileAListingIsInsideRestic_WaitsForIt_ThroughTheRealEndpoints()
    {
        using var client = Client();
        (await client.PostAsJsonAsync("/api/blind/console/password", new { newPassword = "console-pw-123" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        await ConfigureRepoAsync(client);
        _restic.ListGate = new TaskCompletionSource();

        using var listClient = Client();
        var list = listClient.GetAsync("/api/blind/backup/list");
        for (var i = 0; i < 100 && !_restic.Calls.Contains("snapshots --host bmb-blind --json"); i++) await Task.Delay(50);
        _restic.Calls.Should().Contain("snapshots --host bmb-blind --json", "the listing is inside restic now");

        using var wipeClient = Client();
        var wipe = wipeClient.PostAsJsonAsync("/api/blind/wipe", new { consolePassword = "console-pw-123", confirmNodeName = "TestNode" });
        await Task.WhenAny(wipe, Task.Delay(1500));
        wipe.IsCompleted.Should().BeFalse("the wipe must not delete while a listing's restic is still running");
        File.Exists(Path.Combine(_factory.DataPath, "blind", "settings.json")).Should().BeTrue("nothing is deleted before the listing ends");

        _restic.ListGate.SetResult();
        (await list).StatusCode.Should().Be(HttpStatusCode.OK);
        (await wipe).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task EveryJob_ClearsStaleLocksFirst_NeverAllLocks()
    {
        using var client = Client();
        await ConfigureRepoAsync(client);

        (await client.PostAsync("/api/blind/backup/now", content: null)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await WaitUntilDoneAsync(client);
        _restic.Calls.FindIndex(c => c == "unlock").Should().BeGreaterThanOrEqualTo(0)
            .And.BeLessThan(_restic.Calls.FindIndex(c => c.StartsWith("backup")),
                "a lock left by a killed restic would fail this backup's forget/prune for good");

        _restic.Calls.Clear();
        (await client.PostAsJsonAsync("/api/blind/backup/verify", new { full = false })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await WaitUntilDoneAsync(client, kind: "verify");
        _restic.Calls.Should().StartWith("unlock");
        _restic.Calls.Should().NotContain(c => c.Contains("--remove-all"),
            "only stale locks may go — a live lock belongs to someone else's restic");
    }

    [Fact]
    public async Task BackupNow_RecoverySetIsWrittenNextToRepo_NeverInside()
    {
        _recoverySet.Json = """{"format":"bmb-recovery-set-v1"}""";
        using var client = Client();
        await ConfigureRepoAsync(client);

        (await client.PostAsync("/api/blind/backup/now", content: null)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await WaitUntilDoneAsync(client);

        Directory.GetFiles(_repoDir).Should().BeEmpty(
            "nothing the backup writes may land inside the repository tree — prune/check must not see it");
        var nextTo = _repoDir + ".recovery-set.json";
        File.Exists(nextTo).Should().BeTrue();
        (await File.ReadAllTextAsync(nextTo)).Should().Contain("bmb-recovery-set-v1");
    }

    [Fact]
    public async Task BackupNow_WithoutRecoveryMaterial_WritesNoFile()
    {
        using var client = Client();
        await ConfigureRepoAsync(client);

        (await client.PostAsync("/api/blind/backup/now", content: null)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await WaitUntilDoneAsync(client);

        File.Exists(_repoDir + ".recovery-set.json").Should().BeFalse(
            "the stub (and a node with empty recovery tables) publishes nothing");
    }

    [Fact]
    public async Task BackupNow_SecondRunWhileBusy_IsRefused()
    {
        using var client = Client();
        await ConfigureRepoAsync(client);
        _restic.BlockBackup = true; // keep the first job running

        (await client.PostAsync("/api/blind/backup/now", content: null)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await client.PostAsync("/api/blind/backup/now", content: null)).StatusCode.Should().Be(HttpStatusCode.Conflict,
            "a blind node runs one job at a time; the operator learns, not queues silently");

        // While restic uploads, the job says so — not the vacuum step it finished before.
        for (var i = 0; i < 100 && !_restic.Calls.Any(c => c.StartsWith("backup")); i++) await Task.Delay(50);
        var running = (await (await client.GetAsync("/api/blind/status")).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("jobs")[0];
        running.GetProperty("detail").GetString().Should().Be("backing up");
        _restic.BlockBackup = false;
        await WaitUntilDoneAsync(client);
    }

    [Fact]
    public async Task BackupNow_WithoutConfiguration_FailsTheJobWithTheReason()
    {
        using var client = Client();
        (await client.PostAsync("/api/blind/backup/now", content: null)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        var job = await WaitUntilDoneAsync(client);
        job.GetProperty("state").GetString().Should().Be("failed");
        job.GetProperty("detail").GetString().Should().Contain("not configured");
    }

    [Fact]
    public void FreeSpace_RequiresTwoAndAHalfTimesTheDatabase()
    {
        var act = () => BlindBackupService.EnsureFreeSpace(requiredBytes: 2_500_000, availableBytes: 2_499_999);
        act.Should().Throw<InvalidOperationException>().WithMessage("*free space*");
        BlindBackupService.EnsureFreeSpace(requiredBytes: 2_500_000, availableBytes: 2_500_000);
    }

    // ── CPU modes ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mode_SwitchesAndIsVisibleInStatus()
    {
        using var client = Client();

        (await client.PostAsJsonAsync("/api/blind/backup/mode", new { mode = "fast" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await (await client.GetAsync("/api/blind/status")).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("cpu_mode").GetString().Should().Be("fast");

        (await client.PostAsJsonAsync("/api/blind/backup/mode", new { mode = "pause" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await (await client.GetAsync("/api/blind/status")).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("cpu_mode").GetString().Should().Be("pause");

        (await client.PostAsJsonAsync("/api/blind/backup/mode", new { mode = "turbo" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<JsonElement> WaitUntilDoneAsync(HttpClient client, string kind = "backup")
    {
        for (var i = 0; i < 150; i++)
        {
            var s = await (await client.GetAsync("/api/blind/status")).Content.ReadFromJsonAsync<JsonElement>();
            var job = s.GetProperty("jobs").EnumerateArray().FirstOrDefault();
            if (job.ValueKind != JsonValueKind.Undefined && job.GetProperty("kind").GetString() == kind
                && job.GetProperty("state").GetString() is "done" or "failed")
                return job;
            await Task.Delay(100);
        }
        throw new TimeoutException("backup job did not finish in 15s");
    }

    // ── fakes ────────────────────────────────────────────────────────────────

    /// <summary>Scripted restic: records every call, can pretend the repo is missing or stall a backup.</summary>
    private sealed class FakeRestic : IResticRunner
    {
        // Open unless a test closes it: only BackupNow_SecondRunWhileBusy stalls a backup.
        private TaskCompletionSource _gate = Open();
        private bool _initDone;

        private static TaskCompletionSource Open()
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs.SetResult();
            return tcs;
        }

        public List<string> Calls { get; } = [];
        /// <summary>When set, a listing's `snapshots` (no job) waits on it — a listing caught inside restic.</summary>
        public TaskCompletionSource? ListGate { get; set; }
        /// <summary>When set, a `copy` waits on it (cancellable) — a copy to interrupt.</summary>
        public TaskCompletionSource? CopyGate { get; set; }
        /// <summary>RESTIC_REPOSITORY of every call, in order — what the runner would have used.</summary>
        public List<string> Repositories { get; } = [];
        public bool InitMissingOnce;
        public bool SawStageDb;
        public bool BlockBackup
        {
            get => !_gate.Task.IsCompleted;
            set
            {
                if (value) _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                else _gate.TrySetResult();
            }
        }

        public async Task<ResticResult> RunAsync(ResticCall call, CancellationToken ct)
        {
            var args = call.Args;
            Calls.Add(string.Join(' ', args));
            Repositories.Add(call.Repository ?? call.Settings.ResticRepository());
            if (args[0] == "backup")
            {
                SawStageDb = args.Any(a => a.EndsWith("beememorybank.db") && File.Exists(a));
                if (BlockBackup) await _gate.Task.WaitAsync(ct);
                // The --json stream the real binary would emit: a status then a summary.
                return new ResticResult(0,
                    "{\"message_type\":\"status\",\"percent_done\":0.5,\"bytes_done\":10,\"total_bytes\":100}\n" +
                    "{\"message_type\":\"summary\"}\n", "");
            }
            if (args[0] == "init" && call.Repository is { } created)
            {
                // What restic leaves behind: a repository with a config file.
                Directory.CreateDirectory(created);
                await File.WriteAllTextAsync(Path.Combine(created, "config"), "repo", ct);
            }
            if (args[0] == "snapshots" && call.Job is null && ListGate is { } listGate)
                await listGate.Task.WaitAsync(ct);
            if (args[0] == "copy" && CopyGate is { } copyGate)
                await copyGate.Task.WaitAsync(ct);
            if (args[0] == "snapshots" && InitMissingOnce && !_initDone)
            {
                _initDone = true;
                return new ResticResult(10, "", "repository does not exist");
            }
            return new ResticResult(0, "[]", "");
        }
    }

    private sealed class FakeRecoverySet(string? json) : IRecoverySetSource
    {
        public string? Json { get; set; } = json;
        public Task<string?> BuildAsync(CancellationToken ct) => Task.FromResult(Json);
    }
}
