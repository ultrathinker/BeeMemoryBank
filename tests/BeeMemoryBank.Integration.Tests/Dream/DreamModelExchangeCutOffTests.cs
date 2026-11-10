using System.Text.Json.Nodes;
using BeeMemoryBank.Api.Dream;

namespace BeeMemoryBank.Integration.Tests.Dream;

/// <summary>
/// The ask-read-retry loop of folder_review and tree_review (and its texts, which pair_verdict shares) when the output limit
/// stops a reply: a model that reasons spends its cap on reasoning, and the journal has to say that, not "reply is empty".
/// </summary>
public class DreamModelExchangeCutOffTests
{
    private sealed class Log : IDreamRunLog
    {
        public List<(DreamEventKind Kind, string Text)> Lines { get; } = [];

        public Task WriteAsync(DreamEventKind kind, string text, IReadOnlyList<Guid>? articleIds = null, JsonObject? data = null,
            CancellationToken ct = default)
        {
            Lines.Add((kind, text));
            return Task.CompletedTask;
        }
    }

    private static DreamRunContext Ctx(Log log) => new()
    {
        RunId = Guid.NewGuid(),
        Trigger = DreamRunTrigger.Manual,
        Mode = DreamRunMode.Normal,
        Settings = DreamSettings.Default,
        UseModel = true,
        ModelId = "test/model",
        ModelLabel = "Test model",
        Budget = new DreamRunBudget(200, 1_500_000),
        Log = log,
        StartedAtUtc = DateTime.UtcNow,
    };

    private static (string? Value, string? Error) ReadJsonLike(string? content) =>
        content != null && content.TrimStart().StartsWith('{') && content.TrimEnd().EndsWith('}') ? ("read", null) : (null, "The reply holds no JSON object.");

    [Fact]
    public async Task EveryReplyCutOff_IsAnErrorNamedCutOff_AfterOneRetryThatSaysSo()
    {
        var log = new Log();
        var model = new FakeDreamModelClient().Enqueue("", cutOff: true).Enqueue("{\"suggestions\":[", cutOff: true);

        var outcome = await DreamModelExchange.AskAsync<string>(model, Ctx(log), "system", "user text", 5000, "folder_review", [], 1,
            ReadJsonLike, CancellationToken.None);

        outcome.Value.Should().BeNull();
        outcome.Error.Should().Be(DreamJsonReader.CutOffError).And.Be("reply cut off by the output limit");
        model.CallCount.Should().Be(2);
        model.Requests.Should().OnlyContain(r => r.MaxOutputTokens == 5000);
        model.Requests[1].User.Should().StartWith("user text").And.Contain("cut off by the output limit").And.Contain("Think briefly");
        log.Lines.Where(l => l.Kind == DreamEventKind.Error).Select(l => l.Text).Should()
            .Equal("folder_review reply rejected: reply cut off by the output limit", "folder_review reply rejected: reply cut off by the output limit");
        DreamModelExchange.FailureText(outcome.Error).Should().Be("The model reply was cut off by the output limit.");
    }

    [Fact]
    public async Task ACutOffReplyFollowedByAGoodOne_GivesTheValue_AndSumsTheTokens()
    {
        var log = new Log();
        var model = new FakeDreamModelClient().Enqueue("", 900, 6000, cutOff: true).Enqueue("{}", 900, 100);

        var outcome = await DreamModelExchange.AskAsync<string>(model, Ctx(log), "system", "user text", 6000, "tree_review", [], 1,
            ReadJsonLike, CancellationToken.None);

        outcome.Value.Should().Be("read");
        outcome.PromptTokens.Should().Be(1800);
        outcome.CompletionTokens.Should().Be(6100);
    }

    [Fact]
    public async Task ACompleteObjectIsUsed_EvenWhenTheProviderFlaggedItCutOff()
    {
        var model = new FakeDreamModelClient().Enqueue("{\"suggestions\":[]}", cutOff: true);

        var outcome = await DreamModelExchange.AskAsync<string>(model, Ctx(new Log()), "system", "user text", 5000, "folder_review", [], 1,
            ReadJsonLike, CancellationToken.None);

        outcome.Value.Should().Be("read");
        model.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task AnOrdinarilyBadReply_KeepsTheOldRetryTextAndTheSchemaError()
    {
        var log = new Log();
        var model = new FakeDreamModelClient().Enqueue("nothing here").Enqueue("still nothing");

        var outcome = await DreamModelExchange.AskAsync<string>(model, Ctx(log), "system", "user text", 5000, "tree_review", [], 1,
            ReadJsonLike, CancellationToken.None);

        outcome.Error.Should().Be("The reply holds no JSON object.");
        model.Requests[1].User.Should().Be(
            "user text\n\nYour previous reply was rejected: The reply holds no JSON object. Answer again with one JSON object that follows the schema in the instructions.");
        DreamModelExchange.FailureText(outcome.Error).Should().Be("The model reply did not match the schema: The reply holds no JSON object.");
    }
}
