using BeeMemoryBank.Api.Models;

namespace BeeMemoryBank.Api.Services.Providers;

/// <summary>One request to one model of one provider: the approved target, the key of THAT provider (empty for a local provider that needs
/// none) and the model id as the provider knows it.</summary>
public sealed record ModelCall(ProviderEgressTarget Target, string ApiKey, string Model);

/// <summary>Optional limits of a plain completion; null leaves a field out of the request.</summary>
/// <param name="MaxTokens">The output limit (sent in the provider's <see cref="ProviderWireOptions.MaxTokensField"/>).</param>
/// <param name="Temperature">Sampling temperature.</param>
/// <param name="ReasoningEffort">"low", "medium", "high" ...; sent the provider's way (<see cref="ProviderWireOptions.Reasoning"/>), or not at all.</param>
/// <remarks>No client logs a provider's response body, for any call: an error or an answer can quote what was sent, and that is the text of
/// notes (<see cref="ProviderWire"/>).</remarks>
public sealed record CompletionOptions(int? MaxTokens = null, double? Temperature = null, string? ReasoningEffort = null)
{
    public static CompletionOptions Chat { get; } = new();
}

/// <summary>
/// What the chat and Dream ask of a model, whatever wire format its provider speaks. One implementation per protocol
/// (<see cref="ProviderProtocols"/>), registered in DI as <c>IModelProtocolClient</c> and picked by <see cref="ModelClientRegistry"/> from a
/// provider's protocol. Every implementation sends through <see cref="ProviderEgress"/> (the approval and address rules) and nothing else.
///
/// <para>The results are the chat's own shapes (<see cref="ChatCompleteResponse"/>, <see cref="ToolCompletionResult"/>,
/// <see cref="ImageGenResult"/>), so a client of another protocol maps its answer into them; tool calls travel as OpenAI-style
/// <see cref="ChatToolCall"/>s with a JSON argument string, which is what the chat stores and replays. A failure with an HTTP status is a
/// <see cref="ProviderHttpException"/> (the failover classifies it: 401 disables the key, 402/429 pause it, anything else tries the next key);
/// a transport failure is an <see cref="HttpRequestException"/>; a 200 that is not a completion is an <see cref="InvalidOperationException"/>.</para>
/// </summary>
public interface IModelProtocolClient
{
    /// <summary>The <see cref="ProviderProtocols"/> value this client speaks.</summary>
    string Protocol { get; }

    /// <summary>Whether <see cref="GenerateImageAsync"/> may be used with a provider that has these options at all.</summary>
    bool SupportsImageGeneration(ProviderWireOptions options);

    /// <summary>A plain completion (no tools): Dream's call, and the shape of a simple question.</summary>
    Task<ChatCompleteResponse> CompleteAsync(ModelCall call, IReadOnlyList<ChatRoleMessage> messages, CompletionOptions options, CancellationToken ct);

    /// <summary>A non-streaming completion that may answer with tool calls (vision delegation passes no tools).</summary>
    Task<ToolCompletionResult> CompleteWithToolsAsync(ModelCall call, IReadOnlyList<ChatToolMessage> messages,
        IReadOnlyList<ChatToolDefinition>? tools, CancellationToken ct);

    /// <summary>A streamed completion with tools: <paramref name="onTextDelta"/> receives each text fragment as it arrives, the assembled turn
    /// is returned. The caller's cancellation aborts the upstream request.</summary>
    Task<ToolCompletionResult> StreamWithToolsAsync(ModelCall call, IReadOnlyList<ChatToolMessage> messages,
        IReadOnlyList<ChatToolDefinition>? tools, Func<string, CancellationToken, ValueTask> onTextDelta, CancellationToken ct);

    /// <summary>Image generation; only when <see cref="SupportsImageGeneration"/> says so.</summary>
    Task<ImageGenResult> GenerateImageAsync(ModelCall call, IReadOnlyList<ChatToolMessage> messages, CancellationToken ct);

    /// <summary>The models the provider lists itself (the admin's "Fetch models"), or an empty list when the protocol has no such call.</summary>
    Task<IReadOnlyList<ProviderModelInfo>> ListModelsAsync(ProviderEgressTarget target, string apiKey, CancellationToken ct);
}

/// <summary>
/// Picks the client of a protocol. A protocol nobody registered a client for is not usable: the API refuses to create a provider with it
/// (<see cref="IsAvailable"/>), and a stored provider with it fails closed (<see cref="Resolve"/> throws <see cref="ProviderUnavailableException"/>).
/// The clients are registered in <c>ApiServices.cs</c> (OpenAI format, Anthropic Messages, Gemini); a new protocol adds one there.
/// </summary>
public sealed class ModelClientRegistry(IEnumerable<IModelProtocolClient> clients)
{
    private readonly Dictionary<string, IModelProtocolClient> _byProtocol =
        clients.GroupBy(c => c.Protocol, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

    public bool IsAvailable(string? protocol) => protocol is not null && _byProtocol.ContainsKey(protocol);

    public IReadOnlyCollection<string> Protocols => _byProtocol.Keys;

    /// <summary>Whether models of <paramref name="provider"/> may be used for image generation: a client speaks its protocol and offers it
    /// with the provider's options.</summary>
    public bool OffersImageGeneration(ChatProvider provider) =>
        _byProtocol.TryGetValue(provider.Protocol, out var client) && client.SupportsImageGeneration(provider.Options);

    public IModelProtocolClient Resolve(string protocol) =>
        _byProtocol.TryGetValue(protocol, out var client)
            ? client
            : throw new ProviderUnavailableException($"This node has no client for the '{protocol}' protocol yet.");
}
