#!/usr/bin/env bash
# Builds the iPhone full app (mobile/BeeMemoryBank.FullIos) on a Mac with Xcode and the .NET "ios" and "maui-ios" workloads.
#
#   scripts/build-ios-full.sh simulator [Debug|Release]     -> .app for the Apple-silicon simulator (no signing)
#   scripts/build-ios-full.sh device    [Debug|Release]     -> signed .app for an iPhone (install with xcrun devicectl)
#   scripts/build-ios-full.sh ipa       [Release]           -> signed .ipa (the archive App Store Connect / TestFlight takes)
#
# Device signing is taken from the environment, never from the repository (the identity and the profile name carry the Apple team):
#   BMB_IOS_CODESIGN_KEY   the signing identity, e.g. "Apple Development: Jane Doe (ABCDE12345)"
#   BMB_IOS_PROVISION      the provisioning profile's name, e.g. "iOS Team Provisioning Profile: *" (a wildcard team profile works: the
#                          app needs no capability that a wildcard profile cannot carry)
#   BMB_IOS_KEYCHAIN       optional: the keychain that holds the identity (unlock it first)
#   BMB_IOS_BUNDLE_ID      optional: another bundle id than com.beememorybank.mobile
# Build speed, optional:
#   BMB_BUILD_JOBS         MSBuild nodes (-m), 2 by default; a big Mac can take more
#   BMB_IOS_NO_TRIM=1      a test build without trimming (MtouchLink=None): the app is several times larger, but .NET and MAUI are
#                          compiled to native code once and reused, so a small change rebuilds fast. Never for a release or the store.
#                          It also registers the app's types at start (Registrar=dynamic) and keeps no shared generic-code assembly
#                          (_IsDedupEnabled=false): otherwise every build rewrites the .NET and MAUI assemblies and that shared assembly, and their
#                          native code is made again (measured 15.11.2026 on an M5 Max: a one-file change 72 s -> 15 s; the app grows to ~1.2 GB).
# Over ssh codesign needs the desktop session (errSecInternalComponent otherwise): run this script inside it, e.g. with
#   sudo -n launchctl asuser "$(id -u)" sudo -n -u "$(id -un)" -H /bin/bash scripts/build-ios-full.sh device
set -euo pipefail

target="${1:-simulator}"
config="${2:-Debug}"
here="$(cd "$(dirname "$0")/.." && pwd)"
project="$here/mobile/BeeMemoryBank.FullIos/BeeMemoryBank.FullIos.csproj"
export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"

args=(-c "$config" -f net10.0-ios "-m:${BMB_BUILD_JOBS:-2}" -nr:false)
[ -n "${BMB_IOS_BUNDLE_ID:-}" ] && args+=("-p:BmbIosBundleId=$BMB_IOS_BUNDLE_ID")
if [ "${BMB_IOS_NO_TRIM:-}" = "1" ]; then
  [ "$target" = "ipa" ] && { echo "BMB_IOS_NO_TRIM is for test builds, not for an .ipa" >&2; exit 2; }
  args+=("-p:MtouchLink=None" "-p:Registrar=dynamic" "-p:_IsDedupEnabled=false")
fi

signing() {
  : "${BMB_IOS_CODESIGN_KEY:?set BMB_IOS_CODESIGN_KEY to the signing identity}"
  : "${BMB_IOS_PROVISION:?set BMB_IOS_PROVISION to the provisioning profile name}"
  args+=("-p:CodesignKey=$BMB_IOS_CODESIGN_KEY" "-p:CodesignProvision=$BMB_IOS_PROVISION")
  [ -n "${BMB_IOS_KEYCHAIN:-}" ] && args+=("-p:CodesignKeychain=$BMB_IOS_KEYCHAIN")
  return 0
}

case "$target" in
  simulator)
    dotnet build "$project" "${args[@]}" -r iossimulator-arm64
    echo "APP: $here/mobile/BeeMemoryBank.FullIos/bin/$config/net10.0-ios/iossimulator-arm64/BeeMemoryBank.FullIos.app"
    ;;
  device)
    signing
    dotnet build "$project" "${args[@]}" -r ios-arm64
    echo "APP: $here/mobile/BeeMemoryBank.FullIos/bin/$config/net10.0-ios/ios-arm64/BeeMemoryBank.FullIos.app"
    ;;
  ipa)
    signing
    dotnet publish "$project" "${args[@]}" -r ios-arm64 -p:ArchiveOnBuild=true
    echo "IPA: $(ls "$here"/mobile/BeeMemoryBank.FullIos/bin/"$config"/net10.0-ios/ios-arm64/publish/*.ipa)"
    ;;
  *)
    echo "usage: $0 simulator|device|ipa [Debug|Release]" >&2
    exit 2
    ;;
esac
