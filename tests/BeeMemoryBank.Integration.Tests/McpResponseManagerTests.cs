using System.Text.Json;
using System.Text.Json.Nodes;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.McpTools;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using Microsoft.AspNetCore.Http;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// Unit tests for McpResponseManager -- no DI host needed, it only takes a data path, an
/// IHttpContextAccessor (used to key the per-caller max-tokens limit off HttpContext.Items["AuthAgent"]),
/// and a SessionService (used to encrypt/decrypt the on-disk continuation store — see H3 fix:
/// the temp file used to hold overflow responses, which are routinely full decrypted article
/// bodies, as plaintext).
/// </summary>
public class McpResponseManagerTests
{
    private static (McpResponseManager manager, HttpContextAccessor accessor, SessionService session, string dataPath) NewManager(bool unlocked = true)
    {
        var accessor = new HttpContextAccessor();
        // SessionService's IServiceScopeFactory is only touched by TriggerPostUnlockCatchUp when
        // non-null; passing null here (as AgentAuthMiddleware's own auto-unlock effectively does
        // in the DI-less case) keeps UnlockWithDek/Lock fully synchronous and DB-free for tests.
        var session = new SessionService(keySlotRepo: null!, scopeFactory: null);
        if (unlocked)
            session.UnlockWithDek(new byte[32]); // any 32-byte key — AES-256-GCM key size
        var dataPath = Path.Combine(Path.GetTempPath(), "bmb-test-" + Guid.NewGuid().ToString("N"));
        var manager = new McpResponseManager(dataPath, accessor, session);
        return (manager, accessor, session, dataPath);
    }

