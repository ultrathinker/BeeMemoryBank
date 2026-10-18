using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// What a power cut (or a kill) leaves in a data folder at one instant: a copy of every file, taken from the running host with shared reads. Starting a node
/// over the copy is what the next start of the process would have found. The tests take one at every checkpoint of a flow and judge each.
/// </summary>
internal sealed class CrashImages : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bmb_crash_images_" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _labels = [];

    public IReadOnlyList<string> Labels => _labels;

    public string Dir(string label) => Path.Combine(_root, label.Replace(':', '_'));

    public void Take(string label, string dataDir)
    {
        var target = Dir(label);
        if (Directory.Exists(target)) return;   // the first time a checkpoint is reached
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(dataDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(dataDir, file);
            var to = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            try
            {
                using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var output = new FileStream(to, FileMode.Create, FileAccess.Write);
                input.CopyTo(output);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file the host replaced at that instant is not in the image, as it would not be after the cut.
            }
        }
        // The copy's files are files of their own; the cut would have left the very files the marker names.
        CutImage.Adopt(target);
        _labels.Add(label);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}

/// <summary>
/// BMB-306 fix round, findings B and C: a fresh setup, a join and a restore write in the open before the node is encrypted, and the process can die in
/// between. The seal intent (a marker written before the first write; see <see cref="SealIntent"/>) makes the next start wipe what was written, so no start
/// ever serves a node that holds data in the open. The tests take an image of the data folder at every checkpoint of each flow and judge what the next
/// start makes of it: whatever the flow had written is either encrypted or gone, never readable.
/// </summary>
[Collection(ProcessWideRateLimiterCollection.Name)]
public sealed class SealIntentFlowTests
{
    private const string Password = StepANode.Password;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static string NewFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb_seal_intent_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The start of a process over this folder: the gate every process passes (the same call <c>Program</c> makes first).</summary>
    private static RekeySwapResolution Start(string dir)
    {
        var (resolution, lease) = VaultStartup.Enter(dir);
        lease.Dispose();
        SqliteConnection.ClearAllPools();
        return resolution;
    }

    private static void NothingReadableIsLeft(string dir, IEnumerable<string> canaries, string checkpoint)
    {
        SealIntent.IsPending(dir).Should().BeFalse($"after {checkpoint} the start settled the intent");
        MetadataCanaries.ScanDirectory(dir, canaries).Should().BeEmpty($"after {checkpoint} the next start leaves no readable trace of what the flow wrote");
        var db = Path.Combine(dir, VaultStoreFiles.Database);
        if (File.Exists(db) && new FileInfo(db).Length > 0)
            VaultStoreFiles.HasSqliteMagic(db).Should().BeFalse($"a database that is still there after {checkpoint} is the encrypted one, never a plain one");
        VaultStoreFiles.Diagnose(dir).Mode.Should().NotBe(VaultStoreMode.Broken, $"after {checkpoint} the folder is startable");
    }

    private static bool ReadableIn(string dir, IEnumerable<string> canaries) => MetadataCanaries.ScanDirectory(dir, canaries).Count > 0;

    // ---------------------------------------------------------------- the marker is there before the first write

    private sealed class RecordingIntents(ISealIntents inner, string dir) : ISealIntents
    {
        public List<(string Kind, long IdentityRows, bool Pending)> Begun { get; } = [];

        public bool IsPending => inner.IsPending;

        public void Begin(string kind)
        {
            inner.Begin(kind);
            long rows;
            using (var conn = new SqliteConnection($"Data Source={Path.Combine(dir, VaultStoreFiles.Database)};Mode=ReadOnly;Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM tbl_node_identity";
                rows = Convert.ToInt64(cmd.ExecuteScalar());
            }
            Begun.Add((kind, rows, SealIntent.IsPending(dir)));
        }

        public void Complete() => inner.Complete();
    }

    [Fact]
    public async Task The_intent_is_on_disk_before_the_first_row_of_a_setup_is_written_and_is_gone_with_the_encrypted_node()
    {
        var dir = NewFolder();
        RecordingIntents? recorder = null;
        using var node = SealedApiNode.PlainOver(dir, services =>
            services.Replace(ServiceDescriptor.Singleton<ISealIntents>(_ => recorder = new RecordingIntents(new SealIntents(dir), dir))));
        using (var client = node.Client())
        {
            var response = await client.PostAsJsonAsync("/api/init/standalone", new { adminUsername = "admin", displayName = "Intent node", password = "Intent-setup-pw-1" });
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }

        recorder!.Begun.Should().ContainSingle();
        recorder.Begun[0].Kind.Should().Be(SealIntent.KindSetup);
        recorder.Begun[0].IdentityRows.Should().Be(0, "the marker was on disk before the identity row was written");
        recorder.Begun[0].Pending.Should().BeTrue();
        SealIntent.IsPending(dir).Should().BeFalse("the encrypted node ends the intent");
    }

