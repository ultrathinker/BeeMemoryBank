#!/usr/bin/env bash
# Validates the LAYOUT of a staged "Bee Memory Bank.app" for code signing, without any identity: it makes a COPY of the app (ditto),
# signs every file of the two signing lists AD HOC (`codesign --sign -`: no identity, no keychain, no network, no timestamp server),
# inside-out, then the copy of the app, and then verifies it. The staged app itself is only read, never signed, and nothing is uploaded.
#
#   scripts/validate-macos-full-adhoc.sh --app "/path/Bee Memory Bank.app" --manifest "/path/Bee Memory Bank.signing-manifest.txt" \
#       --other "/path/Bee Memory Bank.signing-manifest-other.txt" --out NEWDIR \
#       --mode plain|hardened|hardened-dlv|hardened-dlv-no-jit-children [--smoke]
#
# The two lists come from scripts/pack-macos-full.sh: the Mach-O files, and every other file under Contents/MacOS (codesign will not seal
# the .app until those carry a generic signature too; see the signing branch of the packer).
# Modes (each makes <out>/Bee Memory Bank.app; run the script again with another NEW --out for another mode):
#   plain               ad-hoc signatures, no hardened runtime: proves the bundle layout is signable and verifiable
#   hardened            `--options runtime` and scripts/macos-full.entitlements on every executable: the shape of the real signing
#                       (apart from the identity and the timestamp)
#                       NOTE: with ad-hoc signatures this mode is EXPECTED not to start: an ad-hoc signature has no Team ID, and the
#                       hardened runtime's library validation then refuses every ad-hoc signed library ("have different Team IDs");
#                       with a real identity all files share the Team ID of the identity
#   hardened-dlv        as hardened, plus com.apple.security.cs.disable-library-validation in a temporary copy of the entitlements
#                       (in <out>, never in the repository): a VALIDATION-ONLY mode that takes library validation out of the picture,
#                       to see whether the rest of the hardened configuration (the JIT entitlements) lets the whole node run
#   hardened-dlv-no-jit-children  a CONTROL for hardened-dlv: the app gets all the entitlements, the other programs (bmbd, Api, Web,
#                       bmb, dotnet) only disable-library-validation and NOT the two JIT entitlements: shows whether they need them
#   --smoke             afterwards run scripts/smoke-macos-full.sh against the signed copy (all cases)
#
# Output: <out>/adhoc-<mode>.log (every command's result per file, the codesign -dvv lines, the spctl text, the entitlements read back).
# Exit code 0 when every file and the app verify; 1 when codesign rejected something (the files are listed).
# This script deletes nothing and refuses an existing --out. Works with the bash 3.2 of macOS.

set -u
export LC_ALL=C

APP=""
MANIFEST=""
OTHER=""
OUT=""
MODE=""
SMOKE=0
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENTITLEMENTS="$SCRIPT_DIR/macos-full.entitlements"
EXE_REL="Contents/MacOS/BeeMemoryBank.Desktop"

usage() { echo "usage: scripts/validate-macos-full-adhoc.sh --app APP --manifest FILE --other FILE --out NEWDIR --mode plain|hardened|hardened-dlv|hardened-dlv-no-jit-children [--smoke]"; }
die() { echo "validate-macos-full-adhoc: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --app) APP="${2:?--app needs the staged .app}"; shift 2 ;;
    --manifest) MANIFEST="${2:?--manifest needs the manifest file}"; shift 2 ;;
    --other) OTHER="${2:?--other needs the second list}"; shift 2 ;;
    --out) OUT="${2:?--out needs a NEW folder}"; shift 2 ;;
    --mode) MODE="${2:?--mode needs a value}"; shift 2 ;;
    --smoke) SMOKE=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; die "unknown argument: $1" ;;
  esac
done

[ "$(uname -s)" = "Darwin" ] || die "this runs codesign and has to run on a Mac"
case "$MODE" in plain|hardened|hardened-dlv|hardened-dlv-no-jit-children) ;; *) usage >&2; die "--mode must be plain, hardened, hardened-dlv or hardened-dlv-no-jit-children" ;; esac
[ -d "$APP/Contents/MacOS" ] || die "$APP is not an app bundle"
[ -f "$MANIFEST" ] || die "manifest not found: $MANIFEST"
[ -f "$OTHER" ] || die "second list not found: $OTHER"
[ -f "$ENTITLEMENTS" ] || die "entitlements file missing: $ENTITLEMENTS"
[ -n "$OUT" ] || die "--out is required"
[ ! -e "$OUT" ] || die "$OUT already exists: give a NEW folder (this script deletes nothing)"
for tool in codesign ditto spctl plutil file awk sort; do command -v "$tool" > /dev/null 2>&1 || die "needed tool not found: $tool"; done
APP="$(cd "$APP" && pwd)"
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
COPY="$OUT/Bee Memory Bank.app"
LOG="$OUT/adhoc-$MODE.log"
: > "$LOG"
say() { local line; line="$(date '+%F %T') $*"; echo "$line"; echo "$line" >> "$LOG"; }

