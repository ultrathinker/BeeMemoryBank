using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Storage.Tests;

/// <summary>
/// Pins the second layer of the B2 fix: an unlinked media upload (articleId == null) must be
/// rejected when the caller's scope carries no identity at all. Without this, an anonymous caller
/// that somehow reaches <see cref="MediaRepository.CreateAsync"/> (the HTTP gate is the first
/// layer, this is the second) could still write a row that replicates to every peer.
/// </summary>
public class MediaRepositoryUnlinkedGuardTests : IAsyncLifetime
{
    private DbConnectionFactory _factory = null!;

    public async Task InitializeAsync()
    {
        DapperConfig.Configure();
        _factory = DbConnectionFactory.CreateInMemory($"bmb_media_unlinked_guard_{Guid.NewGuid():N}");
        var runner = new MigrationRunner(_factory);
        await runner.RunMigrationsAsync();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreateAsync_UnlinkedUpload_NoIdentity_Throws()
    {
        // Deny-all scope with no MediaOwnerKey — exactly the shape an anonymous caller resolves
        // to (see CallerScopeMiddleware's "No authenticated identity" branch).
        var scopeHolder = new CallerScopeHolder
        {
            Scope = new HttpCallerScope(
                false,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/" },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        };
        var repo = new MediaRepository(_factory, scopeHolder);

        var act = () => repo.CreateAsync(new Media
        {
            Id = Guid.NewGuid(),
            ArticleId = null,
            FileName = "anon.png",
            ContentType = "image/png",
            FileSize = 100,
            EncryptedDek = new byte[32],
            DekIV = new byte[12],
            IV = new byte[12],
            Status = "A",
            LamportTs = 1,
            CreatedAt = DateTime.UtcNow
        });

        await act.Should().ThrowAsync<UnauthorizedAccessException>(
            "an unlinked media row needs a uploader identity — anonymous writes must fail closed");

        using var conn = _factory.CreateConnection();
        var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_media");
        count.Should().Be(0, "a denied write must not leave a media row behind");
    }

    [Fact]
    public async Task CreateAsync_UnlinkedUpload_SuperadminScope_Succeeds()
    {
        // System / superadmin scope: IsSuperadmin == true short-circuits the guard before the
        // identity check, matching how sync apply and other system-level paths create orphans.
        var scopeHolder = new CallerScopeHolder { Scope = SystemCallerScope.Instance };
        var repo = new MediaRepository(_factory, scopeHolder);

        await repo.CreateAsync(new Media
        {
            Id = Guid.NewGuid(),
            ArticleId = null,
            FileName = "sys.png",
            ContentType = "image/png",
            FileSize = 100,
            EncryptedDek = new byte[32],
            DekIV = new byte[12],
            IV = new byte[12],
            Status = "A",
            LamportTs = 1,
            CreatedAt = DateTime.UtcNow
        });

        using var conn = _factory.CreateConnection();
        var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_media");
        count.Should().Be(1);
    }

    [Fact]
    public async Task CreateAsync_UnlinkedUpload_NonSuperadminWithOwnerKey_Succeeds()
    {
        // Non-superadmin caller with a MediaOwnerKey: the Web upload proxy, MCP agent, chat tool
        // dispatcher, Obsidian/Bee import — every legitimate unlinked-upload path.
        var scopeHolder = new CallerScopeHolder
        {
            Scope = new HttpCallerScope(
                false,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
            {
                MediaOwnerKey = "u1"
            }
        };
        var repo = new MediaRepository(_factory, scopeHolder);

        await repo.CreateAsync(new Media
        {
            Id = Guid.NewGuid(),
            ArticleId = null,
            FileName = "alice.png",
            ContentType = "image/png",
            FileSize = 100,
            EncryptedDek = new byte[32],
            DekIV = new byte[12],
            IV = new byte[12],
            Status = "A",
            LamportTs = 1,
            CreatedAt = DateTime.UtcNow
        });

        using var conn = _factory.CreateConnection();
        var count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_media");
        count.Should().Be(1);
    }
}
