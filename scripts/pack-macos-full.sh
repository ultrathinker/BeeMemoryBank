#!/usr/bin/env bash
# Builds "Bee Memory Bank.app", the full macOS app (desktop shell + bmbd + Api + Web + bmb CLI, with the Vault), from the repository.
#
#   scripts/pack-macos-full.sh [--rid osx-arm64] [--output DIR] [--publish-dir DIR] [--menu-bar-only] [--skip-checks] [--bundle-model FILE] [--wwwroot-in-macos]
#
# What it does (on a Mac; it needs the .NET SDK, plutil, rsync, file, otool, shasum):
#   1. dotnet publish of the five products for osx-arm64, framework-dependent, with ONE shared .NET runtime (the macOS twin of
#      scripts/publish-node.ps1 and scripts/pack-windows.ps1) into <output>/work/publish:
#        desktop/  desktop/BeeMemoryBank.Desktop                 -p:BmbSharedRuntime=root       (its apphost finds ./dotnet)
#        bmbd/     desktop/BeeMemoryBank.Node                    -p:BmbSharedRuntime=subfolder  (apphost finds ../dotnet)
#        api/      server/BeeMemoryBank.Api                      -p:BmbSharedRuntime=subfolder
#        web/      server/BeeMemoryBank.Web                      -p:BmbSharedRuntime=subfolder
#        cli/      server/BeeMemoryBank.Cli  (bmb)               -p:BmbSharedRuntime=subfolder -p:BmbBundleModel=false
#        dotnet/   a copy of the highest 10.x runtime of the SDK that runs this script (hostfxr + NETCore.App + AspNetCore.App + muxer)
#      or it takes a ready folder with exactly these six sub-folders (--publish-dir).
#   2. Assembles <output>/Bee Memory Bank.app, the same tree as the Windows payload, inside Contents/MacOS:
#        Contents/MacOS/BeeMemoryBank.Desktop, *.dll, the Avalonia/Skia/HarfBuzz dylibs  (CFBundleExecutable, the Dock app)
#        Contents/MacOS/bmbd/ api/ web/ cli/ dotnet/   (in dotnet/ the dotted names the host needs, host/fxr/10.0.8 and
#                                                      shared/Microsoft.NETCore.App[/10.0.8], are symbolic links to directories
#                                                      without dots: codesign takes a real directory with a dot for a bundle)
#        Contents/Resources/BeeMemoryBank.icns   desktop/BeeMemoryBank.Desktop/Packaging/BeeMemoryBank.icns, copied as it is (16 px to 512@2x)
#        Contents/Info.plist                     bundle id com.beememorybank.desktop (NOT confirmed by the owner yet), version from VERSION
#        Contents/PkgInfo                        APPL????
#      web/wwwroot (6,390 static files, 6,154 of them icons) is a symbolic link to Contents/Resources/web-wwwroot: codesign wants a
#      signature on every file under Contents/MacOS but seals Resources as plain data (--wwwroot-in-macos keeps the real files in
#      Contents/MacOS/web/wwwroot, and then every one of them needs its own signature).
#      No .pdb files. No model.onnx unless --bundle-model FILE is given (see below). LSUIElement is NOT set (a Dock app);
#      --menu-bar-only sets it to true (a menu-bar-only app, no Dock icon).
#   3. Writes the SIGNING MANIFEST next to the app: <output>/Bee Memory Bank.signing-manifest.txt, every file that `file` calls
#      Mach-O, as a path relative to the .app, sorted. A second, independent scan (the first four bytes of EVERY file) must find
#      no Mach-O that is not in it, or the script stops. A second list, "Bee Memory Bank.signing-manifest-other.txt", names every other
#      file under Contents/MacOS: codesign treats ALL of them (the managed .dll files, the json, even the static web files) as code
#      objects that must carry a signature before it will seal the .app (see the signing branch at the end). LSMinimumSystemVersion is
#      the highest "minos" of the arm64 slices in the manifest, never below 13.0. A short staging report is written next to them.
#   4. Checks the bundle (unless --skip-checks): plutil -lint, the keys of Info.plist, every component and its apphost, the shared
#      runtime, no .pdb, BeeMemoryBank.Vault.dll in bmbd/ and api/ (the full app MUST carry the Vault), no data inside the bundle.
#
# The model file (model.onnx, 118 MB, not in git): the Windows release bundles it in api/ when the file is in
# libs/BeeMemoryBank.Embeddings/Models/. Here it is NOT bundled by default, even if the file is there (it is left out of api/ and
# cli/ on purpose, to keep the app small). The code finds it in this order: BMB_ONNX_MODEL_PATH, <data folder>/model.onnx, next to
# the Api (api/model.onnx). Nothing in the code downloads it. Without a model, semantic search is unavailable (the rest works).
# --bundle-model FILE copies FILE to api/model.onnx after checking its SHA-256 against the one the code expects.
#
# The result is UNSIGNED and NOT notarized. Signing and notarization are an optional branch that is OFF unless BMB_MACOS_SIGN=1 (see
# the optional branch at the end of this file; it has never been run, and it never signs the staged app: it makes a ditto copy in
# <output>/signed/ and signs that). Nothing in this script contains a signing identity, a team id
# or a password: they are read from the environment when the branch is switched on. For a trial without any identity use
# scripts/validate-macos-full-adhoc.sh, which signs a COPY ad hoc.
#
# This script never deletes anything. It refuses to write into an existing "Bee Memory Bank.app" (choose another --output or remove the
# old one yourself); its working files (the publish output, the iconset, the file lists) stay in <output>/work, and the bundle does
# not depend on them. The script works with the bash 3.2 that macOS ships.

set -euo pipefail

APP_NAME="Bee Memory Bank"
BUNDLE_ID="com.beememorybank.desktop"
EXE_NAME="BeeMemoryBank.Desktop"
ICON_NAME="BeeMemoryBank"
BONJOUR_SERVICE="_beememorybank._tcp"
# The lowest macOS the bundle may declare; the real value is the highest "minos" of its native files, but never below this.
FLOOR_MACOS="13.0"
# SHA-256 of the embedding model the code expects (libs/BeeMemoryBank.Embeddings/EmbeddingModelWiring.cs, BundledModelSha256).
MODEL_SHA256="f80102d3f2a1229f387d3c81909990d8945513e347b0eab049f7de3c6f98c193"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
ICON_ICNS="$ROOT/desktop/BeeMemoryBank.Desktop/Packaging/BeeMemoryBank.icns"
ENTITLEMENTS="$SCRIPT_DIR/macos-full.entitlements"

