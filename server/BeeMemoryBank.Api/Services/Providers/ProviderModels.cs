using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeeMemoryBank.Api.Services.Providers;

/// <summary>
/// The wire formats a model provider can speak, one <see cref="IModelProtocolClient"/> each: <see cref="OpenAiChat"/>
/// (<see cref="OpenAiChatClient"/>), <see cref="AnthropicMessages"/> (<see cref="AnthropicMessagesClient"/>) and <see cref="Gemini"/>
/// (<see cref="GeminiClient"/>). The API refuses to create or switch a provider to a protocol no registered client speaks
/// (<see cref="ModelClientRegistry"/>).
/// </summary>
public static class ProviderProtocols
{
    /// <summary>OpenAI's <c>POST {base}/chat/completions</c> (OpenRouter, Z.ai, OpenAI, DeepSeek, Ollama ... and the OpenAI-compatible
    /// endpoints of Anthropic and Google).</summary>
    public const string OpenAiChat = "openai-chat";

    /// <summary>Anthropic's native <c>POST {base}/messages</c> (base <c>https://api.anthropic.com/v1</c>).</summary>
    public const string AnthropicMessages = "anthropic-messages";

    /// <summary>Google's native <c>POST {base}/models/{model}:generateContent</c> (base
    /// <c>https://generativelanguage.googleapis.com/v1beta</c>).</summary>
    public const string Gemini = "gemini";

    public static readonly IReadOnlyList<string> All = [OpenAiChat, AnthropicMessages, Gemini];

    public static bool IsKnown(string? protocol) => protocol is not null && All.Contains(protocol, StringComparer.Ordinal);
}

/// <summary>How a provider is asked to reason (the effort Dream asks for); <see cref="None"/> sends nothing. Which styles a provider may
/// use depends on its protocol (<see cref="For"/>).</summary>
public static class ReasoningStyles
{
    /// <summary>OpenRouter's unified <c>reasoning: { effort }</c>.</summary>
    public const string OpenRouter = "openrouter";
    /// <summary>OpenAI's <c>reasoning_effort</c> (also Google's OpenAI-compatible endpoint).</summary>
    public const string OpenAi = "openai";
    /// <summary>Z.ai's <c>thinking: { type: enabled | disabled }</c>: a low or minimal effort turns thinking off.</summary>
    public const string Zai = "zai";
    /// <summary>Anthropic's <c>output_config: { effort }</c> (the Messages protocol); no <c>thinking</c> field is sent.</summary>
    public const string Anthropic = "anthropic";
    /// <summary>Gemini's <c>generationConfig.thinkingConfig.thinkingLevel</c> (the Gemini protocol), sent to Gemini 3 and later models only.</summary>
    public const string Gemini = "gemini";
    public const string None = "none";

    public static readonly IReadOnlyList<string> All = [OpenRouter, OpenAi, Zai, Anthropic, Gemini, None];

    /// <summary>The styles a provider of <paramref name="protocol"/> may use.</summary>
    public static IReadOnlyList<string> For(string? protocol) => protocol switch
    {
        ProviderProtocols.AnthropicMessages => [Anthropic, None],
        ProviderProtocols.Gemini => [Gemini, None],
        _ => [OpenRouter, OpenAi, Zai, None],
    };
}

/// <summary>Which field carries the output limit.</summary>
public static class MaxTokensFields
{
    public const string MaxTokens = "max_tokens";
    /// <summary>OpenAI's newer name (its reasoning models refuse <c>max_tokens</c>).</summary>
    public const string MaxCompletionTokens = "max_completion_tokens";

    public static readonly IReadOnlyList<string> All = [MaxTokens, MaxCompletionTokens];
}