    // ---------------------------------------------------------------- a fresh setup, cut at every checkpoint

    [Fact]
    public async Task A_fresh_setup_cut_at_any_point_never_starts_as_a_node_that_holds_data_in_the_open()
    {
        var canary = MetadataCanaries.Ascii("setup");
        var dir = NewFolder();
        using var images = new CrashImages();
        using var node = SealedApiNode.PlainOver(dir);
        using (var scope = node.Services.CreateScope())
        {
            // The canary is the admin's name: the display name of the node is in the boot store by design (it is not a secret of the vault).
            await scope.ServiceProvider.GetRequiredService<InitializationService>().InitializeAsync(canary, "Setup node", Password);
            images.Take("after-initialize", dir);

            var seal = node.Services.GetRequiredService<SealMigrationService>();
            seal.StepHook = step => images.Take("seal:" + step, dir);
            seal.SwapStepHook = step => images.Take("swap:" + step, dir);
            await scope.ServiceProvider.GetRequiredService<FreshNodeSealing>().SealAsync(Password, activateMetadata: true);
            images.Take("sealed", dir);
        }
        images.Labels.Should().HaveCountGreaterThan(8, "the checkpoints of the setup and of the migration were all imaged");

        // The control: without the next start some of the images hold the setup in the open - the exposure the intent closes.
        images.Labels.Where(l => ReadableIn(images.Dir(l), [canary])).Should().NotBeEmpty("a cut before the swap leaves the identity readable");

        foreach (var label in images.Labels)
        {
            var image = images.Dir(label);
            var resolution = Start(image);
            NothingReadableIsLeft(image, [canary], label);
            if (VaultStoreFiles.DetectMode(image) == VaultStoreMode.Plain)
                File.Exists(Path.Combine(image, VaultStoreFiles.Database)).Should().BeFalse($"a cut at {label} that did not reach the encrypted node leaves the empty node it was: {resolution.Intent?.Action}");
        }
    }

    [Fact]
    public async Task A_node_started_over_a_cut_setup_is_the_empty_node_and_can_be_set_up_again()
    {
        var canary = MetadataCanaries.Ascii("again");
        var dir = NewFolder();
        using var node = SealedApiNode.PlainOver(dir);
        using (var scope = node.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<InitializationService>().InitializeAsync(canary, "Cut setup", Password);
        // The process dies here: the vault is written, nothing encrypted it.
        ReadableIn(dir, [canary]).Should().BeTrue("the control: the cut leaves the identity readable");

        node.Restart();
        using var client = node.Client();

        using (var scope = node.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsClaimedAsync()).Should().BeFalse("the start wiped the cut setup");
        ReadableIn(dir, [canary]).Should().BeFalse();
        var again = await client.PostAsJsonAsync("/api/init/standalone", new { adminUsername = "admin", displayName = "Second try", password = "Intent-setup-pw-2" });
        again.StatusCode.Should().Be(HttpStatusCode.OK, await again.Content.ReadAsStringAsync());
        VaultStoreFiles.DetectMode(dir).Should().Be(VaultStoreMode.Sealed);
        SealIntent.IsPending(dir).Should().BeFalse();
    }

    // ---------------------------------------------------------------- a join, cut at every checkpoint

    private sealed class Watching(HttpMessageHandler inner, Action<HttpRequestMessage> seen) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            seen(request);
            return base.SendAsync(request, ct);
        }
    }

    [Fact]
    public async Task A_join_cut_at_any_point_never_starts_as_a_node_that_holds_the_hosts_notes_in_the_open()
    {
        RateLimitMiddleware.ResetForTests();
        using var host = await StepANode.CreateAsync("IntentHost");
        var vault = await StepANode.SeedWithCanariesAsync(host);
        await StepANode.ActivateAsync(host);

        var dir = NewFolder();
        using var images = new CrashImages();
        using var joiner = SealedApiNode.PlainOver(dir);
        joiner.Factory.RouteOutboundHttpThrough(new Watching(host.Server.CreateHandler(), request =>
        {
            // The snapshot is asked for after the joiner wrote its own identity, slot and user: the first checkpoint.
            if (request.RequestUri!.AbsolutePath.EndsWith("/api/sync/snapshot/for-join", StringComparison.Ordinal)) images.Take("before-snapshot", dir);
        }));
        var seal = joiner.Services.GetRequiredService<SealMigrationService>();
        seal.StepHook = step => images.Take("seal:" + step, dir);
        seal.SwapStepHook = step => images.Take("swap:" + step, dir);

        using (var client = joiner.Client())
        {
            var response = await client.PostAsJsonAsync("/api/init/join",
                new { adminUsername = "admin", displayName = "Intent joiner", remoteUrl = "http://host", password = Password }, JsonOpts);
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }
        images.Labels.Should().Contain("before-snapshot").And.HaveCountGreaterThan(8);

        // The control: before the encryption the imported notes are readable.
        images.Labels.Where(l => l.StartsWith("seal:", StringComparison.Ordinal) && ReadableIn(images.Dir(l), vault.Canaries)).Should().NotBeEmpty(
            "a cut after the snapshot import and before the swap leaves the host's notes in the open");

        foreach (var label in images.Labels)
        {
            var image = images.Dir(label);
            Start(image);
            NothingReadableIsLeft(image, vault.Canaries, label);
            if (VaultStoreFiles.DetectMode(image) == VaultStoreMode.Plain)
                File.Exists(Path.Combine(image, VaultStoreFiles.Database)).Should().BeFalse($"a join cut at {label} that did not reach the encrypted node leaves nothing of it");
        }
    }

