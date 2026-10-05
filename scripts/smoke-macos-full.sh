#!/usr/bin/env bash
# Headless node gate of the packaged full macOS app: starts the node from INSIDE the staged "Bee Memory Bank.app" (a path with a space
# in it is the normal case) with a temporary data folder, checks it, stops it four different ways, and proves from the process list that
# nothing is left running. No window, no Dock icon, no menu-bar item: the desktop shell (BeeMemoryBank.Desktop) is never started.
#
#   scripts/smoke-macos-full.sh --app "/path/to/Bee Memory Bank.app" [--work DIR] [--log FILE] [--cases LIST] [--wait SECONDS]
#
#   --app    the staged .app (built by scripts/pack-macos-full.sh); it is only READ and executed, never changed
#   --work   a NEW folder for the data folders and the process output of this run (default: a new folder under $TMPDIR). Nothing in
#            it is ever deleted by this script; the log names it at the end
#   --log    the log file (default: <work>/smoke.log); every line has a time stamp
#   --cases  comma-separated, in this order by default: api,stdin,sigterm,sigint,kill9,restart
#              api      the Api alone (apphost from api/), /health on a random loopback port, stopped by SIGTERM
#              stdin    bmbd --auto starts Api and Web and the front; stop = close bmbd's stdin (what the shell's Quit does)
#              sigterm  the same, stop = SIGTERM to bmbd (launchd, logout, `kill`)
#              sigint   the same, stop = SIGINT to bmbd (Ctrl+C in a terminal)
#              kill9    the same, stop = SIGKILL to bmbd: the children must follow on their own (their stdin lifeline closes)
#              restart  bmbd started again on the data folder of the kill9 case (needs kill9 in the same run), then stopped via stdin
#   --wait   how many seconds a process may take to disappear after the stop (default 15)
#
# For every case it is verified, by listing the processes whose command starts with <app>/Contents/MacOS/, that bmbd, Api and Web are
# gone, that nothing else of the app is left, and (for the three graceful stops) that bmbd exited with code 0 and removed .runtime.json
# and node.status.json. After a kill -9 bmbd cannot remove them; the script reports whether they are left (a stale file is handled by
# the shell: it checks the pid before it attaches).
#
# What this script signals: only the processes it started itself (bmbd, the standalone Api), and, when a case failed, the processes that
# are direct children of those, after checking that the pid is alive, belongs to the same user and its command starts with the staged
# app's path. It never signals anything else, never uses killall/pkill, and deletes nothing.
# The apps run with a clean environment (env -i): no DOTNET_ROOT, a minimal PATH, so each apphost must find the bundled runtime in
# Contents/MacOS/dotnet by itself; HOME is the real one; LOCALAPPDATA and TMPDIR point into the work folder, so the real data folder
# (~/Library/Application Support/BeeMemoryBankData) is never touched.
#
# Exit code 0: every selected case passed. 1: a case failed ("SMOKE FAILED"), a selected case could not run ("SMOKE INCOMPLETE": for
# example --cases restart alone, which needs kill9 before it), or the arguments were wrong. Works with the bash 3.2 of macOS.

set -u
export LC_ALL=C

APP=""
WORK=""
LOG=""
CASES="api,stdin,sigterm,sigint,kill9,restart"
WAIT=15

usage() { echo "usage: scripts/smoke-macos-full.sh --app \"/path/to/Bee Memory Bank.app\" [--work DIR] [--log FILE] [--cases api,stdin,sigterm,sigint,kill9,restart] [--wait SECONDS]"; }
die() { echo "smoke-macos-full: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --app) APP="${2:?--app needs the .app folder}"; shift 2 ;;
    --work) WORK="${2:?--work needs a folder}"; shift 2 ;;
    --log) LOG="${2:?--log needs a file}"; shift 2 ;;
    --cases) CASES="${2:?--cases needs a list}"; shift 2 ;;
    --wait) WAIT="${2:?--wait needs a number}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; die "unknown argument: $1" ;;
  esac
done

