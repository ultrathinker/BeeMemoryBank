#!/usr/bin/env bash
# Builds "BeeMemoryBank Blind.app", the quiet blind app for macOS, from the repository.
#
#   scripts/pack-macos-blind.sh [--rid osx-arm64|osx-x64] [--output DIR] [--publish-dir DIR] [--skip-self-check]
#
# What it does (on a Mac; it needs the .NET SDK, plutil, rsync):
#   1. dotnet publish of desktop/BeeMemoryBank.BlindDesktop, self-contained, for the RID (default: this Mac's architecture), into
#      <output>/work/publish - or it takes a ready publish folder (--publish-dir).
#   2. Assembles <output>/BeeMemoryBank Blind.app:
#        Contents/MacOS/        the publish output (one executable, BeeMemoryBank.BlindDesktop; no .pdb files, no crash-dump helper)
#        Contents/Resources/    AppIcon.icns, desktop/BeeMemoryBank.BlindDesktop/Packaging/AppIcon.icns copied as it is (16 px to 512@2x)
#        Contents/Info.plist    bundle id com.beememorybank.blind, version from the VERSION file, LSUIElement (no Dock icon),
#                               NSHighResolutionCapable, LSMinimumSystemVersion
#        Contents/PkgInfo       APPL????
#   3. Checks the bundle: plutil -lint, the layout, exactly one executable, no Vault DLL, the minimum macOS version is not below what
#      the native libraries in the bundle need, and (unless --skip-self-check) that
#      "Contents/MacOS/BeeMemoryBank.BlindDesktop --self-check --data-dir <scratch folder under work/>" exits 0.
#
# The result is UNSIGNED and NOT notarized. Signing and notarization are an optional branch that is OFF unless BMB_MACOS_SIGN=1 (see
# the optional branch at the end of this file). Nothing in this script contains a signing identity, a team id or a
# password: they are read from the environment when the branch is switched on.
#
# This script never deletes anything. It refuses to write into an existing "BeeMemoryBank Blind.app" (choose another --output or
# remove the old one yourself); its working files (the publish output, the iconset, the self-check's scratch folder) stay in
# <output>/work, and the bundle does not depend on them.

set -euo pipefail

APP_NAME="BeeMemoryBank Blind"
BUNDLE_ID="com.beememorybank.blind"
EXE_NAME="BeeMemoryBank.BlindDesktop"
# The highest "minos" of the native libraries .NET 10 ships for macOS. Step 3 verifies it against the real files of every bundle.
MIN_MACOS="12.0"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT="$ROOT/desktop/BeeMemoryBank.BlindDesktop/BeeMemoryBank.BlindDesktop.csproj"
ICON_ICNS="$ROOT/desktop/BeeMemoryBank.BlindDesktop/Packaging/AppIcon.icns"
ENTITLEMENTS="$SCRIPT_DIR/macos-blind.entitlements"

RID=""
OUTPUT=""
PUBLISH_DIR=""
SELF_CHECK=1

usage() { echo "usage: scripts/pack-macos-blind.sh [--rid osx-arm64|osx-x64] [--output DIR] [--publish-dir DIR] [--skip-self-check]"; }
die() { echo "pack-macos-blind: $*" >&2; exit 1; }
say() { echo "pack-macos-blind: $*"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --rid) RID="${2:?--rid needs a value}"; shift 2 ;;
    --output) OUTPUT="${2:?--output needs a folder}"; shift 2 ;;
    --publish-dir) PUBLISH_DIR="${2:?--publish-dir needs a folder}"; shift 2 ;;
    --skip-self-check) SELF_CHECK=0; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; die "unknown argument: $1" ;;
  esac
done

[ "$(uname -s)" = "Darwin" ] || die "this script builds a macOS app bundle and has to run on a Mac"

if [ -z "$RID" ]; then
  case "$(uname -m)" in
    arm64) RID="osx-arm64" ;;
    x86_64) RID="osx-x64" ;;
    *) die "unknown architecture $(uname -m): pass --rid" ;;
  esac
