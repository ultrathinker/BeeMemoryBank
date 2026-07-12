# Task: opt-in HTTPS front on :5311 + "Connect a device" QR page (superplan §5 Ярус 1, Этап 5)

## Context — read carefully, this touches the production request path

`libs/BeeMemoryBank.Core/Services/LocalCaService.cs` (just merged) gives
you everything you need for certs:
```csharp
public X509Certificate2? GetOrCreateCaCertificate()
public X509Certificate2? GetOrCreateLeafCertificate(bool forceReissue = false)
public byte[]? GetCaCertificateDer()
public string? GetCaCertificatePem()
```
`GetOrCreateLeafCertificate()` returns a cert WITH its private key
attached (`.HasPrivateKey == true`), ready to hand to Kestrel.

The front is `desktop/BeeMemoryBank.Node/Front/NodeFront.cs` +
`desktop/BeeMemoryBank.Node/Program.cs`'s `BuildFront(string[] webArgs, ...)`.
Currently: `WebApplication.CreateBuilder(webArgs)` where `webArgs` is
`{"--urls", "http://127.0.0.1:5310"}` (or a `:0` fallback) — **plain
HTTP only, no Kestrel HTTPS/cert config exists anywhere in this repo.**
`NodeFront.RegisterServices(IServiceCollection)` currently only sets
`KestrelServerOptions.Limits.MaxRequestBodySize` — this is the natural
place to ALSO add HTTPS listener config.

**This is the production request path every existing feature depends
on (UI, MCP, sync).** The existing plain-HTTP `:5310` listener must
keep working completely unchanged in every case — HTTPS on `:5311` is
an ADDITIVE second listener, opt-in, never a replacement.

## Goal

1. **Opt-in HTTPS listener.** Add a way to enable a second Kestrel
   listener on `:5311` serving HTTPS, using `LocalCaService`'s leaf
   cert via `ConfigureKestrel`'s `ServerCertificateSelector` (or
   `UseHttps` with a selector callback — research the current, correct
   .NET 10 Kestrel API for a certificate that can be swapped without a
   restart, since the leaf cert rotates every 90 days: the callback
   should call `LocalCaService.GetOrCreateLeafCertificate()` fresh on
   each TLS handshake rather than capturing a cert once at startup, so
   rotation "just works" without needing a process restart). Opt-in
   means: gated behind an explicit flag (an env var like
   `BMB_HTTPS_ENABLED=1`, or a constructor/method parameter you plumb
   through `BuildFront` — your call on the exact mechanism, but it must
   default to OFF/absent, matching "по кнопке" in the superplan — a
   later task wires an actual UI toggle, you're just building the
   capability here). When disabled (the default), behavior must be
   byte-for-byte identical to today: only the plain HTTP listener runs.
2. **Windows firewall rule**, opt-in alongside the HTTPS listener
   (only add it when HTTPS is actually being enabled) — research the
   current correct way to do this from .NET (COM interop via
   `INetFwPolicy2`, or shelling out to `netsh advfirewall firewall add rule`
   — there is no existing precedent in this codebase for either
   approach, pick the more robust one and justify your choice). Must
   require no elevation beyond what running as the current user already
   has for `CurrentUser`-scope operations — if the firewall API
   genuinely requires elevation, say so explicitly in your report
   rather than silently failing or crashing; a caught, logged failure
   that leaves the HTTPS listener itself still running (just without an
   automatic firewall rule) is an acceptable degraded outcome.
3. **"Connect a device" page**: a new Razor page (suggest
   `server/BeeMemoryBank.Web/Pages/Connect.cshtml` — check how other
   simple informational pages in this codebase are structured first)
   showing a QR code (add the `QRCoder` NuGet package — not referenced
   anywhere yet) encoding `https://<lan-ip>:5311`, plus a
   `GET /connect/ca.crt` download endpoint (a new minimal endpoint,
   check `server/BeeMemoryBank.Web/Endpoints/` for the existing
   minimal-endpoint registration convention) serving
   `LocalCaService.GetCaCertificateDer()` bytes with the correct
   `application/x-x509-ca-cert` content type, plus brief iOS/Android CA
   install instructions (a short, honest paragraph — "open this file on
   your phone, it will prompt to install a certificate profile; on
   iOS you then additionally need Settings → General → About →
   Certificate Trust Settings → enable full trust for this CA" — this
   is a real, well-known iOS quirk, don't skip it). You'll need the
   node's LAN IP for the QR — reuse whatever this codebase already has
   for LAN-IP enumeration (check `LocalCaService`'s own
   `GetLanIPv4Addresses` — it's currently private; either make it
   internal/public for reuse, or duplicate the short logic — your call,
   but don't diverge in behavior from what the leaf cert's own SAN list
   already contains).

## Hard constraints

- **Files you may touch:** `desktop/BeeMemoryBank.Node/Front/NodeFront.cs`,
  `desktop/BeeMemoryBank.Node/Program.cs` (only the `BuildFront`
  function and its immediate call site — do not touch anything else in
  this file, especially not the child-process-spawning/orchestration
  logic above it), a new Windows-firewall-helper file (suggest
  `libs/BeeMemoryBank.Core/Services/FirewallService.cs`, matching
  `AutostartService`'s Windows-guard conventions), a new
  `server/BeeMemoryBank.Web/Pages/Connect.cshtml`(`.cs`), a new minimal
  endpoint for `ca.crt`, and `server/BeeMemoryBank.Web/BeeMemoryBank.Web.csproj`
  (QRCoder reference). New test files as appropriate.
- Do NOT touch `LocalCaService.cs` itself (only call its existing public
  API) or anything under `Services/Acme/`, `DdnsUpdater.cs`, or the
  `Mdns*` files — all separate, unrelated, already-merged work.
- **Do not break the existing plain-HTTP behavior under any
  circumstance** — this is the single most important constraint of
  this task. Verify explicitly (see DoD).

## Definition of done

1. `dotnet build` succeeds for the touched projects.
2. **Prove the existing HTTP-only path is unaffected**: with HTTPS
   disabled (the default), start the front for real (or via the
   existing `BuildFront`-based tests if this codebase has any — check
   `tests/BeeMemoryBank.Node.Tests/`) and confirm it behaves exactly as
   before (binds `:5310` HTTP, proxies to Api/Web, nothing about the
   startup sequence changed).
3. **Prove HTTPS actually works when enabled**: start the front with
   HTTPS enabled, connect to `:5311` with TLS, confirm the server
   presents the `LocalCaService`-issued leaf certificate and that a
   client trusting the CA (build an `HttpClient` with a custom
   `HttpClientHandler.ServerCertificateCustomValidationCallback` that
   validates against the CA, mirroring the chain-validation approach
   `LocalCaServiceTests` already uses) can complete a real HTTPS
   request. This is the load-bearing proof for this whole task — don't
   skip it or only test at the unit level.
4. Report back: the exact mechanism you used to gate HTTPS on/off, the
   Kestrel API you used for the swappable-cert selector (exact method/
   type names, confirmed by what actually compiled — don't guess from
   memory), what you achieved for the firewall rule (real, or
   documented limitation), and confirm the QR page + ca.crt endpoint
   work via a real HTTP GET.