[ "$(uname -s)" = "Darwin" ] || die "this gate runs the macOS app and has to run on a Mac"
[ -n "$APP" ] || { usage >&2; die "--app is required"; }
[ -d "$APP/Contents/MacOS" ] || die "$APP is not an app bundle (no Contents/MacOS)"
APP="$(cd "$APP" && pwd)"
MACOS="$APP/Contents/MacOS"
case "$WAIT" in ''|*[!0-9]*) die "--wait must be a whole number of seconds" ;; esac
for exe in bmbd/BeeMemoryBank.Node api/BeeMemoryBank.Api web/BeeMemoryBank.Web; do
  [ -x "$MACOS/$exe" ] || die "not an executable file: $MACOS/$exe"
done
for tool in curl ps awk sed mkfifo date id perl; do command -v "$tool" > /dev/null 2>&1 || die "needed tool not found: $tool"; done

if [ -z "$WORK" ]; then
  WORK="$(mktemp -d "${TMPDIR:-/tmp}/bmb-smoke-full.XXXXXX")"
else
  [ ! -e "$WORK" ] || die "$WORK already exists: give a new folder (this script deletes nothing)"
  mkdir -p "$WORK"
fi
WORK="$(cd "$WORK" && pwd)"
[ -n "$LOG" ] || LOG="$WORK/smoke.log"
[ ! -e "$LOG" ] || die "$LOG already exists: give a new log file"
: > "$LOG"
mkdir -p "$WORK/tmp" "$WORK/localappdata"

say() { local line; line="$(date '+%F %T') $*"; echo "$line"; echo "$line" >> "$LOG"; }

# ---- process list: only processes whose command starts with <app>/Contents/MacOS/ and that are not zombies --------------------
# "pid ppid command" per line.
live_procs() {
  ps -axww -o pid=,ppid=,stat=,command= | awk -v p="$MACOS/" '
    { pid = $1; ppid = $2; st = $3; cmd = $0; sub(/^ *[^ ]+ +[^ ]+ +[^ ]+ +/, "", cmd)
      if (substr(st, 1, 1) != "Z" && index(cmd, p) == 1) print pid " " ppid " " cmd }'
}
pid_live() { live_procs | awk -v x="$1" '$1 == x { f = 1 } END { exit !f }'; }
children_of() { live_procs | awk -v x="$1" '$2 == x { print $1 }'; }

# ---- what this script may signal ---------------------------------------------------------------------------------------------
OWNED=""
# A pid is a non-empty string of digits, nothing else. OWNED is " 123  456 ", so an EMPTY pid would match the gap between two entries
# and `kill -SIG ""` could be reached: every function below refuses an empty or non-numeric pid first.
own() { case "$1" in ''|*[!0-9]*) return 1 ;; esac; OWNED="$OWNED $1 "; }
is_owned() { case "$1" in ''|*[!0-9]*) return 1 ;; esac; case "$OWNED" in *" $1 "*) return 0 ;; *) return 1 ;; esac; }
safe_kill() { # <signal name> <pid>
  local sig="$1" pid="$2" uid
  case "$pid" in ''|*[!0-9]*) say "NOT signalling '$pid': not a process id"; return 1 ;; esac
  if ! is_owned "$pid"; then say "NOT signalling $pid: this script did not start it"; return 1; fi
  if ! pid_live "$pid"; then say "NOT signalling $pid: no live process of the staged app has that pid"; return 1; fi
  uid="$(ps -o uid= -p "$pid" | tr -d ' ')"
  if [ "$uid" != "$(id -u)" ]; then say "NOT signalling $pid: it belongs to another user"; return 1; fi
  say "kill -$sig $pid ($(live_procs | awk -v x="$pid" '$1 == x { $1 = ""; $2 = ""; print }' | cut -c1-120))"
  T_STOP=$(now_ms)
  kill -"$sig" "$pid"
}
# After a failed case: SIGKILL what is left of this run's process tree (the pids it started, and their direct children).
cleanup_owned() {
  local p c
  for p in $OWNED; do
    for c in $(children_of "$p"); do own "$c"; done
  done
  for p in $OWNED; do
    if pid_live "$p"; then say "cleanup: SIGKILL $p (started by this script, or a child of one)"; safe_kill KILL "$p"; fi
  done
}
on_exit() { exec 3>&-; cleanup_owned; }
trap on_exit EXIT

