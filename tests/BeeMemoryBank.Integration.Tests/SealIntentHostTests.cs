using System.Net;
using System.Net.Http.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.TestSupport;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-306 fix round 2 (findings N1, N2, N5): the real Api host over a data folder with a pending seal intent. A start that cannot settle the intent - the leftover will
/// not go, or another process of the node may be running the operation - is refused by name by the host that stays up only to say why, and it opens, serves and wipes
/// nothing. The next start, with nothing in the way, settles it.
/// </summary>
[Collection(ProcessWideRateLimiterCollection.Name)]
public sealed class SealIntentHostTests : IDisposable
{
    private readonly SealedCorpus.Pool _pool = SealedCorpus.Pool.Create(Path.Combine(Path.GetTempPath(), "bmb_intent_host_pool_" + Guid.NewGuid().ToString("N")));
    private readonly string _canary = MetadataCanaries.Ascii("hostintent");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path.GetDirectoryName(_pool.Dir)!, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string NewFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmb_intent_host_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// What a setup that was cut off leaves: the database the migrations made before the operation began, the marker that found it, and the canary the operation
    /// then wrote into that same file.
    /// </summary>
    private string CutSetup(string dir, string kind = SealIntent.KindSetup)
    {
        var db = Path.Combine(dir, VaultStoreFiles.Database);
        File.Copy(_pool.PlainMain, db, overwrite: true);
        SealIntent.Begin(dir, kind);
        using (var fs = new FileStream(db, FileMode.Append, FileAccess.Write)) fs.Write(System.Text.Encoding.UTF8.GetBytes(_canary));
        return db;
    }

    [Fact]
    public async Task A_host_over_a_cut_setup_whose_leftover_cannot_be_wiped_refuses_by_name_serves_nothing_and_the_next_start_settles_it()
    {
        var dir = NewFolder();
        var db = CutSetup(dir);
        var held = new FileStream(db, FileMode.Open, FileAccess.ReadWrite, FileShare.None);   // a leftover that will not go
        using var node = SealedApiNode.PlainOver(dir);
        try
        {
            using var client = node.Client();

            var status = await SealedApiNode.GetJsonAsync(client, "/api/vault/status");
            status.GetProperty("mode").GetString().Should().Be("broken");
            status.GetProperty("broken_reason").GetString().Should().Be(SealIntent.RefusedCode);
            status.GetProperty("files").EnumerateArray().Select(f => f.GetString()).Should().Contain(VaultStoreFiles.Database);
            status.GetProperty("message").GetString().Should().Contain("interrupted").And.Contain(VaultStoreFiles.Database).And.NotContain(dir);
            using (var health = await client.GetAsync("/health")) health.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            using (var setup = await client.PostAsJsonAsync("/api/init/standalone", new { adminUsername = "admin", displayName = "Nope", password = "Intent-setup-pw-4" }))
            {
                setup.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "no setup is served over what the cut one left");
                (await setup.Content.ReadAsStringAsync()).Should().Contain("vault_broken");
            }
            SealIntent.IsPending(dir).Should().BeTrue("the intent stays for the start that can settle it");
        }
        finally
        {
            held.Dispose();
        }
        File.Exists(db).Should().BeTrue("nothing was guessed");

        node.Restart();   // the file is free now

