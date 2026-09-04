using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using Microsoft.AspNetCore.Http;

namespace BeeMemoryBank.Api.McpTools;

/// <summary>
/// Limits the size of MCP responses. Default: 10,000 tokens.
/// Truncated responses are saved to a temporary file for continued reading via bee_continue.
/// </summary>
/// <remarks>
/// This service is registered as a process-wide singleton (it owns the on-disk temp-file store
/// bee_continue reads from across separate requests), but everything it holds is keyed per-caller
/// (see <see cref="CurrentCallerKey"/>, resolved from the identity of the request in flight). For
/// the max-tokens LIMIT, a single shared int field here would mean one agent raising its limit
/// via bee_set_max_tokens silently changes what every other concurrently connected agent's calls
/// return too -- that was a real bug, not just a surprising default. For the spooled responses
/// themselves the same key decides who may read one back at all -- see <see cref="Continue"/>.
/// </remarks>
/// <remarks>
/// SECURITY: the content overflowing max_tokens is routinely a full decrypted article body (e.g.
/// bee_get_article) -- exactly the plaintext this product's whole design promises to keep off
/// disk unencrypted. The temp file therefore holds AES-256-GCM ciphertext under the master DEK
/// (<see cref="ArticleEncryptor"/>, same primitive/AAD-pattern as RemoteAccountService and the AI
/// chat key store), never the raw response. Saving requires <see cref="SessionService.IsUnlocked"/>
/// (there is no DEK to encrypt with otherwise) and reading back degrades to a clear "vault is
/// locked" tool result rather than throwing -- consistent with how every other content-touching
/// MCP path behaves when the session lock kicks in mid-flight.
/// </remarks>
public class McpResponseManager(string dataPath, IHttpContextAccessor httpContextAccessor, SessionService session)
{
    private static readonly TimeSpan TempFileExpiry = TimeSpan.FromHours(24);

    // Constant AAD prefix for the on-disk continuation store (distinct from every other AAD tag
    // used elsewhere in the codebase, so a ciphertext saved here can never be mistaken for/reused
    // as one from another encrypted-at-rest store even though they all share the master DEK).
    private static readonly byte[] ContinuationAad = "bmb-mcp-continuation-v1"u8.ToArray();

    // The owner tag is bound into the AAD, not just stored beside the ciphertext: otherwise
    // anyone who can write to the temp directory could copy another caller's file, rewrite its
    // Owner field to their own key, and have the server (which holds the DEK they don't) decrypt
    // it for them. With the owner in the AAD that rewrite fails the GCM tag check instead.
    private static byte[] AadFor(string owner) => [.. ContinuationAad, .. Encoding.UTF8.GetBytes(owner)];

    // On-disk envelope for one continuation file. Property names are serialized as-is (no
    // camelCase policy applied here -- this file is never read by anything except this class).
    // Owner is the caller key (see CurrentCallerKey) of whoever spooled the response; a file
    // written by an older build has no Owner and deserializes to null, which reads as "belongs
    // to nobody" and is therefore unreadable -- correct, and self-healing within the 24h expiry.
    private sealed record TempFileEnvelope(string IvB64, string CiphertextB64, string Owner);

    public const int MinTokens = 1000;
    public const int MaxTokensCeiling = 100_000;
    private const int DefaultMaxTokens = 10_000;
    private const string SystemCallerKey = "sys";

    private readonly string _tempPath = EnsureTempPath(dataPath);
    private readonly ConcurrentDictionary<string, int> _maxTokensByCaller = new();

