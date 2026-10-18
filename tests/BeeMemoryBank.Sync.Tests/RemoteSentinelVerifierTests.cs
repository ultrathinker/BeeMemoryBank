using System.Text;
using BeeMemoryBank.Sync;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BeeMemoryBank.Sync.Tests;

/// <summary>
/// BMB-307 fix2 (M2): the remote sentinel check reads "the server has no sentinel" with the one predicate. An empty text used to be decoded into an empty sentinel, which
/// no key matches: every sync with an older server that answered an empty sentinel logged a "DEK sentinel mismatch" warning that meant nothing.
/// </summary>
public sealed class RemoteSentinelVerifierTests : SyncTestFixture
{
    private sealed class Capture : ILogger<RemoteSentinelVerifier>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class Answer(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private async Task<List<string>> WarningsForAsync(string body)
    {
        await InitService.InitializeAsync("admin", "LocalNode", "pass");
        (await Session.UnlockAsync("pass")).Should().BeTrue();
        var log = new Capture();
        using var http = new HttpClient(new Answer(body));

        await new RemoteSentinelVerifier(Session, log).VerifyAsync(http, "http://remote.local", CancellationToken.None);

        return log.Warnings;
    }

    [Theory]
    [InlineData("{\"sentinelB64\":\"\"}")]
    [InlineData("{\"sentinelB64\":null}")]
    [InlineData("{}")]
    public async Task AServerWithNoSentinel_EmptyOrNull_IsSkipped_WithoutAMismatchWarning(string body) =>
        (await WarningsForAsync(body)).Should().BeEmpty();

    [Fact]
    public async Task ASentinelOfAnotherKey_IsStillAMismatch()
    {
        var other = Convert.ToBase64String(BeeMemoryBank.Crypto.MasterKeyManager.ComputeSentinel(BeeMemoryBank.Crypto.MasterKeyManager.GenerateMasterDek()));

        (await WarningsForAsync("{\"sentinelB64\":\"" + other + "\"}")).Should().ContainSingle(w => w.Contains("sentinel mismatch"));
    }
}
