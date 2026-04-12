using BeeMemoryBank.Core.Models;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// Tests EventLogger: verifies that events are created and signed correctly.
/// </summary>
public class EventLoggerTests : SyncTestFixture
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await InitService.InitializeAsync("NodeA", Password);
        await Session.UnlockAsync(Password);
    }

    [Fact]
    public async Task Create_WritesEventToLog()
    {
        await ArticleService.CreateAsync("Test", "/Root", [], "body");

        var events = await EventLogRepo.GetAfterSequenceAsync(0);
        events.Should().ContainSingle(e => e.EventType == EventTypes.ArticleCreate);
    }

    [Fact]
    public async Task Create_EventHasCorrectFields()
    {
        var article = await ArticleService.CreateAsync("Title", "/Work", ["tag"], "text");

        var events = await EventLogRepo.GetAfterSequenceAsync(0);
        var evt = events.Single(e => e.EventType == EventTypes.ArticleCreate);

        evt.ArticleId.Should().Be(article.Id);
        evt.LamportTs.Should().Be(article.LamportTs);
        evt.ProtocolVersion.Should().Be(1);
        evt.Signature.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Create_SignatureIsValid()
    {
        await ArticleService.CreateAsync("Article", "/Dev", [], "content");

        var events = await EventLogRepo.GetAfterSequenceAsync(0);
        var evt = events.Single();

        var identity = await NodeRepo.GetAsync();
        var sigPayload = EventSignature.BuildPayload(evt);
        BeeMemoryBank.Crypto.Ed25519Signer.Verify(identity!.Ed25519PublicKey, sigPayload, evt.Signature)
            .Should().BeTrue();
    }

    [Fact]
    public async Task Delete_WritesDeleteEvent()
    {
        var article = await ArticleService.CreateAsync("To delete", "/", [], "x");
        await ArticleService.DeleteAsync(article.Id);

        var events = await EventLogRepo.GetAfterSequenceAsync(0);
        events.Should().Contain(e => e.EventType == EventTypes.ArticleDelete && e.ArticleId == article.Id);
    }

    [Fact]
    public async Task MultipleArticles_EachHasUniqueEvent()
    {
        await ArticleService.CreateAsync("A", "/", [], "a");
        await ArticleService.CreateAsync("B", "/", [], "b");
        await ArticleService.CreateAsync("C", "/", [], "c");

        var events = await EventLogRepo.GetAfterSequenceAsync(0);
        events.Should().HaveCount(3);
        events.Select(e => e.EventId).Distinct().Should().HaveCount(3);
        events.Select(e => e.LamportTs).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task ArticleCreate_LamportTs_StrictlyIncreasing()
    {
        var a1 = await ArticleService.CreateAsync("A1", "/", [], "x");
        var a2 = await ArticleService.CreateAsync("A2", "/", [], "y");
        a2.LamportTs.Should().BeGreaterThan(a1.LamportTs);
    }
}