    [Fact]
    public async Task A_join_whose_snapshot_cannot_be_imported_takes_everything_it_wrote_back_and_says_so()
    {
        RateLimitMiddleware.ResetForTests();
        using var host = await StepANode.CreateAsync("IntentHost2");
        var vault = await StepANode.SeedWithCanariesAsync(host);
        await StepANode.ActivateAsync(host);

        var dir = NewFolder();
        using var joiner = SealedApiNode.PlainOver(dir);
        // The snapshot arrives broken: the import fails after the joiner wrote its own rows.
        joiner.Factory.RouteOutboundHttpThrough(new FailingSnapshot(host.Server.CreateHandler()));
        using var client = joiner.Client();

        var response = await client.PostAsJsonAsync("/api/init/join",
            new { adminUsername = "admin", displayName = "Failed joiner", remoteUrl = "http://host", password = Password }, JsonOpts);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>(JsonOpts)).GetProperty("error").GetString();
        error.Should().Contain("Nothing was kept on this node").And.NotContain("partial state").And.NotContain(Password);
        using (var scope = joiner.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsClaimedAsync()).Should().BeFalse("the identity, the slot and the user of the failed join are gone");
        SealIntent.IsPending(dir).Should().BeFalse("the node is empty again, so the intent ended");
        SqliteConnection.ClearAllPools();
        MetadataCanaries.ScanDirectory(dir, vault.Canaries).Should().BeEmpty();
    }

    private sealed class FailingSnapshot(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/api/sync/snapshot/for-join", StringComparison.Ordinal))
                return await base.SendAsync(request, ct);
            var real = await base.SendAsync(request, ct);
            var bytes = await real.Content.ReadAsByteArrayAsync(ct);
            Array.Clear(bytes, bytes.Length / 2, bytes.Length - bytes.Length / 2);   // the second half of the archive is zeroes: the import cannot read it
            var broken = new HttpResponseMessage(real.StatusCode) { Content = new ByteArrayContent(bytes) };
            foreach (var header in real.Headers) broken.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return broken;
        }
    }

    // ---------------------------------------------------------------- a host that cannot encrypt does not start a setup

    [Fact]
    public async Task A_host_without_the_encrypted_database_services_refuses_a_setup_before_it_writes_anything()
    {
        var dir = NewFolder();
        using var node = SealedApiNode.PlainOver(dir, services => services.RemoveAll<ISealIntents>());
        using var client = node.Client();

        var response = await client.PostAsJsonAsync("/api/init/standalone", new { adminUsername = "admin", displayName = "Cannot", password = "Intent-setup-pw-3" });

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync()).Should().Contain("cannot be set up");
        using var scope = node.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsClaimedAsync()).Should().BeFalse("nothing was written");
    }
}

/// <summary>
/// The same for a restore (the encrypted node a restore ends with is made by the endpoint after the restore engine wrote the vault in the open): an image of
/// the data folder at every checkpoint of the restore and of the encryption that follows it, and the failure that used to leave a readable node behind (the
/// decrypted baseline that cannot be removed).
/// </summary>
[Collection(BaselineRestoreCollection.Name)]
public sealed class SealIntentRestoreTests(BaselineRestoreFixture src)
{
    private static readonly RestoreIdentity Who = new("admin", "Restored PC", BaselineRestoreFixture.Password);

    private static RekeySwapResolution Start(string dir)
    {
        var (resolution, lease) = VaultStartup.Enter(dir);
        lease.Dispose();
        SqliteConnection.ClearAllPools();
        return resolution;
    }