# ---- helpers --------------------------------------------------------------------------------------------------------------
SECRET="smoke-internal-key-not-a-secret-$$-$(date +%s)"
# The clean environment of every app process (used as: env -i "${ENV_BASE[@]}" NAME=value... command args &). The command is started
# directly by env, never through a shell function, so that $! is the pid of the program itself and not of a helper shell.
ENV_BASE=(HOME="$HOME" USER="$(id -un)" LOGNAME="$(id -un)" PATH=/usr/bin:/bin:/usr/sbin:/sbin TMPDIR="$WORK/tmp/" LOCALAPPDATA="$WORK/localappdata")
json_front_url() { sed -n 's/.*"frontUrl" *: *"\([^"]*\)".*/\1/p' "$1" 2> /dev/null | head -1; }
elapsed() { awk -v t="$1" 'BEGIN { printf "%.1f", t / 2 }'; }
RESULTS=""
record() { RESULTS="$RESULTS$1=$2 "; say "RESULT $1 $2${3:+ - $3}"; }

# Milliseconds since the epoch (perl ships with macOS; whole seconds if it is missing).
now_ms() { perl -MTime::HiRes=time -e 'printf "%d\n", time() * 1000' 2> /dev/null || echo $(( $(date +%s) * 1000 )); }
secs() { awk -v t="$1" 'BEGIN { printf "%.2f", t / 1000 }'; }

# wait_gone <max seconds> <pid>...   polls the process list every 0.1 s, counting from T_STOP (set by whoever sends the stop, right
# before it does). Sets GONE_MS (when the last one disappeared), GONE_DETAIL (pid:ms of each) and STILL_ALIVE; returns 0 when none is alive.
T_STOP=0
GONE_MS=0
GONE_DETAIL=""
STILL_ALIVE=""
wait_gone() {
  local max="$1" live p now seen=" "
  shift
  GONE_DETAIL=""
  while :; do
    live="$(live_procs)"
    now=$(( $(now_ms) - T_STOP ))
    STILL_ALIVE=""
    for p in "$@"; do
      if echo "$live" | awk -v x="$p" '$1 == x { f = 1 } END { exit !f }'; then
        STILL_ALIVE="$STILL_ALIVE $p"
      else
        case "$seen" in *" $p "*) ;; *) seen="$seen$p "; GONE_DETAIL="$GONE_DETAIL $p:${now}ms" ;; esac
      fi
    done
    GONE_MS=$now
    [ -z "$STILL_ALIVE" ] && return 0
    [ "$now" -ge $((max * 1000)) ] && return 1
    sleep 0.1
  done
}

NODE_PID=""
API_PID=""
WEB_PID=""
FRONT=""
DATA=""
CASE_DIR=""

# start_bmbd <case name> [existing data folder]  -> NODE_PID, DATA; bmbd's stdin is a fifo whose writer end this script holds as fd 3
start_bmbd() {
  local name="$1" fifo
  CASE_DIR="$WORK/$name"
  mkdir "$CASE_DIR" || return 1
  if [ -n "${2:-}" ]; then DATA="$2"; else DATA="$CASE_DIR/data"; mkdir "$DATA" || return 1; fi
  fifo="$CASE_DIR/stdin.fifo"
  mkfifo "$fifo" || return 1
  exec 3<> "$fifo"
  say "starting: $MACOS/bmbd/BeeMemoryBank.Node --auto --data \"$DATA\" (clean environment, stdin = a pipe held open)"
  # The perl one-liner puts SIGINT and SIGQUIT back to their default and then replaces itself with bmbd (same pid). Without it the
  # signal could arrive ignored: a background job of a non-interactive shell (this script, or whatever started it with `&`) inherits
  # SIGINT as ignored, and a .NET program started that way does not react to Ctrl+C. The shell starts bmbd with default signals.
  set -m
  env -i "${ENV_BASE[@]}" BMB_STDIN_LIFELINE=1 BMB_INTERNAL_KEY="$SECRET" /usr/bin/perl -e '$SIG{INT} = "DEFAULT"; $SIG{QUIT} = "DEFAULT"; exec { $ARGV[0] } @ARGV' \
    "$MACOS/bmbd/BeeMemoryBank.Node" --auto --data "$DATA" \
    < "$fifo" > "$CASE_DIR/bmbd.out" 2> "$CASE_DIR/bmbd.err" 3>&- &
  NODE_PID=$!
  set +m
  own "$NODE_PID"
  say "bmbd pid $NODE_PID"
}

