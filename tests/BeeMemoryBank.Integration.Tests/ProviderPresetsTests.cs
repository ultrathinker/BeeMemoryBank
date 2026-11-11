using BeeMemoryBank.Api.Services.Providers;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The built-in list of providers (an embedded resource made by tools/provider-presets): what ships is usable as it is, and a generated file
/// with something the product does not accept loses that entry rather than the whole list.
/// </summary>
public class ProviderPresetsTests
{
    [Fact]
    public void TheShippedList_HasEveryProviderTheBriefNames_AtItsDocumentedAddress()
    {
        var byId = ProviderPresets.All.ToDictionary(p => p.Id);
        byId.Keys.Should().Contain(["openrouter", "zai", "zai-coding", "anthropic", "google", "openai", "deepseek", "moonshot", "mistral", "groq", "xai",
            "ollama", "lmstudio", "llamacpp"]);
        byId["openrouter"].BaseUrl.Should().Be(BuiltInOpenRouter.BaseUrl);
        byId["openrouter"].Options.Should().Be(ProviderWireOptions.OpenRouter, "the preset and the built-in provider send the same fields");
        byId["anthropic"].BaseUrl.Should().Be("https://api.anthropic.com/v1");
        byId["anthropic"].Protocol.Should().Be(ProviderProtocols.AnthropicMessages, "Anthropic is reached natively (thinking, prompt caching)");
        byId["anthropic"].Options.Should().Be(ProviderWireOptions.AnthropicNative);
        byId["google"].BaseUrl.Should().Be("https://generativelanguage.googleapis.com/v1beta");
        byId["google"].Protocol.Should().Be(ProviderProtocols.Gemini, "Gemini is reached natively (thought signatures in tool loops)");
        byId["google"].Options.Should().Be(ProviderWireOptions.GeminiNative);
        ProviderPresets.All.Should().OnlyContain(p => p.Options.Validate(p.Protocol) == null, "every preset's options are its protocol's");
        byId["groq"].BaseUrl.Should().Be("https://api.groq.com/openai/v1");
        byId["zai-coding"].Note.Should().Be(
            "Z.ai's terms allow the Coding Plan quota only in supported coding tools; using it elsewhere may get the account throttled or frozen.");
        byId["ollama"].BaseUrl.Should().Be("http://localhost:11434/v1");
        ProviderPresets.All.Where(p => p.Local).Should().OnlyContain(p => p.BaseUrl.StartsWith("http://localhost:", StringComparison.Ordinal));
        ProviderPresets.All.Where(p => !p.Local).Should().OnlyContain(p => p.BaseUrl.StartsWith("https://", StringComparison.Ordinal));
        ProviderPresets.All.Where(p => p.Options.ImageGeneration).Select(p => p.Id).Should().Equal("openrouter");
        ProviderPresets.All.SelectMany(p => p.Models).Should().OnlyContain(m => !m.Id.Any(char.IsWhiteSpace));
    }

    /// <summary>The list generated from the catalogue: every provider on the internet comes with models to tick, each with its context window
    /// (the chat's context meter needs it), and the local servers with none (Fetch models lists what is installed there).</summary>
    [Fact]
    public void TheGeneratedList_GivesEveryProviderOnTheInternetModelsWithAContextWindow()
    {
        foreach (var p in ProviderPresets.All)
        {
            if (p.Local)
            {
                p.Models.Should().BeEmpty(p.Id);
                continue;
            }
            p.Models.Should().NotBeEmpty(p.Id);
            p.Models.Should().OnlyContain(m => m.ContextWindow > 0, $"{p.Id} lists a model without a context window");
            p.Models.Select(m => m.Id).Should().OnlyHaveUniqueItems(p.Id);
        }
    }

    [Fact]
    public void AnEntryTheProductDoesNotAccept_IsDropped_AndTheRestStays()
    {
        const string json = """
            {"format":1,"presets":[
              {"id":"good","name":"Good","protocol":"openai-chat","baseUrl":"https://api.good.example/v1/","local":false,"options":{"reasoning":"none"},
               "models":[{"id":"m-1","contextWindow":1000},{"id":"bad id"},{"id":"m-1"},{"id":"m-2","contextWindow":-5}]},
              {"id":"plain","name":"Plain","protocol":"openai-chat","baseUrl":"http://api.plain.example/v1","local":false,"options":{}},
              {"id":"unknown","name":"Unknown","protocol":"telepathy","baseUrl":"https://api.unknown.example/v1","options":{}},
              {"id":"meta","name":"Meta","protocol":"openai-chat","baseUrl":"http://169.254.169.254/v1","local":true,"options":{}},
              {"id":"opts","name":"Opts","protocol":"openai-chat","baseUrl":"https://api.opts.example/v1","options":{"reasoning":"mind-reading"}},
              {"id":"good","name":"Again","protocol":"openai-chat","baseUrl":"https://api.again.example/v1","options":{}},
              {"id":"later","name":"Later","protocol":"gemini","baseUrl":"https://api.later.example/v1","options":{}},
              {"id":"crossed","name":"Crossed","protocol":"anthropic-messages","baseUrl":"https://api.crossed.example/v1","options":{"reasoning":"openrouter"}}
            ]}
            """;

        var list = ProviderPresets.Parse(json);

        list.Select(p => p.Id).Should().Equal("good", "later");
        list[0].BaseUrl.Should().Be("https://api.good.example/v1", "a base URL is kept in its one normalized form");
        list[0].Models.Select(m => m.Id).Should().Equal("m-1");
        list[1].Protocol.Should().Be(ProviderProtocols.Gemini, "a native protocol is kept; the API offers it where a client speaks it");
        list.Select(p => p.Id).Should().NotContain("crossed", "a reasoning style of another protocol is not this one's");
    }
}
