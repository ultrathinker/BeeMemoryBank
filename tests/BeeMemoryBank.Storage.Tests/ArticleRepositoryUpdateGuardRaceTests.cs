using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// Regression coverage for the TOCTOU fix on <see cref="ArticleRepository.UpdateAsync"/>'s
/// pre-update stored-path guard: the guard (which reads the article's CURRENT path) and the
/// UPDATE it protects must run inside one shared BEGIN IMMEDIATE transaction, not on two
/// separate connections/transactions -- see the SECURITY comment on UpdateAsync's self-managed
/// branch for the full reasoning.
///
/// <see cref="UpdateAsync_ConcurrentMoveToDeniedFolder_NeverLeavesArticleRevertedToStalePath"/> is
/// a REAL concurrency test: two repository instances (different <see cref="CallerScopeHolder"/>
/// scopes, same underlying SQLite file) race against the SAME article row, forced to overlap via
/// TaskCompletionSource barriers. It cannot reproduce the pre-fix bug directly -- that race window
/// was a handful of CPU instructions between two Dapper calls, far too narrow for Task scheduling
/// to hit reliably without adding an artificial delay hook to production code, which this fix
/// deliberately does not do. What it DOES prove, under real SQLite locking (not a mock), is the
/// mechanism the fix relies on: BEGIN IMMEDIATE takes the write lock the instant a self-managed
/// UpdateAsync call opens its transaction, so a concurrent writer to the same row cannot land
/// between that call's guard read and its own write -- whichever caller acquires the lock first
/// runs its entire guard-then-write to completion before the other one's guard query can even
/// see the row. The assertion below is the one invariant that holds under EVERY interleaving only
/// because of that -- see its comment for why.
/// </summary>
public class ArticleRepositoryUpdateGuardRaceTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;
    private CallerScopeHolder _scopeHolder = null!;
    private ArticleRepository _repo = null!;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_article_update_race_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();
        _scopeHolder = new CallerScopeHolder();
        _repo = new ArticleRepository(_factory, _scopeHolder);
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task UpdateAsync_ConcurrentMoveToDeniedFolder_NeverLeavesArticleRevertedToStalePath()
    {
        // Repeat a handful of times: which side wins the write-lock race is nondeterministic
        // (that's the point), so a single run could get lucky and not actually overlap.
        for (var i = 0; i < 8; i++)
        {
            // Per-iteration paths: tbl_folder.path is unique, and this loop reuses one DB file
            // across all 8 iterations, so each iteration needs its own folders/article.
            var publicPath = $"/Public{i}";
            var secretsPath = $"/Secrets{i}";
            var publicFolderId = Guid.NewGuid();
            var secretsFolderId = Guid.NewGuid();
            var articleId = Guid.NewGuid();
            var now = DateTime.UtcNow.ToString("o");

            using (var conn = _factory.CreateConnection())
            {
                await conn.ExecuteAsync(
                    "INSERT INTO tbl_folder (id, path, name, status, created_at, updated_at) VALUES (@id, @publicPath, 'Public', 'A', @now, @now)",
                    new { id = publicFolderId, publicPath, now });
                await conn.ExecuteAsync(
                    "INSERT INTO tbl_folder (id, path, name, status, created_at, updated_at) VALUES (@id, @secretsPath, 'Secrets', 'A', @now, @now)",
                    new { id = secretsFolderId, secretsPath, now });
                await conn.ExecuteAsync(
                    @"INSERT INTO tbl_article (id, title, tree_path, folder_id, status, created_at, updated_at)
                      VALUES (@id, 'Race Article', @publicPath, @publicFolderId, 'A', @now, @now)",
                    new { id = articleId, publicPath, publicFolderId, now });
            }

            // "Mover": a fully-privileged caller relocating the article into a folder the
            // attacker below cannot see. Own CallerScopeHolder so its ambient scope can never
            // bleed into the attacker's repository instance despite sharing the same DB file.
            var moverScopeHolder = new CallerScopeHolder { Scope = SystemCallerScope.Instance };
            var moverRepo = new ArticleRepository(_factory, moverScopeHolder);

            // "Attacker": denied on /Secrets{i}, allowed everywhere else. Writes the article back
            // with TreePath = "/Public{i}" -- the path it could legitimately see at some point.
            var attackerScopeHolder = new CallerScopeHolder
            {
                Scope = new HttpCallerScope(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { secretsPath }, [])
            };
            var attackerRepo = new ArticleRepository(_factory, attackerScopeHolder);

            var moveReady = new TaskCompletionSource();
            var attackReady = new TaskCompletionSource();
            Exception? attackException = null;

            var moveTask = Task.Run(async () =>
            {
                moveReady.SetResult();
                await attackReady.Task;
                await moverRepo.UpdateAsync(new Article
                {
                    Id = articleId,
                    Title = "Race Article",
                    TreePath = secretsPath,
                    FolderId = secretsFolderId,
                    Status = "A",
                    UpdatedAt = DateTime.UtcNow
                });
            });

            var attackTask = Task.Run(async () =>
            {
                attackReady.SetResult();
                await moveReady.Task;
                try
                {
                    await attackerRepo.UpdateAsync(new Article
                    {
                        Id = articleId,
                        Title = "Race Article",
                        TreePath = publicPath,
                        FolderId = publicFolderId,
                        Status = "A",
                        UpdatedAt = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    attackException = ex;
                }
            });

            await Task.WhenAll(moveTask, attackTask);

            using var verifyConn = _factory.CreateConnection();
            var finalPath = await verifyConn.ExecuteScalarAsync<string>(
                "SELECT tree_path FROM tbl_article WHERE id = @id", new { id = articleId });

            // Under both admissible serialized orders, the mover's relocation to /Secrets is the
            // final word:
            //   * attacker's transaction wins the write-lock race first -> its guard legitimately
            //     saw stored path "/Public" (true at that moment), so it writes "/Public" and
            //     returns normally -- but the mover's transaction, which was BLOCKED on the same
            //     write lock, then runs afterward and moves the row to "/Secrets" regardless.
            //   * mover's transaction wins first -> article becomes "/Secrets", then the
            //     attacker's guard (running afterward, in its own transaction) reads that fresh
            //     value and throws UnauthorizedAccessException before ever touching the row.
            // In NEITHER order does the attacker's write get to apply AFTER the move while still
            // holding the pre-move authorization decision -- which is exactly what "guard and
            // write share one connection but not one transaction" used to allow: the attacker's
            // guard could read the stale "/Public" verdict, then its own UPDATE could land after
            // the mover committed, silently reverting tree_path back to "/Public" and undoing the
            // move under authorization that was already invalid by write time. If that regressed,
            // this assertion is exactly what would start failing: finalPath would come back
            // publicPath on some iteration.
            finalPath.Should().Be(secretsPath);

            if (attackException != null)
            {
                attackException.Should().BeOfType<UnauthorizedAccessException>();
            }
        }
    }
}
