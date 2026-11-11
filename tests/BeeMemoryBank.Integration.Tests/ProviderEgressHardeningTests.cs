using System.Net;
using System.Net.Http;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Api.Services.Providers;
using BeeMemoryBank.Crypto;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// The egress guard every model provider's client goes through (<see cref="ProviderEgress"/>), one rule per test. It keeps the meaning of
/// the M3 regression test of 2.9.0 (the OpenRouter client's HttpClient follows no redirect, from the REAL DI wiring, because a 307/308
/// would re-POST the conversation, decrypted article bodies included, wherever it pointed) and adds what "any provider" needs: HTTPS only
/// (HTTP only for a provider marked local), the approved host and nothing else, no proxy, and for a provider on the open internet a socket
/// only to a public address, judged on what the name resolves to when the socket is opened.
/// </summary>
public class ProviderEgressHardeningTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    private SocketsHttpHandler PrimaryHandler(string name)
    {
        var handler = _factory.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);
        // Walk past any DelegatingHandlers (logging, etc.) added by the HttpClientFactory pipeline down to the primary handler.
        while (handler is DelegatingHandler delegating && delegating.InnerHandler is not null)
            handler = delegating.InnerHandler;
        return handler.Should().BeOfType<SocketsHttpHandler>().Subject;
    }

    [Theory]
    [InlineData(ProviderEgress.PublicClientName)]
    [InlineData(ProviderEgress.LocalClientName)]
    public void TheProviderClients_FollowNoRedirect_UseNoProxy_AndOpenSocketsThroughTheCheckedConnect(string name)
    {
        var handler = PrimaryHandler(name);
        handler.AllowAutoRedirect.Should().BeFalse("a redirect from a provider must never be followed with the conversation as the payload");
        handler.UseProxy.Should().BeFalse("a proxy would resolve and connect on the node's behalf, and the address check would judge the proxy");
        handler.ConnectCallback.Should().NotBeNull("the address a name resolves to is judged where the socket is opened");
    }

    [Fact]
    public void OneClientPerProtocol_IsRegistered_AndAnUnknownProtocolHasNone()
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetServices<IModelProtocolClient>().Select(c => (c.GetType(), c.Protocol)).Should().BeEquivalentTo(
        [
            (typeof(OpenAiChatClient), ProviderProtocols.OpenAiChat),
            (typeof(AnthropicMessagesClient), ProviderProtocols.AnthropicMessages),
            (typeof(GeminiClient), ProviderProtocols.Gemini),
        ]);
        var registry = scope.ServiceProvider.GetRequiredService<ModelClientRegistry>();
        ProviderProtocols.All.Should().OnlyContain(p => registry.IsAvailable(p));
        registry.Resolve(ProviderProtocols.Gemini).Should().BeOfType<GeminiClient>();
        var resolve = () => registry.Resolve("telepathy");
        resolve.Should().Throw<ProviderUnavailableException>();
    }

    // ── The request rules ───────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, "http://api.example.com/v1/chat/completions", "HTTPS")]
    [InlineData(2, "https://evil.example/v1/chat/completions", "approved host")]
    [InlineData(3, "https://api.example.com:8443/v1/chat/completions", "approved host")]
    [InlineData(4, "https://user:pw@api.example.com/v1/chat/completions", "user information")]
    [InlineData(5, "https://api.example.com.evil.example/v1/chat/completions", "approved host")]
    public void ARequestForAProviderOnTheInternet_GoesOverHttpsToItsApprovedHostOnly(int row, string uri, string reason)
    {
        var target = ProviderEgressTarget.ForTest("https://api.example.com/v1");
        var check = () => ProviderEgress.Check(target, new Uri(uri));
        check.Should().Throw<ProviderUnavailableException>($"row {row}").WithMessage($"*{reason}*");
        ProviderEgress.Check(target, target.Endpoint("chat/completions"));
    }

    [Fact]
    public void PlainHttp_IsOnlyForAProviderMarkedLocal_AndStillOnlyToItsHost()
    {
        var local = ProviderEgressTarget.ForTest("http://localhost:11434/v1", isLocal: true);
        ProviderEgress.Check(local, local.Endpoint("chat/completions"));
        var elsewhere = () => ProviderEgress.Check(local, new Uri("http://127.0.0.2:11434/v1/chat/completions"));
        elsewhere.Should().Throw<ProviderUnavailableException>();

        var (url, error) = ProviderUrl.Normalize("http://api.example.com/v1", isLocal: false);
        url.Should().BeNull();
        error.Should().Contain("local");
    }

    [Theory]
    [InlineData(1, "https://localhost:8080/v1", false, "local")]
    [InlineData(2, "https://127.0.0.1/v1", false, "local")]
    [InlineData(3, "https://10.1.2.3/v1", false, "local")]
    [InlineData(4, "https://[fd00::1]/v1", false, "local")]
    [InlineData(5, "http://169.254.169.254/latest", true, "metadata")]
    [InlineData(6, "https://metadata.google.internal/v1", true, "metadata")]
    [InlineData(7, "https://api.example.com/v1?key=x", false, "query")]
    [InlineData(8, "https://u:p@api.example.com/v1", false, "user name")]
    [InlineData(9, "ftp://api.example.com/v1", false, "https://")]
    [InlineData(10, "api.example.com/v1", false, "absolute")]
    public void ABaseUrl_ThatTheRulesRefuse_IsNeverStored(int row, string raw, bool isLocal, string reason)
    {
        var (url, error) = ProviderUrl.Normalize(raw, isLocal);
        url.Should().BeNull($"row {row}");
        error.Should().ContainEquivalentOf(reason);
    }

    [Fact]
    public void ABaseUrl_IsStoredInOneForm()
    {
        ProviderUrl.Normalize("  HTTPS://API.Z.AI/api/coding/paas/v4/  ", false).Url.Should().Be("https://api.z.ai/api/coding/paas/v4");
        ProviderUrl.Normalize("https://api.example.com:443/v1", false).Url.Should().Be("https://api.example.com/v1");
        ProviderUrl.Normalize("http://LocalHost:11434/v1", true).Url.Should().Be("http://localhost:11434/v1");
        ProviderUrl.DisplayHost("http://localhost:11434/v1").Should().Be("localhost:11434");
        ProviderUrl.DisplayHost("https://api.z.ai/api/paas/v4").Should().Be("api.z.ai");
    }

    // ── The connect-time address rule (no socket is ever opened by these tests: every case is refused before a connect) ────

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static PeerNetwork Resolving(Func<IPAddress, bool> isAllowed, params string[] addresses) =>
        new(isAllowed, (_, _) => Task.FromResult(addresses.Select(IPAddress.Parse).ToArray()));

    [Theory]
    [InlineData(1, "10.0.0.5")]
    [InlineData(2, "192.168.1.20")]
    [InlineData(3, "127.0.0.1")]
    [InlineData(4, "169.254.169.254")]
    [InlineData(5, "::ffff:10.0.0.5")]
    [InlineData(6, "fd00::7")]
    [InlineData(7, "93.184.216.34", "10.0.0.5")]
    public async Task AProviderOnTheInternet_WhoseNameResolvesToANonPublicAddress_IsRefusedAtConnect_AndNothingIsSent(int row, params string[] resolved)
    {
        using var handler = ProviderEgress.PublicHandler(Resolving(HostAddressPolicy.IsPublic, resolved));
        var egress = new ProviderEgress(new SingleClientFactory(handler));
        var target = ProviderEgressTarget.ForTest("https://rebinding.example/v1", name: "Rebinding");

        using var req = new HttpRequestMessage(HttpMethod.Post, target.Endpoint("chat/completions")) { Content = new StringContent("{\"secret\":1}") };
        var send = () => egress.SendAsync(target, req, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);

        (await send.Should().ThrowAsync<ProviderUnavailableException>($"row {row}")).WithMessage("*non-public*");
    }

    [Theory]
    [InlineData(1, "169.254.169.254")]
    [InlineData(2, "fd00:ec2::254")]
    [InlineData(3, "0.0.0.0")]
    public async Task EvenALocalProvider_NeverReachesLinkLocalOrAMetadataAddress(int row, string resolved)
    {
        using var handler = ProviderEgress.LocalHandler(Resolving(PeerNetworkRules.IsAllowed, resolved));
        var egress = new ProviderEgress(new SingleClientFactory(handler));
        var target = ProviderEgressTarget.ForTest("http://my-llm.lan:8080/v1", isLocal: true, name: "LAN");

        using var req = new HttpRequestMessage(HttpMethod.Get, target.Endpoint("models"));
        var send = () => egress.SendAsync(target, req, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);

        (await send.Should().ThrowAsync<ProviderUnavailableException>($"row {row}")).WithMessage("*metadata*");
    }

    // ── The ranges that are not globally reachable (IANA special-purpose registries), for a provider NOT marked local ──────────
    // Each one three ways: as an address literal typed for a provider (refused, never stored), as a literal in a row an older build
    // stored (refused at connect, before a socket), and as the DNS answer of a provider's name (refused at connect). The shared address
    // space 100.64.0.0/10 is the review's case: carrier NAT and Tailscale.

    public static TheoryData<int, string> NotGloballyReachable() => new()
    {
        { 1, "100.64.0.1" }, { 2, "100.127.255.254" }, { 3, "192.0.0.8" }, { 4, "192.0.2.10" }, { 5, "198.18.0.1" }, { 6, "198.19.255.1" },
        { 7, "198.51.100.7" }, { 8, "203.0.113.9" }, { 9, "192.88.99.1" }, { 10, "2001:db8::1" }, { 11, "::" }, { 12, "64:ff9b::6440:1" },
        { 13, "64:ff9b:1::6440:1" }, { 14, "2002:6440:1::1" }, { 15, "2001:0:4136:e378:8000:63bf:3fff:fdd2" }, { 16, "2001:2::1" },
        { 17, "3fff::1" }, { 18, "100::1" }, { 19, "fec0::1" }, { 20, "5f00::1" }, { 21, "::ffff:100.64.0.1" }, { 22, "64:ff9b::c000:20a" },
    };

    private static string Literal(string address) => IPAddress.Parse(address).AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
        ? $"[{address}]" : address;

    [Theory]
    [MemberData(nameof(NotGloballyReachable))]
    public void ABaseUrlAtANonGloballyReachableAddress_IsRefused_ForAProviderOnTheInternet(int row, string address)
    {
        HostAddressPolicy.IsPublic(IPAddress.Parse(address)).Should().BeFalse($"row {row}");
        var (url, error) = ProviderUrl.Normalize($"https://{Literal(address)}/v1", isLocal: false);
        url.Should().BeNull($"row {row}: {address} is not on the public internet");
        error.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [MemberData(nameof(NotGloballyReachable))]
    public async Task ALiteralAddressAnOlderBuildStored_IsRefusedAtConnect_AndNothingIsSent(int row, string address)
    {
        // The guard's own production handler and rule; a literal is judged without a lookup, so no socket is opened when it is refused.
        using var handler = ProviderEgress.PublicHandler();
        var egress = new ProviderEgress(new SingleClientFactory(handler));
        var target = new ProviderEgressTarget(Guid.NewGuid(), "Stored", ProviderProtocols.OpenAiChat, new Uri($"https://{Literal(address)}/v1"), false,
            ProviderWireOptions.Default);

        using var req = new HttpRequestMessage(HttpMethod.Post, target.Endpoint("chat/completions")) { Content = new StringContent("{\"secret\":1}") };
        var send = () => egress.SendAsync(target, req, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);

        (await send.Should().ThrowAsync<ProviderUnavailableException>($"row {row}")).WithMessage("*non-public*");
    }

    [Theory]
    [MemberData(nameof(NotGloballyReachable))]
    public async Task AProviderNameThatResolvesToANonGloballyReachableAddress_IsRefusedAtConnect_AndNothingIsSent(int row, string address)
    {
        using var handler = ProviderEgress.PublicHandler(Resolving(ProviderEgress.PublicNetwork.IsAllowed, address));
        var egress = new ProviderEgress(new SingleClientFactory(handler));
        var target = ProviderEgressTarget.ForTest("https://shared-space.example/v1", name: "Shared");

        using var req = new HttpRequestMessage(HttpMethod.Post, target.Endpoint("chat/completions")) { Content = new StringContent("{\"secret\":1}") };
        var send = () => egress.SendAsync(target, req, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);

        (await send.Should().ThrowAsync<ProviderUnavailableException>($"row {row}")).WithMessage("*non-public*");
    }

    [Theory]
    [InlineData(1, "8.8.8.8")]
    [InlineData(2, "2606:4700:4700::1111")]
    [InlineData(3, "2002:808:808::1")]        // 6to4 of 8.8.8.8: judged as the public address it stands for
    [InlineData(4, "64:ff9b::808:808")]       // NAT64 of 8.8.8.8
    [InlineData(5, "100.63.255.255")]         // just below the shared address space
    [InlineData(6, "100.128.0.1")]            // just above it
    [InlineData(7, "198.20.0.1")]             // just above the benchmarking range
    public void ThePublicInternet_StaysOpenToAProvider(int row, string address) =>
        HostAddressPolicy.IsPublic(IPAddress.Parse(address)).Should().BeTrue($"row {row}");

    [Fact]
    public async Task AProviderRowWhoseAddressIsInTheSharedSpace_GetsNoRequest_EvenWithAValidApprovalStamp()
    {
        await _factory.InitializeNodeAsync(password: "egressPassword");
        (await _factory.Services.GetRequiredService<Core.Services.SessionService>().UnlockAsync("egressPassword")).Should().BeTrue();
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var providers = sp.GetRequiredService<ChatProviderRepository>();
        var p = await ProviderTestKit.AddProviderAsync(sp, "Older row", "https://api.older.example/v1", approve: false);
        // A row as an older build could have stored it, approved as it stands: the address rule is asked again before every call.
        using (var conn = sp.GetRequiredService<ChatDbConnectionFactory>().CreateConnection())
            await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE chat_provider SET base_url = 'https://100.64.0.1/v1' WHERE id = @id", new { id = p.Id });
        await providers.ApproveAsync((await providers.GetAsync(p.Id))!, userId: 1);
        await ProviderTestKit.AddKeyAsync(sp, p.Id, "sk-shared-space-0001");
        var model = await ProviderTestKit.AddModelAsync(sp, p.Id, "shared-model");
        var scripted = new ScriptedProviderHandler((_, _) => ProviderTestKit.Ok("{}"));

        var (route, problem) = await ProviderTestKit.Router(sp, scripted).PrepareAsync(model);

        route.Should().BeNull();
        problem!.Message.Should().Contain("not valid").And.Contain("public internet");
        scripted.Seen.Should().BeEmpty();
    }

    /// <summary>Tailscale is a normal way to reach a home Ollama: a provider marked local may use the shared address space, by literal and by
    /// name. Its client's network rule allows the address, the URL is accepted, and a call goes out to it (to a scripted handler here: a test
    /// opens no socket to a real 100.64 address).</summary>
    [Fact]
    public async Task AProviderMarkedLocal_MayStillUseTheSharedAddressSpace()
    {
        foreach (var address in new[] { "100.64.0.1", "100.101.102.103", "100.127.255.254" })
            PeerNetwork.Production.IsAllowed(IPAddress.Parse(address)).Should().BeTrue($"{address} is a Tailscale or carrier-NAT address");
        PeerNetwork.Production.IsAllowed(IPAddress.Parse("100.100.100.200")).Should().BeFalse("the cloud metadata address inside that space stays refused");
        ProviderUrl.Normalize("http://100.101.102.103:11434/v1", isLocal: true).Url.Should().Be("http://100.101.102.103:11434/v1");

        await _factory.InitializeNodeAsync(password: "egressPassword");
        (await _factory.Services.GetRequiredService<Core.Services.SessionService>().UnlockAsync("egressPassword")).Should().BeTrue();
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var p = await ProviderTestKit.AddProviderAsync(sp, "Home Ollama", "http://100.101.102.103:11434/v1", isLocal: true);
        var model = await ProviderTestKit.AddModelAsync(sp, p.Id, "llama3");
        var scripted = new ScriptedProviderHandler((_, _) => ProviderTestKit.Ok("from the tailnet"));

        var (answer, route) = await ProviderTestKit.Router(sp, scripted).RunAsync([model],
            (r, call, ct) => r.Client.CompleteAsync(call, [new ChatRoleMessage("user", "x")], CompletionOptions.Chat, ct), CancellationToken.None);

        answer.Content.Should().Be("from the tailnet");
        route.Target.IsLocal.Should().BeTrue();
        scripted.Seen.Should().ContainSingle().Which.Url.Should().Be("http://100.101.102.103:11434/v1/chat/completions");
    }

    // ── A redirect is an answer, never followed ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ARedirect_IsAFailureOfThatKey_AndTheConversationGoesNowhereElse()
    {
        var provider = new ScriptedProviderHandler((_, _) =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            r.Headers.Location = new Uri("https://evil.example/collect");
            return r;
        });
        var client = ProviderTestKit.Client(provider);
        var target = ProviderEgressTarget.ForTest("https://api.example.com/v1");

        var call = () => client.CompleteAsync(new ModelCall(target, "sk-test", "m"), [new ChatRoleMessage("user", "secret note")],
            CompletionOptions.Chat, CancellationToken.None);

        (await call.Should().ThrowAsync<ProviderHttpException>()).Which.StatusCode.Should().Be(307);
        provider.Seen.Should().ContainSingle().Which.Url.Should().StartWith("https://api.example.com/v1/");
    }

    // ── Nothing is sent to a provider that is not approved ─────────────────────────────────────────────────────

    [Fact]
    public async Task AProviderThatIsNotApproved_GetsNoRequest_AndItsModelIsNotCallable()
    {
        await _factory.InitializeNodeAsync(password: "egressPassword");
        (await _factory.Services.GetRequiredService<Core.Services.SessionService>().UnlockAsync("egressPassword")).Should().BeTrue();
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var provider = await ProviderTestKit.AddProviderAsync(sp, "Unapproved", "https://api.unapproved.example/v1", approve: false);
        await ProviderTestKit.AddKeyAsync(sp, provider.Id, "sk-unapproved-0001");
        var model = await ProviderTestKit.AddModelAsync(sp, provider.Id, "some-model");
        var scripted = new ScriptedProviderHandler((_, _) => ProviderTestKit.Ok("{}"));
        var router = ProviderTestKit.Router(sp, scripted);

        var (route, problem) = await router.PrepareAsync(model);
        route.Should().BeNull();
        problem!.Message.Should().Contain("not approved");
        problem.Permanent.Should().BeTrue();

        var run = () => router.RunAsync([model], (r, call, ct) => r.Client.CompleteAsync(call, [new ChatRoleMessage("user", "x")], CompletionOptions.Chat, ct),
            CancellationToken.None);
        (await run.Should().ThrowAsync<AllKeysExhaustedException>()).WithMessage("*not approved*");
        scripted.Seen.Should().BeEmpty();
    }

    // ── A key goes only to its own provider's host ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AKey_GoesOnlyToItsOwnProvidersHost_EvenAcrossTheFallbackChain()
    {
        await _factory.InitializeNodeAsync(password: "egressPassword");
        (await _factory.Services.GetRequiredService<Core.Services.SessionService>().UnlockAsync("egressPassword")).Should().BeTrue();
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var a = await ProviderTestKit.AddProviderAsync(sp, "A", "https://a.example/v1");
        var b = await ProviderTestKit.AddProviderAsync(sp, "B", "https://b.example/v1");
        await ProviderTestKit.AddKeyAsync(sp, a.Id, "sk-a-key-0001", 1);
        await ProviderTestKit.AddKeyAsync(sp, a.Id, "sk-a-key-0002", 2);
        await ProviderTestKit.AddKeyAsync(sp, b.Id, "sk-b-key-0001", 1);
        var modelA = await ProviderTestKit.AddModelAsync(sp, a.Id, "model-a");
        var modelB = await ProviderTestKit.AddModelAsync(sp, b.Id, "model-b");
        var scripted = new ScriptedProviderHandler((req, _) =>
            req.RequestUri!.Host == "a.example" ? ProviderTestKit.Status(HttpStatusCode.ServiceUnavailable) : ProviderTestKit.Ok("from b"));
        var router = ProviderTestKit.Router(sp, scripted);

        var (answer, route) = await router.RunAsync([modelA, modelB],
            (r, call, ct) => r.Client.CompleteAsync(call, [new ChatRoleMessage("user", "x")], CompletionOptions.Chat, ct), CancellationToken.None);

        answer.Content.Should().Be("from b");
        route.ProviderName.Should().Be("B");
        scripted.Seen.Select(s => (new Uri(s.Url).Host, s.Key)).Should().Equal(
            ("a.example", "sk-a-key-0001"), ("a.example", "sk-a-key-0002"), ("b.example", "sk-b-key-0001"));
    }

    [Fact]
    public async Task AKeyRowMovedToAnotherProvider_DoesNotOpen_SoItIsNeverSentThere()
    {
        await _factory.InitializeNodeAsync(password: "egressPassword");
        (await _factory.Services.GetRequiredService<Core.Services.SessionService>().UnlockAsync("egressPassword")).Should().BeTrue();
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var a = await ProviderTestKit.AddProviderAsync(sp, "A", "https://a.example/v1");
        var b = await ProviderTestKit.AddProviderAsync(sp, "B", "https://b.example/v1");
        var keyId = await ProviderTestKit.AddKeyAsync(sp, a.Id, "sk-a-key-moved-0001");
        using (var conn = sp.GetRequiredService<ChatDbConnectionFactory>().CreateConnection())
            await Dapper.SqlMapper.ExecuteAsync(conn, "UPDATE chat_provider_key SET provider_id = @b WHERE id = @keyId", new { b = b.Id, keyId });

        var repo = sp.GetRequiredService<ChatSettingsRepository>();
        var rows = await repo.ListByProviderAsync(b.Id);
        rows.Should().ContainSingle();
        (await repo.OpenSecretsAsync(rows)).Should().ContainSingle().Which.Should().BeNull("the key is sealed for provider A");
        var model = await ProviderTestKit.AddModelAsync(sp, b.Id, "model-b");
        var scripted = new ScriptedProviderHandler((_, _) => ProviderTestKit.Ok("{}"));
        var (route, problem) = await ProviderTestKit.Router(sp, scripted).PrepareAsync(model);
        route.Should().BeNull();
        problem!.Message.Should().Contain("cannot be read");
        scripted.Seen.Should().BeEmpty();
    }
}
