#!/usr/bin/env bash
# Builds the iPhone blind app (mobile/BeeMemoryBank.BlindIos) on a Mac with Xcode and the .NET "ios" and "maui-ios" workloads.
#
#   scripts/build-ios-blind.sh simulator [Debug|Release]     -> .app for the Apple-silicon simulator (no signing)
#   scripts/build-ios-blind.sh device    [Debug|Release]     -> signed .app for an iPhone (install with xcrun devicectl)
#   scripts/build-ios-blind.sh ipa       [Release]           -> signed .ipa (the archive App Store Connect / TestFlight takes)
#
# Device signing is taken from the environment, never from the repository (the identity and the profile name carry the Apple team):
#   BMB_IOS_CODESIGN_KEY   the signing identity, e.g. "Apple Development: Jane Doe (ABCDE12345)"
#   BMB_IOS_PROVISION      the provisioning profile's name, e.g. "iOS Team Provisioning Profile: *" (a wildcard team profile works: the
#                          app needs no capability that a wildcard profile cannot carry)
#   BMB_IOS_KEYCHAIN       optional: the keychain that holds the identity (unlock it first)
#   BMB_IOS_BUNDLE_ID      optional: another bundle id than com.beememorybank.blind
# Over ssh codesign needs the desktop session (errSecInternalComponent otherwise): run this script inside it, e.g. with
#   sudo -n launchctl asuser "$(id -u)" sudo -n -u "$(id -un)" -H /bin/bash scripts/build-ios-blind.sh device
set -euo pipefail

target="${1:-simulator}"
config="${2:-Debug}"
here="$(cd "$(dirname "$0")/.." && pwd)"
project="$here/mobile/BeeMemoryBank.BlindIos/BeeMemoryBank.BlindIos.csproj"
export DEVELOPER_DIR="${DEVELOPER_DIR:-/Applications/Xcode.app/Contents/Developer}"

args=(-c "$config" -f net10.0-ios -m:2 -nr:false)
[ -n "${BMB_IOS_BUNDLE_ID:-}" ] && args+=("-p:BmbIosBundleId=$BMB_IOS_BUNDLE_ID")

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
    echo "APP: $here/mobile/BeeMemoryBank.BlindIos/bin/$config/net10.0-ios/iossimulator-arm64/BeeMemoryBank.BlindIos.app"
    ;;
  device)
    signing
    dotnet build "$project" "${args[@]}" -r ios-arm64
    echo "APP: $here/mobile/BeeMemoryBank.BlindIos/bin/$config/net10.0-ios/ios-arm64/BeeMemoryBank.BlindIos.app"
    ;;
  ipa)
    signing
    dotnet publish "$project" "${args[@]}" -r ios-arm64 -p:ArchiveOnBuild=true
    echo "IPA: $(ls "$here"/mobile/BeeMemoryBank.BlindIos/bin/"$config"/net10.0-ios/ios-arm64/publish/*.ipa)"
    ;;
  *)
    echo "usage: $0 simulator|device|ipa [Debug|Release]" >&2
    exit 2
    ;;
esac
