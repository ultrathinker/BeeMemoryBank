using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Endpoints;

/// <summary>
/// The two SSE passthroughs stay explicit because they are NOT plain forwards:
/// <list type="bullet">
/// <item>Streaming requires HttpCompletionOption.ResponseHeadersRead plus a per-chunk copy with a
/// flush after each write, so the browser receives SSE frames incrementally (a buffered forward
/// would hold the whole completion hostage until the model finishes).</item>
/// <item>The browser's abort is forwarded as the upstream cancellation token, so closing the tab
/// cancels the API-side (and provider-side) stream — no wasted tokens on a listener that left.</item>
/// <item>The API commits to text/event-stream only AFTER pre-stream validation; a different
/// content-type means a normal JSON error (409 vault locked, 400 bad request) which must reach the
/// UI verbatim instead of being served as a dead event-stream.</item>
/// </list>
/// All the JSON chat routes (models, keys, settings, conversations, home-pinned, attachments)
/// are pure passthroughs served from ProxyRouteTable by the forwarder.
/// </summary>
public static class ChatProxyEndpoints
{
    public static void MapChatProxyEndpoints(this WebApplication app)
    {
        // DEDICATED SSE passthrough for POST /api-proxy/chat/stream. Identity headers
        // (X-Internal-Key / X-User-Id) are injected by InternalKeyHandler as usual.
        app.MapPost("/api-proxy/chat/stream", async (HttpContext ctx, ApiClient api) =>
        {
            var upstreamReq = new HttpRequestMessage(HttpMethod.Post, "api/chat/stream");
            if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                upstreamReq.Content = new StreamContent(ctx.Request.Body);
                if (!string.IsNullOrEmpty(ctx.Request.ContentType))
                    // Raw copy, NOT via the MediaTypeHeaderValue constructor: it rejects parameters
                    // ("application/json; charset=utf-8" — exactly what every browser fetch sends),
                    // which would turn every streaming request into a FormatException / 500.
                    upstreamReq.Content.Headers.TryAddWithoutValidation(
                        "Content-Type", ctx.Request.ContentType);
            }

            HttpResponseMessage upstream;
            try
            {
                // Forward the client's abort token: closing the tab / navigating away cancels this
                // send, which cancels the Api-side OpenRouter stream (no wasted tokens/billing).
                upstream = await api.SendForwardAsync(upstreamReq, ctx.RequestAborted);
            }
            catch (OperationCanceledException) { return; } // client gone
            catch { ctx.Response.StatusCode = 502; return; } // API unreachable

            using (upstream)
            {
                var upstreamMediaType = upstream.Content.Headers.ContentType?.MediaType ?? "";

                // The Api commits to text/event-stream only AFTER all pre-stream validation passes. A
                // different content-type means a normal (buffered) JSON error — pass it through verbatim
                // with its status so the UI shows the real reason.
                if (!string.Equals(upstreamMediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = (int)upstream.StatusCode;
                    ctx.Response.ContentType = string.IsNullOrEmpty(upstreamMediaType) ? "application/json" : upstreamMediaType;
                    await upstream.Content.CopyToAsync(ctx.Response.Body);
                    return;
                }

                // True streaming passthrough — copy the upstream body to the client as it arrives, flushing
                // per chunk so the browser receives SSE frames incrementally.
                ctx.Response.StatusCode = (int)upstream.StatusCode;
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.Headers["Cache-Control"] = "no-cache";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";

                await using var upstreamStream = await upstream.Content.ReadAsStreamAsync(ctx.RequestAborted);
                var buffer = new byte[8192];
                int read;
                while ((read = await upstreamStream.ReadAsync(buffer.AsMemory(), ctx.RequestAborted)) > 0)
                {
                    await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
        }).RequireAuthorization();

        // Human-in-the-loop: the /stream loop pauses on a write tool call (confirm_required)
        // and the user picks Allow/Deny. This forwards that decision to the Api confirm endpoint,
        // which executes the write (or denial) and streams the CONTINUATION as a fresh SSE response.
        // Same passthrough contract as /stream above.
        app.MapPost("/api-proxy/chat/{conversationId:guid}/confirm", async (Guid conversationId, HttpContext ctx, ApiClient api) =>
        {
            var upstreamReq = new HttpRequestMessage(HttpMethod.Post, $"api/chat/stream/{conversationId}/confirm");
            if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                upstreamReq.Content = new StreamContent(ctx.Request.Body);
                if (!string.IsNullOrEmpty(ctx.Request.ContentType))
                    // Raw copy, NOT via the MediaTypeHeaderValue constructor: it rejects parameters
                    // ("application/json; charset=utf-8" — exactly what every browser fetch sends),
                    // which would turn every streaming request into a FormatException / 500.
                    upstreamReq.Content.Headers.TryAddWithoutValidation(
                        "Content-Type", ctx.Request.ContentType);
            }

            HttpResponseMessage upstream;
            try
            {
                upstream = await api.SendForwardAsync(upstreamReq, ctx.RequestAborted);
            }
            catch (OperationCanceledException) { return; } // client gone
            catch { ctx.Response.StatusCode = 502; return; } // API unreachable

            using (upstream)
            {
                var upstreamMediaType = upstream.Content.Headers.ContentType?.MediaType ?? "";
                if (!string.Equals(upstreamMediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Response.StatusCode = (int)upstream.StatusCode;
                    ctx.Response.ContentType = string.IsNullOrEmpty(upstreamMediaType) ? "application/json" : upstreamMediaType;
                    await upstream.Content.CopyToAsync(ctx.Response.Body);
                    return;
                }

                ctx.Response.StatusCode = (int)upstream.StatusCode;
                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.Headers["Cache-Control"] = "no-cache";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";

                await using var upstreamStream = await upstream.Content.ReadAsStreamAsync(ctx.RequestAborted);
                var buffer = new byte[8192];
                int read;
                while ((read = await upstreamStream.ReadAsync(buffer.AsMemory(), ctx.RequestAborted)) > 0)
                {
                    await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
        }).RequireAuthorization();
    }
}
