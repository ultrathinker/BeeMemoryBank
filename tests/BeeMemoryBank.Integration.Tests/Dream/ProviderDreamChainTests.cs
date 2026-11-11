using System.Net;
using System.Text.Json;
using BeeMemoryBank.Api.Dream;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Providers;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Dream through a provider other than OpenRouter, and through the fallback chain (docs/ai-providers.md): the request goes to the analysis
/// model's own provider with that provider's key and wire options, the next model of the chat's fallback chain answers when every key of
/// the first failed, and every attempt - fallback attempts included - takes a call slot and is charged as the class remarks of
/// <see cref="ProviderDreamModelClient"/> say.
/// </summary>
public class ProviderDreamChainTests : IAsyncLifetime
{
    private const string Password = "dreamChainPassword";
    private readonly BmbWebApplicationFactory _factory = new();

    public async Task InitializeAsync()
    {
        await _factory.InitializeNodeAsync(password: Password);
        (await _factory.Services.GetRequiredService<SessionService>().UnlockAsync(Password)).Should().BeTrue();
    }

    public Task DisposeAsync()
    {
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    private static DreamModelRequest Request(ChatModelRow model, DreamRunBudget budget) =>
        new(model.ModelId, "the system text", "the user text", 700, budget, model.Id);

    private sealed class NoWait : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            base.CreateTimer(callback, state, dueTime == Timeout.InfiniteTimeSpan ? dueTime : TimeSpan.Zero, period);
    }

    [Fact]
    public async Task ADreamCall_GoesToTheAnalysisModelsOwnProvider_WithItsKey_AndItsWireOptions()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var zai = await ProviderTestKit.AddProviderAsync(sp, "Z.ai Coding Plan", "https://api.z.ai/api/coding/paas/v4",
            options: new ProviderWireOptions { Reasoning = ReasoningStyles.Zai, StreamUsage = false, MaxTokensField = MaxTokensFields.MaxTokens });
        await ProviderTestKit.AddKeyAsync(sp, zai.Id, "zai-coding-key-0001");
        var model = await ProviderTestKit.AddModelAsync(sp, zai.Id, "glm-4.6");
        var handler = new ScriptedProviderHandler((_, _) => ProviderTestKit.Ok("{\"verdict\":\"related\"}", 900, 60, "glm-4.6"));
        var client = new ProviderDreamModelClient(ProviderTestKit.Router(sp, handler), NullLogger<ProviderDreamModelClient>.Instance);
        var budget = new DreamRunBudget(10, 100_000);

        var reply = await client.CompleteAsync(Request(model, budget), CancellationToken.None);