    [Fact]
    public async Task A_restore_cut_at_any_point_never_starts_as_a_node_that_holds_the_restored_notes_in_the_open()
    {
        using var target = new RecoveryTestFactory(allowOpenCopies: false);   // the product's own restore and its own encryption
        var dir = target.DataPath;
        using var images = new CrashImages();
        var restore = target.Services.GetRequiredService<RecoveryRestoreService>();
        restore.StepForTests = step => { images.Take("restore:" + step, dir); return Task.CompletedTask; };
        var staging = SnapshotStaging.DirIn(dir);
        BaselineRestoreRun.AfterStagingWork = copy => { if (copy.StartsWith(staging, StringComparison.Ordinal)) images.Take("decrypted-baseline", dir); };
        try
        {
            await restore.RestoreFromBaselineAsync(new PackageRestoreSource(src.Package), Who, blind: null, extraEvidence: []);
        }
        finally
        {
            BaselineRestoreRun.AfterStagingWork = null;
        }
        images.Take("restored-before-encryption", dir);
        SealIntent.IsPending(dir).Should().BeTrue("the restore engine began the intent before its first write; only the encryption ends it");

        var seal = target.Services.GetRequiredService<SealMigrationService>();
        seal.StepHook = step => images.Take("seal:" + step, dir);
        seal.SwapStepHook = step => images.Take("swap:" + step, dir);
        using (var scope = target.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<FreshNodeSealing>().SealAsync(Who.Password, activateMetadata: true, CancellationToken.None, afterRestore: true);
        SealIntent.IsPending(dir).Should().BeFalse("the encrypted node ends the intent");
        images.Labels.Should().Contain(["decrypted-baseline", "restored-before-encryption"]).And.HaveCountGreaterThan(10);

        images.Labels.Where(l => MetadataCanaries.ScanDirectory(images.Dir(l), src.Canaries).Count > 0).Should().NotBeEmpty(
            "the control: a cut after the restore and before the swap leaves the restored notes in the open");
        foreach (var label in images.Labels)
        {
            var image = images.Dir(label);
            Start(image);
            SealIntent.IsPending(image).Should().BeFalse($"after {label} the start settled the intent");
            MetadataCanaries.ScanDirectory(image, src.Canaries).Should().BeEmpty($"after {label} the next start leaves no readable trace of the restored notes");
            var db = Path.Combine(image, VaultStoreFiles.Database);
            if (File.Exists(db) && new FileInfo(db).Length > 0)
                VaultStoreFiles.HasSqliteMagic(db).Should().BeFalse($"after {label} a database that is still there is the encrypted one");
        }
    }

    [Fact]
    public async Task A_restore_whose_cleanup_fails_is_taken_back_at_once_and_leaves_no_readable_node()
    {
        var folder = await src.BuildBlindBackupFolderAsync("intent-cleanup");
        using var target = new RecoveryTestFactory(allowOpenCopies: false);
        var dir = target.DataPath;
        var staging = SnapshotStaging.DirIn(dir);
        using var images = new CrashImages();
        // A folder under a sidecar's name stands for a decrypted copy that cannot be removed, on every platform: the restore then throws, after the
        // target was written in the open - the case that used to leave a readable node behind.
        BaselineRestoreRun.AfterStagingWork = copy =>
        {
            if (!copy.StartsWith(staging, StringComparison.Ordinal)) return;
            Directory.CreateDirectory(copy + "-journal");
            images.Take("cleanup-failed", dir);
        };
        try
        {
            using var client = target.CreateClient();
            (await client.PostAsJsonAsync("/api/restore/backup", new { path = folder, password = Who.Password, adminUsername = "admin", displayName = "Restored PC" }))
                .EnsureSuccessStatusCode();
            var progress = await RestoreWizardWait.UntilEndAsync(client);
            progress.GetProperty("state").GetString().Should().Be("failed", "a decrypted copy that stays makes the restore repair-required, never done: " + progress);
        }
        finally
        {
            BaselineRestoreRun.AfterStagingWork = null;
        }

        // The endpoint took the restore back itself: the node is empty again, and nothing of the restored notes is readable.
        SealIntent.IsPending(dir).Should().BeFalse("the take-back ended the intent");
        using (var scope = target.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsClaimedAsync()).Should().BeFalse("the restore left no identity behind");
        SqliteConnection.ClearAllPools();
        var staged = Directory.Exists(staging) ? Directory.EnumerateFileSystemEntries(staging).ToList() : [];
        foreach (var leftover in staged.Where(Directory.Exists)) Directory.Delete(leftover, recursive: true);   // the planted "sidecar"
        MetadataCanaries.ScanDirectory(dir, src.Canaries).Should().BeEmpty("the take-back wiped what the restore wrote");

        // And had the process died at the failure instead, the next start would have done the same.
        var image = images.Dir("cleanup-failed");
        MetadataCanaries.ScanDirectory(image, src.Canaries).Should().NotBeEmpty("the control: at the failure the restored notes were readable");
        Start(image);
        MetadataCanaries.ScanDirectory(image, src.Canaries).Should().BeEmpty("the next start wipes what the failed restore wrote");
        SealIntent.IsPending(image).Should().BeFalse();
    }
}
