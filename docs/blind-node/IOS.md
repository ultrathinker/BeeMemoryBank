# The iPhone blind copy (`mobile/BeeMemoryBank.BlindIos`)

The iPhone app "Bee Memory Bank Blind" is a blind copy like the Android, Windows and macOS ones: it keeps an encrypted copy of a memory
bank, pairs with a node through a computer, syncs, makes backups, and never holds or asks for the master password. It is built on the same
libraries (Core, Crypto, Search, Storage, Sync, `Blind.PhoneClient`) and the same `Blind.AppCore` (controller, pairing, screen texts);
only the iOS parts are its own. It is source-only for now: there is no public App Store or TestFlight download. Owners preparing one can
follow [IOS-DISTRIBUTION.md](IOS-DISTRIBUTION.md).

## What is the app's own

| Part | Where | What it does |
|---|---|---|
| secrets | `Services/IosKeychainSecretStore.cs` (+ `IosKeychainSecretStoreFactory`), `Platforms/iOS/SecKeychainBackend.cs` | the identity seed, the pairing secret and the backup key of EACH account as three generic-password items of the iOS Keychain (service `com.beememorybank.blind`; item names from `BlindAccountNames`: account 1 keeps `identity-seed-v1` ..., another account `<id>/identity-seed-v1` ...), `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`, not synchronizable: readable by the background rounds while the phone is locked, never in iCloud Keychain or in a backup restored on another phone. Each item carries the macOS store's integrity envelope (version, SHA-256 of the item NAME and secret), so an item copied into another account's slot is refused. A missing secret is `null`; any other Keychain error is an exception, never "lost". Removing an account deletes its three items and no other |
| data | `Platforms/iOS/IosContainer.cs`, `Services/IosFileProtection.cs`, `Platforms/iOS/IosFileAttributes.cs` | `Library/Application Support/BeeMemoryBankBlind` in the app's container is the root: account 1 (the 2.6 copy) lives in it exactly as before, the list `accounts.json` (no secret) and `accounts.lock` are beside it, and every other account has `accounts/<id>/` with the same names inside (database, replica work folder, backups, log, state file). File protection `CompleteUntilFirstUserAuthentication` (encrypted by iOS until the first unlock after a restart) and the no-iCloud/no-computer-backup mark are put on the root and on EVERY account folder, each time an account's runtime is built (`IosAccountFolders`, tested with a fake) |
| state | `Services/IosBlindStateStoreFactory.cs` over `MacOsBlindStateStore.cs` (linked from the macOS adapters) | the small key/value state of each account in `blind-state.json` of its folder, written whole and renamed into place - the name and format the 2.6 app used, so account 1's file is read as it is |
| accounts | `Services/IosBlindRuntime.cs` | ONE `BlindAccountHost` (Blind.AppCore) for the life of the process: a complete blind copy per account, the one device scheduler, the invitation watch. Adding or removing an account builds or ends that account only; a wipe of one account (the controller still has it; the Account page offers Remove) makes it a fresh account in place. iOS gives an app no way to restart itself, and none is needed |
| in-app loop | Blind.AppCore `BlindDeviceScheduler` | while the app is in front: a check at once, then for each account in turn (the one that synced longest ago first) a sync every 15 minutes, the long job (first load, due backup) every hour, the first load at once after pairing, retries of a failed first load; a pause (a limit, a switch, the person) is never a failure |
| device conditions | `Services/IosDeviceConditions.cs`, `Platforms/iOS/IosNetworkPathMonitor.cs` | "Only on Wi-Fi or other unmetered networks": the active `NWPath` is expensive (cellular, Personal Hotspot) or constrained (Low Data Mode) = metered; no answer, no network or a network that needs a connection first = unknown, and an account with the switch on then waits. "Pause on battery saver": `NSProcessInfo.LowPowerModeEnabled`. The app keeps the managed HTTP handler (`UseNativeHttpHandler=false`) so that the pin is checked the same way everywhere; it does NOT promise `NSURLSession`'s own cellular constraints - only this check keeps a copy off a data plan |
| background | `Platforms/iOS/IosBlindBackground.cs`, `Services/IosBackgroundRounds.cs` | two BGTaskScheduler tasks (below), fixed identifiers, serving all accounts |
| "no contact" warning | `Services/BlindSilenceAlarm.cs`, `Platforms/iOS/IosSilenceNotifier.cs` | one pending local notification PER ACCOUNT, three days after that account's last contact with its source ("No contact with <name>'s server for 3 days", for an account made from an invitation; an account paired with codes has a generic title and the name only in the text), moved forward by every sync; account 1 keeps the 2.6 request id; none for an account the person or a limit holds, none for an account the owner has not confirmed; withdrawn with the account. The text is built in `BlindSilencePlanner` and carries the account's name - **so an account's name is shown on the lock screen** (iOS shows a notification there unless the person hides previews); a name the person would not want read over a shoulder is better changed. The reminder is **daily**: a request that iOS showed is no longer pending, which the notifier notices at its next update (`IosSilenceNotifier.NoteFired`, looked at most once a minute), and the planner then plans the next one for the following day, so a silent node is reminded about every day until the person acts, not once |. The notification center answers an add later, and not necessarily in the order the adds were made, so every add carries a generation (`SilenceRequestBook`) and an answer counts only for the newest add of its request id: an older success cannot overwrite a newer add's time, an older error cannot erase a newer success, and a failure of the current add withdraws that request's plan, so the next update plans it again even when nothing changed. The calls to the notification center are not left to the system's order of answering: `SilenceReconciler` (Services, portable) runs the whole read / withdraw / add of an update, and the look at the requests that fired, as ONE job of ONE ordered queue - from the screens and from a background wake alike - and starts a job only after the previous one ended, so a late withdrawal can never delete a newer add of the same request (which would leave the book believing in a warning the phone does not have). A job plans from the newest snapshot handed over when it starts (updates that wait are one job), gives each call `NativeTimeout` (30 s) to answer so that a lost callback cannot stop every later update (a call that was given up invalidates the plan and is done again at the next update; a late answer changes nothing), and looks for fired requests at the start of the job, so the next reminder is added in the same pass. The iOS class (`IosSilenceNotifier`) only gives the reconciler the center as tasks (`ISilenceNotificationCenter`). When the person allows alerts (the system's answer to the first request, or later in Settings), the grant is itself a job of the same queue: the book forgets what it had planned when nothing is known to be pending and the NEWEST snapshot is reconciled at once, so a reminder the system refused before the permission was given is added without waiting for some later update |
| links | `Services/IosLinks.cs`, `Platforms/iOS/AppDelegate.cs` | the app is the handler of `bmb-blind-call:` (the computer's answer to a pairing code) and `bmb-invite:` (a one-time invitation): the system Camera opens either from a QR code, so the app has no scanner of its own and asks for no camera permission |
| screens | `Pages/AccountsPage.xaml(.cs)`, `Pages/AddAccountPage.xaml(.cs)`, `Pages/BlindHomePage.xaml(.cs)` | **Accounts** (the start page: one calm card per account from `BlindAccountsView`, the device line, Add account, the limits of the whole phone, repair states with explicit actions), **Add** (paste field, the preview from `PreviewInvitation`, Add; "with a pairing code" the old way), **Account** (what the single screen was in 2.6, bound to that account's controller: the card's state line and numbers, pause, rename, the store line, the last contact, the newest problem, the background state, the pairing steps for an account paired the old way, **Limits**, backups, log, "Remove this account"). Every sentence on these pages is built in AppCore and only assigned to a control |

Why a project of its own and not a second target of the Android project: an iOS build needs a Mac with Xcode, and a multi-targeted Android
project would no longer restore on the Linux and Windows machines that build the Android app; the Android project's guards
(`ForbiddenReferencesTests`, `BlindManifestTests`, `AppBoundaryTests`) read its project, assets and output, which stay as they were; and the iOS
life cycle shares almost nothing with WorkManager and a foreground service.

## Several accounts

The phone keeps one **account** per source (per memory bank whose copy it holds); [ACCOUNTS.md](ACCOUNTS.md) has the design, this is the iPhone's part.

* **Upgrade moves nothing.** A 2.6.4 install is listed as account 1 (`legacy`) at its first start: its database, `blind-state.json`, Keychain items, silence notification id and the two background task identifiers keep their names. A test builds a 2.6.4 layout, starts the new app on it and checks that
  nothing was moved or renamed and that the sync goes to the same source with the same identity (`IosRuntimeTests`). Account 1 has no limits and uses any network, as before.
* **Add an account.** From an invitation: paste the `bmb-invite:` line on the Add page, or scan the QR code the owner's Web page shows with the **Camera app** - it opens this app on the Add page with the invitation read and previewed. Nothing is added until the person presses Add, and the owner must then confirm
  with the match code shown on both screens. The invitation is a one-time capability, not an encrypted string, and the page says so. An account added this way starts with 2 GB of storage, 200 MB a day, "only on unmetered networks" and "pause on battery saver" (the phone's defaults,
  `BlindAccountDefaults.ForInvited`). The old way (a pairing code, the computer's answer) is still there, per account.
* **Limits.** On the Account page: storage, traffic a day, requests a day, the two switches, Pause. On the Accounts page: the limits of the whole phone. A reached limit is a pause the card explains ("Paused: daily traffic limit reached (200 MB). Resumes at 00:00."), never a failure. The source is told the reason and the end, so its page says "paused by the device's owner" and not "silent" (it is not told about a wait for Wi-Fi or for the battery).
* **Remove.** "Remove this account" says what goes and who is told, stops the account's work, tells the source if the phone is online, deletes the account's three Keychain items, its state and its folder, and takes its entry out of the list last. A removal that the app's death cut off is finished at the next start.
* **One app.** Adding or removing an account does not rebuild the app, the scheduler or another account (`IosRuntimeTests`).

## After sealed metadata is turned on

When the owner turns on sealed metadata ([../encryption.md](../encryption.md)), the copy changes in four ways, all in the libraries it shares
with the Android, Windows and macOS copies (`Blind.PhoneClient`, `Blind.AppCore`); the iPhone head only shows the result:

- **What it holds.** A copy that already loaded learns of the activation by sync and goes back to its first load (the screen says "First
  load: running"): it downloads a sealed baseline instead of a database, and the first adoption removes the plain content the old load held
  (the database is vacuumed with secure delete). From then on the copy stores titles, paths, tags and comments only inside the baseline,
  which it cannot open, and relays the sealed changes it pulls as received.
- **What the screen shows.** The store line replaces the number of notes: *Restore point: made 2 d ago. Changes since: 12. Stored: 4 MiB.*
  Before the first load it says none yet; if the database cannot be read at that moment it says unknown, never zero.
- **Newer baselines.** The hourly long job asks the node it calls for a newer baseline (`GET /api/blind/replica?have=<id>`): 304 when it holds
  the newest, otherwise it fetches the blobs the baseline lists, adopts it and drops the changes of its log that the baseline now covers.
  A failed update is shown like a failed backup and does not hold a due backup back.
- **Backups.** A backup is the sealed package of the node the copy calls, the copy's own events and its pairing record, made as before; the
  computer restores from it with the master password alone ([../snapshot-restore.md](../snapshot-restore.md)).

An iPhone that has not been updated to 2.6 keeps working until the owner turns sealed metadata on; after that the node refuses it (426) until
the app is updated.

## Pairing and TLS

The two codes are the Android app's: the iPhone shows its code (QR and "Copy"), the computer adds it under Blind nodes, "Add Android blind
copy" (an iPhone is added the same way), and answers with a `bmb-blind-call:` code. The iPhone takes it pasted, or as a link: the app is the
handler of the `bmb-blind-call:` scheme, so scanning the computer's QR code with the Camera opens the app with it. Either way it is accepted
only when it was made for this phone's current pairing secret.

The app uses the managed HTTP handler (`UseNativeHttpHandler=false`), not NSURLSession: the node's key is checked by `BlindHttpHandler`'s
callback inside `SslStream` on every TLS handshake, exactly as on the other platforms, and App Transport Security has nothing to say about a
self-signed, pinned node on the local network. A node that answers with another key is refused before a request is sent, and the log and the
screen say "Refused <host>: it answered with another key than the one pinned when this phone was paired. Nothing was sent."

iOS asks the person once before the app may reach a device on the local network (`NSLocalNetworkUsageDescription`).

## Background: what iOS allows

A blind copy listens on no port and keeps no connection open. While the app is in front it runs the loop above; when it goes to the
background the loop pauses (a running job stops where it is and resumes later) and two requests are submitted:

* `com.beememorybank.blind.sync`, a **BGAppRefreshTask**, not before 15 minutes: one sync round (or a piece of the first load). iOS gives it
  about 30 seconds, and decides itself when and how often: a few times a day for an app the person opens often, rarely or never for one they
  do not, never while Background App Refresh is off for the app or Low Power Mode is on.
* `com.beememorybank.blind.work`, a **BGProcessingTask** with network, not before an hour: the first load or a due backup, then a sync. iOS
  runs these typically at night while the phone is idle and charging; the request does not demand the charger (like the other copies), but
  iOS still decides.

Both are submitted again at the start of every round - and only while the phone holds an account that needs a wake (`IosBackgroundPlan.Wanted`): an active account, or one waiting for its owner's answer. An account that is held for good - paused by the person, or by a limit that has no end of its own (a storage limit, a limit of 0) - needs none; a pause that ends by itself (a daily limit at midnight, a wait for Wi-Fi or for the battery) keeps its wake, because the wake after it ends is what resumes the account. A phone with no such account asks iOS for nothing; the requests are asked for again when the app goes to the background and at the end of every wake, so a pause the person lifts is back in the next request. The identifiers are fixed in `Info.plist` and cannot be per account, so **each wake serves
the accounts of the phone in turn**: in the order of the in-app loop (the one that synced longest ago first), each through its own guard (a limit, a switch or the person that says "not now" costs no request), with an even slice of the time the wake is expected to have when there are several accounts
(about 25 seconds for an app refresh, 5 minutes for a processing window; at least 5 and 30 seconds each), so that an account in the middle of a long first load cannot use up every wake. One account is never cut short. **One background round at a time:** the two tasks can be granted together, and the open app has its own loop, so the app has ONE ordered life cycle (`Services/IosLifecycle.cs`, behind `IosBlindRuntime`): coming to the front, going to the background, a background round and the end of the process are transitions that run one after the other in the order they were asked for - `OnSleep` and `OnResume` ask on the main thread, so the order of the callbacks is the order of the queue. A newer transition waits for the older one, so a stop that finishes late can never stop the loop of a newer start; the person's wish (in front or not) is what the loop is made to match when a transition's turn comes, so the final state is always the last wish (in front = the loop runs, in the background = it is stopped). A wake that finds another round queued or on, or the app in front, is completed as "nothing to do" (it is never a failure and iOS is not refused a task); the app coming to the front cancels a round that is on at once and starts the loop only after that round has really ended, so two syncs of one account never run at once; the end of the process cancels the round, lets the transition in flight finish, stops the loop and starts nothing afterwards. The honest price: with several accounts **each one syncs less often in the
background**, the one that waited longest first - the open app syncs all of them. The Account page shows the last round iOS granted to that account, and the Accounts page whether Background App Refresh is off or Low Power Mode is on.

**Logs keep the accounts apart.** What a round did to an account (its failure, its stop) goes only to that account's own log and last-round line, through that account's services. What is about the wake as a whole (how many accounts were served, that iOS ended the wake, a wake that failed or was skipped) - and every error of the app as a whole (going to the background, opening a link, starting the copy: `IosAppErrors`) - goes to a device log beside `accounts.json` (`blind-device-log.jsonl`) in words that name no account, no source and no number of one; no account's log is chosen to hold an app-wide error: removing an account takes its log with it, and the device log holds nothing of it. A failure is always told by its kind (`BlindFailureText`: for a call to the other computer one of the fixed kinds, for a local failure a short sentence of the app's own or its kind), never by the text of an exception, which can carry a host or a path. **Every page goes through the shared busy helper's `Describer`**: `IosFailureSetup.Apply()` (first thing in `MauiProgram`) sets `BusyAction.Describer` to `IosFailureText.Say`, so the default dialog, a result label, a custom `Result` callback and `BusyAction.Describe(ex)` all say the closed text, and a page never formats an exception - the variable of a `catch` goes only to `BusyAction.Describe(ex)`, `BlindHomeView.StatusFailedText(ex)` or the page's `ShowProblem(ex)`. A Keychain failure (`IosKeychainException`) is a marked own sentence made of fixed words and the OSStatus number only - never the item's name, which holds the account's id. This is not a promise for every error or log path: an exception that only propagates can still carry the words it was thrown with (see [FAILURE-TEXT.md](FAILURE-TEXT.md)). The simulator runs no background tasks at all (`BGTaskSchedulerErrorDomain` error 1); they can only be seen on a phone. There, a
round can be brought forward with Apple's debugging hook: attach Xcode's lldb to the app (a development-signed build) while it is still in
front or has just gone to the background, and run
`expression -l objc -- (void)[[BGTaskScheduler sharedScheduler] _simulateLaunchForTaskWithIdentifier:@"com.beememorybank.blind.sync"]`
(or `...blind.work`), then detach; the screen's "Last background round" shows the result. Do not evaluate expressions once iOS has suspended
the app in the background: an interrupted expression leaves the main thread returning into lldb's trap address, and the app crashes the next
time it runs.

**The "no contact" warning works without a push server.** Every successful sync of an account moves that account's pending local notification to "now + 3 days".
While the node answers, it never shows; when the node goes silent - or iOS stops giving the app time, which looks the same from the phone - nothing
moves it any more and iOS shows it on time, even if the app never runs again. It needs the person's permission for notifications (asked
after pairing; without it the screen says that no warning can come).

### What iOS cannot do that Android does

| | Android blind app | iPhone blind app |
|---|---|---|
| periodic sync in the background | WorkManager, every 15 minutes on any network, guaranteed to run (Doze delays it) | only when iOS grants an app-refresh round: a few times a day at best, none when the app is rarely used, Background App Refresh is off or Low Power Mode is on |
| first load and backups in the background | WorkManager foreground job with a notification, runs to the end | only while the app is open, or in a processing window iOS grants (minutes, usually at night on the charger); a large first load can take several openings |
| "Back up now" while the app is closed | foreground service, keeps running | the backup runs only while the app is open; it pauses in the background and resumes |
| start after a reboot | BootReceiver re-enqueues the work | nothing until iOS grants a background round or the person opens the app; the keys are readable only after the first unlock |
| restart after a wipe of one account | a fresh process (helper activity) | the account becomes a fresh account in place (iOS forbids an app to restart itself, and none is needed) |
| several accounts in the background | WorkManager keeps the four unique names and one pass serves every account | the two fixed task identifiers serve the accounts in turn within the short time iOS gives each wake: each account syncs less often; only the open app syncs them all |
| warning about a silent node | the computer's alarm (the phone does not warn) | the computer's alarm, plus the phone's own local notification after three days |
| "Save to..." | the storage access framework picker | the share sheet (Save to Files, AirDrop, another app) |
| distribution | a signed APK anyone can install | only through Apple: a development install on registered devices, TestFlight, or the App Store, each with Apple's review rules |

## Build and run

On a Mac with Xcode 26.5 and the .NET 10.0.300 SDK with the matching iOS workloads (`dotnet workload install ios maui-ios --version 10.0.300.3`, which
carries .NET for iOS 26.5; the newest workload wants Xcode 27 and has not been tried):

```
scripts/build-ios-blind.sh simulator            # .app for the Apple-silicon simulator
xcrun simctl install booted mobile/BeeMemoryBank.BlindIos/bin/Debug/net10.0-ios/iossimulator-arm64/BeeMemoryBank.BlindIos.app
xcrun simctl launch booted com.beememorybank.blind

BMB_IOS_CODESIGN_KEY="Apple Development: <name> (<id>)" BMB_IOS_PROVISION="iOS Team Provisioning Profile: *" \
  scripts/build-ios-blind.sh device Release     # signed .app; xcrun devicectl device install app --device <id> <app>
scripts/build-ios-blind.sh ipa Release          # signed .ipa for App Store Connect / TestFlight (needs a distribution identity and profile)
```

After a change of a trimming setting (such as `UseSystemResourceKeys`), build a device app from a fresh `obj/`: the SDK's incremental
build compiles the changed assemblies ahead of time again, but not those the trimmer copies unchanged (`Konscious.*`,
the SQLitePCLRaw and SQLite3MC assemblies), and the app then stops at launch with "Failed to load AOT module ... out of date".

The signing identity, the profile and the Apple team never go into the repository: they come from the environment. A wildcard team profile
is enough (the app uses no capability a wildcard profile cannot carry: background modes, local notifications and its own Keychain group need
none). Over ssh, `codesign` needs the desktop session (`errSecInternalComponent` otherwise).

`-p:BmbE2E=true` (or a Debug build) makes the app take a call code from `BMB_BLIND_E2E_CALL_CODE` at launch: `tools/ios-e2e` uses it to pair
a simulator without touching the screen, because iOS asks "Open in ...?" for a link opened from outside. On an iPhone, where the Mac cannot
take a screenshot, `BMB_BLIND_E2E_PRINT_PHONE_CODE=1` makes the app repeat its phone code to the console `devicectl ... --console` attaches;
.NET writes `Console` output to NSLog on iOS, so the launch also sets `OS_ACTIVITY_DT_MODE=YES` (as Xcode does) to have NSLog copied to
stderr. A Release build for people contains neither (`IosBoundaryTests`).

## Checks

* `tests/BeeMemoryBank.BlindIos.Tests` (any OS): the app's project references only the shared libraries, the phone client and AppCore; their
  closure is [`IOS-APP.golden.txt`](IOS-APP.golden.txt) (unchanged by the accounts: no new product assembly); the built `.app` (on a Mac; `BMB_REQUIRE_IOS_SCAN=1` makes a missing build fail)
  carries exactly those product assemblies and passes the vault scan of the other blind apps; Info.plist (the two background tasks, the `bmb-blind-call` and `bmb-invite` schemes, no camera permission), entitlements and the privacy
  manifest; the Keychain store and the contract every head runs for secret and state stores (a blob of account A does not open as B, account 1's names unchanged byte for byte); the file protection and no-backup mark on every account folder; the device conditions behind fakes of `NWPathMonitor` and Low Power Mode;
  the background wake over several accounts (order, guard, slices, iOS ending the wake, two overlapping wakes and a wake against the open app, the two logs with two accounts, failure texts that carry a canary); the life cycle in both orderings of sleep and resume (a fake loop that notices any two operations at once, every sequence of up to five sleeps, resumes and wakes), with a round holding the turn and with a disposal in the middle of a transition, and the same ordering against the real scheduler; the generation bookkeeping of the notification callbacks and the reconciler that serializes the native calls (a fake notification center whose calls complete when the test says: a late withdrawal against a newer add, an update during an update, a lost answer, a request that fired); the real busy helper driven with twenty hostile exceptions (paths, tokens, Keychain item names, wrapped in aggregates) through the app's `Describer`, and the page guard that requires the registration and refuses a page that formats an exception itself; the source guard that keeps an exception's message and the status's raw failure fields off every page; the composition with two accounts, pairing, a restart of the app, removal, the wipe of one account; the upgrade of a 2.6.4 install; the screen lines and the per-account silence plan.
* What cannot be compiled on a Windows or Linux machine: `Platforms/iOS/*`, `Pages/*`, `App.xaml.cs`, `MauiProgram.cs` (they need the iOS workload, a Mac with Xcode). Their logic is kept in `Services/*`, which is compiled and tested everywhere; the iOS-only files were read through a syntax check and a check of every AppCore name they use, and the first Mac build is the test of the rest.
* `tools/ios-e2e/ios_e2e.py`: pairing, first load and a sync of the app on a simulator against a full node and its listener; with `--device`
  the same on a connected iPhone (a development-signed E2E build; the person at the phone keeps it unlocked and taps Allow when iOS asks
  about the local network and notifications). Run on an iPhone 11 Pro with iOS 26.5: all steps pass, a note written on the computer
  arrives on the phone.