fi
case "$RID" in osx-arm64|osx-x64) ;; *) die "--rid must be osx-arm64 or osx-x64 (got $RID)" ;; esac

# ---- version: the VERSION file is the only source ---------------------------------------------------------------------------
[ -f "$ROOT/VERSION" ] || die "no VERSION file in $ROOT"
VERSION="$(tr -d '[:space:]' < "$ROOT/VERSION")"
case "$VERSION" in
  [0-9]*.[0-9]*.[0-9]*) ;;
  *) die "VERSION '$VERSION' is not like 2.0.2" ;;
esac
# CFBundleShortVersionString is at most three numbers (no pre-release suffix); CFBundleVersion is the same here.
SHORT_VERSION="$(printf '%s' "$VERSION" | sed -E 's/^([0-9]+\.[0-9]+\.[0-9]+).*$/\1/')"

[ -f "$ICON_ICNS" ] || die "icon not found: $ICON_ICNS"
[ -n "$OUTPUT" ] || OUTPUT="$ROOT/publish/macos-blind/$RID"
mkdir -p "$OUTPUT"
OUTPUT="$(cd "$OUTPUT" && pwd)"
WORK="$OUTPUT/work"
APP="$OUTPUT/$APP_NAME.app"
[ ! -e "$APP" ] || die "$APP already exists: choose another --output, or remove it yourself (this script does not delete)"
mkdir -p "$WORK"

# ---- 1. publish ---------------------------------------------------------------------------------------------------------------
if [ -z "$PUBLISH_DIR" ]; then
  PUBLISH_DIR="$WORK/publish"
  [ ! -e "$PUBLISH_DIR" ] || die "$PUBLISH_DIR already exists: choose another --output (or pass it as --publish-dir)"
  say "publishing $RID (self-contained) into $PUBLISH_DIR"
  dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true -o "$PUBLISH_DIR" -nologo -v q
fi
[ -x "$PUBLISH_DIR/$EXE_NAME" ] || die "no executable $EXE_NAME in $PUBLISH_DIR"

# ---- 2. the bundle ------------------------------------------------------------------------------------------------------------
say "assembling $APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
# No debug symbols, and never the crash-dump helper: the app is the only program in the bundle.
rsync -a --exclude='*.pdb' --exclude='createdump' "$PUBLISH_DIR/" "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/$EXE_NAME"

printf 'APPL????' > "$APP/Contents/PkgInfo"

# The icon: the project's own .icns (drawn for every size from 16 px to 512@2x), copied as it is.
cp "$ICON_ICNS" "$APP/Contents/Resources/AppIcon.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>en</string>
  <key>CFBundleExecutable</key>
  <string>$EXE_NAME</string>
  <key>CFBundleIconFile</key>
  <string>AppIcon</string>
  <key>CFBundleIdentifier</key>
  <string>$BUNDLE_ID</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>$APP_NAME</string>
  <key>CFBundleDisplayName</key>
  <string>$APP_NAME</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>$SHORT_VERSION</string>
  <key>CFBundleVersion</key>
  <string>$SHORT_VERSION</string>
  <key>CFBundleSignature</key>
  <string>????</string>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.utilities</string>
  <key>LSMinimumSystemVersion</key>
  <string>$MIN_MACOS</string>
  <key>LSMultipleInstancesProhibited</key>
  <true/>
  <key>LSUIElement</key>
  <true/>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>NSPrincipalClass</key>
  <string>NSApplication</string>
  <key>NSSupportsAutomaticTermination</key>
  <false/>
  <key>NSSupportsSuddenTermination</key>
  <false/>
</dict>
</plist>
PLIST

# ---- 3. checks ----------------------------------------------------------------------------------------------------------------
say "checking the bundle"
plutil -lint "$APP/Contents/Info.plist"
[ "$(plutil -extract CFBundleIdentifier raw "$APP/Contents/Info.plist")" = "$BUNDLE_ID" ] || die "bundle id differs"
[ "$(plutil -extract LSUIElement raw "$APP/Contents/Info.plist")" = "true" ] || die "LSUIElement is not true"
[ -f "$APP/Contents/Resources/AppIcon.icns" ] || die "no icon"
[ -f "$APP/Contents/PkgInfo" ] || die "no PkgInfo"