# wait_node_ready  -> FRONT, API_PID, WEB_PID; returns 1 on timeout / early exit
wait_node_ready() {
  local i st running
  FRONT=""
  for i in $(seq 1 120); do
    sleep 0.5
    if ! pid_live "$NODE_PID"; then say "bmbd exited early after $(elapsed "$i") s"; return 1; fi
    if [ -s "$DATA/.runtime.json" ]; then
      FRONT="$(json_front_url "$DATA/.runtime.json")"
      [ -n "$FRONT" ] && break
    fi
  done
  [ -n "$FRONT" ] || { say ".runtime.json did not appear within 60 s"; return 1; }
  say "frontUrl $FRONT (after $(elapsed "$i") s)"
  for i in $(seq 1 120); do
    st="$(curl -s -m 3 "$FRONT/node/status" 2> /dev/null || true)"
    running="$(echo "$st" | grep -o '"state":"Running"' | wc -l | tr -d ' ')"
    if [ "$running" = "2" ]; then
      API_PID="$(echo "$st" | sed -n 's/.*"BeeMemoryBank\.Api":{[^}]*"pid":\([0-9]*\).*/\1/p')"
      WEB_PID="$(echo "$st" | sed -n 's/.*"BeeMemoryBank\.Web":{[^}]*"pid":\([0-9]*\).*/\1/p')"
      say "/node/status: Api and Web Running (pids $API_PID, $WEB_PID) after $(elapsed "$i") s more"
      return 0
    fi
    sleep 0.5
  done
  say "Api and Web did not both reach Running within 60 s; last /node/status: $(echo "$st" | cut -c1-300)"
  return 1
}

# check_node_running: /health through the front, the pids against the process list, the runtime in use
check_node_running() {
  local health code ppid_api ppid_web rt ok=0
  health="$(curl -s -m 5 -w ' HTTP=%{http_code}' "$FRONT/health" 2> /dev/null || true)"
  say "GET $FRONT/health -> $health"
  case "$health" in *HTTP=200) ;; *) ok=1 ;; esac
  code="$(curl -s -m 5 -o /dev/null -w '%{http_code}' "$FRONT/" 2> /dev/null || true)"
  say "GET $FRONT/ -> HTTP=$code"
  # a static file of the web host's wwwroot, through the front: proves the web root is found inside the staged app
  static="$(curl -s -m 5 -o /dev/null -w '%{http_code} %{size_download}' "$FRONT/css/site.css" 2> /dev/null || true)"
  say "GET $FRONT/css/site.css -> HTTP/size $static"
  case "$static" in "200 "[1-9]*) ;; *) say "CHECK: the static file /css/site.css is not served (expected 200 and a body)"; ok=1 ;; esac
  [ "$(sed -n 's/.*"pid" *: *\([0-9]*\).*/\1/p' "$DATA/.runtime.json" | head -1)" = "$NODE_PID" ] || { say "CHECK: .runtime.json does not hold bmbd's pid $NODE_PID"; ok=1; }
  ppid_api="$(live_procs | awk -v x="$API_PID" '$1 == x { print $2 }')"
  ppid_web="$(live_procs | awk -v x="$WEB_PID" '$1 == x { print $2 }')"
  if [ "$ppid_api" = "$NODE_PID" ] && [ "$ppid_web" = "$NODE_PID" ]; then
    own "$API_PID"; own "$WEB_PID"
  else
    say "CHECK: Api/Web are not children of bmbd in the process list (ppid $ppid_api / $ppid_web)"; ok=1
  fi
  say "process list of the staged app:"
  live_procs | while IFS= read -r l; do say "    $l" | cut -c1-200; done
  rt="$(lsof -p "$NODE_PID" -Fn 2> /dev/null | sed -n 's/^n\(.*libcoreclr\.dylib\)$/\1/p' | head -1 || true)"
  if [ -z "$rt" ]; then
    say "runtime in use by bmbd: unknown (lsof gave nothing)"
  else
    case "$rt" in
      "$MACOS/dotnet/"*) say "runtime in use by bmbd: $rt (the bundled one)" ;;
      *) say "CHECK: bmbd runs on a runtime outside the app: $rt"; ok=1 ;;
    esac
  fi
  return $ok
}

