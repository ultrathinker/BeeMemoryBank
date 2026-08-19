using System.Text.Json;
using BeeMemoryBank.Api.McpTools;
using BeeMemoryBank.Core.Models;
using Microsoft.AspNetCore.Http;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Unit tests for McpResponseManager -- no DI host needed, it only takes a data path and an
/// IHttpContextAccessor (used to key the per-caller max-tokens limit off HttpContext.Items["AuthAgent"]).
/// </summary>
public class McpResponseManagerTests
{
    private static (McpResponseManager manager, HttpContextAccessor accessor) NewManager()
    {
        var accessor = new HttpContextAccessor();
        var manager = new McpResponseManager(Path.Combine(Path.GetTempPath(), "bmb-test-" + Guid.NewGuid().ToString("N")), accessor);
        return (manager, accessor);
    }

    private static void ActAsAgent(HttpContextAccessor accessor, int agentId)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["AuthAgent"] = new Agent { Id = agentId };
        accessor.HttpContext = ctx;
    }

    [Theory]
    [InlineData(McpResponseManager.MinTokens)]
    [InlineData(McpResponseManager.MaxTokensCeiling)]
    [InlineData(50_000)]
    public void TrySetMaxTokens_WithinRange_Succeeds(int value)
    {
        var (manager, _) = NewManager();

        manager.TrySetMaxTokens(value, out var error).Should().BeTrue();
        error.Should().BeNull();
        manager.MaxTokens.Should().Be(value);
    }

    [Fact]
    public void TrySetMaxTokens_BelowMin_ReturnsErrorAndLeavesDefaultUnchanged()
    {
        var (manager, _) = NewManager();

        manager.TrySetMaxTokens(McpResponseManager.MinTokens - 1, out var error).Should().BeFalse();
        error.Should().Contain(McpResponseManager.MinTokens.ToString()).And.Contain(McpResponseManager.MaxTokensCeiling.ToString());
        manager.MaxTokens.Should().Be(10_000); // default, untouched
    }

    [Fact]
    public void TrySetMaxTokens_AboveCeiling_ReturnsErrorAndLeavesDefaultUnchanged()
    {
        var (manager, _) = NewManager();

        manager.TrySetMaxTokens(McpResponseManager.MaxTokensCeiling + 1, out var error).Should().BeFalse();
        error.Should().NotBeNull();
        manager.MaxTokens.Should().Be(10_000);
    }

    [Fact]
    public void MaxTokens_IsIsolatedPerAgent_RaisingOneAgentsLimitDoesNotAffectAnother()
    {
        // The regression this guards: McpResponseManager is a process-wide singleton (it owns
        // the on-disk continuation store), but the limit itself must not be -- one agent
        // raising its own limit must never change what a different concurrently connected
        // agent's calls return.
        var (manager, accessor) = NewManager();

        ActAsAgent(accessor, agentId: 1);
        manager.TrySetMaxTokens(80_000, out _).Should().BeTrue();
        manager.MaxTokens.Should().Be(80_000);

        ActAsAgent(accessor, agentId: 2);
        manager.MaxTokens.Should().Be(10_000); // agent 2 never touched it -- still the default

        ActAsAgent(accessor, agentId: 1);
        manager.MaxTokens.Should().Be(80_000); // agent 1's own setting persisted, untouched by agent 2
    }

    [Fact]
    public void MaxTokens_UnauthenticatedCallers_AreIsolatedFromAgents()
    {
        var (manager, accessor) = NewManager(); // accessor.HttpContext stays null -> unauthenticated bucket

        manager.TrySetMaxTokens(50_000, out _).Should().BeTrue();
        manager.MaxTokens.Should().Be(50_000);

        ActAsAgent(accessor, agentId: 1);
        manager.MaxTokens.Should().Be(10_000); // agent 1 unaffected by the unauthenticated bucket
    }

    [Fact]
    public void ProcessResponse_WithinLimit_ReturnsUnchanged()
    {
        var (manager, _) = NewManager();
        var response = "short response";

        manager.ProcessResponse(response).Should().Be(response);
    }

    [Fact]
    public void ProcessResponse_PlainText_ExceedsLimit_TruncatesAndHintsIgnoreLimit()
    {
        var (manager, _) = NewManager();
        manager.TrySetMaxTokens(McpResponseManager.MinTokens, out _);
        var response = new string('a', McpResponseManager.MinTokens * 6); // ~2x the truncation budget

        var result = manager.ProcessResponse(response);

        result.Should().Contain("⚠️ TRUNCATED");
        result.Should().Contain("ignoreLimit: true");
        result.Length.Should().BeLessThan(response.Length);
    }

    [Fact]
    public void ProcessResponse_PlainText_EvenIgnoreLimitTooLarge_HintSaysSoUpFront()
    {
        var (manager, _) = NewManager();
        manager.TrySetMaxTokens(McpResponseManager.MinTokens, out _);
        // What's left after the FIRST truncation must itself exceed MaxTokensCeiling.
        var response = new string('a', (McpResponseManager.MaxTokensCeiling * 3) + 400_000);

        var result = manager.ProcessResponse(response);

        result.Should().Contain("Too large for a single call even with ignoreLimit");
        result.Should().Contain($"hard ceiling {McpResponseManager.MaxTokensCeiling}");
        result.Should().NotContain("ignoreLimit: true");
    }

    [Fact]
    public void ProcessResponse_Json_ExceedsLimit_OffsetIsZero_NotAnInteriorPosition()
    {
        // The bug this guards (found in review): the envelope used to set offset to an interior
        // byte position while only ever delivering a 500-char preview, silently dropping
        // everything between the preview and that position when the caller followed the hint.
        var (manager, _) = NewManager();
        manager.TrySetMaxTokens(McpResponseManager.MinTokens, out _);
        var response = "{\"data\":\"" + new string('a', McpResponseManager.MinTokens * 6) + "\"}";

        var result = manager.ProcessResponse(response);

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("offset").GetInt32().Should().Be(0);
        doc.RootElement.GetProperty("hint").GetString().Should().Contain("offset: 0").And.Contain("ignoreLimit: true");
    }

    [Fact]
    public void ProcessResponse_Json_ThenContinueFromZero_DeliversContentWithNoGap()
    {
        var (manager, _) = NewManager();
        manager.TrySetMaxTokens(McpResponseManager.MinTokens, out _);
        var response = "{\"data\":\"" + new string('a', McpResponseManager.MinTokens * 6) + "\"}";

        var envelope = manager.ProcessResponse(response);
        using var doc = JsonDocument.Parse(envelope);
        var guid = doc.RootElement.GetProperty("guid").GetString()!;
        var offset = doc.RootElement.GetProperty("offset").GetInt32();
        offset.Should().Be(0);

        // Paged continuation from the stated offset must start at the TRUE beginning of the
        // document -- no gap between what the envelope showed and what this delivers. All-ASCII
        // content means byte budget == char count, so the cut point is exactly predictable.
        var expectedCharPos = (int)(McpResponseManager.MinTokens * 3.0 * 0.9);
        var firstChunk = manager.Continue(guid, offset);
        firstChunk.Should().StartWith(response[..expectedCharPos]);

        // ignoreLimit must deliver the ENTIRE original document in one call from offset 0.
        var whole = manager.Continue(guid, offset, ignoreLimit: true);
        whole.Should().Be(response);
    }

    [Fact]
    public void Continue_InvalidGuid_ReturnsError()
    {
        var (manager, _) = NewManager();

        var result = manager.Continue("not-a-guid", 0);

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("Invalid continuation guid");
    }

    [Fact]
    public void Continue_NegativeOffset_ReturnsStructuredErrorNotAnException()
    {
        var (manager, _) = NewManager();
        manager.TrySetMaxTokens(McpResponseManager.MinTokens, out _);
        var response = new string('a', McpResponseManager.MinTokens * 6);
        var (guid, _) = TruncateAndExtract(manager, response);

        var result = manager.Continue(guid, -1);

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("-1");
    }

    [Fact]
    public void Continue_OffsetAtEnd_ReturnsComplete()
    {
        var (manager, _) = NewManager();
        manager.TrySetMaxTokens(McpResponseManager.MinTokens, out _);
        var response = new string('a', McpResponseManager.MinTokens * 6);
        var (guid, _) = TruncateAndExtract(manager, response);

        var result = manager.Continue(guid, response.Length);

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("status").GetString().Should().Be("complete");
    }

    [Fact]
    public void Continue_WithoutIgnoreLimit_StillPagesInSmallChunks()
    {
        var (manager, _) = NewManager();
        manager.TrySetMaxTokens(McpResponseManager.MinTokens, out _);
        var response = new string('a', McpResponseManager.MinTokens * 9); // leaves >1 chunk after the first truncation too
        var (guid, offset) = TruncateAndExtract(manager, response);

        var result = manager.Continue(guid, offset);

        result.Should().Contain("⚠️ TRUNCATED");
        result.Length.Should().BeLessThan(response.Length - offset);
    }

    [Fact]
    public void Continue_WithIgnoreLimit_ReturnsAllRemainingInOneCall()
    {
        var (manager, _) = NewManager();
        manager.TrySetMaxTokens(McpResponseManager.MinTokens, out _);
        var response = new string('a', McpResponseManager.MinTokens * 9);
        var (guid, offset) = TruncateAndExtract(manager, response);
        var expectedRemainder = response[offset..];

        var result = manager.Continue(guid, offset, ignoreLimit: true);

        result.Should().Be(expectedRemainder);
        result.Should().NotContain("TRUNCATED");
    }

    [Fact]
    public void Continue_WithIgnoreLimit_ExceedsHardCeiling_StillTruncates()
    {
        var (manager, _) = NewManager();
        manager.TrySetMaxTokens(McpResponseManager.MaxTokensCeiling, out _);
        // Guarantee what's left after the FIRST truncation still exceeds MaxTokensCeiling by a
        // wide margin, regardless of the exact byte-budget math used to compute it.
        var response = new string('a', (McpResponseManager.MaxTokensCeiling * 3 * 2) + 100_000);
        var (guid, offset) = TruncateAndExtract(manager, response);

        var result = manager.Continue(guid, offset, ignoreLimit: true);

        result.Should().Contain("⚠️ TRUNCATED");
        result.Should().Contain($"limit {McpResponseManager.MaxTokensCeiling}");
        result.Length.Should().BeLessThan(response.Length - offset);
    }

    private static (string guid, int offset) TruncateAndExtract(McpResponseManager manager, string response)
    {
        var result = manager.ProcessResponse(response);
        result.Should().Contain("⚠️ TRUNCATED", "the test content must actually exceed the configured limit");

        var guidStart = result.IndexOf("guid: \"", StringComparison.Ordinal) + "guid: \"".Length;
        var guid = result.Substring(guidStart, 32);

        const string offsetMarker = "offset: ";
        var offsetStart = result.IndexOf(offsetMarker, StringComparison.Ordinal) + offsetMarker.Length;
        var offsetEnd = result.IndexOfAny([')', ','], offsetStart);
        var offset = int.Parse(result[offsetStart..offsetEnd]);

        return (guid, offset);
    }
}
