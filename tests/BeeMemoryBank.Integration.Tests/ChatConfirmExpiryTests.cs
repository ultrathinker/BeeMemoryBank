using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// L5 regression test: a pending write tool-call used to stay confirmable forever —
/// <c>POST /api/chat/stream/{id}/confirm</c> resolved a <c>toolCallId</c> against the FULL
/// conversation history with no age check, so a <c>confirm_required</c> card from any earlier
/// turn (one the browser never rendered because it reloaded, or one from days/weeks ago) could
/// still be clicked "Allow" and execute the model's ORIGINAL proposed write today, even though
/// the model has no memory of proposing it. The fix expires a pending call 24h after the
/// assistant message that proposed it was created (see ChatEndpoints.cs's PendingConfirmExpiry).
/// </summary>
public class ChatConfirmExpiryTests : IAsyncLifetime
{
    private readonly BmbWebApplicationFactory _factory = new();
    private HttpClient _adminClient = null!;
    private const string Password = "chatConfirmExpiryTestPassword";
    private const int UserId = 1;

    public async Task InitializeAsync()
    {
        _adminClient = _factory.CreateClient();
        _adminClient.DefaultRequestHeaders.Add("X-User-Id", UserId.ToString());
        await _factory.InitializeNodeAsync(password: Password);

        var unlock = await _adminClient.PostAsJsonAsync("/api/session/unlock", new { password = Password });
        unlock.EnsureSuccessStatusCode();

        // A text model must be configured or the confirm endpoint 400s before ever reaching the
        // pending-call lookup this test targets.
        var addModel = await _adminClient.PostAsJsonAsync("/api/chat/models", new
        {
            modelId = "test/expiry-fixture-model",
            label = "Expiry Fixture Model",
            isText = true
        });
        addModel.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _adminClient.Dispose();
        ((IDisposable)_factory).Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Creates a conversation owned by <see cref="UserId"/> with one assistant message
    /// carrying a pending bee_delete_article tool call, backdated by <paramref name="age"/>.
    /// Returns (conversationId, toolCallId).</summary>
    private async Task<(Guid ConversationId, string ToolCallId)> SeedPendingWriteAsync(TimeSpan age)
    {
        using var scope = _factory.Services.CreateScope();
        var convoRepo = scope.ServiceProvider.GetRequiredService<ChatConversationRepository>();
        var msgRepo = scope.ServiceProvider.GetRequiredService<ChatMessageRepository>();
        var session = scope.ServiceProvider.GetRequiredService<SessionService>();

        var conversationId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await convoRepo.CreateAsync(new ChatConversation
        {
            Id = conversationId,
            UserId = UserId,
            Title = "Expiry test conversation",
            CreatedAt = now - age,
            UpdatedAt = now - age
        });

        var toolCallId = "call_" + Guid.NewGuid().ToString("N")[..12];
        var toolCallsJson = JsonSerializer.Serialize(new List<ChatToolCall>
        {
            new()
            {
                Id = toolCallId,
                Type = "function",
                Function = new ChatToolCallFunction
                {
                    Name = "bee_delete_article",
                    Arguments = JsonSerializer.Serialize(new { id = Guid.NewGuid().ToString() })
                }
            }
        });

        await msgRepo.CreateAsync(new ChatMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Role = "assistant",
            ToolCallsJson = toolCallsJson,
            CreatedAt = now - age
        }, session);

        return (conversationId, toolCallId);
    }

    [Fact]
    public async Task Confirm_OnPendingWriteOlderThan24h_Returns410Gone()
    {
        var (conversationId, toolCallId) = await SeedPendingWriteAsync(TimeSpan.FromHours(25));

        var resp = await _adminClient.PostAsJsonAsync(
            $"/api/chat/stream/{conversationId}/confirm",
            new { toolCallId, allow = true });

        resp.StatusCode.Should().Be((HttpStatusCode)410);
    }

    [Fact]
    public async Task Confirm_OnPendingWriteJustUnder24h_IsNotRejectedAsExpired()
    {
        var (conversationId, toolCallId) = await SeedPendingWriteAsync(TimeSpan.FromHours(23));

        var resp = await _adminClient.PostAsJsonAsync(
            $"/api/chat/stream/{conversationId}/confirm",
            new { toolCallId, allow = true });

        // Not expired -- the request proceeds past the expiry check. It will very likely still
        // fail downstream (no OpenRouter key is configured in this test), but it must NOT be the
        // 410 this test is distinguishing from.
        resp.StatusCode.Should().NotBe((HttpStatusCode)410);
    }
}
