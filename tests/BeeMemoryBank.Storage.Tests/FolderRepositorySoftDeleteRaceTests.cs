using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// Regression coverage for the TOCTOU fix on
/// <see cref="FolderRepository.SoftDeleteByPathPrefixAsync"/>: the descendant-ACL scan
/// (<c>ThrowIfAnyDescendantWriteDeniedCoreAsync</c>) and the cascading UPDATE it guards must run
/// inside one shared BEGIN IMMEDIATE transaction, not on two separate connections. See the
/// SECURITY comment on <c>SoftDeleteByPathPrefixAsync</c> for the full reasoning.
///
/// Same caveat as <see cref="ArticleRepositoryUpdateGuardRaceTests"/>: this is a REAL concurrency
/// test against a real SQLite file (two <see cref="FolderRepository"/> instances, different
/// <see cref="CallerScopeHolder"/> scopes, forced to overlap via TaskCompletionSource barriers),
/// but it cannot reproduce the pre-fix bug directly -- that required a create/move to land in a
/// microscopic gap between two Dapper calls that plain Task scheduling won't reliably hit without
/// an artificial delay hook in production code, which this fix deliberately avoids adding. What it
/// DOES prove, under real locking, is the fix's mechanism: BEGIN IMMEDIATE takes SQLite's write
/// lock for the whole guard-scan-then-UPDATE span, so a concurrent folder create under the prefix
/// cannot land between the scan and the write -- whichever side acquires the lock first runs to
/// completion (commit, or throw and roll back) before the other side's own transaction can even
/// begin. See the assertion's comment for the invariant that only holds because of that.
/// </summary>
public class FolderRepositorySoftDeleteRaceTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_folder_delete_race_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SoftDeleteByPathPrefixAsync_ConcurrentCreateOfDeniedDescendant_NeverSoftDeletesIt()
    {
        for (var i = 0; i < 8; i++)
        {
            // Per-iteration paths: tbl_folder.path is unique, and this loop reuses one DB file
            // across all 8 iterations.
            var workPath = $"/Work{i}";
            var publicPath = $"{workPath}/Public";
            var secretPath = $"{workPath}/Secret";
            var now = DateTime.UtcNow;
            var workId = Guid.NewGuid();
            var publicId = Guid.NewGuid();

            using (var conn = _factory.CreateConnection())
            {
                var nowStr = now.ToString("o");
                await conn.ExecuteAsync(
                    "INSERT INTO tbl_folder (id, path, name, status, created_at, updated_at) VALUES (@id, @workPath, @workName, 'A', @now, @now)",
                    new { id = workId, workPath, workName = $"Work{i}", now = nowStr });
                await conn.ExecuteAsync(
                    "INSERT INTO tbl_folder (id, path, name, parent_path, status, created_at, updated_at) VALUES (@id, @publicPath, 'Public', @workPath, 'A', @now, @now)",
                    new { id = publicId, publicPath, workPath, now = nowStr });
            }

            // "Attacker": allowed on /Work{i} in general, but explicitly denied on
            // /Work{i}/Secret -- requests a cascading delete of the whole /Work{i} subtree.
            var attackerScopeHolder = new CallerScopeHolder
            {
                Scope = new HttpCallerScope(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { secretPath }, [])
            };
            var attackerRepo = new FolderRepository(_factory, attackerScopeHolder);

            // "Creator": a fully-privileged caller creating the restricted descendant under the
            // same prefix while the cascading delete is in flight.
            var creatorScopeHolder = new CallerScopeHolder { Scope = SystemCallerScope.Instance };
            var creatorRepo = new FolderRepository(_factory, creatorScopeHolder);

            var deleteReady = new TaskCompletionSource();
            var createReady = new TaskCompletionSource();
            Exception? deleteException = null;

            var deleteTask = Task.Run(async () =>
            {
                deleteReady.SetResult();
                await createReady.Task;
                try
                {
                    await attackerRepo.SoftDeleteByPathPrefixAsync(workPath, DateTime.UtcNow);
                }
                catch (Exception ex)
                {
                    deleteException = ex;
                }
            });

            var createTask = Task.Run(async () =>
            {
                createReady.SetResult();
                await deleteReady.Task;
                await creatorRepo.CreateAsync(new Folder
                {
                    Id = Guid.NewGuid(),
                    Path = secretPath,
                    Name = "Secret",
                    ParentPath = workPath,
                    Status = "A",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            });

            await Task.WhenAll(deleteTask, createTask);

            using var verifyConn = _factory.CreateConnection();
            var secretStatus = await verifyConn.ExecuteScalarAsync<string?>(
                "SELECT status FROM tbl_folder WHERE path = @secretPath", new { secretPath });
            var publicStatus = await verifyConn.ExecuteScalarAsync<string>(
                "SELECT status FROM tbl_folder WHERE id = @id", new { id = publicId });

            // The one invariant that holds under EVERY interleaving, and only because the scan
            // and the cascading UPDATE now share a single BEGIN IMMEDIATE transaction:
            // /Work/Secret must NEVER end up soft-deleted ('D'), because the attacker is denied
            // on it no matter when it was created relative to the cascade.
            //   * creator wins the write-lock race first -> /Work/Secret exists by the time the
            //     attacker's transaction opens, the descendant scan (running in that SAME
            //     transaction as the UPDATE) sees it, and the whole cascade is refused before any
            //     row is touched -- /Work/Public must therefore also survive untouched ('A').
            //   * attacker wins first -> /Work/Secret does not exist yet at scan time, so it is
            //     simply not part of the descendant set the cascade authorizes against; the
            //     cascade proceeds and deletes /Work/Public (which the attacker IS allowed to
            //     touch), and /Work/Secret is created afterward, once the attacker's transaction
            //     has already released the write lock.
            // If the scan ever again ran on a connection separate from the UPDATE, this is
            // exactly what would start failing: a create that lands between the (now-stale) scan
            // and the UPDATE could get swept up by the UPDATE's plain path-prefix LIKE match and
            // soft-deleted despite never having been authorized.
            secretStatus.Should().Be("A", "a folder denied to the caller must never be soft-deleted by their cascade, regardless of when it was created relative to it");

            if (deleteException == null)
            {
                publicStatus.Should().Be("D", "the attacker's own allowed descendant should have been deleted when the cascade succeeded");
            }
            else
            {
                deleteException.Should().BeOfType<UnauthorizedAccessException>();
                publicStatus.Should().Be("A", "the whole cascade must roll back, not partially apply, when a denied descendant is found");
            }
        }
    }
}