# exactly one program: one Mach-O executable (libraries and the managed dlls are not programs)
PROGRAMS=0
MAX_MINOS="0.0"
while IFS= read -r f; do
  kind="$(file -b "$f")"
  case "$kind" in
    *"Mach-O"*executable*) PROGRAMS=$((PROGRAMS + 1)); say "program: ${f#"$APP/"}" ;;
  esac
  case "$kind" in
    *"Mach-O"*)
      m="$(otool -l "$f" 2>/dev/null | awk '/LC_BUILD_VERSION/{b=1} b&&/minos/{print $2; b=0}' | sort -t. -k1,1n -k2,2n | tail -1)"
      if [ -n "$m" ]; then
        if [ "$(printf '%s\n%s\n' "$m" "$MAX_MINOS" | sort -t. -k1,1n -k2,2n | tail -1)" = "$m" ]; then MAX_MINOS="$m"; fi
      fi ;;
  esac
done < <(find "$APP/Contents/MacOS" -type f ! -name '*.dll' ! -name '*.json' ! -name '*.png')
[ "$PROGRAMS" -eq 1 ] || die "expected exactly one executable program in the bundle, found $PROGRAMS"
say "highest minimum macOS of the native files: $MAX_MINOS (declared: $MIN_MACOS)"
if [ "$(printf '%s\n%s\n' "$MAX_MINOS" "$MIN_MACOS" | sort -t. -k1,1n -k2,2n | tail -1)" != "$MIN_MACOS" ]; then
  die "a native file needs macOS $MAX_MINOS but the bundle declares $MIN_MACOS: raise MIN_MACOS in this script"
fi

# the Vault (the full app's code) must not be in a blind app
if find "$APP" -name 'BeeMemoryBank.Vault.dll' -o -name 'BeeMemoryBank.Hosting*.dll' -o -name 'BeeMemoryBank.Node*.dll' | grep -q .; then
  die "the bundle contains code of the full app or of a node"
fi

if [ "$SELF_CHECK" -eq 1 ]; then
  CHECK_DATA="$WORK/selfcheck-data"
  [ ! -e "$CHECK_DATA" ] || die "$CHECK_DATA already exists: choose another --output"
  say "self-check from inside the bundle (scratch data folder $CHECK_DATA)"
  "$APP/Contents/MacOS/$EXE_NAME" --self-check --data-dir "$CHECK_DATA"
fi