/// <summary>
/// What differs between two providers that speak the same protocol: how the reasoning effort is sent, whether a streamed answer asks for
/// a final usage chunk, which field carries the output limit, whether image generation is offered, whether the prompt is marked for
/// caching. Stored per provider as JSON (<c>chat_provider.options_json</c>), defaulted from its preset. A value that does not parse reads as
/// the defaults, never as an error. <see cref="StreamUsage"/>, <see cref="MaxTokensField"/> and <see cref="ImageGeneration"/> belong to the
/// OpenAI format and <see cref="PromptCaching"/> to Anthropic's; a client ignores what is not its protocol's.
/// </summary>
public sealed record ProviderWireOptions
{
    [JsonPropertyName("reasoning")] public string Reasoning { get; init; } = ReasoningStyles.None;
    [JsonPropertyName("streamUsage")] public bool StreamUsage { get; init; } = true;
    [JsonPropertyName("maxTokensField")] public string MaxTokensField { get; init; } = MaxTokensFields.MaxTokens;
    [JsonPropertyName("imageGeneration")] public bool ImageGeneration { get; init; }
    /// <summary>Anthropic: <c>cache_control: {type: ephemeral}</c> on the last system block and on the last tool definition, so the chat's
    /// long system prompt and tool list are read from the provider's cache on every round of a tool loop.</summary>
    [JsonPropertyName("promptCaching")] public bool PromptCaching { get; init; } = true;

    public static ProviderWireOptions Default { get; } = new();

    /// <summary>What 2.9.0 sent to OpenRouter: <c>reasoning.effort</c> (Dream only), <c>stream_options.include_usage</c>, <c>max_tokens</c>,
    /// image generation through chat/completions.</summary>
    public static ProviderWireOptions OpenRouter { get; } = new()
    {
        Reasoning = ReasoningStyles.OpenRouter, StreamUsage = true, MaxTokensField = MaxTokensFields.MaxTokens, ImageGeneration = true,
    };

    /// <summary>Anthropic's Messages protocol: Dream's effort as <c>output_config.effort</c>, prompt caching on.</summary>
    public static ProviderWireOptions AnthropicNative { get; } = new() { Reasoning = ReasoningStyles.Anthropic, PromptCaching = true };

    /// <summary>Gemini's own protocol: Dream's effort as <c>thinkingLevel</c> on Gemini 3 and later models.</summary>
    public static ProviderWireOptions GeminiNative { get; } = new() { Reasoning = ReasoningStyles.Gemini };

    /// <summary>The options of a provider of <paramref name="protocol"/> that nobody chose options for (a custom provider).</summary>
    public static ProviderWireOptions DefaultFor(string? protocol) => protocol switch
    {
        ProviderProtocols.AnthropicMessages => AnthropicNative,
        ProviderProtocols.Gemini => GeminiNative,
        _ => Default,
    };

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The first invalid value as an English sentence, or null when all are valid.</summary>
    public string? Validate()
    {
        if (!ReasoningStyles.All.Contains(Reasoning)) return $"reasoning must be one of: {string.Join(", ", ReasoningStyles.All)}.";
        if (!MaxTokensFields.All.Contains(MaxTokensField)) return $"maxTokensField must be one of: {string.Join(", ", MaxTokensFields.All)}.";
        return null;
    }

    /// <summary><see cref="Validate()"/>, and what a provider of <paramref name="protocol"/> may have: a reasoning style of that protocol, and
    /// image generation only in the OpenAI format.</summary>
    public string? Validate(string protocol)
    {
        if (Validate() is { } bad) return bad;
        var styles = ReasoningStyles.For(protocol);
        if (!styles.Contains(Reasoning)) return $"reasoning must be one of: {string.Join(", ", styles)} for the {protocol} protocol.";
        if (ImageGeneration && protocol != ProviderProtocols.OpenAiChat)
            return $"Image generation is not available through the {protocol} protocol in this version.";
        return null;
    }

    public static ProviderWireOptions Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try
        {
            var parsed = JsonSerializer.Deserialize<ProviderWireOptions>(json, Json);
            return parsed is null || parsed.Validate() is not null ? Default : parsed;
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this);
}

