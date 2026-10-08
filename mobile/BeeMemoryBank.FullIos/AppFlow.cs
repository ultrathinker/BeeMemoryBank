using BeeMemoryBank.FullIos.Pages;
using BeeMemoryBank.FullIos.Platforms.iOS;
using BeeMemoryBank.FullIos.Services;
using Microsoft.Extensions.Logging;

namespace BeeMemoryBank.FullIos;

/// <summary>
/// Which screen the app shows, and the vault's life with the app's: the welcome pages without a vault, the unlock page while it is
/// locked, the notes while it is open. The notes live in a Shell that is made new at every unlock and thrown away at every lock, so no
/// page that showed a note outlives the open vault. Leaving the screen covers the app (the app-switcher picture shows nothing), runs one
/// last sync round in the seconds iOS grants and locks - at once, or when the app comes back after the chosen grace; the open vault also
/// locks after the chosen idle time.
/// </summary>
public sealed class AppFlow(IServiceProvider services, FullVault vault, QuickUnlock quick, FullSync sync, AutoLockPolicy policy, ILogger<AppFlow> logger)
{
    private const string GraceKey = "lock.grace.seconds";
    private const string IdleKey = "lock.idle.seconds";

    private IDispatcherTimer? _idleTimer;
    private bool _subscribed;

    /// <summary>The end-to-end check drives the unlock itself and must not meet a Face ID prompt it cannot answer.</summary>
    public bool SuppressAutoPrompt { get; set; }

    private static Window? Window => Application.Current?.Windows.FirstOrDefault();

    /// <summary>At start: the database (schema, journal), then the first screen for the vault's state.</summary>
    public async Task StartAsync()
    {
        LoadSettings();
        if (!_subscribed)
        {
            vault.Locked += () =>
            {
#if DEBUG || BMB_E2E
                Console.WriteLine("BMB-E2E event=locked");
#endif
                MainThread.BeginInvokeOnMainThread(ShowUnlock);
            };
            _subscribed = true;
        }
        try
        {
            await Task.Run(() => FullNodeServices.PrepareAsync(services));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Preparing the database failed");
            (Window?.Page as StartPage)?.ShowProblem("The memory bank on this iPhone could not be opened: " + ex.Message);
            return;
        }

        switch (await vault.GetStateAsync())
        {
            case VaultState.NoVault: ShowWelcome(); break;
            case VaultState.Locked: ShowUnlock(); break;
            default: ShowMain(); break;
        }
    }

    public void ShowWelcome() => SetRoot(new NavigationPage(services.GetRequiredService<WelcomePage>()));

    public void ShowUnlock()
    {
        StopOpenVaultWork();
        if (Window?.Page is NavigationPage { RootPage: UnlockPage }) return;
        SetRoot(new NavigationPage(services.GetRequiredService<UnlockPage>()));
    }

    public void ShowMain()
    {
        if (!vault.IsUnlocked)
        {
            ShowUnlock();
            return;
        }
        SetRoot(new AppShell(services));
        policy.Activity(DateTime.UtcNow);
        TouchWatcher.Attach(() => policy.Activity(DateTime.UtcNow));
        StartIdleTimer();
        sync.Start();
    }

    /// <summary>A recovery key, once. After a new vault the page replaces the setup pages, so there is no way back to them.</summary>
    public void ShowRecoveryKey(string key, bool firstTime)
    {
        var page = services.GetRequiredService<RecoveryKeyPage>();
        page.Show(key, firstTime);
        if (firstTime) SetRoot(new NavigationPage(page));
        else _ = Window?.Page?.Navigation.PushModalAsync(new NavigationPage(page));
    }

    /// <summary>After the first unlock of a new or joined vault: Face ID / Touch ID instead of typing the password next time?</summary>
    public async Task OfferQuickUnlockAsync(Page page)
    {
        if (SuppressAutoPrompt || !quick.IsAvailable || quick.IsEnabled || !vault.IsUnlocked) return;
        var name = quick.BiometryName;
        if (!await page.DisplayAlertAsync(name, $"Open your memory bank with {name} instead of typing the master password? The password is not stored.", "Use " + name, "Not now"))
            return;
        try { quick.Enable(); }
        catch (Exception ex) { await page.DisplayAlertAsync(name, ex.Message, "OK"); }
    }