say "mode $MODE; staged app (read only): $APP"
say "copying with ditto -> $COPY"
ditto "$APP" "$COPY" || die "ditto failed"

# the entitlements of this mode. codesign reads them with a strict parser that refuses a file with Windows line endings (a `git archive`
# made on Windows with core.autocrlf=true has them: "Failed to parse entitlements: AMFIUnserializeXML: syntax error near line 1"), so
# a normalised copy (plutil -convert xml1: LF, no comments) in the output folder is what is used.
ENT_BASE="$OUT/entitlements-base.plist"
plutil -convert xml1 -o "$ENT_BASE" "$ENTITLEMENTS" || die "cannot read $ENTITLEMENTS"
ENT_ALL="$ENT_BASE"
ENT_PROGRAMS="$ENT_BASE"
case "$MODE" in
  hardened-dlv|hardened-dlv-no-jit-children)
    ENT_ALL="$OUT/entitlements-with-dlv.plist"
    awk '/<\/dict>/ { print "  <key>com.apple.security.cs.disable-library-validation</key>"; print "  <true/>" } { print }' "$ENT_BASE" > "$ENT_ALL"
    plutil -lint "$ENT_ALL" > /dev/null || die "the temporary entitlements are not valid"
    say "validation-only entitlements (disable-library-validation added): $ENT_ALL"
    ENT_PROGRAMS="$ENT_ALL"
    if [ "$MODE" = "hardened-dlv-no-jit-children" ]; then
      ENT_PROGRAMS="$OUT/entitlements-dlv-only.plist"
      printf '%s\n' '<?xml version="1.0" encoding="UTF-8"?>' \
        '<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">' \
        '<plist version="1.0"><dict><key>com.apple.security.cs.disable-library-validation</key><true/></dict></plist>' > "$ENT_PROGRAMS"
      plutil -lint "$ENT_PROGRAMS" > /dev/null || die "the temporary entitlements are not valid"
      say "the other programs get only: $ENT_PROGRAMS"
    fi ;;
esac
# the app (its main executable) always gets $ENT_ALL in the hardened modes; the other programs get $ENT_PROGRAMS (plain: none)
if [ "$MODE" = "plain" ]; then ENT_PROGRAMS=""; fi

RUNTIME_ARGS=()
if [ "$MODE" != "plain" ]; then RUNTIME_ARGS=(--options runtime); fi

REJECTED=0
SIGNED=0
sign_file() { # <relative path> [entitlements file] [extra codesign args...]
  local rel="$1" ent="${2:-}" out rc
  if [ -n "$ent" ]; then
    out="$(codesign --force --sign - ${RUNTIME_ARGS[@]+"${RUNTIME_ARGS[@]}"} --entitlements "$ent" "$COPY/$rel" 2>&1)"; rc=$?
  else
    out="$(codesign --force --sign - ${RUNTIME_ARGS[@]+"${RUNTIME_ARGS[@]}"} "$COPY/$rel" 2>&1)"; rc=$?
  fi
  if [ "$rc" -eq 0 ]; then SIGNED=$((SIGNED + 1)); else REJECTED=$((REJECTED + 1)); say "SIGN REJECTED ($rc): $rel: $out"; fi
}

# inside-out, as the signing branch of pack-macos-full.sh: the other files, then the Mach-O files deepest first (libraries, then the
# other programs), then the app (which signs the main executable: a bundle's main executable cannot be signed alone). Never --deep.
ORDERED="$OUT/signing-order.txt"
awk -F/ '{ print NF "\t" $0 }' "$MANIFEST" | LC_ALL=C sort -t"$(printf '\t')" -k1,1nr -k2 | cut -f2- > "$ORDERED"
say "signing $(wc -l < "$OTHER" | tr -d ' ') other files (generic signatures), then $(wc -l < "$ORDERED" | tr -d ' ') Mach-O entries ad hoc (order: $ORDERED)"
T0=$(date +%s)
OTHER_REJECTED=0
while IFS= read -r rel; do
  out="$(codesign --force --sign - "$COPY/$rel" 2>&1)"; rc=$?
  if [ "$rc" -eq 0 ]; then SIGNED=$((SIGNED + 1)); else OTHER_REJECTED=$((OTHER_REJECTED + 1)); REJECTED=$((REJECTED + 1)); [ "$OTHER_REJECTED" -le 20 ] && say "SIGN REJECTED ($rc): $rel: $out"; fi
done < "$OTHER"
say "other files done in $(( $(date +%s) - T0 )) s, rejected $OTHER_REJECTED"
while IFS= read -r rel; do
  [ "$rel" = "$EXE_REL" ] && continue
  case "$(file -b "$COPY/$rel" | sed -n 1p)" in
    *"Mach-O"*executable*) ;;
    *) sign_file "$rel" ;;
  esac
