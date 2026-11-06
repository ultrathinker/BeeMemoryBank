#!/usr/bin/env bash
# Builds the ready-to-install iPhone packages of both apps on a Mac, every tier it can, into one folder with a SHA256SUMS file.
#
#   scripts/pack-ios-ipas.sh --output DIR [--apps blind,full] [--tiers dev,adhoc,appstore] [--via-desktop-session]
#   scripts/pack-ios-ipas.sh --check FILE.ipa --tier dev|adhoc|appstore --app blind|full      (only the offline checks of one package)
#
# Tiers (the folder gets one sub-folder each):
#   dev       signed with the Apple Development identity and the team's wildcard development profile; installs on the registered
#             iPhones (xcrun devicectl device install app --device <id> FILE.ipa); the owner trusts the developer once on the phone
#   adhoc     signed for distribution (Apple Distribution) with an Ad Hoc profile of the app's own App ID; installs on the registered
#             iPhones without TestFlight (Finder, Apple Configurator, a link with a manifest)
#   appstore  signed for distribution with an App Store profile; the file Transporter / Xcode Organizer uploads to App Store Connect
#             and TestFlight
#
# How: the existing build-ios-<app>.sh `ipa` target makes a development-signed .ipa AND an .xcarchive (in Xcode's Archives folder);
# the two distribution tiers are exports of that archive by `xcodebuild -exportArchive` with automatic signing and
# -allowProvisioningUpdates, so Xcode itself makes (or reuses) the Apple Distribution certificate, registers the two explicit App IDs and
# makes the Ad Hoc and App Store profiles, using the Apple account that is signed in to Xcode on this Mac. Nothing is uploaded to Apple's
# App Store Connect, and no account name or password is read or written here.
#
# Environment (never in the repository):
#   BMB_IOS_CODESIGN_KEY  identity of the dev tier (default "Apple Development": codesign takes a unique part of the name)
#   BMB_IOS_PROVISION     wildcard development profile of the dev tier (default "iOS Team Provisioning Profile: *")
#   BMB_IOS_KEYCHAIN      optional: the keychain that holds the development identity (unlock it first)
#   BMB_IOS_TEAM_ID       optional: the team of the export (default: taken from the development certificate)
# Over ssh codesign needs the desktop session (errSecInternalComponent otherwise): --via-desktop-session starts this script again inside it
# with `sudo -n launchctl asuser`, which needs the person to be logged in at the Mac and `sudo -n` to work for this user.
#
# Every package is then checked offline (codesign, bundle id, version and build number, minimum OS, entitlements, profile, usage strings,
# icon, launch screen, Mach-O); a tier that fails to build or to check is named at the end and the exit code is 1.
set -uo pipefail

here="$(cd "$(dirname "$0")/.." && pwd)"
export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"

output=""; apps="blind,full"; tiers="dev,adhoc,appstore"; via_desktop=0; check_file=""; check_tier=""; check_app=""
while [ $# -gt 0 ]; do
  case "$1" in
    --output) output="${2:?--output needs a folder}"; shift 2 ;;
    --apps) apps="${2:?}"; shift 2 ;;
    --tiers) tiers="${2:?}"; shift 2 ;;
    --via-desktop-session) via_desktop=1; shift ;;
    --check) check_file="${2:?}"; shift 2 ;;
    --tier) check_tier="${2:?}"; shift 2 ;;
    --app) check_app="${2:?}"; shift 2 ;;
    *) echo "usage: $0 --output DIR [--apps blind,full] [--tiers dev,adhoc,appstore] [--via-desktop-session] | --check FILE.ipa --tier T --app A" >&2; exit 2 ;;
  esac
done

if [ "$via_desktop" = 1 ] && [ -z "${BMB_IOS_IN_DESKTOP_SESSION:-}" ]; then
  exec sudo -n launchctl asuser "$(id -u)" sudo -n -u "$(id -un)" -H env BMB_IOS_IN_DESKTOP_SESSION=1 /bin/bash "$0" \
    ${output:+--output "$output"} --apps "$apps" --tiers "$tiers" ${check_file:+--check "$check_file" --tier "$check_tier" --app "$check_app"}
fi

version="$(tr -d '[:space:]' < "$here/VERSION")"
IFS=. read -r v_major v_minor v_patch <<< "$version"
build_number=$((v_major * 10000 + v_minor * 100 + v_patch))   # the same formula as ApplicationVersion in both projects

