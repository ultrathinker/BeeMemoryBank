using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Recovery;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.Sync.Recovery;
using BeeMemoryBank.Vault.Sealed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// BMB-306 for the restore wizard of a new node (Setup, <c>POST /api/restore/backup</c>): a copy that holds the vault in the open - made before metadata
/// encryption was switched on - is refused whatever it is called, with nothing written; a copy made after the switch restores into a node that ends
/// ENCRYPTED under the master password, with metadata encryption on, before the wizard says it is done. These run on hosts that opt into nothing
/// (<c>allowOpenCopies: false</c>), which is what every host a person can start is.
/// </summary>
[Collection(RestoreScenarioCollection.Name)]
public sealed class RestoreWizardOpenCopyTests(RestoreSourceFixture source)
{
    private static readonly RestoreIdentity Who = new("admin", "Restored PC", RestoreSourceFixture.Password);

    private static async Task<bool> IsInitializedAsync(RecoveryTestFactory node)
    {
        using var scope = node.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<InitializationService>().IsInitializedAsync();
    }

    [Fact]
    public async Task The_wizard_refuses_a_backup_folder_made_before_the_switch_and_writes_nothing()
    {
        using var target = new RecoveryTestFactory(allowOpenCopies: false);
        using var client = target.CreateClient();

        (await client.PostAsJsonAsync("/api/restore/backup", new
        {
            path = source.BackupFolder, password = RestoreSourceFixture.Password, adminUsername = "admin", displayName = "PC"
        })).EnsureSuccessStatusCode();
        var progress = await RestoreWizardWait.UntilEndAsync(client);

        progress.GetProperty("state").GetString().Should().Be("failed", progress.ToString());
        progress.GetProperty("error").GetString().Should().Contain("made before metadata encryption was switched on").And.Contain("Nothing was changed");
        (await IsInitializedAsync(target)).Should().BeFalse("nothing was written");
        VaultStoreFiles.DetectMode(target.DataPath).Should().NotBe(VaultStoreMode.Sealed);
    }

    [Fact]
    public async Task The_restore_service_refuses_a_database_copy_with_no_baseline_and_a_format_1_package()
    {
        using var target = new RecoveryTestFactory(allowOpenCopies: false);

        var copy = () => target.Services.GetRequiredService<BlindRestoreClient>().RestoreFromBackupAsync(source.BackupFolder, Who);
        var package = () => target.Services.GetRequiredService<RecoveryRestoreService>().RestoreFromBlindPackageAsync(
            source.PackagePath, source.Signature, source.ProducerKey, Who,
            new RestoreBlindPeer(source.BlindNodeIdentity, "Blind", source.ProducerKey, "https://blind.test:5610", 0, null), source.BlindEvents);

        (await copy.Should().ThrowAsync<RestoreRefusedException>()).Which.Message.Should().Be(RestoreRefusedException.OpenCopyMessage);
        (await package.Should().ThrowAsync<RestoreRefusedException>()).Which.Message.Should().Be(RestoreRefusedException.OpenCopyMessage);
        (await IsInitializedAsync(target)).Should().BeFalse("nothing was written");
    }
}

/// <summary>The wizard, with a copy that was made after the switch: encrypted and activated at the end, or taken back.</summary>
[Collection(BaselineRestoreCollection.Name)]
public sealed class RestoreWizardEncryptedEndTests(BaselineRestoreFixture src)
{
    private static byte[] Head(string path, int count = 16)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[count];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static object Start(string folder) =>
        new { path = folder, password = BaselineRestoreFixture.Password, adminUsername = "admin", displayName = "Restored PC" };