    private static void ActAsAgent(HttpContextAccessor accessor, int agentId)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["AuthAgent"] = new Agent { Id = agentId };
        accessor.HttpContext = ctx;
    }

    /// <summary>
    /// An agent whose owner user resolved — the shape AgentAuthMiddleware actually produces
    /// post-migration-004 (both AuthAgent and a pre-built CallerIdentity carrying the owner).
    /// </summary>
    private static void ActAsAgentWithOwner(HttpContextAccessor accessor, int agentId, int ownerUserId, bool ownerIsSuperadmin = false)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["AuthAgent"] = new Agent { Id = agentId };
        ctx.Items["CallerIdentity"] = new CallerIdentity(ownerUserId, agentId, $"agent-{agentId}", ownerIsSuperadmin);
        accessor.HttpContext = ctx;
    }

    /// <summary>A human caller (web UI through the internal-key proxy), no agent involved.</summary>
    private static void ActAsUser(HttpContextAccessor accessor, int userId, bool isSuperadmin = false)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items["CallerIdentity"] = new CallerIdentity(userId, AgentId: null, ViaAgentName: null, isSuperadmin);
        accessor.HttpContext = ctx;
    }

    [Theory]
    [InlineData(McpResponseManager.MinTokens)]
    [InlineData(McpResponseManager.MaxTokensCeiling)]
    [InlineData(50_000)]
    public void TrySetMaxTokens_WithinRange_Succeeds(int value)
    {
        var (manager, _, _, _) = NewManager();

        manager.TrySetMaxTokens(value, out var error).Should().BeTrue();
        error.Should().BeNull();
        manager.MaxTokens.Should().Be(value);
    }

    [Fact]
    public void TrySetMaxTokens_BelowMin_ReturnsErrorAndLeavesDefaultUnchanged()
    {
        var (manager, _, _, _) = NewManager();

        manager.TrySetMaxTokens(McpResponseManager.MinTokens - 1, out var error).Should().BeFalse();
        error.Should().Contain(McpResponseManager.MinTokens.ToString()).And.Contain(McpResponseManager.MaxTokensCeiling.ToString());
        manager.MaxTokens.Should().Be(10_000); // default, untouched
    }

    [Fact]
    public void TrySetMaxTokens_AboveCeiling_ReturnsErrorAndLeavesDefaultUnchanged()
    {
        var (manager, _, _, _) = NewManager();

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
        var (manager, accessor, _, _) = NewManager();

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
        var (manager, accessor, _, _) = NewManager(); // accessor.HttpContext stays null -> unauthenticated bucket

        manager.TrySetMaxTokens(50_000, out _).Should().BeTrue();
        manager.MaxTokens.Should().Be(50_000);

        ActAsAgent(accessor, agentId: 1);
        manager.MaxTokens.Should().Be(10_000); // agent 1 unaffected by the unauthenticated bucket
    }

    [Fact]
    public void ProcessResponse_WithinLimit_ReturnsUnchanged()
    {
        var (manager, _, _, _) = NewManager();
        var response = "short response";

        manager.ProcessResponse(response).Should().Be(response);
    }

    [Fact]
    public void ProcessResponse_PlainText_ExceedsLimit_TruncatesAndHintsIgnoreLimit()
    {
        var (manager, _, _, _) = NewManager();
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
        var (manager, _, _, _) = NewManager();
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
        var (manager, _, _, _) = NewManager();
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
        var (manager, _, _, _) = NewManager();
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
        var (manager, _, _, _) = NewManager();

        var result = manager.Continue("not-a-guid", 0);

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("Invalid continuation guid");
    }

    [Fact]
    public void Continue_NegativeOffset_ReturnsStructuredErrorNotAnException()
    {
        var (manager, _, _, _) = NewManager();
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
        var (manager, _, _, _) = NewManager();
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
        var (manager, _, _, _) = NewManager();
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
        var (manager, _, _, _) = NewManager();
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
        var (manager, _, _, _) = NewManager();
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

    // ── H3 fix: the on-disk continuation store must be encrypted, not plaintext ──────────────

    [Fact]
    public void ProcessResponse_ExceedsLimit_TempFileOnDisk_IsNotPlaintext()
    {
        // Core regression test for H3: the overflow content saved to {dataPath}/temp/{guid}.json
        // is routinely a full decrypted article body (e.g. bee_get_article). Before the fix this
        // was written to disk verbatim; assert the marker text used as the "plaintext" fixture
        // below is nowhere in the saved file, and that the file instead looks like the encrypted
        // envelope (ciphertext/iv fields), never a bare copy of the response.
        var (manager, _, _, dataPath) = NewManager();
        manager.TrySetMaxTokens(McpResponseManager.MinTokens, out _);
        var secretMarker = "SECRET-ARTICLE-BODY-" + Guid.NewGuid().ToString("N");
        var response = secretMarker + new string('a', McpResponseManager.MinTokens * 6);

        // Plain-text (non-JSON) overflow: ProcessResponse's truncation envelope is itself plain
        // text with an embedded hint (see TruncateAndExtract below), not a JSON object.
        var (guid, _) = TruncateAndExtract(manager, response);

        var tempFile = Path.Combine(dataPath, "temp", $"{guid}.json");
        File.Exists(tempFile).Should().BeTrue();
        var onDisk = File.ReadAllText(tempFile);

        onDisk.Should().NotContain(secretMarker, "the overflow content must never be persisted as plaintext");
        onDisk.Should().Contain("IvB64").And.Contain("CiphertextB64", "the file must hold the AES-GCM envelope, not the raw response");

        // And round-tripping through the real API still recovers the original content losslessly.
        var recovered = manager.Continue(guid, 0, ignoreLimit: true);
        recovered.Should().Be(response);
    }

    [Fact]
    public void SaveTempFile_WhileLocked_DoesNotWritePlaintextToDisk()
    {
        // If ProcessResponse is ever reached while the session is locked (e.g. a future tool that
        // forgets [RequiresUnlockedSession] but still overflows), there is no DEK to encrypt with.
        // The fix must refuse to persist plaintext rather than falling back to an unencrypted
        // write -- verify no file is created at all.
        var (manager, _, _, dataPath) = NewManager(unlocked: false);
        manager.TrySetMaxTokens(McpResponseManager.MinTokens, out _);
        var response = new string('a', McpResponseManager.MinTokens * 6);

        var (guid, _) = TruncateAndExtract(manager, response);

        var tempFile = Path.Combine(dataPath, "temp", $"{guid}.json");
        File.Exists(tempFile).Should().BeFalse("locked sessions must never write plaintext to the continuation store");
    }

    [Fact]
    public void Continue_WhileLocked_ReturnsVaultLockedErrorNotAnException()
    {
        var (manager, _, session, _) = NewManager();
        manager.TrySetMaxTokens(McpResponseManager.MinTokens, out _);
        var response = new string('a', McpResponseManager.MinTokens * 6);
        var (guid, _) = TruncateAndExtract(manager, response);

        session.Lock();
        var result = manager.Continue(guid, 0);

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("error").GetString().Should().Contain("locked");
    }

    // ── S6 fix: a spooled continuation is readable only by the caller that produced it ────────

    [Fact]
    public void Continue_ByTheAgentThatSpooledIt_StillReturnsTheContent()
    {
        var (manager, accessor, _, _) = NewManager();
        ActAsAgent(accessor, agentId: 1);
        manager.TrySetMaxTokens(McpResponseManager.MinTokens, out _);
        var response = new string('a', McpResponseManager.MinTokens * 6);

        var (guid, _) = TruncateAndExtract(manager, response);

        manager.Continue(guid, 0, ignoreLimit: true).Should().Be(response);
    }

    [Fact]
    public void Continue_ByADifferentAgent_IsByteIdenticalToAGuidThatNeverExisted()
    {
        // The finding: the guid was the only thing needed to read a spooled response back, so any
        // agent that learned one (they travel through tool results, transcripts and logs) could
        // read content its own folder ACL denies it — the original call's ACL is applied once,
        // never re-applied on the way out. The two assertions below are the fix AND its
        // discretion requirement: agent 2 must not be able to tell "someone else's guid" from
        // "no such guid", or bee_continue becomes an oracle for which guids exist.
        var (manager, accessor, _, _) = NewManager();
        ActAsAgent(accessor, agentId: 1);
        manager.TrySetMaxTokens(McpResponseManager.MinTokens, out _);
        var secret = "SECRET-" + new string('a', McpResponseManager.MinTokens * 6);
        var (guid, _) = TruncateAndExtract(manager, secret);

        ActAsAgent(accessor, agentId: 2);
        var stolen = manager.Continue(guid, 0, ignoreLimit: true);
        var neverExisted = manager.Continue(Guid.NewGuid().ToString("N"), 0, ignoreLimit: true);

        stolen.Should().NotContain("SECRET-");
        stolen.Should().Be(neverExisted);
        JsonDocument.Parse(stolen).RootElement.GetProperty("error").GetString()
            .Should().Contain("not found or expired");
    }

    [Fact]
    public void Continue_ByTheAgentsOwnerUser_IsRefused()
    {
        // An agent key can be scoped to a folder subtree and/or read-only independently of the
        // human who owns it, so "same owner" is not "same access". Binding to the owner user
        // instead of the agent would also let one agent read a sibling agent's responses.
        var (manager, accessor, _, _) = NewManager();
        ActAsAgentWithOwner(accessor, agentId: 7, ownerUserId: 42);
        manager.TrySetMaxTokens(McpResponseManager.MinTokens, out _);
        var (guid, _) = TruncateAndExtract(manager, new string('a', McpResponseManager.MinTokens * 6));

        ActAsUser(accessor, userId: 42);

        manager.Continue(guid, 0, ignoreLimit: true).Should().Contain("not found or expired");
    }

    [Fact]
    public void Continue_BySuperadmin_IsRefused_ThereIsNoAdminBypass()
    {
        // Deliberate: ownership is "whose response is this", not a privilege level. A superadmin
        // who wants the content re-runs the tool under their own identity — which re-applies the
        // ACL — instead of reading a blob that was authorized for someone else's scope.
        var (manager, accessor, _, _) = NewManager();
        ActAsAgent(accessor, agentId: 3);
        manager.TrySetMaxTokens(McpResponseManager.MinTokens, out _);
        var (guid, _) = TruncateAndExtract(manager, new string('a', McpResponseManager.MinTokens * 6));

        ActAsUser(accessor, userId: 1, isSuperadmin: true);

        manager.Continue(guid, 0, ignoreLimit: true).Should().Contain("not found or expired");
    }

    [Fact]
    public void Continue_EnvelopeWrittenByAnOlderBuild_WithNoOwner_IsRefused()
    {
        // Files spooled before this fix name no owner. "Belongs to nobody" must read as
        // unreadable rather than readable-by-anyone; the 24h expiry clears them out anyway.
        var (manager, accessor, _, dataPath) = NewManager();
        ActAsAgent(accessor, agentId: 1);
        manager.TrySetMaxTokens(McpResponseManager.MinTokens, out _);
        var (guid, _) = TruncateAndExtract(manager, new string('a', McpResponseManager.MinTokens * 6));

        var tempFile = Path.Combine(dataPath, "temp", $"{guid}.json");
        var node = JsonNode.Parse(File.ReadAllText(tempFile))!.AsObject();
        node.Remove("Owner");
        File.WriteAllText(tempFile, node.ToJsonString());

        manager.Continue(guid, 0, ignoreLimit: true).Should().Contain("not found or expired");
    }

    [Fact]
    public void Continue_OwnerTagRewrittenOnDisk_FailsTheAeadTag_ContentIsNotServed()
    {
        // The owner is bound into the AES-GCM AAD, not merely stored beside the ciphertext:
        // otherwise anyone able to write to the temp directory could re-address another caller's
        // file to themselves and have the server (which holds the DEK they don't) decrypt it.
        var (manager, accessor, _, dataPath) = NewManager();
        ActAsAgent(accessor, agentId: 1);
        manager.TrySetMaxTokens(McpResponseManager.MinTokens, out _);
        var secret = "SECRET-" + new string('a', McpResponseManager.MinTokens * 6);
        var (guid, _) = TruncateAndExtract(manager, secret);

        var tempFile = Path.Combine(dataPath, "temp", $"{guid}.json");
        var node = JsonNode.Parse(File.ReadAllText(tempFile))!.AsObject();
        node["Owner"] = "agent:2";
        File.WriteAllText(tempFile, node.ToJsonString());

        ActAsAgent(accessor, agentId: 2);
        var result = manager.Continue(guid, 0, ignoreLimit: true);

        result.Should().NotContain("SECRET-");
        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("Could not decrypt");
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