# ---- cases ----------------------------------------------------------------------------------------------------------------
case_api() {
  local name=api dir="$WORK/api" url="" i health rc
  mkdir "$dir" && mkdir "$dir/data" || { record api FAIL "cannot create $dir"; return; }
  say "=== case api: the Api alone from $MACOS/api"
  env -i "${ENV_BASE[@]}" BMB_DATA_PATH="$dir/data" ASPNETCORE_URLS="http://127.0.0.1:0" BMB_READY_FILE="$dir/api.ready" BMB_INTERNAL_KEY="$SECRET" BMB_MDNS_ENABLED=false \
    "$MACOS/api/BeeMemoryBank.Api" < /dev/null > "$dir/api.out" 2> "$dir/api.err" &
  NODE_PID=$!
  own "$NODE_PID"
  say "Api pid $NODE_PID"
  for i in $(seq 1 120); do
    sleep 0.5
    pid_live "$NODE_PID" || { say "Api exited early after $(elapsed "$i") s"; break; }
    if [ -s "$dir/api.ready" ]; then url="$(sed -n 's/.*"urls":\["\([^"]*\)".*/\1/p' "$dir/api.ready")"; [ -n "$url" ] && break; fi
  done
  if [ -z "$url" ]; then
    say "--- api.out tail"; tail -15 "$dir/api.out" | cut -c1-200 | while IFS= read -r l; do say "    $l"; done
    cleanup_owned; record api FAIL "no ready file"; return
  fi
  health="$(curl -s -m 5 -w ' HTTP=%{http_code}' "$url/health" 2> /dev/null || true)"
  say "GET $url/health -> $health (after $(elapsed "$i") s)"
  safe_kill TERM "$NODE_PID"
  if wait_gone "$WAIT" "$NODE_PID"; then say "Api gone $(secs "$GONE_MS") s after SIGTERM"; else say "Api still alive after $WAIT s"; cleanup_owned; sleep 0.3; fi
  wait "$NODE_PID" 2> /dev/null; rc=$?
  say "Api exit code $rc"
  case "$health" in
    *HTTP=200) if [ -z "$STILL_ALIVE" ] && [ "$rc" = "0" ]; then record api PASS "/health 200, SIGTERM stop in $(secs "$GONE_MS") s, exit 0"; else cleanup_owned; record api FAIL "stop: alive='$STILL_ALIVE' exit=$rc"; fi ;;
    *) cleanup_owned; record api FAIL "/health: $health" ;;
  esac
}

# stop_graceful <stdin|sigterm|sigint>: the stop, then the proof that bmbd, Api and Web are gone and the runtime file is cleared
stop_and_verify() {
  local how="$1" rc leftovers bad=0
  case "$how" in
    stdin) say "stop: closing bmbd's stdin (fd 3)"; T_STOP=$(now_ms); exec 3>&- ;;
    sigterm) safe_kill TERM "$NODE_PID" || bad=1 ;;
    sigint) safe_kill INT "$NODE_PID" || bad=1 ;;
    kill9) safe_kill KILL "$NODE_PID" || bad=1 ;;
  esac
  if wait_gone "$WAIT" "$NODE_PID" "$API_PID" "$WEB_PID"; then
    say "bmbd, Api and Web are all gone $(secs "$GONE_MS") s after the stop (pid:ms$GONE_DETAIL)"
  else
    say "STILL ALIVE after $WAIT s:$STILL_ALIVE"; bad=1
    cleanup_owned; sleep 0.3   # a plain `wait` on a process that is still alive would never return
  fi
  wait "$NODE_PID" 2> /dev/null; rc=$?
  say "bmbd exit code: $rc"
  exec 3>&-
  leftovers="$(live_procs)"
  if [ -n "$leftovers" ]; then say "CHECK: other processes of the staged app are still running:"; echo "$leftovers" | while IFS= read -r l; do say "    $l" | cut -c1-200; done; bad=1; else say "process list of the staged app: empty"; fi
  STOP_SECONDS="$(secs "$GONE_MS")"
  STOP_RC="$rc"
  if [ "$how" = "kill9" ]; then
    say "after kill -9: .runtime.json $([ -e "$DATA/.runtime.json" ] && echo "LEFT (stale; bmbd could not remove it)" || echo removed), node.status.json $([ -e "$DATA/node.status.json" ] && echo "LEFT (stale)" || echo removed)"
  else
    [ "$rc" = "0" ] || { say "CHECK: bmbd exit code is $rc, expected 0"; bad=1; }
    if [ -e "$DATA/.runtime.json" ] || [ -e "$DATA/node.status.json" ]; then say "CHECK: the runtime/status file is not cleared: $(ls -a "$DATA" | grep -E '^(\.runtime\.json|node\.status\.json)$' | tr '\n' ' ')"; bad=1; else say "runtime file and status file cleared"; fi
  fi
  return $bad
}

