using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services.Providers;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// What differs between providers of the OpenAI format is in their wire options (<see cref="ProviderWireOptions"/>); these tests pin each
/// option to the request it produces, and check that a provider which reports no usage, or streams without the space after "data:", breaks
/// nothing.
/// </summary>
public class OpenAiChatClientWireOptionsTests
{
    private static async Task<JsonElement> DreamStyleRequestAsync(ProviderWireOptions options)
    {
        var handler = new ScriptedProviderHandler((_, _) => ProviderTestKit.Ok("{}"));
        var client = ProviderTestKit.Client(handler);
        await client.CompleteAsync(new ModelCall(ProviderEgressTarget.ForTest("https://api.example.com/v1", options: options), "k", "m"),
            [new ChatRoleMessage("user", "x")], new CompletionOptions(500, 0.0, "low"), CancellationToken.None);
        return JsonDocument.Parse(handler.Seen.Single().Body).RootElement.Clone();
    }

    private static async Task<JsonElement> StreamRequestAsync(ProviderWireOptions options)
    {
        var handler = new ScriptedProviderHandler((_, _) => ProviderTestKit.Sse("m", "hi"));
        var client = ProviderTestKit.Client(handler);
        await client.StreamWithToolsAsync(new ModelCall(ProviderEgressTarget.ForTest("https://api.example.com/v1", options: options), "k", "m"),
            [new ChatToolMessage { Role = "user", Content = "x" }], null, (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        return JsonDocument.Parse(handler.Seen.Single().Body).RootElement.Clone();
    }

    [Theory]
    [InlineData(1, ReasoningStyles.OpenRouter, "reasoning")]
    [InlineData(2, ReasoningStyles.OpenAi, "reasoning_effort")]
    [InlineData(3, ReasoningStyles.Zai, "thinking")]
    [InlineData(4, ReasoningStyles.None, null)]
    public async Task TheReasoningEffort_IsSentTheProvidersWay_OrNotAtAll(int row, string style, string? field)
    {
        var body = await DreamStyleRequestAsync(new ProviderWireOptions { Reasoning = style });
        var present = new[] { "reasoning", "reasoning_effort", "thinking" }.Where(f => body.TryGetProperty(f, out _)).ToList();
        if (field is null) present.Should().BeEmpty($"row {row}");
        else present.Should().Equal([field], $"row {row}");
        if (style == ReasoningStyles.OpenRouter) body.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("low");
        if (style == ReasoningStyles.OpenAi) body.GetProperty("reasoning_effort").GetString().Should().Be("low");
        if (style == ReasoningStyles.Zai) body.GetProperty("thinking").GetProperty("type").GetString().Should().Be("disabled");
    }

    [Fact]
    public async Task TheOutputLimit_GoesInTheProvidersField()
    {
        var classic = await DreamStyleRequestAsync(new ProviderWireOptions { MaxTokensField = MaxTokensFields.MaxTokens });
        classic.GetProperty("max_tokens").GetInt32().Should().Be(500);
        classic.TryGetProperty("max_completion_tokens", out _).Should().BeFalse();

        var openAi = await DreamStyleRequestAsync(new ProviderWireOptions { MaxTokensField = MaxTokensFields.MaxCompletionTokens });
        openAi.GetProperty("max_completion_tokens").GetInt32().Should().Be(500);
        openAi.TryGetProperty("max_tokens", out _).Should().BeFalse();
    }

    [Fact]
    public async Task AStream_AsksForAUsageChunk_OnlyWhereTheProviderTakesIt()
    {
        (await StreamRequestAsync(new ProviderWireOptions { StreamUsage = true })).GetProperty("stream_options").GetProperty("include_usage").GetBoolean()
            .Should().BeTrue();
        (await StreamRequestAsync(new ProviderWireOptions { StreamUsage = false })).TryGetProperty("stream_options", out _).Should().BeFalse();
    }

    [Fact]
    public async Task AProviderThatReportsNoUsage_AndStreamsWithoutTheSpace_BreaksNothing()
    {
        var handler = new ScriptedProviderHandler((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("data:{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"a\"}}]}\n\n: keep-alive\n\ndata:{\"choices\":[{\"index\":0,\"delta\":{\"content\":\"b\"}}]}\n\ndata:[DONE]\n\n",
                System.Text.Encoding.UTF8, "text/event-stream"),
        });
        var deltas = new List<string>();

        var turn = await ProviderTestKit.Client(handler).StreamWithToolsAsync(
            new ModelCall(ProviderEgressTarget.ForTest("http://localhost:11434/v1", isLocal: true), "", "llama3"),
            [new ChatToolMessage { Role = "user", Content = "x" }], null, (d, _) => { deltas.Add(d); return ValueTask.CompletedTask; }, CancellationToken.None);

        turn.Content.Should().Be("ab");
        turn.Model.Should().Be("llama3");
        turn.PromptTokens.Should().BeNull();
        turn.CompletionTokens.Should().BeNull();
        deltas.Should().Equal("a", "b");
        handler.Seen.Single().Key.Should().BeNull("a local provider without a key gets no Authorization header");
    }

    [Fact]
    public async Task TheChatsPlainCall_SendsNoLimitNoTemperatureAndNoReasoning_WhateverTheOptions()
    {
        var handler = new ScriptedProviderHandler((_, _) => ProviderTestKit.Ok("hi", prompt: null));
        var answer = await ProviderTestKit.Client(handler).CompleteAsync(
            new ModelCall(ProviderEgressTarget.ForTest("https://api.example.com/v1", options: new ProviderWireOptions { Reasoning = ReasoningStyles.Zai }), "k", "m"),
            [new ChatRoleMessage("user", "x")], CompletionOptions.Chat, CancellationToken.None);
        answer.PromptTokens.Should().Be(0);
        handler.Seen.Single().Body.Should().NotContain("max_tokens").And.NotContain("temperature").And.NotContain("thinking");
    }

    [Fact]
    public async Task ImageGeneration_IsRefusedBeforeARequest_WhereTheProviderDoesNotOfferIt()
    {
        var handler = new ScriptedProviderHandler((_, _) => ProviderTestKit.Ok("{}"));
        var call = () => ProviderTestKit.Client(handler).GenerateImageAsync(
            new ModelCall(ProviderEgressTarget.ForTest("https://api.example.com/v1"), "k", "m"),
            [new ChatToolMessage { Role = "user", Content = "a cat" }], CancellationToken.None);
        (await call.Should().ThrowAsync<ProviderUnavailableException>()).WithMessage("*Image generation is not available*");
        handler.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchModels_ReadsTheProvidersOwnList()
    {
        var handler = new ScriptedProviderHandler((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":[{\"id\":\"glm-4.6\",\"context_length\":200000},{\"id\":\"bad id\"},{\"id\":\"glm-4.5-air\",\"name\":\"Air\"}]}"),
        });
        var list = await ProviderTestKit.Client(handler).ListModelsAsync(ProviderEgressTarget.ForTest("https://api.z.ai/api/paas/v4"), "k", CancellationToken.None);
        list.Select(m => m.Id).Should().Equal("glm-4.6", "glm-4.5-air");
        list[0].ContextWindow.Should().Be(200000);
        list[1].Name.Should().Be("Air");
        handler.Seen.Single().Url.Should().Be("https://api.z.ai/api/paas/v4/models");
    }

    [Fact]
    public void WireOptions_ThatDoNotParse_ReadAsTheDefaults()
    {
        ProviderWireOptions.Parse("{not json").Should().Be(ProviderWireOptions.Default);
        ProviderWireOptions.Parse("{\"reasoning\":\"telepathy\"}").Should().Be(ProviderWireOptions.Default);
        ProviderWireOptions.Parse(ProviderWireOptions.OpenRouter.ToJson()).Should().Be(ProviderWireOptions.OpenRouter);
    }
}