/// <summary>The built-in provider every node that used the chat before providers existed has: the OpenRouter of 2.9.0.</summary>
/// <remarks>
/// Its keys stay in <c>chat_api_key</c> and its models in <c>chat_model</c>, the tables 2.9.0 reads, and nothing of any other provider is ever
/// written there: a node rolled back to 2.9.0 sees exactly this provider's keys and models (see docs/ai-providers.md). Its address is the one
/// 2.9.0 had compiled in, so it is approved by definition and cannot be changed; another OpenRouter address is a provider of its own.
/// </remarks>
public static class BuiltInOpenRouter
{
    public static readonly Guid Id = Guid.Parse("0b0e7a1c-6f70-4e72-8000-000000000001");
    public const string BaseUrl = "https://openrouter.ai/api/v1";
    public const string Host = "openrouter.ai";
    public const string Name = "OpenRouter";
    public const string PresetId = "openrouter";
}

/// <summary>A row of <c>chat_provider</c>. The approval columns: the sealed stamp is what counts (<see cref="ChatProviderRepository"/>);
/// <see cref="ApprovedHost"/>, <see cref="ApprovedBy"/> and <see cref="ApprovedAt"/> are only shown.</summary>
public sealed class ChatProvider
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? PresetId { get; set; }
    public string Protocol { get; set; } = ProviderProtocols.OpenAiChat;
    public string BaseUrl { get; set; } = "";
    public bool IsLocal { get; set; }
    public string? OptionsJson { get; set; }
    public byte[]? ApprovalCiphertext { get; set; }
    public byte[]? ApprovalIv { get; set; }
    public int? ApprovalKeyVersion { get; set; }
    public string? ApprovedHost { get; set; }
    public int? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public bool IsBuiltIn => Id == BuiltInOpenRouter.Id;

    public ProviderWireOptions Options => ProviderWireOptions.Parse(OptionsJson);
}

// ── API shapes ───────────────────────────────────────────────────────────────

/// <summary>A provider as the admin page lists it. Never a key: only counts and the last error of its keys.</summary>
public sealed record ProviderResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("presetId")] string? PresetId,
    [property: JsonPropertyName("protocol")] string Protocol,
    [property: JsonPropertyName("baseUrl")] string BaseUrl,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("isLocal")] bool IsLocal,
    [property: JsonPropertyName("builtIn")] bool BuiltIn,
    [property: JsonPropertyName("approved")] bool Approved,
    [property: JsonPropertyName("approvalChecked")] bool ApprovalChecked,
    [property: JsonPropertyName("approvedHost")] string? ApprovedHost,
    [property: JsonPropertyName("approvedAt")] DateTime? ApprovedAt,
    [property: JsonPropertyName("options")] ProviderWireOptions Options,
    [property: JsonPropertyName("keyCount")] int KeyCount,
    [property: JsonPropertyName("availableKeyCount")] int AvailableKeyCount,
    [property: JsonPropertyName("modelCount")] int ModelCount,
    [property: JsonPropertyName("lastError")] string? LastError,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("clientAvailable")] bool ClientAvailable);

public sealed record CreateProviderRequest(
    [property: JsonPropertyName("presetId")] string? PresetId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("protocol")] string? Protocol,
    [property: JsonPropertyName("baseUrl")] string? BaseUrl,
    [property: JsonPropertyName("isLocal")] bool? IsLocal,
    [property: JsonPropertyName("options")] ProviderWireOptions? Options);

/// <summary>Every field optional; omitted = unchanged. A new base URL, protocol or local flag withdraws the approval.</summary>
public sealed record UpdateProviderRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("protocol")] string? Protocol,
    [property: JsonPropertyName("baseUrl")] string? BaseUrl,
    [property: JsonPropertyName("isLocal")] bool? IsLocal,
    [property: JsonPropertyName("options")] ProviderWireOptions? Options);

/// <summary>The approval names the host the person was shown; it must still be the provider's host when it arrives.</summary>
public sealed record ApproveProviderRequest(
    [property: JsonPropertyName("host")] string? Host);

/// <summary>The ordered fallback chain of the default text model (and of Dream's model).</summary>
public sealed record UpdateFallbacksRequest(
    [property: JsonPropertyName("fallbackModelIds")] List<Guid>? FallbackModelIds);

/// <summary>One model a provider listed through its own <c>GET {base}/models</c>.</summary>
public sealed record ProviderModelInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("contextWindow")] int? ContextWindow);
