using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BeeMemoryBank.AppPaths;
using BeeMemoryBank.Profiles;

namespace BeeMemoryBank.Desktop.Services;

/// <summary>
/// Outcome of a profile switch attempt. Carries everything the caller (MainWindow / future
/// tray UI) needs to render the result: on success the new profile and its front URL, on
/// failure a human-readable explanation.
///
/// The revert-to-previous-profile case (target failed to start, but the engine successfully
/// restarted the previous profile) is still <c>Success = false</c> - the caller's REQUESTED
/// switch did not happen - but ALSO populates <see cref="Profile"/>/<see cref="FrontUrl"/>
/// with the reverted node's actual values. This matters because bmbd falls back to an
/// OS-assigned port when its preferred one is taken, so the node that comes back up during a
/// revert may not be listening on the SAME url the caller was on before the switch attempt
/// started - a caller that ignores these fields and just keeps using its own stale
/// pre-switch url would point the WebView at a dead endpoint.
/// </summary>
public sealed record SwitchResult
{
    public bool Success { get; init; }
    public string? FrontUrl { get; init; }
    public ProfileEntry? Profile { get; init; }
    public string? ErrorMessage { get; init; }

    public static SwitchResult Ok(ProfileEntry profile, string frontUrl)
        => new() { Success = true, Profile = profile, FrontUrl = frontUrl };

    public static SwitchResult Error(string message)
        => new() { Success = false, ErrorMessage = message };

    /// <summary>The requested switch failed, but the engine reverted to <paramref name="revertedProfile"/>,
    /// which is now listening on <paramref name="revertedFrontUrl"/> (possibly a different
    /// port than before). <see cref="Success"/> is still false.</summary>
    public static SwitchResult Reverted(ProfileEntry revertedProfile, string revertedFrontUrl, string message)
        => new() { Success = false, Profile = revertedProfile, FrontUrl = revertedFrontUrl, ErrorMessage = message };
}

/// <summary>
/// Outcome of <see cref="ProfileSwitchService.RelocateAsync"/>. <see cref="FrontUrl"/> is set
/// whenever the active profile's node is running again afterwards (from the new folder on
/// success, from the old one after a failure), so the caller can re-point the WebView.
/// </summary>
public sealed record RelocateResult
{
    public bool Success { get; init; }
    public ProfileEntry? Profile { get; init; }
    public string? OldDataPath { get; init; }
    public string? FrontUrl { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>True when the move was refused before anything was stopped or copied.</summary>
    public bool Rejected { get; init; }

    public static RelocateResult Ok(ProfileEntry profile, string oldDataPath, string? frontUrl)
        => new() { Success = true, Profile = profile, OldDataPath = oldDataPath, FrontUrl = frontUrl };

    public static RelocateResult Error(string message)
        => new() { Success = false, ErrorMessage = message };

    public static RelocateResult Refused(string message)
        => new() { Success = false, Rejected = true, ErrorMessage = message };

    public static RelocateResult Failed(ProfileEntry profile, string message, string frontUrl)
        => new() { Success = false, Profile = profile, ErrorMessage = message, FrontUrl = frontUrl };
}

/// <summary>
/// Clears all cookies from the embedded WebView. Abstracted behind an interface so that
/// <see cref="ProfileSwitchService"/> stays testable without a real <c>NativeWebView</c>
/// (the WebView is an Avalonia/platform type that cannot be instantiated headlessly in a
/// unit test). The production implementation is <see cref="NativeWebViewCookieClearer"/>.
/// </summary>
public interface IWebViewCookieClearer
{
    Task ClearAllCookiesAsync();
}

/// <summary>
/// Orchestrates switching the active profile/vault: it stops the current node, clears the
/// WebView session, starts the target node, and — on failure — attempts to revert to the
/// previous profile. It builds on <see cref="INodeLifecycleService"/> and
/// <see cref="ProfileService"/> as primitive blocks and never duplicates their logic.
///
/// The same <see cref="INodeLifecycleService"/> instance MUST be shared with the code that
/// originally hosted/attached the current node: <see cref="INodeLifecycleService.StopAsync"/>
/// is ownership-aware, and only the instance that spawned the process can stop it gracefully.
/// </summary>
public sealed class ProfileSwitchService : IDisposable
{
    // Update-state HTTP guard is bounded and fail-open (see IsUpdateApplyingAsync): a slow
    // or dead endpoint must never block an ordinary switch. Keep the ceiling tight so the
    // guard adds at most a few seconds of latency in the common (idle) case.
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StopGracefulTimeout = TimeSpan.FromSeconds(15);

