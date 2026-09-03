using Microsoft.AspNetCore.Http;

namespace BeeMemoryBank.Hosting.AspNetCore;

/// <summary>
/// Response hardening for endpoints that echo back bytes a user or agent uploaded, under a content
/// type that user also chose.
/// </summary>
public static class UserContentResponseHeaders
{
    /// <summary>
    /// Marks the response as untrusted user content.
    ///
    /// <para>
    /// <c>Content-Security-Policy: sandbox</c> — with no allow-tokens — is the load-bearing one.
    /// Uploads accept <c>image/svg+xml</c>, and an SVG document is an active document: script
    /// inside it runs when the file is opened directly by URL. The site's own CSP allows
    /// <c>script-src 'self' 'unsafe-inline'</c>, and media is served from the same origin that
    /// holds the session cookie, so a stored SVG was a full session-takeover primitive — a
    /// folder-restricted agent could plant one and have it run in a superadmin's browser.
    /// An empty sandbox denies scripts and drops the response to an opaque origin, closing that
    /// without touching how the file renders: sandbox applies to DOCUMENT loads, and an
    /// <c>&lt;img src&gt;</c> is not a document load (SVG-in-img never runs script anyway).
    /// </para>
    ///
    /// <para>
    /// <c>X-Content-Type-Options: nosniff</c> stops the browser from re-interpreting a payload as
    /// something more dangerous than its declared type — e.g. HTML smuggled in under image/png.
    /// </para>
    /// </summary>
    public static void ApplyTo(HttpResponse response)
    {
        response.Headers["Content-Security-Policy"] = "sandbox";
        response.Headers["X-Content-Type-Options"] = "nosniff";
    }
}