        reply.Content.Should().Be("{\"verdict\":\"related\"}");
        reply.Provider.Should().Be("Z.ai Coding Plan");
        budget.ModelCalls.Should().Be(1);
        budget.TokensUsed.Should().Be(960);
        var seen = handler.Seen.Should().ContainSingle().Subject;
        seen.Url.Should().Be("https://api.z.ai/api/coding/paas/v4/chat/completions");
        seen.Key.Should().Be("zai-coding-key-0001");
        using var doc = JsonDocument.Parse(seen.Body);
        doc.RootElement.GetProperty("model").GetString().Should().Be("glm-4.6");
        doc.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(700);
        doc.RootElement.GetProperty("thinking").GetProperty("type").GetString().Should().Be("disabled", "Z.ai takes a low effort as thinking off");
        doc.RootElement.TryGetProperty("reasoning", out _).Should().BeFalse("OpenRouter's field is not sent to Z.ai");
        doc.RootElement.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
    }

    [Fact]
    public async Task WhenTheAnalysisModelsProviderFails_TheChainsNextModelAnswers_AndEveryAttemptIsCharged()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var a = await ProviderTestKit.AddProviderAsync(sp, "Provider A", "https://a.example/v1");
        var b = await ProviderTestKit.AddProviderAsync(sp, "Provider B", "https://b.example/v1");
        await ProviderTestKit.AddKeyAsync(sp, a.Id, "sk-a-1", 1);
        await ProviderTestKit.AddKeyAsync(sp, a.Id, "sk-a-2", 2);
        await ProviderTestKit.AddKeyAsync(sp, b.Id, "sk-b-1", 1);
        var modelA = await ProviderTestKit.AddModelAsync(sp, a.Id, "model-a");
        var modelB = await ProviderTestKit.AddModelAsync(sp, b.Id, "model-b");
        await sp.GetRequiredService<ChatSettingsRepository>().SetFallbackModelIdsAsync([modelB.Id]);
        var handler = new ScriptedProviderHandler((req, _) => req.RequestUri!.Host == "a.example"
            ? ProviderTestKit.Status(HttpStatusCode.ServiceUnavailable)
            : ProviderTestKit.Ok("{}", 1500, 120, "model-b"));
        var client = new ProviderDreamModelClient(ProviderTestKit.Router(sp, handler), NullLogger<ProviderDreamModelClient>.Instance);
        var budget = new DreamRunBudget(10, 100_000);

        var reply = await client.CompleteAsync(Request(modelA, budget), CancellationToken.None);

        reply.Provider.Should().Be("Provider B");
        reply.Model.Should().Be("model-b");
        handler.Seen.Select(s => s.Key).Should().Equal("sk-a-1", "sk-a-2", "sk-b-1");
        budget.ModelCalls.Should().Be(3, "each key of A and the fallback's attempt took a call slot");
        budget.TokensUsed.Should().Be(1620, "the two refusals by status cost no tokens, the answer what B reported");
    }

    [Fact]
    public async Task TheCallLimit_StopsTheChainToo_TheFallbackIsNotTriedWithoutASlot()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var a = await ProviderTestKit.AddProviderAsync(sp, "Provider A", "https://a.example/v1");
        var b = await ProviderTestKit.AddProviderAsync(sp, "Provider B", "https://b.example/v1");
        await ProviderTestKit.AddKeyAsync(sp, a.Id, "sk-a-1", 1);
        await ProviderTestKit.AddKeyAsync(sp, b.Id, "sk-b-1", 1);
        var modelA = await ProviderTestKit.AddModelAsync(sp, a.Id, "model-a");
        var modelB = await ProviderTestKit.AddModelAsync(sp, b.Id, "model-b");
        await sp.GetRequiredService<ChatSettingsRepository>().SetFallbackModelIdsAsync([modelB.Id]);
        var handler = new ScriptedProviderHandler((req, _) => req.RequestUri!.Host == "a.example"
            ? ProviderTestKit.Status(HttpStatusCode.TooManyRequests)
            : ProviderTestKit.Ok("{}"));
        var client = new ProviderDreamModelClient(ProviderTestKit.Router(sp, handler), NullLogger<ProviderDreamModelClient>.Instance);
        var budget = new DreamRunBudget(maxModelCalls: 1, maxTokens: 100_000);

        var call = () => client.CompleteAsync(Request(modelA, budget), CancellationToken.None);

        await call.Should().ThrowAsync<DreamBudgetExceededException>();
        handler.Seen.Should().ContainSingle().Which.Key.Should().Be("sk-a-1");
        budget.ModelCalls.Should().Be(1);
    }

    [Fact]
    public async Task AnUnapprovedProvider_GetsNoDreamRequest_AndThatIsASetupFailureWithoutWaits()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var p = await ProviderTestKit.AddProviderAsync(sp, "Not approved", "https://na.example/v1", approve: false);
        await ProviderTestKit.AddKeyAsync(sp, p.Id, "sk-na-1");
        var model = await ProviderTestKit.AddModelAsync(sp, p.Id, "model-na");
        var handler = new ScriptedProviderHandler((_, _) => ProviderTestKit.Ok("{}"));
        var client = new ProviderDreamModelClient(ProviderTestKit.Router(sp, handler), NullLogger<ProviderDreamModelClient>.Instance,
            time: new NoWait());
        var budget = new DreamRunBudget(10, 100_000);

        var call = () => client.CompleteAsync(Request(model, budget), CancellationToken.None);

        (await call.Should().ThrowAsync<DreamProviderException>()).Which.Message.Should().Contain("not approved");
        handler.Seen.Should().BeEmpty();
        budget.ModelCalls.Should().Be(0);
    }

    [Fact]
    public async Task TheDreamStatus_SaysWhenTheModelsProviderIsNotApproved()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var p = await ProviderTestKit.AddProviderAsync(sp, "Not approved", "https://na.example/v1", approve: false);
        await ProviderTestKit.AddKeyAsync(sp, p.Id, "sk-na-1");
        var model = await ProviderTestKit.AddModelAsync(sp, p.Id, "model-na");
        var router = sp.GetRequiredService<ModelRouter>();

        (await DreamModelResolver.ProblemAsync(router, model)).Should().Be(DreamErrorCodes.ProviderNotApproved);
        await sp.GetRequiredService<ChatProviderRepository>().ApproveAsync(p, 1);
        (await DreamModelResolver.ProblemAsync(router, model)).Should().BeNull();
        (await DreamModelResolver.LabelAsync(sp.GetRequiredService<ChatProviderRepository>(), model)).Should().Be("model-na - Not approved");
    }
}
