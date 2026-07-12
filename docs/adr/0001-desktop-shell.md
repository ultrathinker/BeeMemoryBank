# ADR 0001: Desktop Shell WebView Package Selection

## Status
Accepted

## Context
We need to embed the BeeMemoryBank Web UI inside an Avalonia desktop application (`BeeMemoryBank.Desktop`). This shell should feel like a native desktop application, replacing the need for the user to open a separate browser.
The embedded WebView must support:
- Heavy JavaScript execution (including complex UI features like a graph view).
- Standard web form controls (e.g. textarea-based editors).
- File downloads.
- Reliable visibility state transitions (surviving the window being minimized, hidden, and shown repeatedly via the system tray).

## Evaluated Options

1. **`Avalonia.Controls.WebView`** (Official Avalonia Control)
   - **Type**: First-party, open-source component (transitioned to FOSS from Avalonia Accelerate).
   - **Architecture**: Leverages native platform web rendering engines: WebView2 (Chromium) on Windows, WebKit on macOS/iOS, and WPE/WebKitGTK on Linux.
   - **Pros**: Smallest application size (uses the pre-installed Windows WebView2 runtime), official support, cross-platform APIs, simple XAML usage without custom initialization in `Program.cs`.
   - **Cons**: Behaviour on non-Windows platforms requires testing and depends on system dependencies (like `webkit2gtk` on Linux).

2. **`WebViewControl-Avalonia` / `OutSystems.WebView`** (CefGlue/Chromium)
   - **Type**: Community/Corporate wrapper.
   - **Architecture**: Bundles Chromium Embedded Framework (CEF) via CefGlue.
   - **Pros**: Identical Chromium engine across all desktop platforms (extremely consistent rendering).
   - **Cons**: Massive distribution size (hundreds of megabytes due to bundled CEF), complex setup, and higher memory usage.

3. **Legacy Third-Party Wrappers** (e.g. `WebView.Avalonia` / `jkh404/Avalonia.WebView2`)
   - **Type**: Unofficial wrappers.
   - **Pros**: Historically filled the gap before the official WebView became FOSS.
   - **Cons**: Mostly unmaintained, incompatible with Avalonia 12.x, and requires tedious initialization hacks in `App.axaml.cs` and `Program.cs`.

## Decision
We chose **`Avalonia.Controls.WebView`** (specifically version **12.0.1** to align with Avalonia **12.1.0**).

### Justification
- **First-party support:** Officially maintained by the Avalonia team, guaranteeing long-term support and alignment with the framework's release cycles.
- **Native performance & size:** Uses the platform's built-in WebView2 runtime on Windows, avoiding bloating the installer with a separate Chromium build.
- **Developer Experience:** Standard, clean XAML registration (`<NativeWebView />`) with no boilerplate initialization code.

## Verification Checklist & Findings

What was actually verified end-to-end in this environment: the full process
chain (`BeeMemoryBank.Desktop.exe` → `bmbd.exe --auto` → real `Api.exe` +
`Web.exe`) starts correctly, `.runtime.json`/`node.status.json` are written
with the right PIDs/URLs, and the front's `/health` endpoint responds
correctly through a WebView pointed at it — confirmed via headless HTTP
probing and process-tree inspection, **not** by a human driving the actual
rendered UI. No screenshot or interactive tool was available in this pass.

The items below are **expectations based on WebView2/Chromium's known
behavior**, not independently confirmed against this app's actual pages —
they need a real visual/interactive pass before being trusted:

* **JS-heavy content (graph view), textarea-based editor, file downloads:**
  not yet interactively verified. WebView2 is Chromium-based and these
  should work the same as modern Edge, but "should" isn't "verified" -
  treat this app's specific pages (D3 graph, EasyMDE editor, image
  drag&drop) as unconfirmed until someone actually drives the UI.
* **Window survival (hide/show to tray):** the close-to-tray mechanism
  (canceling `Closing`, calling `Hide()` instead of destroying the window)
  is implemented and matches the intended pattern for preserving WebView
  DOM/JS state across hide/show cycles, but wasn't observed running for the
  originally-planned 30 minutes, nor visually confirmed to actually
  preserve state rather than e.g. silently reloading.
* One resource-contention issue was found and is **not yet fixed**:
  forcefully killing `BeeMemoryBank.Desktop.exe` (e.g. via Task Manager, or
  a crash) can leave its `msedgewebview2.exe` helper processes running,
  holding locks on the WebView2 profile directory - a rapid relaunch right
  after can then fail with a COM `HRESULT 0x800700AA` ("resource in use").
  Normal exit via the tray's "Exit" item disposes the WebView properly and
  doesn't hit this; only abnormal termination does. No equivalent of
  bmbd's Job Object protection exists for Desktop's own WebView2 children.

### Known Gaps & Limitations
- **macOS/Linux verification:** Visually and interactively unverified due to the Windows-only test environment. Non-Windows behavior is noted as a gap that must be validated in future stages.
- **Interactive/visual verification of the checklist above**: needs a human driving the actual UI - not something that could be completed headlessly in this pass.
