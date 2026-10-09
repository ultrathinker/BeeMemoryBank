using Dapper;
using Microsoft.Extensions.Logging;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// A0 wire hygiene (brief P1-A0): the shared applier's skip/drop log lines name ids, not content —
/// no tag names, no folder paths. The lines run wherever the applier runs, a blind node included,
/// where they would sit in plaintext next to a database it cannot read. A logger provider renders
/// the attached exception after the formatted message too, so the sink is checked on both — the
/// repository's InvalidOperationException messages carry the tag names (review round: the first
/// cut of this test dropped the exception and never saw that leak).
/// </summary>
public class ApplierLogHygieneTests : IAsyncLifetime
{
    private const string CanaryTagOld = "Canary Tag Old";
    private const string CanaryTagNew = "Canary Tag New";
    private const string CanaryMergeSource = "Canary Merge Source";
    private const string CanaryDeleteName = "Canary Delete Name";
    private const string CanaryPath = "/Canary/Secret/Path";
    private const string CanaryFolderName = "Canary Folder Name";
    private const string CanaryDeletePath = "/Canary/Delete/Path";
    private const string CanaryTamperedId = "/Tampered/Entity/Path";

    private readonly CapturingFixture _nodeA = new();
    private readonly CapturingFixture _nodeB = new();

    public async Task InitializeAsync()
    {
        await _nodeA.InitializeAsync();
        await _nodeA.InitService.InitializeAsync("admin", "NodeA", "passwordA");
        await _nodeA.Session.UnlockAsync("passwordA");
        await _nodeB.InitializeAsync();
        await _nodeB.InitService.InitializeAsync("admin", "NodeB", "passwordB");
        await _nodeB.Session.UnlockAsync("passwordB");

        var identityA = (await _nodeA.NodeRepo.GetAsync())!;
        var identityB = (await _nodeB.NodeRepo.GetAsync())!;
        var now = DateTime.UtcNow;
        await _nodeB.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = identityA.NodeId, DisplayName = identityA.DisplayName,
            Ed25519PublicKey = identityA.Ed25519PublicKey, Status = "A", CreatedAt = now, UpdatedAt = now
        });
        await _nodeA.WhitelistRepo.CreateAsync(new WhitelistEntry
        {
            NodeId = identityB.NodeId, DisplayName = identityB.DisplayName,
            Ed25519PublicKey = identityB.Ed25519PublicKey, Status = "A", CreatedAt = now, UpdatedAt = now
        });
    }

    public async Task DisposeAsync()
    {
        await _nodeA.DisposeAsync();
        await _nodeB.DisposeAsync();
    }

    [Fact]
    public async Task ApplyLogs_NameNoContent()
    {
        // A rename of a tag B has never heard of takes the "skipping" branch, which logs the tag name.
        await _nodeA.EventLogger.LogConceptTagRenameAsync(CanaryTagOld, CanaryTagNew);
        await _nodeA.EventLogger.LogConceptTagMergeAsync(CanaryMergeSource, "Canary Merge Target");
        await _nodeA.EventLogger.LogConceptTagDeleteAsync(CanaryDeleteName);
        // A folder create for a path hard-deleted for good on B takes the hard-delete gate, which
        // logs the derived entity id — the folder path.
        await _nodeA.EventLogger.LogFolderCreateAsync(new Folder
        {
            Id = Guid.NewGuid(), Path = CanaryPath, Name = CanaryFolderName,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        // A folder delete that outranks B's folder but not an article edited inside it after the
        // delete takes the "not applied" branch, which logs the folder path.
        var doomedId = Guid.NewGuid();
        var identityA = (await _nodeA.NodeRepo.GetAsync())!;
        var folderRepo = new BeeMemoryBank.Storage.Sqlite.FolderRepository(_nodeB.Factory, new CallerScopeHolder());
        await folderRepo.CreateAsync(new Folder
        {
            Id = doomedId, Path = CanaryDeletePath, Name = "Doomed", Status = "A",
            LamportTs = 1, SourceNodeId = identityA.NodeId,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        using (var conn = _nodeB.Factory.CreateConnection())
            await conn.ExecuteAsync(
                "INSERT INTO tbl_article (id, title, tree_path, status, created_at, updated_at, lamport_ts, source_node_id, folder_id) " +
                "VALUES (@id, 't', @p, 'A', @at, @at, 100, @src, (SELECT id FROM tbl_folder WHERE path = @p))",
                new { id = Guid.NewGuid().ToString(), p = CanaryDeletePath, at = DateTime.UtcNow.ToString("O"), src = identityA.NodeId.ToString() });
        await _nodeA.EventLogger.LogFolderDeleteAsync(doomedId, CanaryDeletePath, DateTime.UtcNow);

        var events = await _nodeA.EventLogRepo.GetAfterSequenceAsync(0);
        // The gate reads tbl_hard_delete_audit, so B knows the path was purged even though this
        // test never delivers the hard_delete event itself.
        using (var conn = _nodeB.Factory.CreateConnection())
            await conn.ExecuteAsync(
                "INSERT INTO tbl_hard_delete_audit (occurred_at, entity_type, entity_identifier, lamport_ts) " +
                "VALUES (@at, 'folder', @path, 10000)",
                new { at = DateTime.UtcNow.ToString("O"), path = CanaryPath });

        foreach (var evt in events.Where(e => e.EventType != EventTypes.HardDelete))
        {
            if (evt.EventType == EventTypes.FolderCreate)
                evt.EntityId = CanaryTamperedId; // not signature-covered — the tamper the mismatch log exists for
            await _nodeB.ApplyFromAsync(_nodeA, evt);
        }

        var log = string.Join("\n", _nodeB.Lines);
        log.Should().NotContain(CanaryTagOld).And.NotContain(CanaryTagNew)
            .And.NotContain(CanaryMergeSource).And.NotContain(CanaryDeleteName)
            .And.NotContain(CanaryPath).And.NotContain(CanaryDeletePath)
            .And.NotContain(CanaryFolderName).And.NotContain(CanaryTamperedId);
        // The lines are still there — they name ids instead.
        log.Should().Contain("skipping").And.Contain("hard-deleted")
            .And.Contain("concept_tag_merge").And.Contain("concept_tag_delete")
            .And.Contain("FolderDelete");
    }

    private sealed class CapturingFixture : SyncTestFixture
    {
        public List<string> Lines { get; } = new();

        protected override ILogger<EventApplier> CreateApplierLogger() => new CaptureLogger(Lines);
    }

    private sealed class CaptureLogger(List<string> lines) : ILogger<EventApplier>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lines.Add(formatter(state, exception));
            // What a real provider (console, file) appends for a call that passes an exception —
            // the canaries ride in the exception messages as much as in the templates.
            if (exception is not null)
                lines.Add(exception.ToString());
        }
    }
}