RID="osx-arm64"
OUTPUT=""
PUBLISH_DIR=""
MENU_BAR_ONLY=0
WWWROOT_LINK=1
CHECKS=1
MODEL_FILE=""

usage() { echo "usage: scripts/pack-macos-full.sh [--rid osx-arm64] [--output DIR] [--publish-dir DIR] [--menu-bar-only] [--skip-checks] [--bundle-model FILE] [--wwwroot-in-macos]"; }
die() { echo "pack-macos-full: $*" >&2; exit 1; }
say() { echo "pack-macos-full: $*"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --rid) RID="${2:?--rid needs a value}"; shift 2 ;;
    --output) OUTPUT="${2:?--output needs a folder}"; shift 2 ;;
    --publish-dir) PUBLISH_DIR="${2:?--publish-dir needs a folder}"; shift 2 ;;
    --menu-bar-only) MENU_BAR_ONLY=1; shift ;;
    --wwwroot-in-macos) WWWROOT_LINK=0; shift ;;
    --skip-checks) CHECKS=0; shift ;;
    --bundle-model) MODEL_FILE="${2:?--bundle-model needs a file}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; die "unknown argument: $1" ;;
  esac
done

[ "$(uname -s)" = "Darwin" ] || die "this script builds a macOS app bundle and has to run on a Mac"
[ "$RID" = "osx-arm64" ] || die "only --rid osx-arm64 is supported (x64 needs its own publish, the native libraries are RID-specific; got $RID)"
for tool in dotnet plutil rsync file otool shasum awk sort find od; do
  command -v "$tool" > /dev/null 2>&1 || die "needed tool not found: $tool"
done

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
if [ -n "$MODEL_FILE" ]; then
  [ -f "$MODEL_FILE" ] || die "--bundle-model: $MODEL_FILE is not a file"
  [ "$(shasum -a 256 "$MODEL_FILE" | awk '{print $1}')" = "$MODEL_SHA256" ] || die "--bundle-model: $MODEL_FILE is not the model the code expects (SHA-256 differs)"
fi

[ -n "$OUTPUT" ] || OUTPUT="$ROOT/publish/macos-full/$RID"
mkdir -p "$OUTPUT"
OUTPUT="$(cd "$OUTPUT" && pwd)"
WORK="$OUTPUT/work"
APP="$OUTPUT/$APP_NAME.app"
MANIFEST="$OUTPUT/$APP_NAME.signing-manifest.txt"
OTHER="$OUTPUT/$APP_NAME.signing-manifest-other.txt"
REPORT="$OUTPUT/$APP_NAME.staging-report.txt"
MACOS="$APP/Contents/MacOS"
[ ! -e "$APP" ] || die "$APP already exists: choose another --output, or remove it yourself (this script does not delete)"
[ ! -e "$MANIFEST" ] || die "$MANIFEST already exists: choose another --output"
[ ! -e "$OTHER" ] || die "$OTHER already exists: choose another --output"
[ ! -e "$REPORT" ] || die "$REPORT already exists: choose another --output"
mkdir -p "$WORK"