    private static string EnsureTempPath(string dataPath)
    {
        var path = Path.Combine(dataPath, "temp");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Stable identity of whoever is calling right now: <c>agent:{id}</c>, <c>user:{id}</c>, or
    /// <c>sys</c>. Keys both the per-caller max-tokens limit and the ownership of a spooled
    /// continuation (see <see cref="Continue"/>).
    /// </summary>
    /// <remarks>
    /// Every MCP tool call arrives as its own HTTP request carrying its own bearer token, and
    /// AgentAuthMiddleware resolves it per-request, so <see cref="CallerIdentity.Extract"/> is an
    /// accurate answer for the call in flight. The agent id wins over its owner user id on
    /// purpose: an agent key can be restricted to a folder subtree and/or read-only independently
    /// of the human who owns it, so two agents of one owner are two different access scopes and
    /// must not share a bucket. Callers with no identity at all -- in-process/system use (no
    /// HttpContext) and anonymous HTTP requests -- share "sys"; CallerScopeMiddleware hands an
    /// anonymous request a deny-all ACL scope, so nothing it can spool is anyone else's content.
    /// </remarks>
    private string CurrentCallerKey
    {
        get
        {
            var ctx = httpContextAccessor.HttpContext;
            if (ctx is null)
                return SystemCallerKey;

            var caller = CallerIdentity.Extract(ctx);
            if (caller.AgentId is { } agentId)
                return $"agent:{agentId}";
            if (caller.UserId is { } userId)
                return $"user:{userId}";
            return SystemCallerKey;
        }
    }

    public int MaxTokens => _maxTokensByCaller.GetValueOrDefault(CurrentCallerKey, DefaultMaxTokens);

    /// <summary>
    /// Sets the caller's own response limit. Returns false (with an error message, leaving the
    /// limit unchanged) for a value outside [MinTokens, MaxTokensCeiling] -- never silently
    /// clamps, so a caller can tell "you asked for too much" from "it worked".
    /// </summary>
    public bool TrySetMaxTokens(int maxTokens, out string? error)
    {
        if (maxTokens < MinTokens || maxTokens > MaxTokensCeiling)
        {
            error = $"maxTokens must be between {MinTokens} and {MaxTokensCeiling} (got {maxTokens}).";
            return false;
        }

        _maxTokensByCaller[CurrentCallerKey] = maxTokens;
        error = null;
        return true;
    }

    public string ProcessResponse(string response)
    {
        var limit = MaxTokens;
        var tokens = TokenEstimator.EstimateTokens(response);

        if (tokens <= limit)
            return response;

        var guid = Guid.NewGuid().ToString("N");
        SaveTempFile(guid, response, CurrentCallerKey);
        CleanupExpiredFiles();

        if (IsJsonResponse(response))
        {
            // JSON can't be truncated mid-structure and stay parseable, so this call only ever
            // returns a small preview, never a usable prefix -- offset MUST start at 0 (the true
            // beginning of the saved document). Setting it to an interior byte position while
            // only ever delivering a preview (the previous behavior) silently drops everything
            // between the preview and that position -- found in review, not a cosmetic issue.
            var preview = response.Length > 500 ? response[..500] : response;
            return JsonSerializer.Serialize(new
            {
                truncated = true,
                reason = $"Response exceeded max_tokens limit (~{tokens} tokens, limit {limit}).",
                preview,
                guid,
                offset = 0,
                totalChars = response.Length,
                hint = BuildContinueHint(guid, 0, tokens)
            });
        }

        // 90% of budget for content, 10% for warning
        var targetBytes = (int)(limit * 3.0 * 0.9);
        var charPos = TokenEstimator.FindCharPositionForByteLimit(response, targetBytes);
        var truncated = response[..charPos].TrimEnd() + "\n... [TRUNCATED]";
        var remainingTokens = TokenEstimator.EstimateTokens(response[charPos..]);

        var warning = $"\n⚠️ TRUNCATED: Response too large (~{tokens} tokens, limit {limit}). " +
                      $"Showed {charPos} of {response.Length} chars. " +
                      BuildContinueHint(guid, charPos, remainingTokens);
        return truncated + "\n" + warning;
    }

    private static bool IsJsonResponse(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsWhiteSpace(s[i])) continue;
            return s[i] == '{' || s[i] == '[';
        }
        return false;
    }

    public string Continue(string guid, int offset, bool ignoreLimit = false)
    {
        if (!Guid.TryParse(guid, out _))
            return JsonSerializer.Serialize(new { error = "Invalid continuation guid." });

        var filePath = Path.Combine(_tempPath, $"{guid}.json");

        TempFileEnvelope? envelope = null;
        try
        {
            if (File.Exists(filePath))
                envelope = JsonSerializer.Deserialize<TempFileEnvelope>(File.ReadAllText(filePath, Encoding.UTF8));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            envelope = null;
        }

        // SECURITY: a continuation belongs to the caller that produced it, and to nobody else.
        // The guid travels through tool results, transcripts and logs, so treating it as a bearer
        // capability means any agent that sees or guesses one reads a spooled response in full --
        // typically a decrypted article body from folders its own key is denied, since the ACL was
        // applied once when the original tool ran and is never re-applied on the way back out.
        // Superadmins get NO exception: this is not a privilege level but "whose response is
        // this", and an admin who wants the content re-runs the tool under their own identity.
        // Do not add a bypass here.
        //
        // A mismatch answers with exactly the unknown-guid response below -- a distinct error (or
        // a different one for the locked-vault / corrupt-file cases, which is why this runs before
        // both) would turn bee_continue into an oracle for "does this guid exist". An envelope too
        // damaged to name an owner is answered the same way for the same reason; both messages
        // tell the caller to re-run the original tool call, so nothing is lost.
        var callerKey = CurrentCallerKey;
        if (envelope is null || !string.Equals(envelope.Owner, callerKey, StringComparison.Ordinal))
            return NotFoundResponse();

        // The file holds ciphertext under the master DEK -- decrypting needs an unlocked session,
        // same invariant every other content-touching MCP path relies on. Degrade gracefully
        // rather than throw: the session can lock between the original call and this one.
        if (!session.IsUnlocked)
            return JsonSerializer.Serialize(new
            {
                error = "Vault is locked. Unlock it, then call bee_continue again."
            });

        string fullContent;
        var masterDek = session.GetMasterDek();
        try
        {
            fullContent = ArticleEncryptor.Decrypt(
                Convert.FromBase64String(envelope.CiphertextB64),
                Convert.FromBase64String(envelope.IvB64),
                masterDek, AadFor(callerKey));
        }
        // ArgumentNullException covers an envelope that parsed but is missing a field (its
        // owner already checked out, so this is a damaged file, not an access attempt).
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentNullException)
        {
            // Most likely cause: the master DEK rotated between the save and this read, so the
            // old ciphertext no longer unwraps under the current DEK. Whatever the cause, this is
            // an expected/recoverable condition for a stored blob, not a bug -- report it the same
            // way an expired/missing file is reported above, not as a 500.
            return JsonSerializer.Serialize(new
            {
                error = "Could not decrypt the saved continuation (it may predate a DEK rotation). Re-run the original tool call."
            });
        }
        finally
        {
            Array.Clear(masterDek);
        }

        if (offset < 0)
            return JsonSerializer.Serialize(new { error = $"Invalid offset {offset}: must be >= 0." });
        if (offset >= fullContent.Length)
            return JsonSerializer.Serialize(new
            {
                status = "complete",
                message = "All content has been delivered."
            });

        var remaining = fullContent[offset..];
        var tokens = TokenEstimator.EstimateTokens(remaining);
        // ignoreLimit always means "use the hard ceiling for this one call", never a number
        // above it -- it exists so a caller never HAS to touch the shared session-wide limit
        // (via bee_set_max_tokens) just to read one large document in a single call.
        var effectiveLimit = ignoreLimit ? MaxTokensCeiling : MaxTokens;

        if (tokens <= effectiveLimit)
            return remaining;

        var targetBytes = (int)(effectiveLimit * 3.0 * 0.9);
        var charPos = TokenEstimator.FindCharPositionForByteLimit(remaining, targetBytes);
        var truncated = remaining[..charPos].TrimEnd() + "\n... [TRUNCATED]";
        var newOffset = offset + charPos;
        var remainingTokens = TokenEstimator.EstimateTokens(remaining[charPos..]);

        var warning = $"\n⚠️ TRUNCATED: Response too large (~{tokens} tokens, limit {effectiveLimit}). " +
                      $"Showed {newOffset} of {fullContent.Length} chars. " +
                      BuildContinueHint(guid, newOffset, remainingTokens);
        return truncated + "\n" + warning;
    }

    /// <summary>
    /// The single answer for "you cannot read this guid" -- unknown, expired, unreadable, or
    /// owned by someone else. One method so the four stay literally identical; splitting them
    /// back apart is what would leak which of the four actually happened.
    /// </summary>
    private static string NotFoundResponse() => JsonSerializer.Serialize(new
    {
        error = "Continuation file not found or expired (24h). Re-run the original tool call."
    });

    /// <summary>
    /// Always states the exact remaining token cost instead of just listing both APIs -- a hint
    /// that hands the caller a number lets it decide in one step; a hint that just lists "call
    /// bee_continue, or try ignoreLimit" makes it guess and often re-ask.
    /// </summary>
    private static string BuildContinueHint(string guid, int offset, int remainingTokens)
    {
        var basic = $"Call bee_continue(guid: \"{guid}\", offset: {offset}) for the next chunk.";

        if (remainingTokens <= MaxTokensCeiling)
        {
            return basic +
                   $" To get all remaining ~{remainingTokens} tokens in a single call instead: " +
                   $"bee_continue(guid: \"{guid}\", offset: {offset}, ignoreLimit: true).";
        }

        return basic +
               $" Too large for a single call even with ignoreLimit (~{remainingTokens} tokens, " +
               $"hard ceiling {MaxTokensCeiling}) — must be read incrementally.";
    }

    /// <summary>Encrypts <paramref name="content"/> under the master DEK and writes the envelope
    /// to disk, stamped with <paramref name="owner"/> -- the only caller key that will ever be
    /// allowed to read it back. No-op when the session is locked -- there is no DEK to encrypt
    /// with, and writing the plaintext instead is exactly the bug this class exists to not have.
    /// The caller (<see cref="ProcessResponse"/>) still returns its inline truncated preview
    /// either way; a bee_continue call against a guid that was never actually saved reports
    /// "not found", which is the truth.</summary>
    private void SaveTempFile(string guid, string content, string owner)
    {
        if (!session.IsUnlocked)
            return;

        Directory.CreateDirectory(_tempPath);
        var masterDek = session.GetMasterDek();
        try
        {
            var (ciphertext, iv) = ArticleEncryptor.Encrypt(content, masterDek, AadFor(owner));
            var envelope = JsonSerializer.Serialize(new TempFileEnvelope(
                Convert.ToBase64String(iv), Convert.ToBase64String(ciphertext), owner));
            File.WriteAllText(Path.Combine(_tempPath, $"{guid}.json"), envelope, Encoding.UTF8);
        }
        finally
        {
            Array.Clear(masterDek);
        }
    }

    private void CleanupExpiredFiles()
    {
        try
        {
            var cutoff = DateTime.UtcNow - TempFileExpiry;
            foreach (var file in Directory.GetFiles(_tempPath, "*.json"))
            {
                // Use LastWriteTimeUtc instead of CreationTimeUtc — on Linux (ext4/btrfs),
                // creation time may not be supported or may be unreliable.
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    try { File.Delete(file); }
                    catch { /* best effort */ }
                }
            }
        }
        catch { /* best effort */ }
    }
}