    private readonly ProfileService _profiles;
    private readonly INodeLifecycleService _nodeLifecycle;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    // Single-flight guard for the WHOLE switch operation. NodeLifecycleService itself
    // serializes its own StartOrAttachAsync/StopAsync calls, but that alone does not stop two
    // overlapping SwitchToAsync calls from interleaving at the orchestration level: e.g.
    // switch-to-B's Stop(A)+Start(B) and switch-to-C's Stop(no-op)+Start(C) could each acquire
    // NodeLifecycleService's gate in turn, and whichever Start call runs LAST simply
    // overwrites the other's successfully-started (not failed - so the orphan-cleanup catch
    // path never fires) process, permanently losing the ability to stop it. Rejecting a
    // second concurrent switch outright (rather than queueing it) keeps behavior predictable:
    // the caller asked to switch to a specific target and should not have that silently
    // reordered behind an unrelated switch it did not request.
    private readonly SemaphoreSlim _switchGate = new(1, 1);

    /// <summary>
    /// Creates a switch service that owns its own <see cref="HttpClient"/> for the update
    /// guard. Use this in production wiring.
    /// </summary>
    public ProfileSwitchService(ProfileService profiles, INodeLifecycleService nodeLifecycle)
        : this(profiles, nodeLifecycle, CreateDefaultHttpClient(), ownsHttpClient: true)
    {
    }

    /// <summary>
    /// Creates a switch service with an explicit <see cref="HttpClient"/>. Intended for
    /// tests (e.g. pointing at a local stub server) but also usable in production where a
    /// shared, pre-configured client is preferred.
    /// </summary>
    internal ProfileSwitchService(ProfileService profiles, INodeLifecycleService nodeLifecycle, HttpClient httpClient)
        : this(profiles, nodeLifecycle, httpClient, ownsHttpClient: false)
    {
    }

    private ProfileSwitchService(ProfileService profiles, INodeLifecycleService nodeLifecycle, HttpClient httpClient, bool ownsHttpClient)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _nodeLifecycle = nodeLifecycle ?? throw new ArgumentNullException(nameof(nodeLifecycle));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
    }

