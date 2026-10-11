using System.Net.Http.Json;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.IO;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Storage.Sqlite;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.Sync.Metadata;
using BeeMemoryBank.TestSupport;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>A full node whose vault factory is keyed: the key source is registered, so the composition root builds a sealed <see cref="DbConnectionFactory"/>.</summary>
public sealed class SealedStepAFactory(InMemoryVaultKeySource keys) : StepAFactory
{
    protected override void ConfigureStepAServices(IServiceCollection services) => services.AddSingleton<IVaultKeySource>(keys);
}

/// <summary>
/// The baseline producer of an ENCRYPTED full node (docs/design/step-b-spec.md 8.3, red test 15.1.8): the working copy is a keyed <c>VACUUM INTO</c>, the
/// BMBCH1 container is streamed from its plain image, the self-check opens the container into a second sealed copy and checks that, and no plain file is on
/// the disk at any moment. The container opens (with the metadata key) to a database a plain reader imports, with every canary in it. The host is the
/// real one; only the gate of track 3 is missing.
/// </summary>
[Collection(HeavyOperationCollection.Name)]
public sealed class BaselineSealedProducerTests : IAsyncLifetime
{
    private readonly InMemoryVaultKeySource _keys = new();
    private SealedStepAFactory _node = null!;
    private StepANode.SeededVault _vault = null!;
    private string _keyId = null!;
    private byte[] _mek = null!;
    private readonly string _work = Path.Combine(Path.GetTempPath(), "bmb-sealed-baseline-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _node = new SealedStepAFactory(_keys);
        await _node.InitializeNodeAsync("SealedBaseline", StepANode.Password);
        using (var client = _node.CreateClient())
            (await client.PostAsJsonAsync("/api/session/login", new { username = "admin", password = StepANode.Password })).EnsureSuccessStatusCode();
        _vault = await StepANode.SeedWithCanariesAsync(_node);
        (_keyId, _mek) = await StepANode.ActivateAsync(_node);
        Directory.CreateDirectory(_work);
    }

    public Task DisposeAsync()
    {
        _node.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_baseline_of_an_encrypted_node_is_made_without_a_plain_file_and_opens_to_a_valid_database_with_the_canaries()
    {
        _node.Services.GetRequiredService<DbConnectionFactory>().IsSealed.Should().BeTrue("the host built its vault factory over the key source");
        var staging = SnapshotStaging.DirIn(_node.DataPath);
        Directory.CreateDirectory(staging);
        using var watch = new PlainFileWatch([staging], _vault.Canaries);

        BaselineFiles files;
        using (var scope = _node.Services.CreateScope())
            files = await scope.ServiceProvider.GetRequiredService<BaselineProducer>().ProduceAsync(BaselineReasons.OnDemand);
        watch.Stop();

        watch.Seen.Keys.Should().NotBeEmpty("the watch saw the working copy and the self-check copy");
        watch.Plain.Should().BeEmpty("no plain database exists in the staging folder at any moment: the copy, the container and the self-check are sealed");
        watch.CanaryHits.Should().BeEmpty();
        MetadataCanaries.ScanDirectory(files.Directory, _vault.Canaries).Should().BeEmpty("the baseline directory shows no title, path or tag");
        VaultStoreFiles.HasSqliteMagic(Path.Combine(_node.DataPath, "beememorybank.db")).Should().BeFalse();
        Directory.EnumerateFiles(staging).Should().BeEmpty("both working copies are gone");

        // Control, and the 2.6 plain reader: the container opens with the metadata key to a database a plain connection reads, with every canary in it.
        var opened = Path.Combine(_work, "opened.db");
        await StepANode.OpenBaselineAsync(files.Directory, _keyId, _mek, opened);
        MetadataCanaries.ScanFile(opened, _vault.Canaries).Select(h => h.Canary).Distinct().Should().BeEquivalentTo(_vault.Canaries);
        using var db = new SqliteConnection($"Data Source={opened};Mode=ReadOnly;Pooling=False");
        db.Open();
        db.ExecuteScalar<string>("PRAGMA integrity_check").Should().Be("ok");
        db.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_article").Should().BeGreaterThan(0);
        db.ExecuteScalar<long>("SELECT COUNT(*) FROM tbl_blob").Should().Be(0, "a baseline carries no blob store");
    }
}
