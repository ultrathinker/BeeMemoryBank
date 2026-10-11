using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Services.BlindBackup;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Sync.Blind;
using BeeMemoryBank.Sync.Blind.Format2;
using BeeMemoryBank.TestSupport;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Docker blind backups after Step A (docs/design/step-a-spec.md 9.8, red test 16.3 item 9): what restic is given to back up
/// - the staging copy of the database, the media folder and the current sealed baseline - holds no canary once the first
/// format-2 adoption purged the legacy content; before it, it does (the control of the scan). The restic here is scripted:
/// at its "backup" call it scans every path it was given.
/// </summary>
public sealed class BlindFormat2BackupTests : IAsyncLifetime
{
    private readonly ScanningRestic _restic = new();
    private readonly Factory _blind;
    private readonly string _repoDir = Path.Combine(Path.GetTempPath(), "bmb_f2_repo_" + Guid.NewGuid().ToString("N"));
    private StepASeeder _seeder = null!;
    private readonly List<string> _canaries = [MetadataCanaries.Ascii("bkptitle"), MetadataCanaries.NonAscii("bkpfolder")];

    public BlindFormat2BackupTests() => _blind = new Factory(_restic);

    private sealed class Factory(ScanningRestic restic) : BlindNodeFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s =>
            {
                s.AddSingleton<IResticRunner>(restic);
                s.AddSingleton<IRecoverySetSource>(new NoRecoverySet());
            });
        }
    }

    public async Task InitializeAsync()
    {
        _ = _blind.Services;
        _seeder = await StepASeeder.CreateAsync();
        using var conn = _blind.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection();
        var now = DateTime.UtcNow.ToString("O");
        await conn.ExecuteAsync("INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at) VALUES (@id, @title, @path, 'A', @now, @now)",
            new { id = Guid.NewGuid().ToString(), title = _canaries[0], path = "/" + _canaries[1], now });
        Directory.CreateDirectory(_repoDir);
        using var client = _blind.CreateClient();
        (await client.PutAsJsonAsync("/api/blind/backup/settings", new { repoType = "folder", repoFolder = _repoDir, resticPassword = "pw-restic" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    public Task DisposeAsync()
    {
        _blind.Dispose();
        _seeder.Dispose();
        try { Directory.Delete(_repoDir, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AfterTheFirstAdoption_TheBackupInputHoldsNoCanary_AndCarriesTheSealedBaseline()
    {
        // Control: before the adoption the staging copy is the 2.5 database with the metadata in it.
        await BackupAsync();
        _restic.Hits.Should().NotBeEmpty("the scan sees the legacy content in the backup input");
        _restic.Paths.Should().NotContain(p => p.EndsWith(Path.Combine("baselines", "current"), StringComparison.Ordinal));

        // The first format-2 adoption (spec 9.2, 9.3).
        await _blind.Services.GetRequiredService<IWhitelistRepository>().CreateAsync(new WhitelistEntry
        {
            NodeId = _seeder.NodeId, DisplayName = "PC", Ed25519PublicKey = _seeder.PublicKey, Status = "A", IsSuperadmin = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, LamportTs = 1, SourceNodeId = _seeder.NodeId,
        });
        var baseline = _seeder.WriteBaseline(Path.Combine(_seeder.DataPath, "baseline"), [], lamportAtCut: 10);
        (await _blind.Services.GetRequiredService<IBaselineStore>().AdoptAsync(baseline, 0, producerAuthorized: true))
            .Outcome.Should().Be(AdoptOutcome.Adopted);
        SqliteConnection.ClearAllPools();

        await BackupAsync();
        _restic.Hits.Should().BeEmpty("the purge left no metadata in the database the backup copies (spec 9.8)");
        _restic.Paths.Should().Contain(p => p.EndsWith(Path.Combine("baselines", "current"), StringComparison.Ordinal),
            "the sealed baseline goes into every snapshot: a backup folder is a restore source");
        File.Exists(Path.Combine(_blind.DataPath, "blind", "stage", "beememorybank.db")).Should().BeFalse("the staging copy is still removed");
    }

    private async Task BackupAsync()
    {
        _restic.Reset(_canaries);
        using var client = _blind.CreateClient();
        (await client.PostAsync("/api/blind/backup/now", content: null)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await _blind.Services.GetRequiredService<BlindJobManager>().WhenIdleAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        var s = await (await client.GetAsync("/api/blind/status")).Content.ReadFromJsonAsync<JsonElement>();
        var job = s.GetProperty("jobs").EnumerateArray().First();
        job.GetProperty("state").GetString().Should().Be("done", job.ToString());
        _restic.Scanned.Should().BeTrue("restic was asked to back up");
    }

    private sealed class ScanningRestic : IResticRunner
    {
        private List<string> _canaries = [];
        public List<CanaryHit> Hits { get; } = [];
        public List<string> Paths { get; } = [];
        public bool Scanned { get; private set; }

        public void Reset(List<string> canaries)
        {
            _canaries = canaries;
            Hits.Clear();
            Paths.Clear();
            Scanned = false;
        }

        public Task<ResticResult> RunAsync(ResticCall call, CancellationToken ct)
        {
            var args = call.Args;
            if (args[0] == "backup")
            {
                foreach (var path in args.Skip(1).Where(a => File.Exists(a) || Directory.Exists(a)))
                {
                    Paths.Add(path);
                    Hits.AddRange(Directory.Exists(path) ? MetadataCanaries.ScanDirectory(path, _canaries) : MetadataCanaries.ScanFile(path, _canaries));
                }
                Scanned = true;
                return Task.FromResult(new ResticResult(0, "{\"message_type\":\"summary\"}\n", ""));
            }
            if (args[0] == "init" && call.Repository is { } created)
            {
                Directory.CreateDirectory(created);
                File.WriteAllText(Path.Combine(created, "config"), "repo");
            }
            return Task.FromResult(new ResticResult(0, "[]", ""));
        }
    }

    private sealed class NoRecoverySet : IRecoverySetSource
    {
        public Task<string?> BuildAsync(CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
