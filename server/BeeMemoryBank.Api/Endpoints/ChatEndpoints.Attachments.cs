using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BeeMemoryBank.Api.Helpers;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Api.Services;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Core.Services;
using BeeMemoryBank.Crypto;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace BeeMemoryBank.Api.Endpoints;

public static partial class ChatEndpoints
{
    // ── Phase 5 helpers (vision + image generation) ────────────────────────────

    // MIME allow-list for chat image attachments (plan §2 Phase 5). Validated server-side as well
    // as client-side — never trust the client alone. GIF is accepted even though vision models see
    // only the first frame.
    private static readonly HashSet<string> AllowedImageMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/webp", "image/gif"
    };

    /// <summary>Decodes + validates an inline image attachment. Server-side MIME allow-list, size
    /// cap, AND a magic-byte check against the claimed MIME (catches a spoofed content-type). Returns
    /// the validated bytes + normalized MIME, or an error message. Never throws.</summary>
    private static (byte[]? Bytes, string Mime, string? Error) ValidateAttachment(ChatStreamAttachment att)
    {
        if (string.IsNullOrWhiteSpace(att.Mime) || !AllowedImageMimes.Contains(att.Mime))
            return (null, "", "Unsupported image type. Allowed: PNG, JPEG, WebP, GIF.");

        if (string.IsNullOrWhiteSpace(att.DataBase64))
            return (null, "", "Empty image data.");

        byte[] bytes;
        try { bytes = Convert.FromBase64String(att.DataBase64); }
        catch { return (null, "", "Image data is not valid base64."); }

        if (bytes.Length == 0)
            return (null, "", "Empty image data.");
        if (bytes.Length > MaxAttachmentBytes)
            return (null, "", $"Image is too large ({bytes.Length / (1024 * 1024.0):F1} MB); the limit is {MaxAttachmentBytes / (1024 * 1024)} MB.");

        if (!MatchesMagicBytes(bytes, att.Mime))
            return (null, "", "Image bytes do not match the declared type.");

        // Normalize the MIME to the canonical lowercase form from the allow-list.
        var mime = AllowedImageMimes.First(m => m.Equals(att.Mime, StringComparison.OrdinalIgnoreCase));
        return (bytes, mime, null);
    }

    private static bool MatchesMagicBytes(byte[] b, string mime) => mime switch
    {
        "image/png" => b.Length >= 8
            && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
            && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A,
        "image/jpeg" => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF,
        "image/gif" => b.Length >= 4 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38,
        "image/webp" => b.Length >= 12
            && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 // "RIFF"
            && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50, // "WEBP"
        _ => false
    };

    /// <summary>Builds the egress vision data URL for an image, downscaling to
    /// <see cref="VisionMaxDimension"/> on the longest side and re-encoding as JPEG q85 to keep the
    /// OpenRouter payload reasonable (plan §2 Phase 5: "a simple max-dimension resize is enough").
    /// Reuses ImageSharp (already a Core dependency). Falls back to the original bytes (as a data
    /// URL) if ImageSharp cannot load/encode them.</summary>
    private static string BuildVisionDataUrl(byte[] blob, string mime)
    {
        try
        {
            using var image = Image.Load(blob);
            var w = image.Width;
            var h = image.Height;
            if (w > VisionMaxDimension || h > VisionMaxDimension)
            {
                var scale = (double)VisionMaxDimension / Math.Max(w, h);
                w = Math.Max(1, (int)Math.Round(w * scale));
                h = Math.Max(1, (int)Math.Round(h * scale));
                image.Mutate(ctx => ctx.Resize(w, h));
            }
            using var ms = new MemoryStream();
            image.SaveAsJpeg(ms, new JpegEncoder { Quality = 85 });
            return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch
        {
            return "data:" + mime + ";base64," + Convert.ToBase64String(blob);
        }
    }

    /// <summary>Vision delegation: makes a SEPARATE, non-streaming, tools-less OpenRouter call to
    /// the effective vision model with all the attached images + the user's message text. Returns
    /// the text description the vision model produced, which the caller injects into the text
    /// model's context. Uses multi-key failover (same helper as the main loop). Never returns null —
    /// on a no-content response, returns a placeholder so the text model has something to work
    /// with.</summary>
    private static async Task<string> RunVisionDelegationAsync(
        ChatSettingsRepository repo, OpenRouterClient openRouter, ILogger logger,
        IReadOnlyList<KeyMaterial> keys, string visionModelId, string userMessage,
        List<(byte[] Bytes, string Mime)> images, CancellationToken ct)
    {
        var dataUrls = images.Select(i => BuildVisionDataUrl(i.Bytes, i.Mime)).ToList();
        var visionMessages = new List<ChatToolMessage>
        {
            new()
            {
                Role = "user",
                // Plain multimodal question — no Bee tools exposed to the vision model.
                Content = "Describe and answer based on " + (dataUrls.Count > 1 ? "these images: " : "this image: ") + userMessage,
                ImageDataUrls = dataUrls
            }
        };

        // CompleteWithToolsAsync with tools=null goes through ToWire() which serializes the image as
        // a multimodal content array — exactly what a vision model needs. With no tools, ToolCalls is
        // null and we just read Content.
        var result = await RunWithFailoverAsync(repo, keys,
            (pk, token) => openRouter.CompleteWithToolsAsync(pk, visionModelId, visionMessages, null, token),
            ct);

        return string.IsNullOrEmpty(result.Content)
            ? "(The vision model provided no description of the image.)"
            : result.Content;
    }

    /// <summary>Handles a <c>generate_image</c> tool call from the text model's tool loop. Makes a
    /// non-streaming OpenRouter call to the effective image-gen model with the given prompt, stores
    /// the resulting image(s) as chat_attachment (kind=generated-image) linked to the assistant
    /// message that requested it, emits <c>event: image</c> SSE frames so the UI renders them
    /// inline, and feeds a tool result back so the text model can continue. If no image-gen model is
    /// configured, returns a graceful error tool result (not a crash) so the model can tell the user
    /// in plain text. Reuses the existing GenerateImageAsync + ResolveImageSourceToBytesAsync
    /// plumbing from the old image-gen path.</summary>
    private static async Task RunGenerateImageToolAsync(
        ChatLoopContext lc, ResolvedToolCall tc, JsonElement args, Guid assistantMessageId)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Local error-JSON helper (ChatEndpoints has no access to ChatToolDispatcher.ErrorJson).
        string Err(string msg) => JsonSerializer.Serialize(new { error = msg }, SseJsonOpts);

        // No image-gen model configured → graceful error tool result.
        if (string.IsNullOrEmpty(lc.EffectiveImageGenModelId))
        {
            sw.Stop();
            var noModelJson = Err("No image generation model is configured. Tell the user that image generation is not available on this node.");
            lc.ConvoMessages.Add(new ChatToolMessage { Role = "tool", ToolCallId = tc.Id, Content = noModelJson });
            await SafePersistToolMessage(lc.MsgRepo, lc.Logger, lc.ConversationId, tc.Id, noModelJson, lc.Session);
            await lc.Sse("tool_call_result", new { tool = tc.Name, callId = tc.Id, ok = true, durationMs = (int)sw.ElapsedMilliseconds, error = (string?)null });
            return;
        }

        var prompt = args.TryGetProperty("prompt", out var pEl) ? pEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            sw.Stop();
            var badArgsJson = Err("prompt is required");
            lc.ConvoMessages.Add(new ChatToolMessage { Role = "tool", ToolCallId = tc.Id, Content = badArgsJson });
            await SafePersistToolMessage(lc.MsgRepo, lc.Logger, lc.ConversationId, tc.Id, badArgsJson, lc.Session);
            await lc.Sse("tool_call_result", new { tool = tc.Name, callId = tc.Id, ok = false, durationMs = (int)sw.ElapsedMilliseconds, error = "prompt is required" });
            return;
        }

        // Build a single-user-message request with just the prompt (no conversation context — the
        // text model already refined the prompt based on the conversation).
        var imgMessages = new List<ChatToolMessage>
        {
            new() { Role = "user", Content = prompt }
        };

        try
        {
            var result = await RunWithFailoverAsync(lc.Repo, lc.Keys,
                (pk, token) => lc.OpenRouter.GenerateImageAsync(pk, lc.EffectiveImageGenModelId, imgMessages, token),
                lc.Ct);

            // Materialize each image source → bytes, store as attachment, emit inline SSE event.
            // Reuses the same ResolveImageSourceToBytesAsync + ChatImageEvent plumbing as the old
            // image-gen path. savedIds carries the stored attachment ids back to the model so it can
            // address them with bee_insert_image_into_article.
            var savedCount = 0;
            var savedIds = new List<Guid>();
            foreach (var source in result.ImageSources)
            {
                var (imgBytes, imgMime) = await ResolveImageSourceToBytesAsync(source, lc.Ct);
                if (imgBytes is null || imgBytes.Length == 0) continue;

                var attachmentId = Guid.NewGuid();
                try
                {
                    await lc.AttachRepo.CreateAsync(new ChatAttachment
                    {
                        Id = attachmentId,
                        MessageId = assistantMessageId,
                        Kind = ChatAttachmentKind.GeneratedImage,
                        Mime = imgMime,
                        Blob = imgBytes,
                        CreatedAt = DateTime.UtcNow
                    }, lc.Session);
                }
                catch (Exception ex)
                {
                    lc.Logger.LogWarning(ex, "Failed to persist generated image attachment");
                    continue;
                }

                savedCount++;
                savedIds.Add(attachmentId);
                var inlineUrl = "data:" + imgMime + ";base64," + Convert.ToBase64String(imgBytes);
                await lc.Sse("image", new ChatImageEvent(attachmentId, inlineUrl, imgMime));
            }

            sw.Stop();

            // Don't claim success if nothing was actually produced/stored — the model would otherwise
            // hallucinate "here's your image" from a fake-positive tool result.
            if (savedCount == 0)
            {
                var noImgJson = Err("The image model did not return an image. Tell the user image generation failed and they can try again.");
                lc.ConvoMessages.Add(new ChatToolMessage { Role = "tool", ToolCallId = tc.Id, Content = noImgJson });
                await SafePersistToolMessage(lc.MsgRepo, lc.Logger, lc.ConversationId, tc.Id, noImgJson, lc.Session);
                await lc.Sse("tool_call_result", new { tool = tc.Name, callId = tc.Id, ok = false, durationMs = (int)sw.ElapsedMilliseconds, error = "No image returned" });
                return;
            }

            var okJson = JsonSerializer.Serialize(new
            {
                ok = true,
                message = "Image generated.",
                attachmentIds = savedIds,
                hint = "Use an attachmentId with bee_insert_image_into_article to save this image into an article."
            }, SseJsonOpts);
            lc.ConvoMessages.Add(new ChatToolMessage { Role = "tool", ToolCallId = tc.Id, Content = okJson });
            await SafePersistToolMessage(lc.MsgRepo, lc.Logger, lc.ConversationId, tc.Id, okJson, lc.Session);
            await lc.Sse("tool_call_result", new { tool = tc.Name, callId = tc.Id, ok = true, durationMs = (int)sw.ElapsedMilliseconds, error = (string?)null });
        }
        catch (OperationCanceledException) { throw; }
        catch (AllKeysExhaustedException ex)
        {
            sw.Stop();
            var errJson = Err("Image generation failed (all API keys exhausted): " + ex.Message);
            lc.ConvoMessages.Add(new ChatToolMessage { Role = "tool", ToolCallId = tc.Id, Content = errJson });
            await SafePersistToolMessage(lc.MsgRepo, lc.Logger, lc.ConversationId, tc.Id, errJson, lc.Session);
            await lc.Sse("tool_call_result", new { tool = tc.Name, callId = tc.Id, ok = false, durationMs = (int)sw.ElapsedMilliseconds, error = ex.Message });
        }
        catch (OpenRouterHttpException ex)
        {
            sw.Stop();
            var errJson = Err("Image generation failed: " + ex.Message);
            lc.ConvoMessages.Add(new ChatToolMessage { Role = "tool", ToolCallId = tc.Id, Content = errJson });
            await SafePersistToolMessage(lc.MsgRepo, lc.Logger, lc.ConversationId, tc.Id, errJson, lc.Session);
            await lc.Sse("tool_call_result", new { tool = tc.Name, callId = tc.Id, ok = false, durationMs = (int)sw.ElapsedMilliseconds, error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            sw.Stop();
            var errJson = Err("Image generation failed: " + ex.Message);
            lc.ConvoMessages.Add(new ChatToolMessage { Role = "tool", ToolCallId = tc.Id, Content = errJson });
            await SafePersistToolMessage(lc.MsgRepo, lc.Logger, lc.ConversationId, tc.Id, errJson, lc.Session);
            await lc.Sse("tool_call_result", new { tool = tc.Name, callId = tc.Id, ok = false, durationMs = (int)sw.ElapsedMilliseconds, error = ex.Message });
        }
    }

    /// <summary>One generated-image SSE event surfaced to the UI: the attachment id (for the
    /// persistent /attachments/{id} render + the "Save to Bee" action) and an inline data: URL for
    /// immediate rendering.</summary>
    private sealed record ChatImageEvent(
        [property: JsonPropertyName("attachmentId")] Guid AttachmentId,
        [property: JsonPropertyName("dataUrl")] string DataUrl,
        [property: JsonPropertyName("mime")] string Mime);

    /// <summary>Materializes an image source returned by an image-gen model to raw bytes + a MIME.
    /// Handles three shapes: <c>data:&lt;mime&gt;;base64,&lt;…&gt;</c> (decode directly), a bare
    /// base64 string (decode, assume image/png), and an http(s) URL (fetch server-side, size/time
    /// capped, so it renders inline under the strict <c>img-src</c> CSP). Returns null on failure.</summary>
    private static async Task<(byte[]? Bytes, string Mime)> ResolveImageSourceToBytesAsync(string source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source))
            return (null, "image/png");

        var span = source.AsSpan().Trim();
        if (span.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            // data:<mime>;base64,<payload>
            try
            {
                var comma = source.IndexOf(',');
                if (comma < 0) return (null, "image/png");
                var header = source.Substring(5, comma - 5); // after "data:"
                var mime = "image/png";
                var semi = header.IndexOf(';');
                if (semi > 0) mime = header[..semi];
                var payload = source[(comma + 1)..];
                return (Convert.FromBase64String(payload), mime);
            }
            catch { return (null, "image/png"); }
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            try
            {
                // SSRF guard lives entirely in ImageFetchClient's ConnectCallback: it resolves the
                // host ONCE, rejects loopback / RFC1918 / IPv6 ULA / link-local (incl. the
                // 169.254.169.254 cloud-metadata endpoint) / multicast / unspecified addresses, and
                // connects to the validated IP — so a malicious/compromised model cannot reach
                // internal services or the app's own ports, cannot bypass the check via a redirect
                // (AllowAutoRedirect=false), and cannot win a DNS-rebinding race (the validated
                // address IS the connected address). A disallowed host throws here and the catch
                // below maps it to a null result.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(20));
                using var resp = await ImageFetchClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!resp.IsSuccessStatusCode) return (null, "image/png");
                // Hard cap the fetched bytes so a malicious/buggy URL can't exhaust memory.
                var declared = resp.Content.Headers.ContentLength ?? long.MaxValue;
                if (declared > 20L * 1024 * 1024) return (null, "image/png");
                using var ms = new MemoryStream();
                using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
                var buf = new byte[8192];
                int n;
                while ((n = await stream.ReadAsync(buf.AsMemory(), cts.Token)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > 20L * 1024 * 1024) return (null, "image/png");
                }
                var mime = resp.Content.Headers.ContentType?.MediaType ?? "image/png";
                return (ms.ToArray(), mime);
            }
            catch { return (null, "image/png"); }
        }

        // Bare base64 (e.g. a b64_json value that slipped through without a data: prefix).
        try { return (Convert.FromBase64String(source), "image/png"); }
        catch { return (null, "image/png"); }
    }

    /// <summary>True for loopback / private / link-local / multicast / broadcast / unspecified
    /// destinations — i.e. addresses the image fetch must never reach. Handles IPv4 and IPv6
    /// (including IPv4-mapped IPv6). Unknown address families are rejected (defense-in-depth).</summary>
    private static bool IsPrivateOrLoopbackAddress(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6)
            a = a.MapToIPv4();

        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            if (b[0] == 127) return true;                          // loopback 127.0.0.0/8
            if (b[0] == 10) return true;                           // private 10.0.0.0/8
            if (b[0] == 172 && (b[1] & 0xF0) == 0x10) return true; // private 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;           // private 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return true;           // link-local 169.254.0.0/16 (incl. cloud metadata)
            if (b[0] == 0) return true;                            // "this network"/unspecified 0.0.0.0/8
            if (b[0] >= 224) return true;                          // multicast 224.0.0.0/4 + broadcast 255.255.255.255
            return false;
        }
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(a)) return true;  // ::1
            if (a.IsIPv6LinkLocal) return true;        // fe80::/10
            if (a.IsIPv6SiteLocal) return true;        // fec0::/10 (deprecated, still block)
            var b6 = a.GetAddressBytes();
            if (b6.Length == 16)
            {
                if ((b6[0] & 0xFE) == 0xFC) return true; // ULA fc00::/7 (RFC4193, the IPv6 RFC1918 analogue)
                var allZero = true;
                for (int i = 0; i < 16; i++) { if (b6[i] != 0) { allZero = false; break; } }
                if (allZero) return true; // IPv6 unspecified "::"
            }
            return false;
        }
        return true; // unknown family → reject
    }

    // Dedicated HttpClient for fetching model-returned image URLs (image-gen). Reused across calls
    // (recommended HttpClient usage). NOT the pinned OpenRouter egress client — these are image CDN
    // URLs returned by OpenRouter/providers for a user-requested generation, fetched server-side so
    // they render inline under the strict img-src CSP. See ResolveImageSourceToBytesAsync.
    //
    // SSRF HARDENING (the handler does BOTH jobs):
    //  - AllowAutoRedirect = false → a public host that passes the address check cannot 302 to an
    //    internal address; any 3xx is a non-success status and ResolveImageSourceToBytesAsync
    //    rejects it (closes the redirect-bypass).
    //  - ConnectCallback → resolves the host to IP(s) ONCE, validates EVERY resolved address via
    //    IsPrivateOrLoopbackAddress, and connects DIRECTLY to a validated IP. The address that was
    //    validated is therefore the EXACT address connected to — closing the DNS-rebinding TOCTOU
    //    a separate "check-then-connect" pair would leave open.
    private static readonly HttpClient ImageFetchClient = BuildImageFetchClient();

    private static HttpClient BuildImageFetchClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (ctx, ct) =>
            {
                var host = ctx.DnsEndPoint.Host;
                var port = ctx.DnsEndPoint.Port;

                IPAddress[] addrs;
                if (IPAddress.TryParse(host, out var literal))
                    addrs = new[] { literal };
                else
                {
                    try { addrs = await Dns.GetHostAddressesAsync(host, ct); }
                    catch { throw new HttpRequestException($"Unable to resolve image host '{host}'."); }
                }

                // Reject the whole request if ANY resolved address is disallowed (loopback /
                // RFC1918 / IPv6 ULA / link-local incl. cloud metadata / multicast / unspecified).
                IPAddress? chosen = null;
                foreach (var a in addrs)
                {
                    if (IsPrivateOrLoopbackAddress(a))
                        throw new HttpRequestException($"Refusing to fetch an image from a private/loopback address ({a}).");
                    chosen ??= a;
                }
                if (chosen is null)
                    throw new HttpRequestException($"Image host '{host}' resolved to no usable address.");

                // Connect to the validated IP directly (not a DnsEndPoint, which would re-resolve
                // and re-open the rebinding window). ownsSocket:true so disposing this stream
                // disposes the socket.
                var socket = new Socket(chosen.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(chosen, port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
    }
}
