using System.Net;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Providers;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.Logging.Abstractions;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Every request of the two native clients leaves through <see cref="ProviderEgress"/>: through the named client of the provider's kind
/// (public or local), and refused by the guard - nothing sent - when it does not match the approval (plain HTTP to a provider that is not
/// local) or when the approved name resolves to an address such a provider may not use.
/// </summary>
public class NativeProviderEgressTests
{
    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static PeerNetwork Resolving(params string[] addresses) =>
        new(HostAddressPolicy.IsPublic, (_, _) => Task.FromResult(addresses.Select(IPAddress.Parse).ToArray()));

    private static IModelProtocolClient Client(string protocol, ProviderEgress egress) => protocol == ProviderProtocols.AnthropicMessages
        ? new AnthropicMessagesClient(egress, NullLogger<AnthropicMessagesClient>.Instance)
        : new GeminiClient(egress, NullLogger<GeminiClient>.Instance);

    private static HttpResponseMessage Answer(string protocol) => protocol == ProviderProtocols.AnthropicMessages
        ? ProviderTestKit.Json(HttpStatusCode.OK, "{\"model\":\"m\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"stop_reason\":\"end_turn\"}")
        : ProviderTestKit.Json(HttpStatusCode.OK, "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}");

    /// <summary>Every call a client makes, once.</summary>
    private static IEnumerable<Func<Task>> EveryCall(IModelProtocolClient client, ProviderEgressTarget target)
    {
        var call = new ModelCall(target, "key", "gemini-3-pro");
        List<ChatToolMessage> messages = [new() { Role = "user", Content = "the note text" }];
        yield return () => client.CompleteAsync(call, [new ChatRoleMessage("user", "the note text")], new CompletionOptions(100), CancellationToken.None);
        yield return () => client.CompleteWithToolsAsync(call, messages, null, CancellationToken.None);
        yield return () => client.StreamWithToolsAsync(call, messages, null, (_, _) => ValueTask.CompletedTask, CancellationToken.None);
        yield return () => client.ListModelsAsync(target, "key", CancellationToken.None);
    }

    [Theory]
    [InlineData(1, ProviderProtocols.AnthropicMessages)]
    [InlineData(2, ProviderProtocols.Gemini)]
    public async Task EveryRequest_LeavesThroughTheEgressClientOfTheProvidersKind(int row, string protocol)
    {
        var handler = new ScriptedProviderHandler((_, _) => Answer(protocol));
        var factory = new HandlerClientFactory(handler);
        var client = Client(protocol, new ProviderEgress(factory));
        var publicTarget = ProviderEgressTarget.ForTest("https://api.example.com/v1", protocol: protocol);
        var localTarget = ProviderEgressTarget.ForTest("http://localhost:4000/v1", isLocal: true, protocol: protocol);

        await client.CompleteWithToolsAsync(new ModelCall(publicTarget, "key", "gemini-3-pro"), [new ChatToolMessage { Role = "user", Content = "x" }], null,
            CancellationToken.None);
        await client.CompleteWithToolsAsync(new ModelCall(localTarget, "", "gemini-3-pro"), [new ChatToolMessage { Role = "user", Content = "x" }], null,
            CancellationToken.None);

        factory.Names.Should().Equal([ProviderEgress.PublicClientName, ProviderEgress.LocalClientName], $"row {row}");
        handler.Seen.Select(s => new Uri(s.Url).Host).Should().Equal("api.example.com", "localhost");
        handler.Seen[1].Key.Should().BeNull("a local provider without a key gets no key header");
    }

    [Theory]
    [InlineData(1, ProviderProtocols.AnthropicMessages)]
    [InlineData(2, ProviderProtocols.Gemini)]
    public async Task ARequestThatDoesNotMatchTheApproval_IsRefusedBeforeAnythingIsSent(int row, string protocol)
    {
        var handler = new ScriptedProviderHandler((_, _) => Answer(protocol));
        var client = Client(protocol, new ProviderEgress(new HandlerClientFactory(handler)));
        // A row an older build could have stored: plain http for a provider that is not local.
        var target = new ProviderEgressTarget(Guid.NewGuid(), "Plain", protocol, new Uri("http://api.example.com/v1"), false,
            ProviderWireOptions.DefaultFor(protocol));

        foreach (var call in EveryCall(client, target))
            (await call.Should().ThrowAsync<ProviderUnavailableException>($"row {row}")).WithMessage("*HTTPS*");
        handler.Seen.Should().BeEmpty();
    }

    [Theory]
    [InlineData(1, ProviderProtocols.AnthropicMessages, "10.0.0.5")]
    [InlineData(2, ProviderProtocols.Gemini, "169.254.169.254")]
    [InlineData(3, ProviderProtocols.AnthropicMessages, "100.64.0.1")]
    [InlineData(4, ProviderProtocols.Gemini, "127.0.0.1")]
    public async Task AProviderNameThatResolvesToANonPublicAddress_IsRefusedAtConnect_ForEveryCall(int row, string protocol, string address)
    {
        using var handler = ProviderEgress.PublicHandler(Resolving(address));
        var client = Client(protocol, new ProviderEgress(new SingleClientFactory(handler)));
        var target = ProviderEgressTarget.ForTest("https://rebinding.example/v1", name: "Rebinding", protocol: protocol);

        foreach (var call in EveryCall(client, target))
            (await call.Should().ThrowAsync<ProviderUnavailableException>($"row {row}")).WithMessage("*non-public*");
    }
}