resolve_link() {
  local p="$1" t
  while [ -L "$p" ]; do
    t="$(readlink "$p")"
    case "$t" in /*) p="$t" ;; *) p="$(dirname "$p")/$t" ;; esac
  done
  printf '%s' "$p"
}

# What `file -b` says about a file, ONE line: for a fat (universal) Mach-O file it prints the summary line and then one more line
# per architecture, and only the first line is the summary.
kind_of() { file -b "$1" | sed -n 1p; }

# The larger of two "major.minor" versions.
ver_max() { printf '%s\n%s\n' "$1" "$2" | sort -t. -k1,1n -k2,2n | tail -1; }

# ---- 1. publish ---------------------------------------------------------------------------------------------------------------
publish_one() { # <project dir> <sub-folder> <root|subfolder> [more msbuild properties]
  local rel="$1" sub="$2" mode="$3"
  local proj="$ROOT/$rel"
  shift 3
  say "publishing $rel -> $sub ($RID, framework-dependent, BmbSharedRuntime=$mode)"
  dotnet publish "$proj" -c Release -r "$RID" --self-contained false -p:BmbSharedRuntime="$mode" ${1+"$@"} \
    -p:UseSharedCompilation=false -nodeReuse:false -o "$PUBLISH_DIR/$sub" -nologo -v q
}

# One shared .NET runtime for every component (the macOS twin of step 4b of scripts/publish-node.ps1). It is COPIED from the
# runtime folder of the SDK that runs this script; nothing there is changed.
bundle_runtime() { # <target folder>
  local out="$1" droot="" bin core fxr
  if [ -n "${DOTNET_ROOT:-}" ] && [ -d "$DOTNET_ROOT/shared/Microsoft.NETCore.App" ]; then
    droot="$DOTNET_ROOT"
  else
    bin="$(resolve_link "$(command -v dotnet)")"
    droot="$(cd "$(dirname "$bin")" && pwd -P)"
  fi
  [ -d "$droot/shared/Microsoft.NETCore.App" ] || die "no shared/Microsoft.NETCore.App under $droot (set DOTNET_ROOT to a dotnet install folder)"
  core="$(ls "$droot/shared/Microsoft.NETCore.App" | grep -E '^10\.[0-9]+\.[0-9]+$' | sort -t. -k1,1n -k2,2n -k3,3n | tail -1 || true)"
  [ -n "$core" ] || die "no Microsoft.NETCore.App 10.x release under $droot"
  [ -d "$droot/shared/Microsoft.AspNetCore.App/$core" ] || die "Microsoft.AspNetCore.App $core is missing under $droot (it must match NETCore.App)"
  fxr="$(ls "$droot/host/fxr" | grep -E '^10\.[0-9]+\.[0-9]+$' | sort -t. -k1,1n -k2,2n -k3,3n | tail -1 || true)"
  [ -n "$fxr" ] || die "no host/fxr 10.x under $droot"
  case "$(kind_of "$droot/dotnet")" in *arm64*) ;; *) die "$droot/dotnet is not an arm64 program: $(kind_of "$droot/dotnet")" ;; esac
  say "bundling the shared .NET runtime $core (hostfxr $fxr) from $droot -> $out"
  [ ! -e "$out" ] || die "$out already exists"
  mkdir -p "$out/host/fxr" "$out/shared/Microsoft.NETCore.App" "$out/shared/Microsoft.AspNetCore.App"
  cp -R "$droot/host/fxr/$fxr" "$out/host/fxr/$fxr"
  cp -R "$droot/shared/Microsoft.NETCore.App/$core" "$out/shared/Microsoft.NETCore.App/$core"
  cp -R "$droot/shared/Microsoft.AspNetCore.App/$core" "$out/shared/Microsoft.AspNetCore.App/$core"
  cp "$droot/dotnet" "$out/"
  for n in LICENSE.txt ThirdPartyNotices.txt; do
    if [ -f "$droot/$n" ]; then cp "$droot/$n" "$out/"; fi
  done
}

if [ -z "$PUBLISH_DIR" ]; then
  PUBLISH_DIR="$WORK/publish"
  [ ! -e "$PUBLISH_DIR" ] || die "$PUBLISH_DIR already exists: choose another --output (or pass it as --publish-dir)"
  mkdir -p "$PUBLISH_DIR"
  publish_one desktop/BeeMemoryBank.Desktop/BeeMemoryBank.Desktop.csproj desktop root
  publish_one desktop/BeeMemoryBank.Node/BeeMemoryBank.Node.csproj bmbd subfolder
  publish_one server/BeeMemoryBank.Api/BeeMemoryBank.Api.csproj api subfolder
  publish_one server/BeeMemoryBank.Web/BeeMemoryBank.Web.csproj web subfolder
  # The CLI uses the Api's model.onnx from the sibling api/ folder (CliServiceProvider): it never carries its own copy.
  publish_one server/BeeMemoryBank.Cli/BeeMemoryBank.Cli.csproj cli subfolder -p:BmbBundleModel=false
  bundle_runtime "$PUBLISH_DIR/dotnet"
else
  PUBLISH_DIR="$(cd "$PUBLISH_DIR" && pwd)"
  say "using the ready publish folder $PUBLISH_DIR"
fi
for c in desktop bmbd api web cli dotnet; do
  [ -d "$PUBLISH_DIR/$c" ] || die "the publish folder has no '$c' sub-folder: $PUBLISH_DIR"
done
for rel in desktop/$EXE_NAME bmbd/BeeMemoryBank.Node api/BeeMemoryBank.Api web/BeeMemoryBank.Web cli/bmb dotnet/dotnet; do
  [ -x "$PUBLISH_DIR/$rel" ] || die "no executable $rel in $PUBLISH_DIR"
done

# ---- 2. the bundle ------------------------------------------------------------------------------------------------------------
# The shared runtime goes in with a layout that codesign accepts. codesign treats EVERY directory under Contents/MacOS whose name has a
# dot as a nested bundle and then refuses to seal the .app ("bundle format unrecognized, invalid, or unsuitable"; a tiny test bundle
# shows it for "a.b", "x.App", "10.0.8" and ".hidden", not for "8"), and the .NET host insists on the dotted names host/fxr/<version>,
# shared/<Framework>.App and shared/<Framework>.App/<version>. So the real files live in directories without dots and the dotted names
# are relative symbolic links to them (a symbolic link is sealed as a link):
#   dotnet/host/fxr/v10-0-8/              real files       dotnet/host/fxr/10.0.8                -> v10-0-8
#   dotnet/shared/fx-netcore/v10-0-8/     real files       dotnet/shared/fx-netcore/10.0.8       -> v10-0-8
#                                                          dotnet/shared/Microsoft.NETCore.App   -> fx-netcore
#   dotnet/shared/fx-aspnetcore/...       the same for Microsoft.AspNetCore.App
# The apphosts, hostfxr and CoreCLR reach the runtime through the links (the node gate runs on exactly this layout).
stage_runtime() {
  local src="$PUBLISH_DIR/dotnet" dst="$MACOS/dotnet" v fw short safe
  mkdir -p "$dst/host/fxr" "$dst/shared"
  rsync -a --exclude='*.pdb' --exclude='/host/' --exclude='/shared/' "$src/" "$dst/"
  for v in $(ls "$src/host/fxr"); do
    safe="v$(printf '%s' "$v" | tr . -)"
    rsync -a --exclude='*.pdb' "$src/host/fxr/$v/" "$dst/host/fxr/$safe/"
    ln -s "$safe" "$dst/host/fxr/$v"
  done
  for fw in $(ls "$src/shared"); do
    case "$fw" in
      Microsoft.NETCore.App) short="netcore" ;;
      Microsoft.AspNetCore.App) short="aspnetcore" ;;
      *) die "unexpected shared framework in the runtime copy: $fw" ;;
    esac
    mkdir -p "$dst/shared/fx-$short"
    for v in $(ls "$src/shared/$fw"); do
      safe="v$(printf '%s' "$v" | tr . -)"
      rsync -a --exclude='*.pdb' "$src/shared/$fw/$v/" "$dst/shared/fx-$short/$safe/"
      ln -s "$safe" "$dst/shared/fx-$short/$v"
    done
    ln -s "fx-$short" "$dst/shared/$fw"
  done
}

say "assembling $APP"
mkdir -p "$MACOS" "$APP/Contents/Resources"
# No debug symbols. model.onnx is left out of api/ and cli/ whatever the repository holds (it is added back only by --bundle-model).
rsync -a --exclude='*.pdb' "$PUBLISH_DIR/desktop/" "$MACOS/"
mkdir -p "$MACOS/bmbd" "$MACOS/web"
rsync -a --exclude='*.pdb' "$PUBLISH_DIR/bmbd/" "$MACOS/bmbd/"
if [ "$WWWROOT_LINK" -eq 1 ]; then
  rsync -a --exclude='*.pdb' --exclude='/wwwroot/' "$PUBLISH_DIR/web/" "$MACOS/web/"
  rsync -a "$PUBLISH_DIR/web/wwwroot/" "$APP/Contents/Resources/web-wwwroot/"
  ln -s ../../Resources/web-wwwroot "$MACOS/web/wwwroot"
else
  rsync -a --exclude='*.pdb' "$PUBLISH_DIR/web/" "$MACOS/web/"
fi
stage_runtime
for c in api cli; do
  mkdir -p "$MACOS/$c"
  rsync -a --exclude='*.pdb' --exclude='/model.onnx' "$PUBLISH_DIR/$c/" "$MACOS/$c/"
done
if [ -n "$MODEL_FILE" ]; then
  say "bundling the model: $MODEL_FILE -> api/model.onnx"
  cp "$MODEL_FILE" "$MACOS/api/model.onnx"
fi
chmod +x "$MACOS/$EXE_NAME" "$MACOS/bmbd/BeeMemoryBank.Node" "$MACOS/api/BeeMemoryBank.Api" "$MACOS/web/BeeMemoryBank.Web" "$MACOS/cli/bmb" "$MACOS/dotnet/dotnet"

printf 'APPL????' > "$APP/Contents/PkgInfo"

# The icon: the project's own .icns (drawn for every size from 16 px to 512@2x), copied as it is.
cp "$ICON_ICNS" "$APP/Contents/Resources/$ICON_NAME.icns"

# ---- 3. what is in the bundle: the file kinds, the signing manifest, the minimum macOS --------------------------------------
say "scanning the bundle (file -b on every file)"
BAD_NAMES="$(find "$APP" \( -name $'*\n*' -o -name $'*\t*' \) | wc -l | tr -d ' ')"
[ "$BAD_NAMES" = "0" ] || die "a file name in the bundle holds a tab or a newline: the manifest could not list it"
KINDS="$WORK/file-kinds.tsv"
[ ! -e "$KINDS" ] || die "$KINDS already exists: choose another --output"
find "$APP" -type f -print0 | LC_ALL=C sort -z | while IFS= read -r -d '' f; do
  printf '%s\t%s\n' "$(kind_of "$f")" "${f#"$APP/"}"
done > "$KINDS"
FILE_COUNT="$(wc -l < "$KINDS" | tr -d ' ')"
[ "$FILE_COUNT" = "$(find "$APP" -type f | wc -l | tr -d ' ')" ] || die "the file list does not match the bundle"

awk -F'\t' '$1 ~ /Mach-O/ { print $2 }' "$KINDS" | LC_ALL=C sort > "$MANIFEST"
MANIFEST_COUNT="$(wc -l < "$MANIFEST" | tr -d ' ')"
[ "$MANIFEST_COUNT" -gt 0 ] || die "no Mach-O file found in the bundle"
# Every other file under Contents/MacOS. codesign refuses to seal the .app until each of them is signed too (generic signatures, kept in
# extended attributes): "code object is not signed at all - In subcomponent: .../Contents/MacOS/...dll". Info.plist, PkgInfo and
# Resources/ are not code and are sealed as resources.
awk -F'\t' '$1 !~ /Mach-O/ && index($2, "Contents/MacOS/") == 1 { print $2 }' "$KINDS" | LC_ALL=C sort > "$OTHER"
OTHER_COUNT="$(wc -l < "$OTHER" | tr -d ' ')"

# The manifest must hold every Mach-O of the bundle. `file -b` made it; this is a second, independent test of the first four bytes
# of every file that is NOT in it (thin and fat Mach-O magics, both byte orders). It is run again before signing.
assert_manifest_complete() { # [app folder to scan; default: the staged app]
  local root="${1:-$APP}" rel magic missing=0
  while IFS= read -r rel; do
    magic="$(od -An -tx1 -N4 "$root/$rel" 2> /dev/null | tr -d ' \n' || true)"
    case "$magic" in
      cffaedfe|cefaedfe|feedfacf|feedface|cafebabe|cafebabf|bebafeca|bfbafeca)
        echo "pack-macos-full: a Mach-O file is not in the signing manifest: $rel ($magic)" >&2; missing=$((missing + 1)) ;;
    esac
  done < <(find "$root" -type f | awk -v p="$root/" 'NR == FNR { m[$0] = 1; next } index($0, p) == 1 { r = substr($0, length(p) + 1); if (!(r in m)) print r }' "$MANIFEST" -)
  [ "$missing" -eq 0 ] || die "$missing Mach-O file(s) in the bundle are not in the signing manifest"
}
say "cross-checking the manifest ($MANIFEST_COUNT Mach-O files) against the first bytes of all $FILE_COUNT files"
assert_manifest_complete

# Every Mach-O must carry an arm64 slice; the highest minimum macOS of those slices decides LSMinimumSystemVersion.
MAX_MINOS="0.0"
NOT_ARM64=0
while IFS= read -r rel; do
  f="$APP/$rel"
  case "$(kind_of "$f")" in *arm64*) ;; *) echo "pack-macos-full: no arm64 slice: $rel" >&2; NOT_ARM64=$((NOT_ARM64 + 1)); continue ;; esac
  m="$(otool -arch arm64 -l "$f" 2>/dev/null | awk '
      /^ *cmd LC_BUILD_VERSION/ { b = 1; next }
      /^ *cmd LC_VERSION_MIN_MACOSX/ { v = 1; next }
      b && /^ *minos / { print $2; exit }
      v && /^ *version / { print $2; exit }' || true)"
  if [ -n "$m" ]; then
    m="$(printf '%s' "$m" | cut -d. -f1-2)"
    case "$m" in *.*) ;; *) m="$m.0" ;; esac
    MAX_MINOS="$(ver_max "$m" "$MAX_MINOS")"
  fi
done < "$MANIFEST"
[ "$NOT_ARM64" -eq 0 ] || die "$NOT_ARM64 Mach-O file(s) without an arm64 slice are in the bundle"
MIN_MACOS="$(ver_max "$MAX_MINOS" "$FLOOR_MACOS")"
say "highest minimum macOS of the native files: $MAX_MINOS; declared LSMinimumSystemVersion: $MIN_MACOS (floor $FLOOR_MACOS)"

# ---- Info.plist ---------------------------------------------------------------------------------------------------------------
UI_ELEMENT=""
if [ "$MENU_BAR_ONLY" -eq 1 ]; then
  UI_ELEMENT="  <key>LSUIElement</key>
  <true/>"
fi
{
cat <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>en</string>
  <key>CFBundleExecutable</key>
  <string>$EXE_NAME</string>
  <key>CFBundleIconFile</key>
  <string>$ICON_NAME</string>
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
  <string>public.app-category.productivity</string>
  <key>LSMinimumSystemVersion</key>
  <string>$MIN_MACOS</string>
  <key>LSMultipleInstancesProhibited</key>
  <true/>
$UI_ELEMENT
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>NSPrincipalClass</key>
  <string>NSApplication</string>
  <key>NSSupportsAutomaticGraphicsSwitching</key>
  <true/>
  <key>NSSupportsAutomaticTermination</key>
  <false/>
  <key>NSSupportsSuddenTermination</key>
  <false/>
  <key>NSLocalNetworkUsageDescription</key>
  <string>Bee Memory Bank looks for other nodes on your local network, announces itself there when you switch on Devices on my network, and lets a phone or another computer join this Mac.</string>
  <key>NSBonjourServices</key>
  <array>
    <string>$BONJOUR_SERVICE</string>
  </array>
  <key>NSAppTransportSecurity</key>
  <dict>
    <key>NSAllowsLocalNetworking</key>
    <true/>
  </dict>
</dict>
</plist>
PLIST
} | sed '/^$/d' > "$APP/Contents/Info.plist"

# ---- 4. checks ----------------------------------------------------------------------------------------------------------------
PROBLEMS=0
bad() { echo "pack-macos-full: CHECK FAILED: $*" >&2; PROBLEMS=$((PROBLEMS + 1)); }
plist_get() { plutil -extract "$1" raw -o - "$APP/Contents/Info.plist" 2> /dev/null || true; }
expect_plist() { # <key> <value>
  local got
  got="$(plist_get "$1")"
  [ "$got" = "$2" ] || bad "Info.plist $1 is '$got', expected '$2'"
}
count_find() { find "$@" | wc -l | tr -d ' '; }

check_app() {
  local rel f kind n dll v1 v2 dirs

  # Info.plist
  plutil -lint "$APP/Contents/Info.plist" > /dev/null || bad "plutil -lint says Info.plist is not valid"
  expect_plist CFBundleIdentifier "$BUNDLE_ID"
  expect_plist CFBundleExecutable "$EXE_NAME"
  expect_plist CFBundleName "$APP_NAME"
  expect_plist CFBundleDisplayName "$APP_NAME"
  expect_plist CFBundlePackageType APPL
  expect_plist CFBundleIconFile "$ICON_NAME"
  expect_plist CFBundleShortVersionString "$SHORT_VERSION"
  expect_plist CFBundleVersion "$SHORT_VERSION"
  expect_plist LSApplicationCategoryType public.app-category.productivity
  expect_plist LSMinimumSystemVersion "$MIN_MACOS"
  expect_plist LSMultipleInstancesProhibited true
  expect_plist NSHighResolutionCapable true
  expect_plist NSBonjourServices.0 "$BONJOUR_SERVICE"
  expect_plist NSAppTransportSecurity.NSAllowsLocalNetworking true
  [ -n "$(plist_get NSLocalNetworkUsageDescription)" ] || bad "Info.plist has no NSLocalNetworkUsageDescription"
  if [ "$MENU_BAR_ONLY" -eq 1 ]; then
    expect_plist LSUIElement true
  else
    [ -z "$(plist_get LSUIElement)" ] || bad "Info.plist sets LSUIElement, but this is the Dock app (use --menu-bar-only for a menu-bar app)"
  fi
  [ "$(ver_max "$MIN_MACOS" "$FLOOR_MACOS")" = "$MIN_MACOS" ] || bad "LSMinimumSystemVersion $MIN_MACOS is below $FLOOR_MACOS"
  [ "$(ver_max "$MIN_MACOS" "$MAX_MINOS")" = "$MIN_MACOS" ] || bad "a native file needs macOS $MAX_MINOS but the bundle declares $MIN_MACOS"
  [ "$(cat "$APP/Contents/PkgInfo")" = "APPL????" ] || bad "PkgInfo is not APPL????"
  case "$(kind_of "$APP/Contents/Resources/$ICON_NAME.icns" 2> /dev/null)" in *icon*) ;; *) bad "Resources/$ICON_NAME.icns is missing or not an icon" ;; esac

  # the five hosts: each an executable arm64 program next to its managed assembly, framework-dependent
  for rel in "$EXE_NAME" bmbd/BeeMemoryBank.Node api/BeeMemoryBank.Api web/BeeMemoryBank.Web cli/bmb; do
    f="$MACOS/$rel"
    if [ ! -f "$f" ]; then bad "missing: Contents/MacOS/$rel"; continue; fi
    [ -x "$f" ] || bad "not executable: Contents/MacOS/$rel"
    kind="$(kind_of "$f")"
    case "$kind" in *"Mach-O"*executable*arm64*) ;; *) bad "Contents/MacOS/$rel is not an arm64 Mach-O program ($kind)" ;; esac
    dll="${f}.dll"
    [ -f "$dll" ] || bad "missing: ${dll#"$APP/"}"
    [ -f "${f}.runtimeconfig.json" ] || bad "missing: ${f#"$APP/"}.runtimeconfig.json"
    [ -f "${f}.deps.json" ] || bad "missing: ${f#"$APP/"}.deps.json"
    if grep -q includedFrameworks "${f}.runtimeconfig.json" 2> /dev/null; then bad "${rel} is self-contained, the runtime must be the shared one"; fi
  done
  # the apphosts of the sub-folders find the shared runtime one level up
  for rel in bmbd/BeeMemoryBank.Node api/BeeMemoryBank.Api web/BeeMemoryBank.Web cli/bmb; do
    if [ -f "$MACOS/$rel" ] && ! LC_ALL=C grep -a -q -F '../dotnet' "$MACOS/$rel"; then bad "the apphost $rel does not look for ../dotnet"; fi
  done
  [ -d "$MACOS/web/wwwroot" ] || bad "missing: Contents/MacOS/web/wwwroot"
  [ -f "$MACOS/web/wwwroot/css/site.css" ] || bad "missing: web/wwwroot/css/site.css (the web root is empty or the link is broken)"
  if [ "$WWWROOT_LINK" -eq 1 ]; then
    [ -L "$MACOS/web/wwwroot" ] || bad "web/wwwroot must be a symbolic link to Contents/Resources/web-wwwroot"
    [ "$(count_find "$APP/Contents/Resources/web-wwwroot" -type f)" -gt 0 ] || bad "Contents/Resources/web-wwwroot is empty"
  fi

  # native libraries of the products
  for rel in libAvaloniaNative.dylib libSkiaSharp.dylib libHarfBuzzSharp.dylib api/libe_sqlite3.dylib api/libonnxruntime.dylib; do
    [ -f "$MACOS/$rel" ] || bad "missing: Contents/MacOS/$rel"
  done

  # the shared runtime: present once, nothing carries its own
  [ -x "$MACOS/dotnet/dotnet" ] || bad "missing or not executable: Contents/MacOS/dotnet/dotnet"
  dirs="$(count_find "$MACOS/dotnet/host/fxr" -mindepth 1 -maxdepth 1 -type d)"
  [ "$dirs" = "1" ] || bad "expected one real hostfxr folder in dotnet/host/fxr, found $dirs"
  [ "$(count_find "$MACOS/dotnet/host/fxr" -mindepth 1 -maxdepth 1 -type l)" = "1" ] || bad "expected one symbolic link (the version name) in dotnet/host/fxr"
  [ -f "$MACOS/dotnet/host/fxr/$(ls "$MACOS/dotnet/host/fxr" | grep -v '^v' | head -1)/libhostfxr.dylib" ] || bad "missing: dotnet/host/fxr/<version>/libhostfxr.dylib"
  # codesign: no directory with a dot in its name may be a real directory under Contents/MacOS, the dotted names must be links
  n="$(count_find "$MACOS" -type d -name '*.*')"
  [ "$n" = "0" ] || bad "$n real director(ies) under Contents/MacOS have a dot in the name (codesign takes them for bundles): $(find "$MACOS" -type d -name '*.*' | head -3 | tr '\n' ' ')"
  for rel in dotnet/shared/Microsoft.NETCore.App dotnet/shared/Microsoft.AspNetCore.App; do
    [ -L "$MACOS/$rel" ] || bad "$rel must be a symbolic link"
  done
  v1="$(ls "$MACOS/dotnet/shared/Microsoft.NETCore.App" 2> /dev/null | head -1)"
  v2="$(ls "$MACOS/dotnet/shared/Microsoft.AspNetCore.App" 2> /dev/null | head -1)"
  [ -n "$v1" ] && [ "$v1" = "$v2" ] || bad "the shared runtimes differ or are missing (NETCore.App '$v1', AspNetCore.App '$v2')"
  for rel in "dotnet/shared/Microsoft.NETCore.App/$v1/libcoreclr.dylib" "dotnet/shared/Microsoft.NETCore.App/$v1/libhostpolicy.dylib" \
             "dotnet/shared/Microsoft.NETCore.App/$v1/System.Private.CoreLib.dll" "dotnet/shared/Microsoft.AspNetCore.App/$v1/Microsoft.AspNetCore.dll"; do
    [ -f "$MACOS/$rel" ] || bad "missing: Contents/MacOS/$rel"
  done
  n="$(count_find "$APP" -name libcoreclr.dylib)"
  [ "$n" = "1" ] || bad "expected exactly one libcoreclr.dylib (the shared runtime), found $n"

  # the Vault: the full app MUST carry it (the Vault's home), in the node and in the Api
  [ -f "$MACOS/bmbd/BeeMemoryBank.Vault.dll" ] || bad "bmbd/BeeMemoryBank.Vault.dll is missing: the full app must contain the Vault"
  [ -f "$MACOS/api/BeeMemoryBank.Vault.dll" ] || bad "api/BeeMemoryBank.Vault.dll is missing: the full app must contain the Vault"
  # and nothing of the blind apps
  n="$(count_find "$APP" -name 'BeeMemoryBank.BlindDesktop*')"
  [ "$n" = "0" ] || bad "the bundle holds $n file(s) of the blind desktop app"

  # no debug symbols
  n="$(count_find "$APP" -name '*.pdb')"
  [ "$n" = "0" ] || bad "the bundle holds $n .pdb file(s)"

  # the model: only when --bundle-model was given, and then exactly api/model.onnx with the expected hash
  n="$(count_find "$APP" -name 'model.onnx')"
  if [ -n "$MODEL_FILE" ]; then
    [ "$n" = "1" ] && [ -f "$MACOS/api/model.onnx" ] || bad "expected exactly api/model.onnx, found $n model.onnx file(s)"
    [ "$(shasum -a 256 "$MACOS/api/model.onnx" | awk '{print $1}')" = "$MODEL_SHA256" ] || bad "api/model.onnx is not the expected model"
  else
    [ "$n" = "0" ] || bad "the bundle holds a model.onnx, but --bundle-model was not given"
  fi

  # the data folder is outside the bundle: BmbPaths puts it under the user's Application Support, never beside the program
  grep -q 'BeeMemoryBankData' "$ROOT/libs/BeeMemoryBank.AppPaths/BmbPaths.cs" || bad "BmbPaths no longer names BeeMemoryBankData"
  grep -q 'SpecialFolder.LocalApplicationData' "$ROOT/libs/BeeMemoryBank.AppPaths/BmbPaths.cs" || bad "BmbPaths no longer starts from LocalApplicationData (~/Library/Application Support on a Mac)"
  n="$(count_find "$APP" \( -name BeeMemoryBankData -o -name '*.db' -o -name '*.db-wal' -o -name '*.vault.lease' -o -name .runtime.json -o -name node.status.json -o -name profiles.json -o -name desktop-settings.json \))"
  [ "$n" = "0" ] || bad "the bundle holds $n file(s) of a data folder"
  n="$(count_find "$MACOS" -maxdepth 2 -type d -name data)"
  [ "$n" = "0" ] || bad "the bundle holds a data folder"

  # the manifest
  [ "$(LC_ALL=C sort -u "$MANIFEST" | wc -l | tr -d ' ')" = "$MANIFEST_COUNT" ] || bad "the manifest holds duplicates"
  while IFS= read -r rel; do
    [ -f "$APP/$rel" ] || bad "the manifest names a file that is not there: $rel"
  done < "$MANIFEST"
  for rel in "Contents/MacOS/$EXE_NAME" Contents/MacOS/bmbd/BeeMemoryBank.Node Contents/MacOS/api/BeeMemoryBank.Api Contents/MacOS/web/BeeMemoryBank.Web \
             Contents/MacOS/cli/bmb Contents/MacOS/dotnet/dotnet; do
    grep -q -x -F -- "$rel" "$MANIFEST" || bad "the manifest lacks $rel"
  done
  assert_manifest_complete

  # the second list: every file under Contents/MacOS that is not a Mach-O file, and nothing else
  n="$(count_find "$MACOS" -type f)"
  [ "$n" = "$((MANIFEST_COUNT + OTHER_COUNT))" ] || bad "Contents/MacOS holds $n files, the two signing lists name $((MANIFEST_COUNT + OTHER_COUNT))"
  [ -z "$(LC_ALL=C comm -12 "$MANIFEST" "$OTHER")" ] || bad "a file is in both signing lists"
  while IFS= read -r rel; do
    [ -f "$APP/$rel" ] || bad "the second signing list names a file that is not there: $rel"
  done < "$OTHER"

  # the entitlements file (used by the signing branch and the ad-hoc validation)
  plutil -lint "$ENTITLEMENTS" > /dev/null || bad "plutil -lint says the entitlements file is not valid"
  # (plutil -extract cannot be used here: it reads the dots of a key as a path)
  for key in com.apple.security.cs.allow-jit com.apple.security.cs.allow-unsigned-executable-memory; do
    [ "$(plutil -p "$ENTITLEMENTS" | grep -c -F "\"$key\" => true")" = "1" ] || bad "entitlements: $key is not true"
  done
  [ "$(plutil -p "$ENTITLEMENTS" | grep -c '=>')" = "2" ] || bad "entitlements: expected exactly two keys"
}

if [ "$CHECKS" -eq 1 ]; then
  say "checking the bundle"
  check_app
  [ "$PROBLEMS" -eq 0 ] || die "$PROBLEMS check(s) failed (see above); the bundle is left in place for inspection: $APP"
  say "all checks passed"
else
  say "checks skipped (--skip-checks)"
fi

# ---- the staging report -------------------------------------------------------------------------------------------------------
{
  echo "Bee Memory Bank.app staging report"
  echo "version:                $VERSION (bundle $SHORT_VERSION)"
  echo "bundle id:              $BUNDLE_ID"
  echo "rid:                    $RID"
  echo "built:                  $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "shared .NET runtime:    $(ls "$MACOS/dotnet/shared/Microsoft.NETCore.App" | head -1)"
  echo "LSMinimumSystemVersion: $MIN_MACOS (highest minos of the native files: $MAX_MINOS, floor $FLOOR_MACOS)"
  echo "dock app:               $([ "$MENU_BAR_ONLY" -eq 1 ] && echo "no, menu-bar only (LSUIElement)" || echo "yes (no LSUIElement)")"
  echo "model.onnx bundled:     $([ -n "$MODEL_FILE" ] && echo "yes, api/model.onnx" || echo "no")"
  echo "web/wwwroot:            $([ "$WWWROOT_LINK" -eq 1 ] && echo "a link to Contents/Resources/web-wwwroot" || echo "real files in Contents/MacOS/web/wwwroot")"
  echo "files in the bundle:    $FILE_COUNT"
  echo "Mach-O files:           $MANIFEST_COUNT (the signing manifest: $(basename "$MANIFEST"))"
  echo "other files in MacOS:   $OTHER_COUNT (signed generically before the .app can be sealed: $(basename "$OTHER"))"
  echo "size:                   $(du -sh "$APP" | awk '{print $1}')"
  echo "checks:                 $([ "$CHECKS" -eq 1 ] && echo passed || echo skipped)"
  echo "signed:                 no (see the optional branch of pack-macos-full.sh)"
} > "$REPORT"

# ---- 5. optional: signing and notarization (OFF by default, never run) --------------------------------------------------------
# Switch on with BMB_MACOS_SIGN=1. Read from the environment, never written down here:
#   BMB_MACOS_SIGN_IDENTITY   the "Developer ID Application: ..." identity (name or hash) in the keychain that codesign should use
#   BMB_MACOS_KEYCHAIN        optional: a keychain file to take the identity AND the notary profile from (a dedicated release keychain, not the login one)
# and for notarization (BMB_MACOS_NOTARIZE=1), either
#   BMB_MACOS_NOTARY_PROFILE  a profile stored earlier with "xcrun notarytool store-credentials", or
#   BMB_MACOS_TEAM_ID, BMB_MACOS_NOTARY_APPLE_ID, BMB_MACOS_NOTARY_PASSWORD (an app-specific password)
# The staged app is NEVER signed in place. It is copied with ditto (which keeps extended attributes, links and permissions) to
# <output>/signed/Bee Memory Bank.app - a new folder, refused if it exists - and THAT copy is signed, so a failure midway leaves the
# unsigned staged app untouched and a half-signed copy that is simply not used. The zip for notarization, stapling and the final spctl
# assessment all use the signed copy.
# What codesign needs (found by trying it, scripts/validate-macos-full-adhoc.sh): it will not seal the .app until EVERY file under
# Contents/MacOS is signed - the Mach-O files (the manifest) and all the others (the second list: managed .dll, json). The others get
# a plain generic signature (it lives in extended attributes of the file, so the app must be copied with ditto or put in a disk image,
# never with a tool that drops extended attributes). The main executable cannot be signed alone: it is signed by the signature of the
# .app. Order: the other files, then the Mach-O files deepest folder first (libraries, then the other programs with the entitlements),
# then the .app (which signs the main executable with the entitlements). Never --deep: a file missing from the lists must be an error,
# not something a recursive option quietly signs.
# BMB_MACOS_OTHER_TIMESTAMP=1 asks Apple's timestamp server for a timestamp for each of the other files too (hundreds of requests);
# without it they are signed with --timestamp=none, which is enough for codesign to seal the .app. Whether the notary service wants
# more of them is UNVERIFIED.
if [ "${BMB_MACOS_SIGN:-0}" = "1" ]; then
  : "${BMB_MACOS_SIGN_IDENTITY:?set BMB_MACOS_SIGN_IDENTITY to sign}"
  [ -f "$ENTITLEMENTS" ] || die "entitlements file missing: $ENTITLEMENTS"
  SIGNED_DIR="$OUTPUT/signed"
  SIGNED_APP="$SIGNED_DIR/$APP_NAME.app"
  [ ! -e "$SIGNED_APP" ] || die "$SIGNED_APP already exists: choose another --output, or remove it yourself (this script does not delete)"
  KEYCHAIN_ARGS=()
  if [ -n "${BMB_MACOS_KEYCHAIN:-}" ]; then KEYCHAIN_ARGS=(--keychain "$BMB_MACOS_KEYCHAIN"); fi
  # codesign's entitlements parser is strict (a file with Windows line endings, as `git archive` makes on Windows with autocrlf, is
  # refused): it gets a normalised copy.
  ENT_FILE="$WORK/entitlements.plist"
  [ ! -e "$ENT_FILE" ] || die "$ENT_FILE already exists"
  plutil -convert xml1 -o "$ENT_FILE" "$ENTITLEMENTS" || die "cannot read $ENTITLEMENTS"
  OTHER_TS="--timestamp=none"
  if [ "${BMB_MACOS_OTHER_TIMESTAMP:-0}" = "1" ]; then OTHER_TS="--timestamp"; fi
  assert_manifest_complete "$APP"
  say "copying the staged app with ditto -> $SIGNED_APP (the staged app stays unsigned)"
  mkdir -p "$SIGNED_DIR"
  ditto "$APP" "$SIGNED_APP" || die "ditto failed"
  assert_manifest_complete "$SIGNED_APP"
  say "signing the copy (hardened runtime) with the identity from BMB_MACOS_SIGN_IDENTITY"
  ORDERED="$WORK/signing-order.txt"
  [ ! -e "$ORDERED" ] || die "$ORDERED already exists"
  awk -F/ '{ print NF "\t" $0 }' "$MANIFEST" | LC_ALL=C sort -t$'\t' -k1,1nr -k2 | cut -f2- > "$ORDERED"
  # 1. every other file under Contents/MacOS (generic signatures)
  while IFS= read -r rel; do
    codesign --force "$OTHER_TS" ${KEYCHAIN_ARGS[@]+"${KEYCHAIN_ARGS[@]}"} --sign "$BMB_MACOS_SIGN_IDENTITY" "$SIGNED_APP/$rel"
  done < "$OTHER"
  # 2. the Mach-O libraries, deepest first
  while IFS= read -r rel; do
    if [ "$rel" = "Contents/MacOS/$EXE_NAME" ]; then continue; fi
    case "$(kind_of "$SIGNED_APP/$rel")" in
      *"Mach-O"*executable*) ;;
      *) codesign --force --timestamp --options runtime ${KEYCHAIN_ARGS[@]+"${KEYCHAIN_ARGS[@]}"} --sign "$BMB_MACOS_SIGN_IDENTITY" "$SIGNED_APP/$rel" ;;
    esac
  done < "$ORDERED"
  # 3. the other programs (each hosts its own .NET runtime, so each gets the JIT entitlements)
  while IFS= read -r rel; do
    if [ "$rel" = "Contents/MacOS/$EXE_NAME" ]; then continue; fi
    case "$(kind_of "$SIGNED_APP/$rel")" in
      *"Mach-O"*executable*)
        codesign --force --timestamp --options runtime --entitlements "$ENT_FILE" ${KEYCHAIN_ARGS[@]+"${KEYCHAIN_ARGS[@]}"} \
          --sign "$BMB_MACOS_SIGN_IDENTITY" "$SIGNED_APP/$rel" ;;
    esac
  done < "$ORDERED"
  # 4. the bundle, which signs the main executable
  codesign --force --timestamp --options runtime --entitlements "$ENT_FILE" ${KEYCHAIN_ARGS[@]+"${KEYCHAIN_ARGS[@]}"} \
    --sign "$BMB_MACOS_SIGN_IDENTITY" "$SIGNED_APP"
  # every file verifies on its own (the main executable only together with the bundle), then the bundle
  while IFS= read -r rel; do
    if [ "$rel" = "Contents/MacOS/$EXE_NAME" ]; then continue; fi
    codesign --verify --strict "$SIGNED_APP/$rel"
  done < "$MANIFEST"
  while IFS= read -r rel; do codesign --verify --strict "$SIGNED_APP/$rel"; done < "$OTHER"
  codesign --verify --deep --strict --verbose=2 "$SIGNED_APP"
  echo "signed copy:            $SIGNED_APP (signed with the identity from BMB_MACOS_SIGN_IDENTITY; the staged app above is unsigned)" >> "$REPORT"

  if [ "${BMB_MACOS_NOTARIZE:-0}" = "1" ]; then
    ZIP="$OUTPUT/$APP_NAME-$VERSION-$RID.zip"
    [ ! -e "$ZIP" ] || die "$ZIP already exists"
    ditto -c -k --keepParent "$SIGNED_APP" "$ZIP"
    if [ -n "${BMB_MACOS_NOTARY_PROFILE:-}" ]; then
      xcrun notarytool submit "$ZIP" --keychain-profile "$BMB_MACOS_NOTARY_PROFILE" ${BMB_MACOS_KEYCHAIN:+--keychain "$BMB_MACOS_KEYCHAIN"} --wait
    else
      : "${BMB_MACOS_TEAM_ID:?set BMB_MACOS_TEAM_ID (or BMB_MACOS_NOTARY_PROFILE) to notarize}"
      : "${BMB_MACOS_NOTARY_APPLE_ID:?set BMB_MACOS_NOTARY_APPLE_ID}"
      : "${BMB_MACOS_NOTARY_PASSWORD:?set BMB_MACOS_NOTARY_PASSWORD}"
      xcrun notarytool submit "$ZIP" --team-id "$BMB_MACOS_TEAM_ID" --apple-id "$BMB_MACOS_NOTARY_APPLE_ID" \
        --password "$BMB_MACOS_NOTARY_PASSWORD" --wait
    fi
    xcrun stapler staple "$SIGNED_APP"
    spctl --assess --type execute --verbose=2 "$SIGNED_APP"
    # a zip cannot be stapled: the file to distribute is made again from the stapled copy (ditto keeps the extended attributes)
    DIST_ZIP="$OUTPUT/$APP_NAME-$VERSION-$RID-stapled.zip"
    [ ! -e "$DIST_ZIP" ] || die "$DIST_ZIP already exists"
    ditto -c -k --keepParent "$SIGNED_APP" "$DIST_ZIP"
  fi
else
  say "UNSIGNED and not notarized (BMB_MACOS_SIGN is not 1)"
fi

say "bundle size: $(du -sh "$APP" | awk '{print $1}'), $FILE_COUNT files, $MANIFEST_COUNT Mach-O files in the manifest, $OTHER_COUNT other files under Contents/MacOS"
say "manifest: $MANIFEST"
say "other files: $OTHER"
say "done: $APP"
if [ -n "${SIGNED_APP:-}" ]; then say "signed copy: $SIGNED_APP"; fi
