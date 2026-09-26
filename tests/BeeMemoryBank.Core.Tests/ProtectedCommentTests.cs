using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Storage.Sqlite;
using Dapper;

namespace BeeMemoryBank.Core.Tests;

/// <summary>
/// Comments of a protected article sit behind its passphrase like the body (BMB-36): they used to be
/// readable by anyone who could open the vault. Also: comments an Android build stored in plaintext
/// get sealed, and plaintext comment events leave the log (BMB-35 follow-up).
/// </summary>
public class ProtectedCommentTests : TestFixture
{
    private const string Phrase = "Qa-Phrase-1";
    private CommentRepository _commentRepo = null!;
    private CommentService _comments = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _commentRepo = new CommentRepository(Factory, ScopeHolder);
        _comments = new CommentService(_commentRepo, new ArticleBodyRepository(Factory), Session,
            new NullEventLogger(), ArticleRepo, new EventLogRepository(Factory));
        await InitService.InitializeAsync("admin", "TestNode", "password");
        await Session.UnlockAsync("password");
    }

    private async Task<Guid> ProtectedArticleAsync()
    {
        var article = await ArticleService.CreateAsync("secret", "/", [], "body");
        await ArticleService.ProtectAsync(article.Id, Phrase, "hint");
        return article.Id;
    }

    [Fact]
    public async Task Commenting_a_protected_article_needs_its_passphrase()
    {
        var id = await ProtectedArticleAsync();
        var act = () => _comments.CreateAsync(id, "no phrase");
        await act.Should().ThrowAsync<ArticleLockedException>();
    }

    [Fact]
    public async Task A_protected_articles_comment_opens_only_with_the_passphrase()
    {
        var id = await ProtectedArticleAsync();
        var comment = await _comments.CreateAsync(id, "the real text", Phrase);

        (await _comments.DecryptTextAsync(comment)).Should().Be(CommentService.LockedText,
            "the vault key alone must not open it");
        (await _comments.DecryptTextAsync(comment, "wrong")).Should().Be(CommentService.LockedText);
        (await _comments.DecryptTextAsync(comment, Phrase)).Should().Be("the real text");
    }

    [Fact]
    public async Task Protecting_and_unprotecting_reseal_existing_comments_and_keep_their_time()
    {
        var article = await ArticleService.CreateAsync("open", "/", [], "body");
        var original = await _comments.CreateAsync(article.Id, "written while open");

        await ArticleService.ProtectAsync(article.Id, Phrase, null);
        (await _comments.ReprotectAsync(article.Id, null, Phrase)).Should().Be(1);
        var locked = (await _comments.GetOpenedByArticleAsync(article.Id)).Single();
        locked.Locked.Should().BeTrue();
        locked.Comment.CreatedAt.Should().BeCloseTo(original.CreatedAt, TimeSpan.FromSeconds(1));
        (await _comments.GetOpenedByArticleAsync(article.Id, Phrase)).Single().Text.Should().Be("written while open");

        await ArticleService.UnprotectAsync(article.Id, Phrase);
        await _comments.ReprotectAsync(article.Id, Phrase, null);
        var reopened = (await _comments.GetOpenedByArticleAsync(article.Id)).Single();
        reopened.Locked.Should().BeFalse();
        reopened.Text.Should().Be("written while open");
    }

    [Fact]
    public async Task Legacy_plaintext_comments_are_sealed_and_their_events_removed()
    {
        var article = await ArticleService.CreateAsync("a", "/", [], "body");
        var plain = await _commentRepo.CreateAsync(article.Id, "typed on the phone");
        using (var conn = Factory.CreateConnection())
            await conn.ExecuteAsync(
                @"INSERT INTO tbl_event (event_id, node_id, lamport_ts, event_type, article_id, payload, signature, protocol_version, created_at)
                  VALUES (@eid, @nid, 1, 'comment_create', @aid, @payload, x'00', 1, @now)",
                new
                {
                    eid = Guid.NewGuid().ToString(), nid = Guid.NewGuid().ToString(), aid = article.Id.ToString(),
                    payload = $"{{\"comment_id\":\"{plain.CommentId}\",\"article_id\":\"{article.Id}\",\"text\":\"typed on the phone\",\"encrypted\":false}}",
                    now = DateTime.UtcNow.ToString("o")
                });

        (await _comments.SealLegacyPlaintextAsync()).Should().Be(1);

        var rows = await _commentRepo.GetByArticleIdAsync(article.Id);
        rows.Should().ContainSingle().Which.Encrypted.Should().BeTrue();
        rows[0].Text.Should().BeEmpty("the stored row no longer holds the text in clear");
        (await _comments.DecryptTextAsync(rows[0])).Should().Be("typed on the phone");
        using (var conn = Factory.CreateConnection())
            (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM tbl_event WHERE payload LIKE '%typed on the phone%'"))
                .Should().Be(0, "the plaintext event is gone from the log");
    }
}