        using (var scope = node.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsClaimedAsync()).Should().BeFalse("the next start wiped the cut setup: the empty node it was");
        SealIntent.IsPending(dir).Should().BeFalse();
        SqliteConnection.ClearAllPools();
        MetadataCanaries.ScanDirectory(dir, [_canary]).Should().BeEmpty();
    }

    [Fact]
    public async Task A_host_that_starts_beside_a_process_that_may_be_running_the_operation_is_refused_by_name_and_wipes_nothing()
    {
        var dir = NewFolder();
        using var other = VaultStartup.TryAcquireShared(dir);   // the process that runs the operation holds the vault lease
        other.Should().NotBeNull();
        var db = CutSetup(dir, SealIntent.KindJoin);
        var written = File.ReadAllBytes(db);
        using var node = SealedApiNode.PlainOver(dir);
        using var client = node.Client();

        var status = await SealedApiNode.GetJsonAsync(client, "/api/vault/status");

        status.GetProperty("mode").GetString().Should().Be("broken");
        status.GetProperty("broken_reason").GetString().Should().Be(SealIntent.BusyCode);
        status.GetProperty("files").EnumerateArray().Select(f => f.GetString()).Should().Contain(SealIntent.FileName);
        status.GetProperty("message").GetString().Should().Contain("pending").And.NotContain(dir);
        using (var health = await client.GetAsync("/health")) health.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        File.ReadAllBytes(db).Should().Equal(written, "another live process may own the operation: nothing was wiped");
        SealIntent.IsPending(dir).Should().BeTrue();
        SealIntent.Complete(dir);
    }

    [Fact]
    public async Task A_host_over_a_copied_data_folder_of_a_cut_setup_refuses_by_name_answers_no_query_with_its_clear_data_and_wipes_nothing()
    {
        // What a Docker volume copy or an image taken while the setup ran hands the next host: the marker, its id and a database that holds clear data, but files the
        // operation never saw. The host refuses them by name, answers every query with the refusal (never a row of the copy), and does not touch the copy.
        var source = NewFolder();
        CutSetup(source);
        var copy = NewFolder();
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        var db = Path.Combine(copy, VaultStoreFiles.Database);
        var written = File.ReadAllBytes(db);
        using var node = SealedApiNode.PlainOver(copy);
        using var client = node.Client();

        var status = await SealedApiNode.GetJsonAsync(client, "/api/vault/status");

        status.GetProperty("mode").GetString().Should().Be("broken");
        status.GetProperty("broken_reason").GetString().Should().Be(SealIntent.ForeignCode);
        status.GetProperty("files").EnumerateArray().Select(f => f.GetString()).Should().Contain(VaultStoreFiles.Database);
        foreach (var route in new[] { "/api/folders/tree", "/api/articles", "/api/search?q=" + _canary, "/api/vault/status", "/health" })
        {
            using var response = await client.GetAsync(route);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain(_canary, "no query is answered from the clear database of a copy: " + route);
            if (route != "/api/vault/status") response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, route);
        }
        File.ReadAllBytes(db).Should().Equal(written, "a copy is somebody's file: nothing was wiped");
        SealIntent.IsPending(copy).Should().BeTrue();
        SealIntent.Complete(source);
    }

    [Fact]
    public async Task A_setup_asked_for_while_another_operation_is_pending_begins_nothing_and_leaves_the_marker_as_it_was()
    {
        var dir = NewFolder();
        using var node = SealedApiNode.PlainOver(dir);
        using var client = node.Client();
        SealIntent.Begin(dir, SealIntent.KindJoin);   // another operation of this node, not ended
        var markerBefore = File.ReadAllText(SealIntent.PathFor(dir));
        var media = Path.Combine(dir, "media");
        Directory.CreateDirectory(media);
        File.WriteAllText(Path.Combine(media, "other-operation.enc"), "written by the operation that is running");

        using var response = await client.PostAsJsonAsync("/api/init/standalone", new { adminUsername = "admin", displayName = "Second", password = "Intent-setup-pw-5" });

        response.IsSuccessStatusCode.Should().BeFalse("a second operation cannot begin over the first");
        File.ReadAllText(SealIntent.PathFor(dir)).Should().Be(markerBefore, "the running operation's marker is not touched");
        File.Exists(Path.Combine(media, "other-operation.enc")).Should().BeTrue("the failed setup took back nothing of the operation that is running");
        using (var scope = node.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsClaimedAsync()).Should().BeFalse("nothing was written");
        SealIntent.Complete(dir);
    }
}

/// <summary>
/// BMB-306 fix round 2 (finding N5): a restore composed without the services that mark and encrypt (the Api's own composition always has them) is refused before any
/// key is recovered or any file is written, and one that meets a pending operation does not begin.
/// </summary>
[Collection(BaselineRestoreCollection.Name)]
public sealed class SealIntentRestoreCompositionTests(BaselineRestoreFixture src)
{
    private static readonly RestoreIdentity Who = new("admin", "Restored PC", BaselineRestoreFixture.Password);