done < "$ORDERED"
while IFS= read -r rel; do
  [ "$rel" = "$EXE_REL" ] && continue
  case "$(file -b "$COPY/$rel" | sed -n 1p)" in
    *"Mach-O"*executable*) sign_file "$rel" "$ENT_PROGRAMS" ;;
  esac
done < "$ORDERED"
# the bundle (and with it the main executable)
if [ "$MODE" = "plain" ]; then
  APP_OUT="$(codesign --force --sign - "$COPY" 2>&1)"; APP_RC=$?
else
  APP_OUT="$(codesign --force --sign - --options runtime --entitlements "$ENT_ALL" "$COPY" 2>&1)"; APP_RC=$?
fi
if [ "$APP_RC" -eq 0 ]; then say "bundle signed: $APP_OUT"; else REJECTED=$((REJECTED + 1)); say "SIGN REJECTED ($APP_RC): the app bundle: $APP_OUT"; fi
say "signed $SIGNED files, rejected $REJECTED"

# ---- verify: every file on its own, then the app ---------------------------------------------------------------------------------
BAD_VERIFY=0
T0=$(date +%s)
while IFS= read -r rel; do
  [ "$rel" = "$EXE_REL" ] && continue
  out="$(codesign --verify --strict --verbose=2 "$COPY/$rel" 2>&1)"; rc=$?
  if [ "$rc" -ne 0 ]; then BAD_VERIFY=$((BAD_VERIFY + 1)); [ "$BAD_VERIFY" -le 20 ] && say "VERIFY FAILED ($rc): $rel: $out"; fi
done < <(cat "$MANIFEST" "$OTHER")
say "per-file verify (codesign --verify --strict, the main executable is verified with the app): $BAD_VERIFY failed, in $(( $(date +%s) - T0 )) s"

APP_VERIFY="$(codesign --verify --deep --strict --verbose=2 "$COPY" 2>&1)"; APP_VERIFY_RC=$?
say "codesign --verify --deep --strict --verbose=2 <app>: exit $APP_VERIFY_RC"
echo "$APP_VERIFY" | while IFS= read -r l; do say "    $l"; done

say "--- codesign -dvv <app>"
codesign -dvv "$COPY" 2>&1 | while IFS= read -r l; do say "    $l"; done
for rel in Contents/MacOS/bmbd/BeeMemoryBank.Node Contents/MacOS/api/BeeMemoryBank.Api Contents/MacOS/dotnet/dotnet Contents/MacOS/api/libonnxruntime.dylib \
           Contents/MacOS/BeeMemoryBank.Desktop.dll; do
  say "--- codesign -dvv $rel (flags and signature kind)"
  codesign -dvv "$COPY/$rel" 2>&1 | grep -E "^(Identifier|Format|CodeDirectory|Signature|TeamIdentifier|Authority)" | while IFS= read -r l; do say "    $l"; done
done
for rel in "$EXE_REL" Contents/MacOS/bmbd/BeeMemoryBank.Node Contents/MacOS/api/BeeMemoryBank.Api Contents/MacOS/web/BeeMemoryBank.Web Contents/MacOS/cli/bmb Contents/MacOS/dotnet/dotnet; do
  say "--- entitlements read back from $rel"
  codesign -d --entitlements - --xml "$COPY/$rel" 2>&1 | tr '\n' ' ' | cut -c1-500 | while IFS= read -r l; do say "    $l"; done
done

say "--- spctl --assess --type execute --verbose=4 <app>  (an ad-hoc app is expected to be rejected)"
SPCTL="$(spctl --assess --type execute --verbose=4 "$COPY" 2>&1)"; SPCTL_RC=$?
say "spctl exit $SPCTL_RC"
echo "$SPCTL" | while IFS= read -r l; do say "    $l"; done

RC=0
if [ "$REJECTED" -ne 0 ] || [ "$BAD_VERIFY" -ne 0 ] || [ "$APP_VERIFY_RC" -ne 0 ]; then RC=1; fi
say "VALIDATION $MODE: signed=$SIGNED sign_rejected=$REJECTED verify_failed=$BAD_VERIFY app_verify_exit=$APP_VERIFY_RC spctl_exit=$SPCTL_RC"

if [ "$SMOKE" -eq 1 ]; then
  say "--- smoke gate on the signed copy"
  bash "$SCRIPT_DIR/smoke-macos-full.sh" --app "$COPY" --work "$OUT/smoke-work" --log "$OUT/smoke-$MODE.log" > "$OUT/smoke-$MODE.stdout" 2>&1
  SMOKE_RC=$?
  say "smoke gate exit $SMOKE_RC (log $OUT/smoke-$MODE.log)"
  grep -E "RESULT|SMOKE" "$OUT/smoke-$MODE.log" | while IFS= read -r l; do say "    $l"; done
  [ "$SMOKE_RC" -eq 0 ] || RC=1
fi
say "left in place (nothing is deleted): $OUT"
exit $RC