# ---- 4. optional: signing and notarization (OFF by default) -------------------------------------------------------------------
# Switch on with BMB_MACOS_SIGN=1. Read from the environment, never written down here:
#   BMB_MACOS_SIGN_IDENTITY   the "Developer ID Application: ..." identity (name or hash) in the keychain that codesign should use
#   BMB_MACOS_KEYCHAIN        optional: a keychain file to take the identity AND the notary profile from (a dedicated release keychain, not the login one)
# and for notarization (BMB_MACOS_NOTARIZE=1), either
#   BMB_MACOS_NOTARY_PROFILE  a profile stored earlier with "xcrun notarytool store-credentials", or
#   BMB_MACOS_TEAM_ID, BMB_MACOS_NOTARY_APPLE_ID, BMB_MACOS_NOTARY_PASSWORD (an app-specific password)
if [ "${BMB_MACOS_SIGN:-0}" = "1" ]; then
  : "${BMB_MACOS_SIGN_IDENTITY:?set BMB_MACOS_SIGN_IDENTITY to sign}"
  [ -f "$ENTITLEMENTS" ] || die "entitlements file missing: $ENTITLEMENTS"
  # codesign's entitlements parser is strict: a file with Windows line endings (what `git archive` makes on Windows with autocrlf) is
  # refused ("AMFIUnserializeXML: syntax error"), so it gets a normalised copy, exactly as scripts/pack-macos-full.sh does.
  ENT_FILE="$WORK/entitlements.plist"
  [ ! -e "$ENT_FILE" ] || die "$ENT_FILE already exists"
  plutil -convert xml1 -o "$ENT_FILE" "$ENTITLEMENTS" || die "cannot read $ENTITLEMENTS"
  KEYCHAIN_ARGS=()
  if [ -n "${BMB_MACOS_KEYCHAIN:-}" ]; then KEYCHAIN_ARGS=(--keychain "$BMB_MACOS_KEYCHAIN"); fi
  say "signing (hardened runtime) with the identity from BMB_MACOS_SIGN_IDENTITY"
  # inside-out: every Mach-O file first (libraries, then the executable), the bundle last
  while IFS= read -r f; do
    case "$(file -b "$f")" in
      *"Mach-O"*)
        if [ "$f" = "$APP/Contents/MacOS/$EXE_NAME" ]; then continue; fi
        codesign --force --timestamp --options runtime "${KEYCHAIN_ARGS[@]+"${KEYCHAIN_ARGS[@]}"}" --sign "$BMB_MACOS_SIGN_IDENTITY" "$f" ;;
    esac
  done < <(find "$APP/Contents/MacOS" -type f ! -name '*.dll' ! -name '*.json' ! -name '*.png')
  # codesign will not seal the .app until EVERY other file under Contents/MacOS (the managed .dll files, json, png ...) is signed too:
  # "code object is not signed at all in subcomponent ...dll". They get a plain generic signature (it lives in extended attributes, so the
  # app must be copied with ditto or put in a disk image, never with a tool that drops extended attributes). Same rule as the full app.
  while IFS= read -r f; do
    case "$(file -b "$f")" in
      *"Mach-O"*) ;;
      *) codesign --force --timestamp=none "${KEYCHAIN_ARGS[@]+"${KEYCHAIN_ARGS[@]}"}" --sign "$BMB_MACOS_SIGN_IDENTITY" "$f" ;;
    esac
  done < <(find "$APP/Contents/MacOS" -type f)
  codesign --force --timestamp --options runtime --entitlements "$ENT_FILE" "${KEYCHAIN_ARGS[@]+"${KEYCHAIN_ARGS[@]}"}" \
    --sign "$BMB_MACOS_SIGN_IDENTITY" "$APP/Contents/MacOS/$EXE_NAME"
  codesign --force --timestamp --options runtime --entitlements "$ENT_FILE" "${KEYCHAIN_ARGS[@]+"${KEYCHAIN_ARGS[@]}"}" \
    --sign "$BMB_MACOS_SIGN_IDENTITY" "$APP"
  codesign --verify --deep --strict --verbose=2 "$APP"

  if [ "${BMB_MACOS_NOTARIZE:-0}" = "1" ]; then
    ZIP="$OUTPUT/$APP_NAME-$VERSION-$RID.zip"
    ditto -c -k --keepParent "$APP" "$ZIP"
    if [ -n "${BMB_MACOS_NOTARY_PROFILE:-}" ]; then
      xcrun notarytool submit "$ZIP" --keychain-profile "$BMB_MACOS_NOTARY_PROFILE" ${BMB_MACOS_KEYCHAIN:+--keychain "$BMB_MACOS_KEYCHAIN"} --wait
    else
      : "${BMB_MACOS_TEAM_ID:?set BMB_MACOS_TEAM_ID (or BMB_MACOS_NOTARY_PROFILE) to notarize}"
      : "${BMB_MACOS_NOTARY_APPLE_ID:?set BMB_MACOS_NOTARY_APPLE_ID}"
      : "${BMB_MACOS_NOTARY_PASSWORD:?set BMB_MACOS_NOTARY_PASSWORD}"
      xcrun notarytool submit "$ZIP" --team-id "$BMB_MACOS_TEAM_ID" --apple-id "$BMB_MACOS_NOTARY_APPLE_ID" \
        --password "$BMB_MACOS_NOTARY_PASSWORD" --wait
    fi
    xcrun stapler staple "$APP"
    spctl --assess --type execute --verbose=2 "$APP"
  fi
else
  say "UNSIGNED and not notarized (BMB_MACOS_SIGN is not 1)"
fi

say "done: $APP"