    /// <summary>
    /// Switches the active node from the current profile (<paramref name="currentProfileId"/>,
    /// if any) to <paramref name="targetProfileId"/>.
    /// </summary>
    /// <param name="targetProfileId">The profile to switch to.</param>
    /// <param name="currentProfileId">
    /// The profile that is currently active (profile A), used both as the update-guard key
    /// source and as the revert target if the new profile fails to start. Pass <c>null</c>
    /// on a first launch / when nothing is running (no stop, no revert will be attempted).
    /// </param>
    /// <param name="activeFrontUrl">
    /// Front URL of the currently-active node, used solely for the update-in-progress guard.
    /// Pass <c>null</c> to skip the guard entirely (e.g. first launch, or when explicitly
    /// not testing it).
    /// </param>
    /// <param name="cookieClearer">WebView cookie clearer (session hygiene between profiles).</param>
    /// <param name="progress">Optional receiver for human-readable status lines.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// The signature carries BOTH <paramref name="currentProfileId"/> and
    /// <paramref name="activeFrontUrl"/> (rather than just the front URL from the draft):
    /// <paramref name="currentProfileId"/> is required for step 7 (revert to A) — without it
    /// the service could not know which vault to fall back to, nor where to read the
    /// internal-key for the guard from. <paramref name="activeFrontUrl"/> is kept separate
    /// because the guard needs a live base URL, which is not derivable from the profile id
    /// alone (the node may be down or on an auto-selected port).
    /// </remarks>
    public async Task<SwitchResult> SwitchToAsync(
        string targetProfileId,
        string? currentProfileId,
        string? activeFrontUrl,
        IWebViewCookieClearer cookieClearer,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        // Single-flight: reject a second concurrent switch outright rather than queueing it
        // (see _switchGate's own comment for why queueing would be worse).
        if (!await _switchGate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            return SwitchResult.Error("Another profile switch is already in progress. Please wait for it to finish.");
        }

        try
        {
            return await SwitchToCoreAsync(targetProfileId, currentProfileId, activeFrontUrl, cookieClearer, progress, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    private async Task<SwitchResult> SwitchToCoreAsync(
        string targetProfileId,
        string? currentProfileId,
        string? activeFrontUrl,
        IWebViewCookieClearer cookieClearer,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetProfileId))
        {
            return SwitchResult.Error("Target profile id must not be empty.");
        }

        if (cookieClearer == null)
        {
            throw new ArgumentNullException(nameof(cookieClearer));
        }

        // ── Step 1: Guard — refuse to switch while an update is mid-apply on the active node.
        // Fail-open: any error (timeout, dead endpoint, parse failure) is treated as "no
        // update in progress". Blocking an ordinary switch because of a transient network
        // blip would be worse than very occasionally missing a real apply-in-progress.
        if (!string.IsNullOrEmpty(activeFrontUrl))
        {
            var (blocked, reason) = await IsUpdateApplyingAsync(activeFrontUrl, currentProfileId, ct).ConfigureAwait(false);
            if (blocked)
            {
                return SwitchResult.Error(
                    $"Cannot switch profiles right now — an update is being applied on the active node ({reason}). " +
                    "Please wait for it to finish and try again.");
            }
        }

        // A cancellation that arrived during the guard's HTTP call is swallowed there as
        // fail-open (by design - a cancelled guard must not be mistaken for "update is
        // applying"). But proceeding into Stop(A)/Start(B) with an already-cancelled token is
        // a different problem: StopAsync would link that token and could skip straight to a
        // hard Kill of the perfectly-healthy current node instead of the intended graceful
        // stdin-close wait, just because the caller cancelled for an unrelated reason (e.g.
        // the window closing). Check explicitly rather than let that happen implicitly.
        if (ct.IsCancellationRequested)
        {
            return SwitchResult.Error("Profile switch was cancelled before it started.");
        }

        // ── Step 2: Resolve target profile.
        ProfileEntry targetProfile;
        try
        {
            targetProfile = _profiles.GetById(targetProfileId);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException)
        {
            return SwitchResult.Error(ex.Message);
        }

        // Resolve the previous (current) profile for stop logging + revert. Best-effort: if
        // it can't be resolved we still proceed (no stop to do, nothing to revert to).
        ProfileEntry? currentProfile = null;
        if (!string.IsNullOrEmpty(currentProfileId))
        {
            try { currentProfile = _profiles.GetById(currentProfileId); }
            catch (Exception ex)
            {
                Debug.WriteLine($"ProfileSwitchService: could not resolve current profile '{currentProfileId}' for revert: {ex.Message}");
            }
        }

        // ── Step 3: Stop the current node (only if there was one). Ownership-aware: a hosted
        // node is gracefully stopped; an attached (foreign) node is left untouched.
        if (currentProfile != null)
        {
            progress?.Report($"Stopping current node (profile '{currentProfile.Name}')...");
            try
            {
                await _nodeLifecycle.StopAsync(StopGracefulTimeout, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // StopAsync is best-effort at the switch boundary too: log and continue.
                // The port will be reclaimed by the OS as the process dies even if the wait
                // threw; failing here would leave the user stranded mid-switch.
                Debug.WriteLine($"ProfileSwitchService: error stopping current node: {ex.Message}");
            }
        }

        // ── Step 4: WebView hygiene — clear cookies of the just-stopped profile. Secondary
        // defense: the per-DB session store already rejects a foreign session cookie, so a
        // failure here must NOT abort the switch (log + continue).
        progress?.Report("Clearing WebView session...");
        try
        {
            await cookieClearer.ClearAllCookiesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ProfileSwitchService: cookie clear failed (continuing anyway): {ex.Message}");
        }

        // ── Step 5: Start / attach the target node.
        progress?.Report($"Starting node for profile '{targetProfile.Name}'...");
        var startResult = await _nodeLifecycle.StartOrAttachAsync(targetProfile.DataPath, progress, ct).ConfigureAwait(false);

        // The rescue found conflicting data at the target vault and copied it into a fresh,
        // unregistered recovered-<date> vault instead. Register a profile for it now so it shows
        // up in Manage Storages instead of sitting invisible on disk (same as the equivalent
        // handling in MainWindow.HostOrAttachAsync for the first-launch path) - this can be set
        // REGARDLESS of whether the start itself went on to succeed, since the rescue already
        // physically happened before the start attempt.
        if (!string.IsNullOrEmpty(startResult.RecoveredVaultDir))
        {
            try
            {
                var recoveredName = $"Recovered {DateTime.Now:yyyy-MM-dd HH:mm}";
                _profiles.AddProfile(recoveredName, startResult.RecoveredVaultDir);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ProfileSwitchService: failed to register recovered vault as a profile: {ex.Message}");
            }
        }

        // ── Step 6: Success — record last-used STRICTLY after a successful start.
        if (startResult.Success && !string.IsNullOrEmpty(startResult.FrontUrl))
        {
            try
            {
                _profiles.SetLastUsed(targetProfileId);
            }
            catch (Exception ex)
            {
                // Persisting lastUsed is best-effort; don't turn a successful switch into a
                // failure just because the registry write hiccupped.
                Debug.WriteLine($"ProfileSwitchService: SetLastUsed failed after successful start: {ex.Message}");
            }

            progress?.Report($"Switched to profile '{targetProfile.Name}'.");
            return SwitchResult.Ok(targetProfile, startResult.FrontUrl!);
        }

        // ── Step 7: Start of B failed — attempt to revert to A so the app stays usable.
        var failureMsg = startResult.ErrorMessage ?? "unknown error";
        progress?.Report($"Failed to start profile '{targetProfile.Name}': {failureMsg}");

        if (currentProfile == null)
        {
            // Nothing was running before and nothing to fall back to.
            return SwitchResult.Error(
                $"Failed to start profile '{targetProfile.Name}': {failureMsg}. No previous profile to revert to.");
        }

        progress?.Report($"Reverting to previous profile '{currentProfile.Name}'...");
        NodeLifecycleResult revertResult;
        try
        {
            revertResult = await _nodeLifecycle.StartOrAttachAsync(currentProfile.DataPath, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return SwitchResult.Error(
                $"Failed to switch to profile '{targetProfile.Name}': {failureMsg}. " +
                $"Reverting to previous profile '{currentProfile.Name}' also failed: {ex.Message}");
        }

        if (revertResult.Success && !string.IsNullOrEmpty(revertResult.FrontUrl))
        {
            // App is back on profile A and usable — but the requested switch did not succeed.
            // lastUsed is unchanged here (we never set it for B), so it still correctly points
            // at A. Carry the REVERTED node's actual FrontUrl/Profile: bmbd falls back to an
            // OS-assigned port if its preferred one is taken, so A may now be listening on a
            // different url than it was before the switch attempt started - the caller must
            // re-sync to this url, not keep using whatever it had before.
            return SwitchResult.Reverted(
                currentProfile, revertResult.FrontUrl!,
                $"Failed to switch to profile '{targetProfile.Name}': {failureMsg}. " +
                $"Reverted to previous profile '{currentProfile.Name}'.");
        }

        // Both B and the revert to A failed — caller must surface this; no further auto-retry.
        var revertFailure = revertResult.ErrorMessage ?? "unknown error";
        return SwitchResult.Error(
            $"Failed to switch to profile '{targetProfile.Name}': {failureMsg}. " +
            $"Attempt to revert to previous profile '{currentProfile.Name}' also failed: {revertFailure}.");
    }

    /// <summary>
    /// Moves a profile's data to <paramref name="newDataPath"/>: copies the vault (verified),
    /// repoints the profile and, when it is the active one, restarts its node from the new
    /// place. The old folder is left untouched; the caller decides what to do with it.
    /// </summary>
    /// <remarks>
    /// Shares the single-flight gate with <see cref="SwitchToAsync"/>: a move and a switch both
    /// stop and start the hosted node and must never interleave. On any failure the profile
    /// keeps (or gets back) its old path, and an active profile is started again from there.
    /// </remarks>
    public async Task<RelocateResult> RelocateAsync(
        string profileId,
        string newDataPath,
        string? activeProfileId,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (!await _switchGate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            return RelocateResult.Refused("Another profile operation is already in progress. Please wait for it to finish.");
        }

        try
        {
            return await RelocateCoreAsync(profileId, newDataPath, activeProfileId, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    private async Task<RelocateResult> RelocateCoreAsync(
        string profileId,
        string newDataPath,
        string? activeProfileId,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        ProfileEntry profile;
        try
        {
            profile = _profiles.GetById(profileId);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException)
        {
            return RelocateResult.Refused(ex.Message);
        }

        var oldPath = profile.DataPath;
        var targetError = VaultCopier.ValidateTarget(oldPath, newDataPath);
        if (targetError != null)
        {
            return RelocateResult.Refused(targetError);
        }

        var isActive = string.Equals(profileId, activeProfileId, StringComparison.Ordinal);
        if (!isActive && VaultFiles.IsLockedByNode(oldPath))
        {
            return RelocateResult.Refused(
                $"Profile '{profile.Name}' is in use by another running Bee Memory Bank node. Stop it and try again.");
        }

        if (isActive)
        {
            progress?.Report($"Stopping profile '{profile.Name}'...");
            try
            {
                await _nodeLifecycle.StopAsync(StopGracefulTimeout, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ProfileSwitchService: error stopping node before move: {ex.Message}");
            }

            if (VaultFiles.IsLockedByNode(oldPath))
            {
                return await RestartAfterFailedMoveAsync(profile, oldPath,
                    "The profile's node did not stop, so its data cannot be copied safely.", progress, ct).ConfigureAwait(false);
            }
        }

        var destinationExisted = Directory.Exists(newDataPath);
        try
        {
            await Task.Run(() => VaultCopier.CopyVerified(oldPath, newDataPath, progress, ct), ct).ConfigureAwait(false);
            _profiles.SetDataPath(profileId, newDataPath);
        }
        catch (Exception ex)
        {
            if (!destinationExisted)
            {
                // Only a folder this move created is ours to clean up; a pre-existing (empty)
                // folder the user picked is left in place.
                try { Directory.Delete(newDataPath, recursive: true); }
                catch (Exception cleanupEx) { Debug.WriteLine($"ProfileSwitchService: partial copy cleanup failed: {cleanupEx.Message}"); }
            }

            var message = $"Could not copy the data: {ex.Message}";
            return isActive
                ? await RestartAfterFailedMoveAsync(profile, oldPath, message, progress, ct).ConfigureAwait(false)
                : RelocateResult.Error(message);
        }

        var moved = _profiles.GetById(profileId);
        if (!isActive)
        {
            progress?.Report($"Moved profile '{moved.Name}'.");
            return RelocateResult.Ok(moved, oldPath, frontUrl: null);
        }

        progress?.Report($"Starting profile '{moved.Name}' from the new folder...");
        var start = await _nodeLifecycle.StartOrAttachAsync(moved.DataPath, progress, ct).ConfigureAwait(false);
        if (start.Success && !string.IsNullOrEmpty(start.FrontUrl))
        {
            return RelocateResult.Ok(moved, oldPath, start.FrontUrl);
        }

        // The copy is fine on disk but the node would not start from it: go back to the
        // original folder, which was never touched.
        try { _profiles.SetDataPath(profileId, oldPath); }
        catch (Exception ex) { Debug.WriteLine($"ProfileSwitchService: could not restore old data path: {ex.Message}"); }

        return await RestartAfterFailedMoveAsync(profile, oldPath,
            $"The profile did not start from the new folder ({start.ErrorMessage ?? "unknown error"}). " +
            $"It stays in {oldPath}; the copy in {newDataPath} was not removed.", progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Points the ACTIVE profile at a folder that already holds a vault and restarts its node
    /// from there (the "Open an existing profile" path of the first-run wizard). Nothing is
    /// copied; the previous folder is left as it is. On failure the old path is restored and
    /// the node started again from it.
    /// </summary>
    public async Task<RelocateResult> OpenFolderAsActiveAsync(
        string activeProfileId,
        string folder,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        if (!await _switchGate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            return RelocateResult.Refused("Another profile operation is already in progress. Please wait for it to finish.");
        }

        try
        {
            ProfileEntry profile;
            try
            {
                profile = _profiles.GetById(activeProfileId);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException)
            {
                return RelocateResult.Refused(ex.Message);
            }

            if (VaultFiles.Inspect(folder) != VaultFolderState.Vault)
            {
                return RelocateResult.Refused(
                    $"No Bee Memory Bank profile was found in this folder (it has no {VaultFiles.DatabaseFileName}).");
            }
            if (VaultFiles.IsLockedByNode(folder))
            {
                return RelocateResult.Refused("This profile is in use by another running Bee Memory Bank node.");
            }

            var oldPath = profile.DataPath;
            try
            {
                // Validates the path and refuses a folder another profile already uses.
                _profiles.SetDataPath(activeProfileId, folder);
            }
            catch (ArgumentException ex)
            {
                return RelocateResult.Refused(ex.Message);
            }

            progress?.Report($"Stopping profile '{profile.Name}'...");
            try
            {
                await _nodeLifecycle.StopAsync(StopGracefulTimeout, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ProfileSwitchService: error stopping node before opening a folder: {ex.Message}");
            }

            var opened = _profiles.GetById(activeProfileId);
            progress?.Report("Opening the profile folder...");
            var start = await _nodeLifecycle.StartOrAttachAsync(opened.DataPath, progress, ct).ConfigureAwait(false);
            if (start.Success && !string.IsNullOrEmpty(start.FrontUrl))
            {
                return RelocateResult.Ok(opened, oldPath, start.FrontUrl);
            }

            try { _profiles.SetDataPath(activeProfileId, oldPath); }
            catch (Exception ex) { Debug.WriteLine($"ProfileSwitchService: could not restore old data path: {ex.Message}"); }

            return await RestartAfterFailedMoveAsync(profile, oldPath,
                $"The profile in {folder} did not start ({start.ErrorMessage ?? "unknown error"}).", progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    private async Task<RelocateResult> RestartAfterFailedMoveAsync(
        ProfileEntry profile, string oldPath, string message, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report($"Starting profile '{profile.Name}' again...");
        var restart = await _nodeLifecycle.StartOrAttachAsync(oldPath, progress, ct).ConfigureAwait(false);
        return restart.Success && !string.IsNullOrEmpty(restart.FrontUrl)
            ? RelocateResult.Failed(profile, message, restart.FrontUrl!)
            : RelocateResult.Error($"{message} Starting the profile again also failed: {restart.ErrorMessage ?? "unknown error"}.");
    }

    /// <summary>
    /// Queries the active node's update state machine and returns <c>(true, reason)</c> when
    /// an update is currently <c>Applying</c> (the only state where interrupting a switch
    /// could corrupt the install). Every other outcome — other states, non-2xx, timeout,
    /// parse error, missing internal key — fails OPEN (returns <c>false</c>).
    ///
    /// <para>For a node THIS Desktop process hosted (spawned via <see cref="INodeLifecycleService"/>),
    /// <see cref="ResolveInternalKey"/> resolves the real key — <see cref="NodeLifecycleService"/>
    /// generates it and threads it through to bmbd/Api via env vars (see its own comment), so the
    /// request actually authenticates and this guard functions. For a node this process merely
    /// ATTACHED to (already running, spawned by some other process) there is no way to learn its
    /// key — that case, and only that case, always fails open by design.</para>
    /// </summary>
    private async Task<(bool blocked, string? reason)> IsUpdateApplyingAsync(
        string activeFrontUrl,
        string? currentProfileId,
        CancellationToken ct)
    {
        try
        {
            var key = ResolveInternalKey(currentProfileId);

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{activeFrontUrl.TrimEnd('/')}/node/update/status");
            if (!string.IsNullOrEmpty(key))
            {
                request.Headers.TryAddWithoutValidation("X-Internal-Key", key);
            }
            request.Headers.TryAddWithoutValidation("X-User-Role", "superadmin");

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Debug.WriteLine($"ProfileSwitchService: update guard returned {(int)response.StatusCode}; failing open.");
                return (false, null);
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("currentStep", out var stepEl)
                && stepEl.ValueKind == JsonValueKind.String)
            {
                var step = stepEl.GetString();
                // Block ONLY on the Applying state — the moment a binary swap + health checks
                // run and interrupting could leave the app half-updated. Downloading/Checking
                // are passive enough that stopping the node is recoverable, and the guard's
                // charter is to avoid creating a new failure point, not to be exhaustive.
                if (string.Equals(step, "Applying", StringComparison.OrdinalIgnoreCase))
                {
                    return (true, step);
                }
            }

            return (false, null);
        }
        catch (Exception ex)
        {
            // Network/timeout/parse error → fail open. A blocking guard that misfires on a
            // flaky connection would be strictly worse than occasionally missing a real apply.
            Debug.WriteLine($"ProfileSwitchService: update guard failed (failing open): {ex.Message}");
            return (false, null);
        }
    }

    /// <summary>
    /// Resolves the internal key used to authenticate the guard request, mirroring App.axaml.cs:
    /// prefer the <c>BMB_INTERNAL_KEY</c> env var, then fall back to <c>.internal-key</c> in the
    /// active node's data directory (the current profile's <see cref="ProfileEntry.DataPath"/>).
    /// Returns <c>null</c> if no key is available (the guard request is still sent without the
    /// header — it will typically 401, which fails open).
    /// </summary>
    private string? ResolveInternalKey(string? currentProfileId)
    {
        var key = Environment.GetEnvironmentVariable("BMB_INTERNAL_KEY");
        if (!string.IsNullOrEmpty(key))
        {
            return key;
        }

        if (string.IsNullOrEmpty(currentProfileId))
        {
            return null;
        }

        try
        {
            var profile = _profiles.GetById(currentProfileId);
            var keyFile = Path.Combine(profile.DataPath, ".internal-key");
            if (File.Exists(keyFile))
            {
                return File.ReadAllText(keyFile).Trim();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ProfileSwitchService: could not read .internal-key for guard: {ex.Message}");
        }

        return null;
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var client = new HttpClient { Timeout = GuardTimeout };
        return client;
    }

    /// <summary>
    /// Disposes the <see cref="HttpClient"/> only when this instance created it (the
    /// production constructor); an externally-supplied client (tests, or a shared client
    /// passed by the caller) is left for its owner to dispose.
    /// </summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