pb() { /usr/libexec/PlistBuddy -c "Print :$2" "$1" 2>/dev/null; }

bundle_id_of() { case "$1" in blind) echo com.beememorybank.blind ;; full) echo com.beememorybank.mobile ;; esac; }
project_of() { case "$1" in blind) echo BeeMemoryBank.BlindIos ;; full) echo BeeMemoryBank.FullIos ;; esac; }
file_name_of() { case "$1" in blind) echo "BeeMemoryBank-Blind-$version-ios-$2.ipa" ;; full) echo "BeeMemoryBank-$version-ios-$2.ipa" ;; esac; }

fails=0
pass() { echo "PASS $1"; }
fail() { echo "FAIL $1${2:+ - $2}"; fails=$((fails + 1)); }
check() { if [ "$2" = "$3" ]; then pass "$1"; else fail "$1" "expected '$3', found '$2'"; fi; }

# Offline checks of one package: check_ipa FILE tier app
check_ipa() {
  local ipa="$1" tier="$2" app="$3" work project bid plist exe profile now
  project="$(project_of "$app")"; bid="$(bundle_id_of "$app")"
  work="$(mktemp -d "${TMPDIR:-/tmp}/bmb-ipa-check.XXXXXX")"
  echo "== $(basename "$ipa") ($tier, $app)  $(du -h "$ipa" | cut -f1)"
  unzip -q "$ipa" -d "$work" || { fail "ipa unzips"; return; }
  local bundle="$work/Payload/$project.app"
  [ -d "$bundle" ] || { fail "Payload/$project.app exists"; return; }
  plist="$bundle/Info.plist"
  if codesign --verify --deep --strict "$bundle" 2>/dev/null; then pass "codesign --verify --deep --strict"; else fail "codesign --verify --deep --strict"; fi
  check "bundle id" "$(pb "$plist" CFBundleIdentifier)" "$bid"
  check "marketing version = VERSION" "$(pb "$plist" CFBundleShortVersionString)" "$version"
  check "build number = major*10000+minor*100+patch" "$(pb "$plist" CFBundleVersion)" "$build_number"
  check "platform" "$(pb "$plist" DTPlatformName)" "iphoneos"
  check "minimum OS" "$(pb "$plist" MinimumOSVersion)" "15.0"
  exe="$(pb "$plist" CFBundleExecutable)"
  local build; build="$(vtool -show-build "$bundle/$exe" 2>/dev/null | tr -s ' ' | tr '\n' ' ')"
  case "$build" in *"platform IOS"*"minos 15.0"*) pass "Mach-O is an iOS binary with minos 15.0 ($(echo "$build" | grep -o 'sdk [0-9.]*'))" ;; *) fail "Mach-O build version" "$build" ;; esac
  case "$(lipo -archs "$bundle/$exe" 2>/dev/null)" in arm64) pass "Mach-O is arm64 only" ;; *) fail "Mach-O architectures" "$(lipo -archs "$bundle/$exe" 2>/dev/null)" ;; esac
  check "iPhone only (UIDeviceFamily 1)" "$(plutil -extract UIDeviceFamily json -o - "$plist" 2>/dev/null)" "[1]"
  [ -n "$(pb "$plist" NSLocalNetworkUsageDescription)" ] && pass "local network usage string" || fail "local network usage string"
  if [ "$app" = full ]; then
    [ -n "$(pb "$plist" NSFaceIDUsageDescription)" ] && pass "Face ID usage string" || fail "Face ID usage string"
  else
    pb "$plist" NSCameraUsageDescription > /dev/null && fail "blind app asks for no camera permission" || pass "blind app asks for no camera permission"
    case "$(plutil -extract BGTaskSchedulerPermittedIdentifiers json -o - "$plist" 2>/dev/null)" in
      *"$bid.sync"*"$bid.work"*) pass "both background task identifiers declared" ;; *) fail "background task identifiers" ;; esac
  fi
  [ -f "$bundle/PrivacyInfo.xcprivacy" ] && pass "privacy manifest present" || fail "privacy manifest present"
  [ -n "$(pb "$plist" UILaunchStoryboardName)" ] && [ -d "$bundle/$(pb "$plist" UILaunchStoryboardName).storyboardc" ] && pass "launch screen present" || fail "launch screen present"
  if assetutil --info "$bundle/Assets.car" 2>/dev/null | tr -d ' \n' | grep -q '"Name":"appicon"'; then pass "app icon in Assets.car"; else fail "app icon in Assets.car"; fi
  if assetutil --info "$bundle/Assets.car" 2>/dev/null | tr -d ' \n' | grep -q '"PixelWidth":1024'; then pass "1024 px App Store icon rendition"; else fail "1024 px App Store icon rendition"; fi
  # App Store Connect refuses a marketing icon with transparency
  if assetutil --info "$bundle/Assets.car" 2>/dev/null | python3 -I -c '
