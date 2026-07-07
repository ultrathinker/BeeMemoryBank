using System.Text;
using BeeMemoryBank.Core.Models;
using BeeMemoryBank.Web.Services;

namespace BeeMemoryBank.Web.Endpoints;

public static class ChatProxyEndpoints
{
    public static void MapChatProxyEndpoints(this WebApplication app)
    {
        // ─── AI Chat proxy (Phase 1) ─────────────────────────────────────────────────
        // Thin JSON passthroughs to the Api /api/chat/* endpoints. Identity headers
        // (X-Internal-Key / X-User-Id / X-User-Role) are injected by InternalKeyHandler on ApiClient's
        // HttpClient. These are EXPLICIT routes (registered before the W1 catch-all) — they must NOT be
        // served by the catch-all forwarder (plan §2 Phase 1, §4). The catch-all is GET-pilot-only anyway.
        // Phase 2 streaming will add a dedicated SSE route here (plan §2 Phase 2).

        // Per-conversation model picker — open to any authenticated user (Api returns enabled models only).
        app.MapGet("/api-proxy/chat/models", async (ApiClient api) =>
        {
            var f = await api.ForwardGetAsync("chat/models");
            return Results.Content(f.Body, f.ContentType ?? "application/json", Encoding.UTF8, f.Status);
        }).RequireAuthorization();

        // Admin catalogue (all models incl. disabled) — superadmin only.
        app.MapGet("/api-proxy/chat/models/all", async (ApiClient api) =>
        {
            var f = await api.ForwardGetAsync("chat/models/all");
            return Results.Content(f.Body, f.ContentType ?? "application/json", Encoding.UTF8, f.Status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        // Add a model — superadmin only.
        app.MapPost("/api-proxy/chat/models", async (HttpContext ctx, ApiClient api) =>
        {
            using var sr = new StreamReader(ctx.Request.Body);
            var json = await sr.ReadToEndAsync();
            var (ok, body, status) = await api.PostRawAsync("chat/models", json);
            return Results.Content(body ?? "", "application/json", null, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        // Toggle a model — superadmin only.
        app.MapMethods("/api-proxy/chat/models/{id:guid}", new[] { "PATCH" }, async (Guid id, HttpContext ctx, ApiClient api) =>
        {
            using var sr = new StreamReader(ctx.Request.Body);
            var json = await sr.ReadToEndAsync();
            var (ok, body, status) = await api.PostRawAsync($"chat/models/{id}", json, method: "PATCH");
            return Results.Content(body ?? "", "application/json", null, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        // Delete a model — superadmin only.
        app.MapDelete("/api-proxy/chat/models/{id:guid}", async (Guid id, ApiClient api) =>
        {
            var (ok, body, status) = await api.PostRawAsync($"chat/models/{id}", "", method: "DELETE");
            return Results.Content(body ?? "", "application/json", null, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        // Auto-approve-writes setting — superadmin only (get + toggle).
        app.MapGet("/api-proxy/chat/settings/auto-approve", async (ApiClient api) =>
        {
            var f = await api.ForwardGetAsync("chat/settings/auto-approve");
            return Results.Content(f.Body, f.ContentType ?? "application/json", Encoding.UTF8, f.Status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapMethods("/api-proxy/chat/settings/auto-approve", new[] { "PATCH" }, async (HttpContext ctx, ApiClient api) =>
        {
            using var sr = new StreamReader(ctx.Request.Body);
            var json = await sr.ReadToEndAsync();
            var (ok, body, status) = await api.PostRawAsync("chat/settings/auto-approve", json, method: "PATCH");
            return Results.Content(body ?? "", "application/json", null, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        // "Allow AI chat for users" node-wide kill switch — superadmin only (get + toggle).
        app.MapGet("/api-proxy/chat/settings/chat-enabled", async (ApiClient api) =>
        {
            var f = await api.ForwardGetAsync("chat/settings/chat-enabled");
            return Results.Content(f.Body, f.ContentType ?? "application/json", Encoding.UTF8, f.Status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapMethods("/api-proxy/chat/settings/chat-enabled", new[] { "PATCH" }, async (HttpContext ctx, ApiClient api) =>
        {
            using var sr = new StreamReader(ctx.Request.Body);
            var json = await sr.ReadToEndAsync();
            var (ok, body, status) = await api.PostRawAsync("chat/settings/chat-enabled", json, method: "PATCH");
            return Results.Content(body ?? "", "application/json", null, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        // "Am I allowed to use chat?" — open to ANY authenticated user (NOT superadmin-gated). Used by
        // the nav link, the /AI page, and the homepage composer to decide whether to show chat entry points.
        app.MapGet("/api-proxy/chat/access", async (ApiClient api) =>
        {
            var f = await api.ForwardGetAsync("chat/access");
            return Results.Content(f.Body, f.ContentType ?? "application/json", Encoding.UTF8, f.Status);
        }).RequireAuthorization();

        // Effective TEXT model (read-only display label for the chat composer) — open to ANY
        // authenticated user (NOT superadmin-gated; mirrors /api-proxy/chat/models).
        app.MapGet("/api-proxy/chat/settings/effective-text-model", async (ApiClient api) =>
        {
            var f = await api.ForwardGetAsync("chat/settings/effective-text-model");
            return Results.Content(f.Body, f.ContentType ?? "application/json", Encoding.UTF8, f.Status);
        }).RequireAuthorization();

        // Pinned default models (text/vision/image-gen) — superadmin only (get + set).
        app.MapGet("/api-proxy/chat/settings/defaults", async (ApiClient api) =>
        {
            var f = await api.ForwardGetAsync("chat/settings/defaults");
            return Results.Content(f.Body, f.ContentType ?? "application/json", Encoding.UTF8, f.Status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        app.MapMethods("/api-proxy/chat/settings/defaults", new[] { "PATCH" }, async (HttpContext ctx, ApiClient api) =>
        {
            using var sr = new StreamReader(ctx.Request.Body);
            var json = await sr.ReadToEndAsync();
            var (ok, body, status) = await api.PostRawAsync("chat/settings/defaults", json, method: "PATCH");
            return Results.Content(body ?? "", "application/json", null, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        // List API keys — superadmin only.
        app.MapGet("/api-proxy/chat/keys", async (ApiClient api) =>
        {
            var f = await api.ForwardGetAsync("chat/keys");
            return Results.Content(f.Body, f.ContentType ?? "application/json", Encoding.UTF8, f.Status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        // Add an API key — superadmin only.
        app.MapPost("/api-proxy/chat/keys", async (HttpContext ctx, ApiClient api) =>
        {
            using var sr = new StreamReader(ctx.Request.Body);
            var json = await sr.ReadToEndAsync();
            var (ok, body, status) = await api.PostRawAsync("chat/keys", json);
            return Results.Content(body ?? "", "application/json", null, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        // Toggle an API key — superadmin only.
        app.MapMethods("/api-proxy/chat/keys/{id:guid}", new[] { "PATCH" }, async (Guid id, HttpContext ctx, ApiClient api) =>
        {
            using var sr = new StreamReader(ctx.Request.Body);
            var json = await sr.ReadToEndAsync();
            var (ok, body, status) = await api.PostRawAsync($"chat/keys/{id}", json, method: "PATCH");
            return Results.Content(body ?? "", "application/json", null, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        // Delete an API key — superadmin only.
        app.MapDelete("/api-proxy/chat/keys/{id:guid}", async (Guid id, ApiClient api) =>
        {
            var (ok, body, status) = await api.PostRawAsync($"chat/keys/{id}", "", method: "DELETE");
            return Results.Content(body ?? "", "application/json", null, statusCode: status);
        }).RequireAuthorization(policy => policy.RequireRole("superadmin"));

        // ─── AI Chat proxy (Phase 2) — SSE streaming + conversation history ───────────
        // See docs/ai-chat-implementation-plan.md §2 Phase 2 + §6 ("Streaming disambiguated").

        // DEDICATED SSE passthrough — MUST NOT be served by the W1 catch-all (it buffers via
        // ReadAsStringAsync, which would break streaming) and MUST NOT use Results.File (download/seek
        // semantics). Instead: forward with HttpCompletionOption.ResponseHeadersRead (api.SendForwardAsync),
        // set text/event-stream + X-Accel-Buffering:no on the Web response, and copy the upstream stream to
        // ctx.Response.Body chunk-by-chunk with a flush after each write. ctx.RequestAborted is forwarded so
        // a browser disconnect cancels the upstream Api call (which in turn cancels the OpenRouter stream).
        // Identity headers (X-Internal-Key / X-User-Id) are injected by InternalKeyHandler as usual.
        //
        // Non-SSE upstream responses (the Api writes a normal JSON error BEFORE committing to the stream —
        // e.g. 409 vault-locked, 400 bad request) are passed through as ordinary JSON with their status, so
        // the UI can render the error instead of a dead event-stream.
        app.MapPost("/api-proxy/chat/stream", async (HttpContext ctx, ApiClient api) =>
        {
            var upstreamReq = new HttpRequestMessage(HttpMethod.Post, "api/chat/stream");
            if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                upstreamReq.Content = new StreamContent(ctx.Request.Body);
                if (!string.IsNullOrEmpty(ctx.Request.ContentType))
                    upstreamReq.Content.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue(ctx.Request.ContentType);
            }

            HttpResponseMessage upstream;
            try
            {
                // Forward the client's abort token: closing the tab / navigating away cancels this send,
                // which cancels the Api-side OpenRouter stream (no wasted tokens/billing).
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

        // ─── AI Chat proxy (Phase 3) — confirm-gate SSE passthrough ───────────────────
        // Phase 3 human-in-the-loop: the /stream loop pauses on a write tool call (confirm_required) and
        // the user picks Allow/Deny. This route forwards that decision to the Api confirm endpoint, which
        // executes the write (or denial) and streams the CONTINUATION as a fresh SSE response. Same SSE
        // passthrough contract as /stream above (ResponseHeadersRead + per-chunk flush + abort forwarding;
        // non-SSE upstream = a JSON error passed through verbatim). See ChatEndpoints /confirm.

        app.MapPost("/api-proxy/chat/{conversationId:guid}/confirm", async (Guid conversationId, HttpContext ctx, ApiClient api) =>
        {
            var upstreamReq = new HttpRequestMessage(HttpMethod.Post, $"api/chat/stream/{conversationId}/confirm");
            if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                upstreamReq.Content = new StreamContent(ctx.Request.Body);
                if (!string.IsNullOrEmpty(ctx.Request.ContentType))
                    upstreamReq.Content.Headers.ContentType =
                        new System.Net.Http.Headers.MediaTypeHeaderValue(ctx.Request.ContentType);
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

        // Conversation history (thin JSON passthroughs — these do NOT need SSE handling). Identity headers
        // (X-User-Id in particular) are injected by InternalKeyHandler, and the Api scopes every read/write
        // to the caller's own user_id (plan §2 Phase 2: "a user must never see another user's conversations").

        app.MapGet("/api-proxy/chat/conversations", async (ApiClient api) =>
        {
            var f = await api.ForwardGetAsync("chat/conversations");
            return Results.Content(f.Body, f.ContentType ?? "application/json", Encoding.UTF8, f.Status);
        }).RequireAuthorization();

        // Homepage pinned chat — per-user, no role gate. GET returns {conversationId: guid|null};
        // DELETE clears the caller's pin (never deletes the conversation).
        app.MapGet("/api-proxy/chat/home-pinned", async (ApiClient api) =>
        {
            var f = await api.ForwardGetAsync("chat/home-pinned");
            return Results.Content(f.Body, f.ContentType ?? "application/json", Encoding.UTF8, f.Status);
        }).RequireAuthorization();

        app.MapDelete("/api-proxy/chat/home-pinned", async (ApiClient api) =>
        {
            var (ok, body, status) = await api.PostRawAsync("chat/home-pinned", "", method: "DELETE");
            return Results.Content(body ?? "", "application/json", null, statusCode: status);
        }).RequireAuthorization();

        app.MapGet("/api-proxy/chat/conversations/{id:guid}/messages", async (Guid id, ApiClient api) =>
        {
            var f = await api.ForwardGetAsync($"chat/conversations/{id}/messages");
            return Results.Content(f.Body, f.ContentType ?? "application/json", Encoding.UTF8, f.Status);
        }).RequireAuthorization();

        app.MapMethods("/api-proxy/chat/conversations/{id:guid}", new[] { "PATCH" }, async (Guid id, HttpContext ctx, ApiClient api) =>
        {
            using var sr = new StreamReader(ctx.Request.Body);
            var json = await sr.ReadToEndAsync();
            var (ok, body, status) = await api.PostRawAsync($"chat/conversations/{id}", json, method: "PATCH");
            return Results.Content(body ?? "", "application/json", null, statusCode: status);
        }).RequireAuthorization();

        app.MapDelete("/api-proxy/chat/conversations/{id:guid}", async (Guid id, ApiClient api) =>
        {
            var (ok, body, status) = await api.PostRawAsync($"chat/conversations/{id}", "", method: "DELETE");
            return Results.Content(body ?? "", "application/json", null, status);
        }).RequireAuthorization();

        // Phase 5: serve a chat attachment's bytes (vision uploads + generated images). Thin passthrough to
        // the Api GET /api/chat/attachments/{id}; ownership is enforced API-side (chat_attachment → message
        // → conversation(user_id)). Read to bytes (matches /api-proxy/media/{id}) so Results.File owns the
        // payload cleanly. CSP img-src allows 'self' so this renders without any CSP change.
        app.MapGet("/api-proxy/chat/attachments/{id:guid}", async (Guid id, ApiClient api) =>
        {
            var upstreamReq = new HttpRequestMessage(HttpMethod.Get, $"api/chat/attachments/{id}");
            HttpResponseMessage upstream;
            try { upstream = await api.SendForwardAsync(upstreamReq); }
            catch { return Results.StatusCode(502); }

            using (upstream)
            {
                if (!upstream.IsSuccessStatusCode) return Results.StatusCode((int)upstream.StatusCode);
                var data = await upstream.Content.ReadAsByteArrayAsync();
                var contentType = upstream.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                return Results.File(data, contentType);
            }
        }).RequireAuthorization();
    }
}