case_node() { # stdin | sigterm | sigint | kill9
  local how="$1" detail
  say "=== case $how: bmbd --auto from $MACOS/bmbd, stop by $how"
  OWNED=""
  if ! start_bmbd "$how"; then record "$how" FAIL "cannot start"; return; fi
  if ! wait_node_ready; then
    say "--- bmbd.out tail"; tail -20 "$CASE_DIR/bmbd.out" | cut -c1-200 | while IFS= read -r l; do say "    $l"; done
    say "--- bmbd.err tail"; tail -10 "$CASE_DIR/bmbd.err" | cut -c1-200 | while IFS= read -r l; do say "    $l"; done
    exec 3>&-; sleep 3; cleanup_owned; record "$how" FAIL "the node did not start"; return
  fi
  if ! check_node_running; then cleanup_owned; exec 3>&-; record "$how" FAIL "running checks"; return; fi
  if stop_and_verify "$how"; then
    detail="all gone in $STOP_SECONDS s after the stop, bmbd exit $STOP_RC"
    [ "$how" = "kill9" ] || detail="$detail, runtime file cleared"
    record "$how" PASS "$detail"
  else
    cleanup_owned
    record "$how" FAIL "see the lines above"
  fi
  if [ "$how" = "kill9" ]; then KILL9_DATA="$DATA"; fi
}

KILL9_DATA=""
case_restart() {
  say "=== case restart: bmbd again on the data folder of the kill9 case, then a graceful stop"
  if [ -z "$KILL9_DATA" ]; then record restart SKIP "the kill9 case did not run"; return; fi
  OWNED=""
  if ! start_bmbd restart "$KILL9_DATA"; then record restart FAIL "cannot start"; return; fi
  if ! wait_node_ready; then
    say "--- bmbd.out tail"; tail -20 "$CASE_DIR/bmbd.out" | cut -c1-200 | while IFS= read -r l; do say "    $l"; done
    exec 3>&-; sleep 3; cleanup_owned; record restart FAIL "the node did not start again after kill -9"; return
  fi
  if ! check_node_running; then cleanup_owned; exec 3>&-; record restart FAIL "running checks"; return; fi
  if stop_and_verify stdin; then record restart PASS "started again after kill -9, stopped via stdin in $STOP_SECONDS s"; else cleanup_owned; record restart FAIL "stop"; fi
}

# ---- run ------------------------------------------------------------------------------------------------------------------
say "smoke-macos-full: app=$APP"
say "macOS $(sw_vers -productVersion 2> /dev/null || echo ?) $(uname -m), bundle version $(plutil -extract CFBundleShortVersionString raw -o - "$APP/Contents/Info.plist" 2> /dev/null || echo ?), work folder $WORK, cases $CASES, wait $WAIT s"
say "processes of the staged app before the run: $(live_procs | wc -l | tr -d ' ') (must be 0)"
if [ -n "$(live_procs)" ]; then
  say "another instance of the staged app is already running; this gate needs the app to itself. Stopping."
  OWNED=""
  exit 1
fi

IFS=',' read -r -a SELECTED <<< "$CASES"
for c in "${SELECTED[@]}"; do
  case "$c" in
    api) case_api ;;
    stdin|sigterm|sigint|kill9) case_node "$c" ;;
    restart) case_restart ;;
    *) die "unknown case '$c'" ;;
  esac
done

say "--- summary: $RESULTS"
say "left in place (nothing is deleted by this script): $WORK (data folders, bmbd.out/err, $LOG)"
# A selected case that could not run (SKIP: restart without kill9 before it) is not a pass: the run is INCOMPLETE and exits 1.
# Cases the user did not select are never recorded, so the default full run (kill9 before restart) is unchanged.
case "$RESULTS" in
  *FAIL*) say "SMOKE FAILED"; exit 1 ;;
  *SKIP*) say "SMOKE INCOMPLETE: a selected case did not run (see SKIP above)"; exit 1 ;;
  *) say "SMOKE PASSED"; exit 0 ;;
esac
