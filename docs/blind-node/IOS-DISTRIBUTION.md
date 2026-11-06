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

## 6. One command for every file, and the signing notes

`scripts/pack-ios-ipas.sh` runs on the Mac and puts every file of this checklist into one folder, with a `SHA256SUMS`:

```bash
scripts/pack-ios-ipas.sh --output ~/ios-packages --via-desktop-session
```

| Folder | Signed with | Installs on | Use |
|---|---|---|---|
| `dev/` | Apple Development identity, the team's wildcard development profile | the iPhones registered to the team | try the build on your own phone: `xcrun devicectl device install app --device <id> FILE.ipa` |
| `adhoc/` | Apple Distribution, an Ad Hoc profile of the app's own App ID | the iPhones registered to the team (up to 100), without TestFlight | section 4 |
| `appstore/` | Apple Distribution, an App Store profile of the app's own App ID | nothing directly | upload with Transporter or Xcode Organizer (sections 2 and 3) |

Both apps are built, then each file is checked offline (`--check FILE.ipa --tier T --app A` checks one file): `codesign --verify --deep --strict`,
bundle id, marketing version equal to `VERSION`, build number, minimum OS 15.0, an arm64-only iOS binary built with the iOS 26 SDK, the usage strings,
the privacy manifest, the launch screen, the app icon with an opaque 1024 px rendition, no `get-task-allow` in a distribution file, the profile's
expiry, its device list (none in an App Store profile) and that it names exactly the app's App ID. A tier that cannot be built or checked is named at the end
and the exit code is 1.

How the files are signed (no names, identifiers or passwords belong in this repository):

* **The development tier** uses the existing `build-ios-<app>.sh ipa Release` with `BMB_IOS_CODESIGN_KEY` (default `Apple Development`, a unique part of
  the identity's name is enough) and `BMB_IOS_PROVISION` (default `iOS Team Provisioning Profile: *`, the wildcard profile Xcode makes for the team).
  That script also leaves an `.xcarchive` in Xcode's Archives folder.
* **The two distribution tiers are exports of that archive** by `xcodebuild -exportArchive` with `signingStyle=automatic` and
  `-allowProvisioningUpdates` (`method` `release-testing` for Ad Hoc, `app-store-connect` for the App Store). Xcode, signed in to the team's Apple
  account (Xcode > Settings > Accounts), registers the two explicit App IDs, makes the cloud-managed Apple Distribution certificate and the Ad Hoc and App
  Store profiles, and signs. The private key of a cloud-managed certificate never reaches the Mac, so `build-ios-<app>.sh ipa` with
  `BMB_IOS_CODESIGN_KEY='Apple Distribution'` finds no identity to use; export the archive as above instead (or install a locally made distribution
  certificate and use section 2). The only things the account holder must do by hand are the steps Apple keeps for the person: accepting a changed
  Program License Agreement, signing in to Xcode, and creating the App Store Connect records (section 1). If the Mac's Xcode has lost the account (it
  was signed out, or its keychain entries are gone), the exports stop with `error: exportArchive No Accounts` and `No signing certificate "iOS
  Distribution" found`, while the development tier still builds; the script then names the cause. Sign in again (Xcode > Settings > Accounts > + >
  Apple ID) and run `pack-ios-ipas.sh --tiers adhoc,appstore` (the development build is repeated, it is incremental).
* **Over ssh** `codesign` and the Xcode account need the owner's desktop session: `--via-desktop-session` starts the script again with
  `sudo -n launchctl asuser`. Unlock the keychain that holds the development identity first and give it as `BMB_IOS_KEYCHAIN` if it is not a default one.
* **Version and build number.** The marketing version is the root `VERSION`; the build number is `major*10000 + minor*100 + patch`, so 2.7.3 is build
  20703. App Store Connect accepts one upload per (version, build) pair: a rebuild of the same version cannot be uploaded again, the next upload needs a
  new patch version.
* **Export compliance.** `Info.plist` does not set `ITSAppUsesNonExemptEncryption`, so App Store Connect asks the export-compliance question for each
  uploaded build. The answer is the owner's (the apps encrypt the person's own data with standard algorithms); verify it against Apple's current
  questions.
* **Expiry.** A development or distribution certificate and the profiles made from it last one year from the day Xcode made them; `pack-ios-ipas.sh`
  prints the days left of each file's profile. Running it again with `-allowProvisioningUpdates` (it always does) renews what has expired. A new
  Ad Hoc device must be registered in the developer portal first; the next export then includes it in the profile, and the old Ad Hoc file does not
  install there.
* **First start of a development build.** A phone that has not trusted the team's development certificate refuses to start the app ("its profile has not
  been explicitly trusted by the user"): Settings > General > VPN & Device Management, the developer entry, Trust. Ad Hoc and App Store builds need no
  such step.
