# Distributing the iPhone apps

This is the owner checklist for both source-only iPhone apps. `mobile/BeeMemoryBank.BlindIos` is a blind copy: it stores an encrypted
replica and never holds or asks for the master password. `mobile/BeeMemoryBank.FullIos` is the regular app: it keeps the vault on the
phone, unlocks with the master password or optional Face ID / Touch ID, and calls peers but cannot be called. Keep that distinction in
the App Store descriptions and review notes. The code, limits, and privacy behavior are described in [IOS.md](IOS.md),
[the regular-app guide](../full-node/IOS.md), and [PRIVACY.md](../../PRIVACY.md).

## 1. Create the App Store Connect record

1. In Certificates, Identifiers and Profiles, register these explicit App IDs. Do not use the build-only `BmbIosBundleId` override for a public record.

   | App | Bundle ID |
   |---|---|
   | Blind copy | `com.beememorybank.blind` |
   | Regular app | `com.beememorybank.mobile` |

2. In App Store Connect, create a separate iOS app record for each bundle ID. Choose the SKU, primary language, price and availability there.
3. Complete each app's name, support and privacy URLs, age rating, export-compliance answers, App Privacy details, screenshots and the
   privacy-policy link. The repository privacy document states that the app has no telemetry, analytics, crash reporting or project
   account; verify every answer against the final build and the current App Store Connect questionnaire.

## 2. Sign an upload build

Use a Mac with Xcode 26.x and the .NET 10 SDK with the `ios` and `maui-ios` workloads. Create an App Store distribution certificate and
an App Store provisioning profile for each bundle ID in Apple's developer tools. Keep the signing identity, profile name and any account
credentials outside this repository.

From the repository root, place the signing values only in the current shell environment and create the archive and IPA:

```bash
export BMB_IOS_CODESIGN_KEY='<distribution signing identity>'
export BMB_IOS_PROVISION='<App Store provisioning profile name>'
scripts/build-ios-blind.sh ipa Release
scripts/build-ios-full.sh ipa Release
```

Each script runs `dotnet publish` for `net10.0-ios`, `ios-arm64` with `ArchiveOnBuild=true` and prints its IPA path. Each derives the
marketing version from the root `VERSION` file and the build number from its numeric `major.minor.patch` value. Before upload, confirm
that App Store Connect accepts both values as newer than the last uploaded build.

Upload each printed IPA using Xcode Organizer or Apple's Transporter, select its app record, and wait for processing to finish in App
Store Connect. Do not put an Apple account, app-specific password, certificate, provisioning profile, team identifier or device identifier
in a command, checked-in file or report.

## 3. TestFlight and review

1. Add each processed build to an internal TestFlight group first. For the blind copy verify pairing, a first load, manual sync, a failed
   pinned-TLS call, the local-network permission, and the local three-day no-contact notification. For the regular app verify create,
   join, password and biometric unlock, local Markdown rendering, and client sync.
2. For external TestFlight testing, provide the required beta information and submit the build for Apple's beta review. Give reviewers a
   safe test node and complete pairing instructions; never give them a master password or access to a real vault.
3. For the App Store, fill in review notes that explain the blind-copy model, pairing flow, local-network access, background behavior and
   how a reviewer can exercise the app. Submit only after the product metadata, privacy answers and screenshots match the tested build.

Apple reviews each TestFlight external build and App Store submission under its current policies. Recheck the current requirements before
submitting because Apple changes them independently of this repository.

## 4. Ad hoc alternative

For a small known-device rollout, register the devices in Apple's developer portal, create an Ad Hoc profile for the relevant explicit App
ID, and sign its IPA with that profile using the matching script. Distribute it through a permitted ad hoc channel. Apple's membership
limits ad hoc registration to 100 devices of each type per membership year; the registered-device list and provisioning profile control who can install it.

## 5. Renew before expiry

Apple development and distribution certificates, provisioning profiles and the developer-program membership expire on Apple's schedule,
normally yearly. Renew the membership and expiring certificates/profiles before they do, create a new build, and upload it to TestFlight
or the App Store. Ad hoc installs stop working when their profile expires, so plan replacement builds before that date.