    /// <summary>The lock button, and every automatic lock.</summary>
    public void LockNow() => vault.Lock();

    /// <summary>A touch the window did not see (typing): the person is still here.</summary>
    public void Activity() => policy.Activity(DateTime.UtcNow);

    public TimeSpan Grace
    {
        get => policy.BackgroundGrace;
        set
        {
            policy.BackgroundGrace = value;
            Preferences.Default.Set(GraceKey, (int)value.TotalSeconds);
        }
    }

    public TimeSpan Idle
    {
        get => policy.IdleTimeout;
        set
        {
            policy.IdleTimeout = value;
            Preferences.Default.Set(IdleKey, (int)value.TotalSeconds);
        }
    }

    // ---- the app's life ----

    /// <summary>The app is no longer active (another app, the app switcher, a system prompt): cover it.</summary>
    public void Deactivated() => PrivacyCover.Show();

    public void Activated()
    {
        PrivacyCover.Hide();
        if (Window?.Page is NavigationPage { CurrentPage: UnlockPage unlock }) _ = unlock.OfferQuickUnlockAsync();
    }

    /// <summary>The app is in front and active (not behind a system prompt, not in the background).</summary>
    public static bool IsActive => UIKit.UIApplication.SharedApplication.ApplicationState == UIKit.UIApplicationState.Active;

    /// <summary>The app went to the background: one last round, then lock at once or when it comes back after the grace.</summary>
    public void Stopped()
    {
        policy.Left(DateTime.UtcNow);
        StopIdleTimer();
        if (!vault.IsUnlocked) return;

        var lockNow = policy.BackgroundGrace == TimeSpan.Zero;
        IosBackground.Run("bmb-last-round", async ct =>
        {
            try
            {
                await sync.StopAsync();
                // The round signs the peers' challenges with this node's key, which needs the open vault: before the lock, not after.
                var round = await sync.RoundAsync(ct);
                logger.LogInformation("Last round before the background: {Applied} applied, {Failed} failed", round.Applied, round.Failed);
#if DEBUG || BMB_E2E
                Console.WriteLine($"BMB-E2E event=background-round reached={round.Reached} failed={round.Failed}");
#endif
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { logger.LogWarning(ex, "The last round before the background failed"); }
            finally
            {
                if (lockNow) MainThread.BeginInvokeOnMainThread(vault.Lock);
            }
        });
    }

    /// <summary>Back on the screen: locked if the grace ran out, otherwise the open vault's loop goes on.</summary>
    public void Resumed()
    {
        if (!vault.IsUnlocked)
        {
            policy.Returned(DateTime.UtcNow);
            return;
        }
        if (policy.Returned(DateTime.UtcNow))
        {
            vault.Lock();
            return;
        }
        StartIdleTimer();
        sync.Start();
    }

    private void StartIdleTimer()
    {
        if (_idleTimer is not null || Application.Current is null) return;
        _idleTimer = Application.Current.Dispatcher.CreateTimer();
        _idleTimer.Interval = TimeSpan.FromSeconds(15);
        _idleTimer.Tick += (_, _) =>
        {
            if (vault.IsUnlocked && policy.IsIdle(DateTime.UtcNow)) vault.Lock();
        };
        _idleTimer.Start();
    }

    private void StopIdleTimer()
    {
        _idleTimer?.Stop();
        _idleTimer = null;
    }

    private void StopOpenVaultWork()
    {
        StopIdleTimer();
        _ = sync.StopAsync();
    }

    private void LoadSettings()
    {
        policy.BackgroundGrace = TimeSpan.FromSeconds(Preferences.Default.Get(GraceKey, 0));
        policy.IdleTimeout = TimeSpan.FromSeconds(Preferences.Default.Get(IdleKey, 300));
    }

    private static void SetRoot(Page page)
    {
        if (Window is { } window) window.Page = page;
    }
}