    [Theory]
    [InlineData("intents")]
    [InlineData("sealing")]
    public async Task A_restore_in_a_host_without_the_sealing_services_is_refused_before_any_key_is_recovered_or_anything_is_written(string missing)
    {
        using var target = new RecoveryTestFactory(allowOpenCopies: false, services: s =>
        {
            if (missing == "intents") s.RemoveAll<ISealIntents>();
            else s.RemoveAll<FreshNodeSealing>();
        });
        var dir = target.DataPath;
        var restore = target.Services.GetRequiredService<RecoveryRestoreService>();
        var steps = new List<string>();
        restore.StepForTests = step => { steps.Add(step); return Task.CompletedTask; };

        var act = async () => await restore.RestoreFromBaselineAsync(new PackageRestoreSource(src.Package), Who, blind: null, extraEvidence: []);

        (await act.Should().ThrowAsync<RestoreRefusedException>()).Which.Message.Should().Be(RestoreRefusedException.CannotEncryptMessage);
        steps.Should().BeEmpty("no step of the restore ran");
        SealIntent.IsPending(dir).Should().BeFalse("nothing began");
        using (var scope = target.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsClaimedAsync()).Should().BeFalse("nothing was restored");
        SqliteConnection.ClearAllPools();
        MetadataCanaries.ScanDirectory(dir, src.Canaries).Should().BeEmpty();
    }

    [Fact]
    public async Task A_restore_over_a_key_slot_that_is_no_torn_bootstrap_is_refused_as_not_empty_and_begins_nothing()
    {
        using var target = new RecoveryTestFactory(allowOpenCopies: false);
        var dir = target.DataPath;
        var restore = target.Services.GetRequiredService<RecoveryRestoreService>();
        // A slot with no identity row: not initialized as far as the restore's own guard goes, but somebody's key material, and no restore bootstrap is in progress.
        await target.Services.GetRequiredService<IKeySlotRepository>().CreateAsync(new MasterKeyStore
        {
            SlotType = "user", EncryptedMasterDek = new byte[48], IV = new byte[12], Salt = new byte[16],
            ArgonMemory = CryptoConstants.DefaultArgonMemory, ArgonIterations = CryptoConstants.DefaultArgonIterations,
            ArgonParallelism = CryptoConstants.DefaultArgonParallelism, CreatedAt = DateTime.UtcNow,
        });

        var act = async () => await restore.RestoreFromBaselineAsync(new PackageRestoreSource(src.Package), Who, blind: null, extraEvidence: []);

        (await act.Should().ThrowAsync<SealIntentException>()).Which.Code.Should().Be(SealIntent.NotEmptyCode);
        SealIntent.IsPending(dir).Should().BeFalse("nothing began");
        SqliteConnection.ClearAllPools();
        MetadataCanaries.ScanDirectory(dir, src.Canaries).Should().BeEmpty();
    }

    [Fact]
    public async Task A_restore_that_meets_a_pending_operation_does_not_begin_and_writes_nothing()
    {
        using var target = new RecoveryTestFactory(allowOpenCopies: false);
        var dir = target.DataPath;
        var restore = target.Services.GetRequiredService<RecoveryRestoreService>();   // the host has started: a marker written before it would be settled by that start
        SealIntent.Begin(dir, SealIntent.KindSetup);   // another operation of this node, not ended
        var markerBefore = File.ReadAllText(SealIntent.PathFor(dir));

        var act = async () => await restore.RestoreFromBaselineAsync(new PackageRestoreSource(src.Package), Who, blind: null, extraEvidence: []);

        await act.Should().ThrowAsync<Exception>("a second operation cannot begin over the first");
        File.ReadAllText(SealIntent.PathFor(dir)).Should().Be(markerBefore, "the running operation's marker is not touched");
        using (var scope = target.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsClaimedAsync()).Should().BeFalse("nothing was restored");
        SqliteConnection.ClearAllPools();
        MetadataCanaries.ScanDirectory(dir, src.Canaries).Should().BeEmpty();
        SealIntent.Complete(dir);
    }
}