    [Fact]
    public async Task A_wizard_restore_ends_with_an_encrypted_node_with_metadata_encryption_on_that_the_password_opens()
    {
        var folder = await src.BuildBlindBackupFolderAsync("wizard-encrypted-end");
        using var target = new RecoveryTestFactory(allowOpenCopies: false);
        using var client = target.CreateClient();

        (await client.PostAsJsonAsync("/api/restore/backup", Start(folder))).EnsureSuccessStatusCode();
        var progress = await RestoreWizardWait.UntilEndAsync(client);

        progress.GetProperty("state").GetString().Should().Be("done", progress.ToString());
        VaultStoreFiles.DetectMode(target.DataPath).Should().Be(VaultStoreMode.Sealed, "the restored node is encrypted before the wizard says done");
        File.Exists(Path.Combine(target.DataPath, VaultStoreFiles.Boot)).Should().BeTrue();
        Head(Path.Combine(target.DataPath, VaultStoreFiles.Database)).Should().NotEqual("SQLite format 3\0"u8.ToArray());
        Directory.EnumerateFiles(target.DataPath, "*.seal-*", SearchOption.AllDirectories).Should().BeEmpty("no replaced plain file is left");
        target.Services.GetRequiredService<SessionService>().IsUnlocked.Should().BeFalse("the key is not left in memory");

        (await target.Services.GetRequiredService<SessionService>().UnlockAsync(BaselineRestoreFixture.Password)).Should().BeTrue("the master password opens it");
        target.Services.GetRequiredService<ISyncProtocolPolicy>().SealsMetadata.Should().BeTrue("metadata encryption is on");
        using var scope = target.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<INodeIdentityRepository>().GetAsync()).Should().NotBeNull();
    }

    [Fact]
    public async Task A_wizard_restore_that_cannot_encrypt_the_node_takes_it_back_and_says_so()
    {
        var folder = await src.BuildBlindBackupFolderAsync("wizard-cannot-encrypt");
        using var target = new RecoveryTestFactory(allowOpenCopies: false, services: s =>
        {
            var original = s.Single(d => d.ServiceType == typeof(ISealMigrationHost)).ImplementationFactory!;
            s.Replace(ServiceDescriptor.Singleton<ISealMigrationHost>(sp => new RefusingHost((ISealMigrationHost)original(sp))));
        });
        using var client = target.CreateClient();

        (await client.PostAsJsonAsync("/api/restore/backup", Start(folder))).EnsureSuccessStatusCode();
        var progress = await RestoreWizardWait.UntilEndAsync(client);

        progress.GetProperty("state").GetString().Should().Be("failed", progress.ToString());
        progress.GetProperty("error").GetString().Should().Contain("could not be encrypted").And.Contain("run the restore again");
        using var scope = target.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<InitializationService>().IsClaimedAsync())
            .Should().BeFalse("what the failed restore wrote was taken back: no node is left to run in the open");
        VaultStoreFiles.DetectMode(target.DataPath).Should().NotBe(VaultStoreMode.Sealed);
    }

    private sealed class RefusingHost(ISealMigrationHost inner) : ISealMigrationHost
    {
        public Task PrepareVaultAsync(CancellationToken ct) => inner.PrepareVaultAsync(ct);

        public Task<IReadOnlyList<SealBlocker>> HostBlockersAsync(SealOptions options, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SealBlocker>>([new SealBlocker("test_refusal", "The test host refuses to encrypt.")]);

        public Task<IReadOnlyList<SealWarning>> HostWarningsAsync(CancellationToken ct) => inner.HostWarningsAsync(ct);
    }
}

internal static class RestoreWizardWait
{
    /// <summary>Polls <c>GET /api/restore/progress</c> until the wizard has ended (done, failed or cancelled), and returns what it said last.</summary>
    public static async Task<JsonElement> UntilEndAsync(HttpClient client)
    {
        JsonElement last = default;
        for (var i = 0; i < 600; i++)
        {
            using var response = await client.GetAsync("/api/restore/progress");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                last = (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
                var state = last.GetProperty("state").GetString();
                if (state is "done" or "failed" or "cancelled" or "boxes_remaining") return last;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("The restore wizard did not end in a minute. Last: " + last);
    }
}