import json, sys
icons = [e for e in json.load(sys.stdin) if e.get("PixelWidth") == 1024 and e.get("Name") == "appicon"]
sys.exit(0 if icons and all(e.get("Opaque") for e in icons) else 1)'; then pass "the 1024 px icon is opaque"; else fail "the 1024 px icon is opaque"; fi
  [ -n "$(pb "$plist" ITSAppUsesNonExemptEncryption)" ] && pass "export-compliance key set" \
    || echo "INFO ITSAppUsesNonExemptEncryption is not in Info.plist: App Store Connect asks the export-compliance question for each build"
  # signature, entitlements, profile
  local authority ent; authority="$(codesign -dvv "$bundle" 2>&1 | grep '^Authority=' | head -1 | sed -e 's/^Authority=//' -e 's/:.*//')"
  ent="$work/entitlements.plist"; codesign -d --entitlements :- "$bundle" > "$ent" 2>/dev/null
  case "$tier" in dev) check "signing identity type" "$authority" "Apple Development" ;; *) check "signing identity type" "$authority" "Apple Distribution" ;; esac
  if [ "$tier" = dev ]; then check "get-task-allow (development build)" "$(pb "$ent" get-task-allow)" "true"
  else check "no get-task-allow=true in a distribution build" "$(pb "$ent" get-task-allow)" "false"; fi
  case "$(plutil -extract keychain-access-groups json -o - "$ent" 2>/dev/null)" in *"$bid\"]") pass "keychain group is the app's own" ;; *) fail "keychain group is the app's own" ;; esac
  profile="$work/profile.plist"
  if security cms -D -i "$bundle/embedded.mobileprovision" > "$profile" 2>/dev/null; then
    now="$(date +%s)"
    local exp; exp="$(plutil -extract ExpirationDate raw -o - "$profile" 2>/dev/null)"
    local exp_s; exp_s="$(date -j -u -f '%Y-%m-%dT%H:%M:%SZ' "$exp" +%s 2>/dev/null || echo 0)"
    if [ "$exp_s" -gt "$now" ]; then pass "profile valid until $(date -j -u -f '%s' "$exp_s" '+%Y-%m-%d') ($(( (exp_s - now) / 86400 )) days left)"; else fail "profile not expired"; fi
    local devices; devices="$(pb "$profile" ProvisionedDevices | grep -c -E '^[[:space:]]+[0-9A-Fa-f-]{20,}[[:space:]]*$')"
    case "$tier" in
      appstore) [ "$devices" = 0 ] && pass "App Store profile lists no devices" || fail "App Store profile lists no devices" "$devices" ;;
      *) [ "$devices" -gt 0 ] && pass "profile lists $devices device(s)" || fail "profile lists devices" ;;
    esac
    case "$tier" in
      dev) ;;
      *) case "$(pb "$profile" Entitlements:application-identifier)" in ??????????."$bid") pass "profile is for exactly this App ID" ;; *) fail "profile is for exactly this App ID" ;; esac ;;
    esac
  else
    fail "embedded.mobileprovision readable"
  fi
  rm -rf "$work"
}

if [ -n "$check_file" ]; then
  check_ipa "$check_file" "${check_tier:?--tier needed}" "${check_app:?--app needed}"
  [ "$fails" = 0 ] && echo "CHECKS-OK" || echo "CHECKS-FAILED: $fails"
  [ "$fails" = 0 ]; exit $?
fi

[ -n "$output" ] || { echo "--output DIR is required" >&2; exit 2; }
mkdir -p "$output"
output="$(cd "$output" && pwd)"
dev_key="${BMB_IOS_CODESIGN_KEY:-Apple Development}"
dev_profile="${BMB_IOS_PROVISION:-iOS Team Provisioning Profile: *}"
team="${BMB_IOS_TEAM_ID:-}"
if [ -z "$team" ]; then
  team="$(security find-certificate -c "$dev_key" -p ${BMB_IOS_KEYCHAIN:+"$BMB_IOS_KEYCHAIN"} 2>/dev/null | openssl x509 -noout -subject 2>/dev/null \
    | grep -o -E 'OU ?= ?[A-Z0-9]{10}' | head -1 | grep -o -E '[A-Z0-9]{10}$' || true)"
