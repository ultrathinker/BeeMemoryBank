# The iPhone blind copy (`mobile/BeeMemoryBank.BlindIos`)

The iPhone app "Bee Memory Bank Blind" is a blind copy like the Android, Windows and macOS ones: it keeps an encrypted copy of a memory
bank, pairs with a node through a computer, syncs, makes backups, and never holds or asks for the master password. It is built on the same
libraries (Core, Crypto, Search, Storage, Sync, `Blind.PhoneClient`) and the same `Blind.AppCore` (controller, pairing, screen texts);
only the iOS parts are its own. It is source-only for now: there is no public App Store or TestFlight download. Owners preparing one can
follow [IOS-DISTRIBUTION.md](IOS-DISTRIBUTION.md).

## What is the app's own

| Part | Where | What it does |
|---|---|---|
| secrets | `Services/IosKeychainSecretStore.cs`, `Platforms/iOS/SecKeychainBackend.cs` | the identity seed, the pairing secret and the backup key as three generic-password items of the iOS Keychain (service `com.beememorybank.blind`), `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`, not synchronizable: readable by the background rounds while the phone is locked, never in iCloud Keychain or in a backup restored on another phone. Each item carries the macOS store's integrity envelope (version, SHA-256 of purpose and secret). A missing secret is `null`; any other Keychain error is an exception, never "lost" |
| data | `Platforms/iOS/IosContainer.cs` | `Library/Application Support/BeeMemoryBankBlind` in the app's container: the database, the replica work folder, the backups, the log and the state file. File protection `CompleteUntilFirstUserAuthentication` (encrypted by iOS until the first unlock after a restart), excluded from iCloud and computer backups (a backup restored elsewhere would hold a database without its keys) |
| state | `MacOsBlindStateStore.cs` (linked from the macOS adapters) | the small key/value state in one JSON file, written whole and renamed into place |
| in-app loop | `BlindTimerScheduler.cs` (linked from the desktop host) | while the app is in front: a check at once, a sync every 15 minutes, the long job (first load, due backup) every hour, the first load at once after pairing, retries of a failed first load |
| background | `Platforms/iOS/IosBlindBackground.cs`, `Services/IosBackgroundRounds.cs` | two BGTaskScheduler tasks (below) |
| "no contact" warning | `Services/BlindSilenceAlarm.cs`, `Platforms/iOS/IosSilenceNotifier.cs` | one pending local notification, three days after the last contact with the node, moved forward by every sync |
| first-run after a wipe | `Services/IosBlindRuntime.cs` | iOS gives an app no way to restart itself: "Disconnect and wipe" ends with a new composition in the same process (like the desktop copies), and the screen moves over to it |
| screen | `Pages/BlindHomePage.xaml(.cs)` | the Android screen's counterpart, plus the number of notes, the last contact, the newest problem, the background state and when the silence warning would come |

Why a project of its own and not a second target of the Android project: an iOS build needs a Mac with Xcode, and a multi-targeted Android
project would no longer restore on the Linux and Windows machines that build the Android app; the Android project's guards
(`ForbiddenReferencesTests`, `BlindManifestTests`, `AppBoundaryTests`) read its project, assets and output, which stay as they were; and the iOS
life cycle shares almost nothing with WorkManager and a foreground service.

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

Both are submitted again at the start of every round. The screen shows the last round iOS granted and whether Background App Refresh is
off. The simulator runs no background tasks at all (`BGTaskSchedulerErrorDomain` error 1); they can only be seen on a phone. There, a
round can be brought forward with Apple's debugging hook: attach Xcode's lldb to the app (a development-signed build) while it is still in
front or has just gone to the background, and run
`expression -l objc -- (void)[[BGTaskScheduler sharedScheduler] _simulateLaunchForTaskWithIdentifier:@"com.beememorybank.blind.sync"]`
(or `...blind.work`), then detach; the screen's "Last background round" shows the result. Do not evaluate expressions once iOS has suspended
the app in the background: an interrupted expression leaves the main thread returning into lldb's trap address, and the app crashes the next
time it runs.

**The "no contact" warning works without a push server.** Every successful sync moves one pending local notification to "now + 3 days".
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
| restart after "Disconnect and wipe" | a fresh process (helper activity) | a fresh composition in the same process (iOS forbids an app to restart itself) |
| warning about a silent node | the computer's alarm (the phone does not warn) | the computer's alarm, plus the phone's own local notification after three days |
| "Save to..." | the storage access framework picker | the share sheet (Save to Files, AirDrop, another app) |
| distribution | a signed APK anyone can install | only through Apple: a development install on registered devices, TestFlight, or the App Store, each with Apple's review rules |

## Build and run

On a Mac with Xcode 26.x and the .NET 10 SDK with the iOS workloads (`dotnet workload install ios maui-ios`; Xcode 26.5 was used for
verification):

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
`SQLitePCLRaw.lib.e_sqlite3.ios`), and the app then stops at launch with "Failed to load AOT module ... out of date".

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
  closure is [`IOS-APP.golden.txt`](IOS-APP.golden.txt); the built `.app` (on a Mac; `BMB_REQUIRE_IOS_SCAN=1` makes a missing build fail)
  carries exactly those product assemblies and passes the vault scan of the other blind apps; Info.plist, entitlements and the privacy
  manifest; the Keychain store; the composition with pairing, a restart of the app and the wipe; the screen lines and the silence plan.
* `tools/ios-e2e/ios_e2e.py`: pairing, first load and a sync of the app on a simulator against a full node and its listener; with `--device`
  the same on a connected iPhone (a development-signed E2E build; the person at the phone keeps it unlocked and taps Allow when iOS asks
  about the local network and notifications). Run on an iPhone 11 Pro with iOS 26.5: all steps pass, a note written on the computer
  arrives on the phone.
