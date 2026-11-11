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
/// Dream through the native Anthropic Messages and Gemini protocols: its output limit, its low effort the protocol's way (Anthropic:
/// <c>output_config.effort</c> and no temperature, which that API refuses; Gemini: <c>thinkingLevel</c> and temperature 0), the budget
/// charged with the usage the provider reports (Anthropic's cache counted in the prompt, Gemini's thoughts in the completion), a cut-off
/// seen as one, and a status refusal on the way to the chain's next model costing a call and no tokens.
/// </summary>
public class NativeProviderDreamTests : IAsyncLifetime
{
    private const string Password = "nativeDreamPassword";
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

    private static HttpResponseMessage AnthropicAnswer(string text, string stop = "end_turn") =>
        ProviderTestKit.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            type = "message", role = "assistant", model = "claude-opus-5-5", content = new[] { new { type = "text", text } }, stop_reason = stop,
            usage = new { input_tokens = 900, cache_creation_input_tokens = 0, cache_read_input_tokens = 100, output_tokens = 60 },
        }));

    private static HttpResponseMessage GeminiAnswer(string text, string finish = "STOP") =>
        ProviderTestKit.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            candidates = new[] { new { content = new { role = "model", parts = new[] { new { text } } }, finishReason = finish } },
            usageMetadata = new { promptTokenCount = 900, candidatesTokenCount = 40, thoughtsTokenCount = 20 },
            modelVersion = "gemini-3-pro",
        }));

    private static async Task<ChatModelRow> ModelAsync(IServiceProvider sp, string protocol, string name, string baseUrl, string model, string key)
    {
        var provider = await ProviderTestKit.AddProviderAsync(sp, name, baseUrl, protocol: protocol);
        await ProviderTestKit.AddKeyAsync(sp, provider.Id, key);
        return await ProviderTestKit.AddModelAsync(sp, provider.Id, model);
    }

    [Fact]
    public async Task ThroughAnthropic_DreamSendsItsLimitAndALowEffort_NoTemperature_AndIsChargedTheReportedUsage()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var model = await ModelAsync(sp, ProviderProtocols.AnthropicMessages, "Anthropic", "https://api.anthropic.com/v1", "claude-opus-5-5", "sk-ant-dream");
        var handler = new ScriptedProviderHandler((_, _) => AnthropicAnswer("{\"verdict\":\"related\"}"));
        var client = new ProviderDreamModelClient(ProviderTestKit.Router(sp, handler), NullLogger<ProviderDreamModelClient>.Instance);
        var budget = new DreamRunBudget(10, 100_000);

        var reply = await client.CompleteAsync(Request(model, budget), CancellationToken.None);

        reply.Content.Should().Be("{\"verdict\":\"related\"}");
        reply.Provider.Should().Be("Anthropic");
        reply.CutOff.Should().BeFalse();
        budget.ModelCalls.Should().Be(1);
        budget.TokensUsed.Should().Be(1060, "prompt = input + cache read, plus the output");
        var seen = handler.Seen.Single();
        seen.Url.Should().Be("https://api.anthropic.com/v1/messages");
        seen.Key.Should().Be("sk-ant-dream");
        using var doc = JsonDocument.Parse(seen.Body);
        var body = doc.RootElement;
        body.GetProperty("max_tokens").GetInt32().Should().Be(700);
        body.GetProperty("output_config").GetProperty("effort").GetString().Should().Be(ProviderDreamModelClient.ReasoningEffort);
        body.TryGetProperty("temperature", out _).Should().BeFalse("Dream's temperature 0 is dropped for this protocol");
        body.GetProperty("system")[0].GetProperty("text").GetString().Should().Be("the system text");
        body.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString().Should().Be("the user text");
    }

    [Fact]
    public async Task ThroughGemini_DreamSendsItsLimitTemperatureZeroAndALowThinkingLevel_AndIsChargedTheReportedUsage()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var model = await ModelAsync(sp, ProviderProtocols.Gemini, "Google Gemini", "https://generativelanguage.googleapis.com/v1beta", "gemini-3-pro", "AIza-dream");
        var handler = new ScriptedProviderHandler((_, _) => GeminiAnswer("{}"));
        var client = new ProviderDreamModelClient(ProviderTestKit.Router(sp, handler), NullLogger<ProviderDreamModelClient>.Instance);
        var budget = new DreamRunBudget(10, 100_000);

        var reply = await client.CompleteAsync(Request(model, budget), CancellationToken.None);

        reply.Provider.Should().Be("Google Gemini");
        budget.ModelCalls.Should().Be(1);
        budget.TokensUsed.Should().Be(960, "prompt + candidates + thoughts");
        var seen = handler.Seen.Single();
        seen.Url.Should().Be("https://generativelanguage.googleapis.com/v1beta/models/gemini-3-pro:generateContent");
        seen.Key.Should().Be("AIza-dream");
        using var doc = JsonDocument.Parse(seen.Body);
        var config = doc.RootElement.GetProperty("generationConfig");
        config.GetProperty("maxOutputTokens").GetInt32().Should().Be(700);
        config.GetProperty("temperature").GetDouble().Should().Be(0.0);
        config.GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString().Should().Be("low");
        doc.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString().Should().Be("the system text");
    }

    [Fact]
    public async Task ACutOffAnswer_IsSeenAsOne_InBothProtocols()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var claude = await ModelAsync(sp, ProviderProtocols.AnthropicMessages, "Anthropic", "https://api.anthropic.com/v1", "claude-opus-5-5", "sk-ant-dream");
        var gemini = await ModelAsync(sp, ProviderProtocols.Gemini, "Google Gemini", "https://generativelanguage.googleapis.com/v1beta", "gemini-3-pro", "AIza-dream");
        var handler = new ScriptedProviderHandler((req, _) => req.RequestUri!.Host == "api.anthropic.com"
            ? AnthropicAnswer("{\"verd", stop: "max_tokens")
            : GeminiAnswer("{\"verd", finish: "MAX_TOKENS"));
        var client = new ProviderDreamModelClient(ProviderTestKit.Router(sp, handler), NullLogger<ProviderDreamModelClient>.Instance);

        (await client.CompleteAsync(Request(claude, new DreamRunBudget(10, 100_000)), CancellationToken.None)).CutOff.Should().BeTrue();
        (await client.CompleteAsync(Request(gemini, new DreamRunBudget(10, 100_000)), CancellationToken.None)).CutOff.Should().BeTrue();
    }

    [Fact]
    public async Task AnOverloadedAnthropic_MovesDreamToTheChainsGeminiModel_AndTheRefusalCostsACallButNoTokens()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var claude = await ModelAsync(sp, ProviderProtocols.AnthropicMessages, "Anthropic", "https://api.anthropic.com/v1", "claude-opus-5-5", "sk-ant-dream");
        var gemini = await ModelAsync(sp, ProviderProtocols.Gemini, "Google Gemini", "https://generativelanguage.googleapis.com/v1beta", "gemini-3-pro", "AIza-dream");
        await sp.GetRequiredService<ChatSettingsRepository>().SetFallbackModelIdsAsync([gemini.Id]);
        var handler = new ScriptedProviderHandler((req, _) => req.RequestUri!.Host == "api.anthropic.com"
            ? ProviderTestKit.Json((HttpStatusCode)529, "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}")
            : GeminiAnswer("{}"));
        var client = new ProviderDreamModelClient(ProviderTestKit.Router(sp, handler), NullLogger<ProviderDreamModelClient>.Instance);
        var budget = new DreamRunBudget(10, 100_000);

        var reply = await client.CompleteAsync(Request(claude, budget), CancellationToken.None);

        reply.Provider.Should().Be("Google Gemini");
        handler.Seen.Select(s => s.Key).Should().Equal("sk-ant-dream", "AIza-dream");
        budget.ModelCalls.Should().Be(2);
        budget.TokensUsed.Should().Be(960, "the 529 cost no tokens, the answer what Gemini reported");
    }
}
