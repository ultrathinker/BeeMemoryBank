using BeeMemoryBank.FullIos.Pages;
using BeeMemoryBank.FullIos.Platforms.iOS;
using BeeMemoryBank.FullIos.Services;
using BeeMemoryBank.Storage.Sealed;
using BeeMemoryBank.Vault.Sealed;
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
    private const string InvisibleKey = "sync.invisible";

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
            var start = services.GetService<PhoneStart>();
            if (start is { CanStart: false })
            {
                // The reason, in words (spec 2.5); nothing was opened, created, changed or deleted.
                (Window?.Page as StartPage)?.ShowProblem(start.Broken!.Message);
                return;
            }

            // An encrypted phone cannot read its database before the unlock page has opened it (spec 5.8): the schema, the folders and the pragmas
            // run at each open (FullNodeServices.AddOpenWork). A plain phone prepares its database now, as it always did.
            if (start is { Mode: VaultStoreMode.Sealed })
            {
                FullNodeServices.SweepStaging(services);
                await PhoneVaultLifecycle.PrepareSealedStartAsync(services);
            }
            else
            {
                await Task.Run(() => FullNodeServices.PrepareAsync(services));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Preparing the database failed");
            // Our own calm sentence on the screen; the exception itself is in the log above, never its raw text on the page.
            (Window?.Page as StartPage)?.ShowProblem(Localization.AppStrings.Get("OpenPrepareError"));
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

    /// <summary>
    /// The main screen after a new vault or a join, with the phone's database encrypted first (every database is encrypted, no
    /// button). Behind the "please wait" pop-up, with the screen kept on. A failure is told in a dialog over the notes. An unlock never encrypts:
    /// a phone that still holds a plain copy from an older build is set up again (delete the app, join again).
    /// </summary>
    public async Task EncryptThenShowMainAsync()
    {
        var sealing = services.GetRequiredService<PhoneSealing>();
        if (sealing.IsNeeded)
        {
            string? problem = null;
            await BeeMemoryBank.Busy.BusyAction.RunAsync(null, async busy =>
            {
                DeviceDisplay.Current.KeepScreenOn = true;
                try
                {
                    // Right after Face ID iOS may still count the app as not in front, which the encryption's "keep the app open" check refuses.
                    for (var i = 0; i < 30 && !IsActive; i++) await Task.Delay(100);
                    var run = sealing.SealAsync(await StartsNetworkAsync());
                    while (!run.IsCompleted)
                    {
                        if (sealing.Progress is { } p) busy.Report(p.Done, p.Step);
                        await Task.WhenAny(run, Task.Delay(400));
                    }
                    await run;
                }
                catch (PhoneSealingException ex)
                {
                    problem = ex.Code == PhoneSealingException.OldNetwork
                        ? Localization.AppStrings.Get("AutoEncryptOldNetwork")
                        : Localization.AppStrings.Format("AutoEncryptFailed", ex.Message);
                    NoteEncryptionFailure(ex.Code + ": " + ex.Message);
                }
                finally
                {
                    DeviceDisplay.Current.KeepScreenOn = false;
                }
            }, new BeeMemoryBank.Busy.BusyOptions
            {
                Long = true,
                Title = Localization.AppStrings.Get("AutoEncryptTitle"),
                Message = Localization.AppStrings.Get("AutoEncryptMessage"),
                Result = (ok, text) =>
                {
                    if (ok) return;
                    // A failure that is NOT a PhoneSealingException with our own text: our own calm sentence in the dialog, the detail in the log file.
                    problem = Localization.AppStrings.Format("AutoEncryptFailed", Localization.AppStrings.Get("AutoEncryptFailedReason"));
                    NoteEncryptionFailure(text);
                },
            });
            logger.LogInformation("Phone database encryption at open: {Result}.", problem is null ? "done" : "not done");
            // The notes first, then the reason on top of them: a dialog over the unlock page went away with it when the main screen replaced it.
            ShowMain();
            if (problem is not null && Window?.Page is { } page)
                await Controls.AppDialog.AlertAsync(page, Localization.AppStrings.Get("AutoEncryptTitle"), problem);
            return;
        }
        ShowMain();
    }

    /// <summary>
    /// The last reason the database could not be encrypted, in a small file beside the vault folder (no note text, no key, no password: the
    /// encryption's own sentence), so that a failure the person only glimpsed can still be read off the phone.
    /// </summary>
    private static void NoteEncryptionFailure(string reason)
    {
        try
        {
            File.WriteAllText(Path.Combine(FileSystem.AppDataDirectory, "encrypt-last-error.txt"),
                DateTime.UtcNow.ToString("O") + " " + reason + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a diagnostic: the dialog has said it already.
        }
    }

    /// <summary>A vault alone on this phone (no other device in its list) starts its own network, so it may switch the new sync format on.</summary>
    private async Task<bool> StartsNetworkAsync()
    {
        var self = (await vault.IdentityAsync())?.NodeId;
        using var scope = services.CreateScope();
        var peers = await scope.ServiceProvider.GetRequiredService<BeeMemoryBank.Core.Interfaces.IWhitelistRepository>().GetAllActiveAsync();
        return peers.All(p => p.NodeId == self);
    }

    /// <summary>A recovery key, once. After a new vault the page replaces the setup pages, so there is no way back to them.</summary>
    public void ShowRecoveryKey(string key, bool firstTime)
    {
        var page = services.GetRequiredService<RecoveryKeyPage>();
        page.Show(key, firstTime);
        if (firstTime) SetRoot(new NavigationPage(page));
        else _ = Window?.Page?.Navigation.PushModalAsync(new NavigationPage(page));
    }

    /// <summary>
    /// After the first unlock of a new or joined vault, and after a password unlock that followed a Face ID unlock that no longer opened (an older
    /// quick unlock a key rotation had already replaced): Face ID / Touch ID instead of typing the password next time?
    /// </summary>
    public async Task OfferQuickUnlockAsync(Page page)
    {
        if (SuppressAutoPrompt || !quick.IsAvailable || quick.IsEnabled || !vault.IsUnlocked) return;
        await AskQuickUnlockAsync(page);
    }

    /// <summary>The same question over the main screen the password unlock has just shown, after Face ID had turned itself off.</summary>
    public async Task OfferQuickUnlockAgainAsync()
    {
        if (Window?.Page is { } page) await OfferQuickUnlockAsync(page);
    }

    /// <summary>The question itself, in the app's own words. The end-to-end check photographs it even on a simulator without biometrics.</summary>
    internal async Task AskQuickUnlockAsync(Page page)
    {
        var name = quick.BiometryName;
        if (!await page.DisplayAlertAsync(name, Localization.AppStrings.Format("QuickUnlockQuestion", name),
                Localization.AppStrings.Format("QuickUnlockUse", name), Localization.AppStrings.Get("QuickUnlockNotNow")))
            return;
        try { await quick.EnableAsync(); }
        // Our own refusal ("Face ID is not set up on this iPhone") is said as it is; any other kind is told calmly, never its raw text.
        catch (Exception ex) { await page.DisplayAlertAsync(name, BeeMemoryBank.Sync.FailureText.Say(ex), Localization.AppStrings.Get("Ok")); }
    }

    /// <summary>The lock button, and every automatic lock.</summary>
    public void LockNow() => vault.Lock();

    /// <summary>
    /// The danger zone: stop syncing, lock, clear the Face ID secret, then delete ONLY this app's own
    /// data directory (the database and its -wal/-shm/-journal sidecars, the sealed boot/journal/mirror
    /// files and the media folder) and show the Welcome gate as a fresh install. Nothing outside the
    /// app's container is touched. The list of deleted files is SecurityOps.WipeFiles (the same set).
    /// </summary>
    public async Task WipeAsync()
    {
        await sync.StopAsync();
        // The folder (the door with it) is deleted next; only the Keychain secret is the app's to clear here.
        quick.ForgetSecret();
        // The lock, the delete and the fresh schema (SecurityOps.WipeDataAsync, where the tests run it on a real encrypted vault).
        // Never show a fresh Welcome over data that is still there: the busy helper says the failure.
        if (!await new SecurityOps(services).WipeDataAsync(services.GetRequiredService<FullNodePaths>()))
            throw new IOException(BeeMemoryBank.FullIos.Localization.AppStrings.Get("WipeIncomplete"));

        ShowWelcome();
    }

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

    /// <summary>Invisible mode: FullSync skips every round while it is on; the choice is remembered.</summary>
    public bool Invisible
    {
        get => services.GetRequiredService<BeeMemoryBank.Core.Services.InvisibleModeService>().IsInvisible;
        set
        {
            services.GetRequiredService<BeeMemoryBank.Core.Services.InvisibleModeService>().IsInvisible = value;
            Preferences.Default.Set(InvisibleKey, value);
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
                var round = await sync.RoundAsync(ct, automatic: true);
                logger.LogInformation("Last round before the background: {Applied} applied, {Failed} failed", round.Applied, round.Failed);
#if DEBUG || BMB_E2E
                Console.WriteLine($"BMB-E2E event=background-round reached={round.Reached} failed={round.Failed}");
#endif
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { logger.LogWarning(ex, "The last round before the background failed"); }
            finally
            {
                if (lockNow) await LockForBackgroundAsync();
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

    /// <summary>The lock as the app leaves the screen: on an encrypted phone the database closes with it, before iOS suspends the app (spec 5.4, 12.2).</summary>
    private async Task LockForBackgroundAsync()
    {
        if (services.GetService<PhoneVaultLifecycle>() is { } lifecycle)
            await lifecycle.OnBackgroundAsync();
        else
            MainThread.BeginInvokeOnMainThread(vault.Lock);
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
        services.GetRequiredService<BeeMemoryBank.Core.Services.InvisibleModeService>().IsInvisible = Preferences.Default.Get(InvisibleKey, false);
    }

    private static void SetRoot(Page page)
    {
        if (Window is { } window) window.Page = page;
    }
}