fi
archives_root="$HOME/Library/Developer/Xcode/Archives"
failed_tiers=""
echo "=== iPhone packages $version (build $build_number): apps=$apps tiers=$tiers -> $output"

for app in ${apps//,/ }; do
  project="$(project_of "$app")"
  [ -n "$project" ] || { echo "unknown app '$app'"; exit 2; }
  marker="$(mktemp "${TMPDIR:-/tmp}/bmb-ipa-marker.XXXXXX")"
  echo "=== $app: development-signed archive and .ipa (build-ios-$app.sh ipa Release)"
  if ! BMB_IOS_CODESIGN_KEY="$dev_key" BMB_IOS_PROVISION="$dev_profile" bash "$here/scripts/build-ios-$app.sh" ipa Release; then
    echo "FAILED: the development build of $app"; failed_tiers="$failed_tiers $app/build"; continue
  fi
  dev_ipa="$here/mobile/$project/bin/Release/net10.0-ios/ios-arm64/publish/$project.ipa"
  archive="$(find "$archives_root" -maxdepth 2 -name "$project *.xcarchive" -newer "$marker" 2>/dev/null | sort | tail -1)"
  rm -f "$marker"
  for tier in ${tiers//,/ }; do
    mkdir -p "$output/$tier"
    target="$output/$tier/$(file_name_of "$app" "$tier")"
    case "$tier" in
      dev) cp "$dev_ipa" "$target" ;;
      adhoc|appstore)
        if [ -z "$archive" ] || [ -z "$team" ]; then echo "FAILED: $app/$tier needs the archive ('$archive') and the team id"; failed_tiers="$failed_tiers $app/$tier"; continue; fi
        method=release-testing; [ "$tier" = appstore ] && method=app-store-connect
        stage="$(mktemp -d "${TMPDIR:-/tmp}/bmb-ipa-export.XXXXXX")"
        cat > "$stage/ExportOptions.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>method</key><string>$method</string>
  <key>teamID</key><string>$team</string>
  <key>signingStyle</key><string>automatic</string>
  <key>destination</key><string>export</string>
  <key>manageAppVersionAndBuildNumber</key><false/>
  <key>stripSwiftSymbols</key><true/>
</dict></plist>
PLIST
        echo "=== $app: export for $tier (xcodebuild -exportArchive, method $method)"
        if xcodebuild -exportArchive -archivePath "$archive" -exportPath "$stage/out" -exportOptionsPlist "$stage/ExportOptions.plist" -allowProvisioningUpdates \
           > "$stage/export.log" 2>&1 && [ -f "$stage/out/$project.ipa" ]; then
          cat "$stage/export.log"
          cp "$stage/out/$project.ipa" "$target"
        else
          cat "$stage/export.log"
          if grep -q 'No Accounts' "$stage/export.log"; then
            echo "FAILED: the $tier export of $app - Xcode has no Apple account signed in on this Mac (Xcode > Settings > Accounts > + > Apple ID; only the account holder can do it). Then run again with --tiers adhoc,appstore"
          else
            echo "FAILED: the $tier export of $app (Apple may need the owner: an agreement to accept, or a login in Xcode's Accounts)"
          fi
          failed_tiers="$failed_tiers $app/$tier"
          rm -rf "$stage"; continue
        fi
        rm -rf "$stage" ;;
      *) echo "unknown tier '$tier'"; exit 2 ;;
    esac
    check_ipa "$target" "$tier" "$app" || true
  done
done

( cd "$output" && find . -name '*.ipa' | sed 's|^\./||' | sort | xargs shasum -a 256 > SHA256SUMS )
echo "=== SHA256SUMS"; cat "$output/SHA256SUMS"
[ -n "$failed_tiers" ] && echo "NOT BUILT:$failed_tiers"
if [ "$fails" != 0 ]; then echo "OFFLINE CHECKS FAILED: $fails"; fi
if [ -n "$failed_tiers" ] || [ "$fails" != 0 ]; then exit 1; fi
echo "ALL-PACKAGES-OK"
