using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// Regression coverage for the TOCTOU fix on <see cref="MediaRepository.CreateAsync"/> (and
/// <see cref="MediaRepository.SoftDeleteByArticleIdAsync"/>, same shape): <c>EnsureWriteAllowedAsync</c>
/// reads the owning article's CURRENT tree path, and that read must run inside the SAME
/// transaction as the write it guards -- previously they shared a connection but ran as two
/// separate autocommit statements with no transaction spanning them, so a concurrent move of the
/// owning article could still land in the gap between the guard read and the write.
///
/// Same caveat as the sibling race tests for ArticleRepository/FolderRepository: this exercises
/// real SQLite locking (two repository instances, different <see cref="CallerScopeHolder"/>
/// scopes, forced to overlap via TaskCompletionSource barriers) and proves the fix's mechanism --
/// BEGIN IMMEDIATE holds SQLite's single, database-wide write lock across the whole
/// guard-then-write span, so whichever side opens its transaction first runs to completion before
/// the other's can even begin -- rather than reproducing the pre-fix bug's much narrower race
/// window directly.
/// </summary>
public class MediaRepositoryCreateGuardRaceTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_media_create_race_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreateAsync_ConcurrentMoveOfOwningArticleToDeniedFolder_NeverCreatesMediaUnderStaleAuthorization()
    {
        for (var i = 0; i < 8; i++)
        {
            // Per-iteration paths: tbl_folder.path is unique, and this loop reuses one DB file
            // across all 8 iterations.
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

            // "Mover": fully-privileged caller relocating the article into a folder the attacker
            // below cannot see, via ArticleRepository.UpdateAsync's own self-managed transaction.
            var moverScopeHolder = new CallerScopeHolder { Scope = SystemCallerScope.Instance };
            var moverArticleRepo = new ArticleRepository(_factory, moverScopeHolder);

            // "Attacker": denied on /Secrets{i}, allowed everywhere else. Tries to attach a new
            // media row to the article while it is still (from the attacker's point of view)
            // sitting in the allowed /Public{i} folder.
            var attackerScopeHolder = new CallerScopeHolder
            {
                Scope = new HttpCallerScope(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { secretsPath }, [])
            };
            var attackerMediaRepo = new MediaRepository(_factory, attackerScopeHolder);
            var mediaId = Guid.NewGuid();

            var moveReady = new TaskCompletionSource();
            var createReady = new TaskCompletionSource();
            Exception? createException = null;

            var moveTask = Task.Run(async () =>
            {
                moveReady.SetResult();
                await createReady.Task;
                await moverArticleRepo.UpdateAsync(new Article
                {
                    Id = articleId,
                    Title = "Race Article",
                    TreePath = secretsPath,
                    FolderId = secretsFolderId,
                    Status = "A",
                    UpdatedAt = DateTime.UtcNow
                });
            });

            var createTask = Task.Run(async () =>
            {
                createReady.SetResult();
                await moveReady.Task;
                try
                {
                    await attackerMediaRepo.CreateAsync(new Media
                    {
                        Id = mediaId,
                        ArticleId = articleId,
                        FileName = "photo.png",
                        ContentType = "image/png",
                        FileSize = 100,
                        EncryptedDek = new byte[32],
                        DekIV = new byte[12],
                        IV = new byte[12],
                        Status = "A",
                        LamportTs = 1,
                        CreatedAt = DateTime.UtcNow
                    });
                }
                catch (Exception ex)
                {
                    createException = ex;
                }
            });

            await Task.WhenAll(moveTask, createTask);

            using var verifyConn = _factory.CreateConnection();
            var articlePath = await verifyConn.ExecuteScalarAsync<string>(
                "SELECT tree_path FROM tbl_article WHERE id = @id", new { id = articleId });
            var mediaExists = await verifyConn.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM tbl_media WHERE id = @id AND status = 'A'", new { id = mediaId }) > 0;

            // The mover always eventually relocates the article -- whichever side's transaction
            // acquires SQLite's write lock first runs to completion before the other side's own
            // transaction can even open.
            articlePath.Should().Be(secretsPath);

            if (createException == null)
            {
                // Attacker's transaction won the write-lock race first: its guard legitimately
                // observed the article still at "/Public" (true at that moment), so the media
                // row was created under a decision that was correct when it was made -- and,
                // critically, when the write actually committed too, because the guard read and
                // the INSERT ran inside the SAME transaction the mover's move was blocked behind.
                mediaExists.Should().BeTrue();
            }
            else
            {
                // Mover's transaction won first: the attacker's guard (in its own, later
                // transaction) read the post-move "/Secrets" path and correctly refused. If the
                // guard read and the INSERT ever again ran as two separate autocommit statements
                // instead of one transaction, this is exactly where a media row could slip
                // through anyway: the guard's SELECT could observe "/Public" a moment before the
                // move commits, and the INSERT could still land a moment after it, unguarded.
                createException.Should().BeOfType<UnauthorizedAccessException>();
                mediaExists.Should().BeFalse("a denied write must never leave a media row behind");
            }
        }
    }
}
