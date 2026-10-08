"""End-to-end check of the iPhone blind app on a simulator, against a full node (the "PC") and the listener it pairs phones to.

  python3 tools/ios-e2e/ios_e2e.py --app <BeeMemoryBank.BlindIos.app> --full-url http://127.0.0.1:5300 [--sim <udid>] [--note]
  python3 tools/ios-e2e/ios_e2e.py --app <signed device .app> --full-url ... --device <devicectl id> [--note]
  (the full node's internal key in BMB_E2E_FULL_KEY; the full node set up, unlocked and paired with a blind node or a hub already)

On an iPhone (--device; a development-signed Debug build or one made with -p:BmbE2E=true) the phone code is read from the app's console
(BMB_BLIND_E2E_PRINT_PHONE_CODE) instead of a screenshot, and the state and the database are copied out of the app's container with
devicectl. The person at the phone answers iOS's own prompts once: "find devices on your local network" and notifications.

What it does, without touching the screen:
  1. installs and starts the app (a Debug build, or one made with -p:BmbE2E=true);
  2. reads the phone code from the QR code on the screen (CoreImage, qr_decode.swift) - the screenshot is overwritten right after, because
     the code carries the phone's one-time secret and backup key;
  3. asks the PC to pair it (POST /api/blind-nodes/android, the listener: --listener or the first blind one) and gets the call code;
  4. starts the app again with the call code (BMB_BLIND_E2E_CALL_CODE): opening it as a bmb-blind-call: link makes iOS ask "Open in ...?",
     which a script cannot answer;
  5. waits for the first load and a sync (the app's state file in its container) and counts the notes in the app's database;
  6. with --note: writes a note on the PC and waits until the app holds one more (the app syncs each time it is started).

Every step prints PASS/FAIL. Nothing is removed: the app stays installed with its data, the screenshots stay in --work.
"""
import argparse
import json
import os
import re
import sqlite3
import subprocess
import sys
import time
import urllib.error
import urllib.request

BID = 'com.beememorybank.blind'
FAILS = []
HERE = os.path.dirname(os.path.abspath(__file__))


def say(m):
    print(m, flush=True)


def check(name, ok, detail=''):
    say(('PASS ' if ok else 'FAIL ') + name + ((' - ' + detail) if detail and not ok else ''))
    if not ok:
        FAILS.append(name)
    return ok


def run(args, env=None, timeout=300):
    return subprocess.run(args, capture_output=True, text=True, timeout=timeout, env=env)


def simctl(*args, env=None):
    return run(['xcrun', 'simctl', *args], env=env)


def http(method, url, key, body=None, timeout=120):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method,
                                 headers={'X-Internal-Key': key, 'X-User-Role': 'superadmin', 'Content-Type': 'application/json'})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, r.read().decode()
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()


def container(sim):
    data = simctl('get_app_container', sim, BID, 'data').stdout.strip()
    return os.path.join(data, 'Library', 'Application Support', 'BeeMemoryBankBlind')


def state(sim):
    try:
        with open(os.path.join(container(sim), 'blind-state.json')) as f:
            return json.load(f).get('values', {})
    except (OSError, ValueError):
        return {}


def notes(sim):
    try:
        db = sqlite3.connect('file:%s?mode=ro' % os.path.join(container(sim), 'beememorybank.db'), uri=True)
        try:
            return db.execute("SELECT COUNT(*) FROM tbl_article WHERE status = 'A'").fetchone()[0]
        finally:
            db.close()
    except sqlite3.Error:
        return -1


def restart(sim, env=None):
    simctl('terminate', sim, BID)
    return simctl('launch', sim, BID, env=env)


class Device:
    """The same steps on an iPhone, through xcrun devicectl."""

    def __init__(self, device, work):
        self.device = device
        self.work = work

    def ctl(self, *args, timeout=300):
        return run(['xcrun', 'devicectl', 'device', *args, '--device', self.device], timeout=timeout)

    def install(self, app):
        return self.ctl('install', 'app', app, timeout=900)

    def launch(self, env=None, tries=3):
        """Starts the app again. A refused launch (the phone locked itself for a moment, the old process is still ending) gets two more
        tries, and the reason iOS gave is printed."""
        # --device before the bundle id: whatever follows it is handed to the app as its arguments.
        args = ['xcrun', 'devicectl', 'device', 'process', 'launch', '--device', self.device, '--terminate-existing']
        if env:
            args += ['--environment-variables', json.dumps(env)]
        for attempt in range(tries):
            r = run(args + [BID])
            if r.returncode == 0:
                break
            say('  launch refused (try %d): %s' % (attempt + 1, launch_error(r)))
            time.sleep(10)
        return r

    def phone_code(self, seconds=60):
        """Starts the app with its console attached and reads the phone code it prints (an E2E build only).
        On iOS, .NET's Console writes to NSLog, not to stdout; OS_ACTIVITY_DT_MODE makes NSLog repeat it on stderr, which the console
        devicectl attaches carries (what Xcode does for its own console)."""
        env = {'BMB_BLIND_E2E_PRINT_PHONE_CODE': '1', 'OS_ACTIVITY_DT_MODE': 'YES'}
        proc = subprocess.Popen(['xcrun', 'devicectl', 'device', 'process', 'launch', '--device', self.device, '--terminate-existing',
                                 '--console', '--environment-variables', json.dumps(env), BID],
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        code, end = None, time.time() + seconds
        try:
            while time.time() < end and code is None:
                line = proc.stdout.readline()
                if not line:
                    break
                m = re.search(r'BMB_E2E_PHONE_CODE (bmb-blind-phone:\S+)', line)
                if m:
                    code = m.group(1)
        finally:
            proc.terminate()
            try:
                proc.wait(timeout=20)
            except subprocess.TimeoutExpired:
                proc.kill()
        return code

    def fetch(self, name):
        """Copies one file of the copy's data folder out of the app's container; returns its local path or None."""
        local = os.path.join(self.work, 'device-' + name)
        r = self.ctl('copy', 'from', '--domain-type', 'appDataContainer', '--domain-identifier', BID,
                     '--source', 'Library/Application Support/BeeMemoryBankBlind/' + name, '--destination', local)
        return local if r.returncode == 0 and os.path.exists(local) else None

    def state(self):
        path = self.fetch('blind-state.json')
        try:
            with open(path) as f:
                return json.load(f).get('values', {})
        except (TypeError, OSError, ValueError):
            return {}

    def notes(self):
        db = self.fetch('beememorybank.db')
        self.fetch('beememorybank.db-wal')
        if db is None:
            return -1
        try:
            conn = sqlite3.connect(db)
            try:
                return conn.execute("SELECT COUNT(*) FROM tbl_article WHERE status = 'A'").fetchone()[0]
            finally:
                conn.close()
        except sqlite3.Error:
            return -1


def launch_error(r):
    """The reason devicectl gives for a refused launch, without the call code it may echo back."""
    text = re.sub(r'bmb-blind-(phone|call):\S+', r'bmb-blind-\1:<redacted>', r.stdout + r.stderr)
    reasons = re.findall(r'NSLocalizedFailureReason = (.+)', text) or re.findall(r'ERROR: (.+)', text)
    return '; '.join(reasons[:2]) or text.strip()[-200:]


def wait_for(fn, seconds, every=5):
    end = time.time() + seconds
    while time.time() < end:
        value = fn()
        if value:
            return value
        time.sleep(every)
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--app', required=True)
    ap.add_argument('--full-url', required=True)
    ap.add_argument('--sim', default='booted')
    ap.add_argument('--device', help='devicectl id of an iPhone: run on it instead of a simulator')
    ap.add_argument('--listener', help='node id of the listener the phone is told to call (default: the first blind one)')
    ap.add_argument('--work', default=os.path.join(os.getcwd(), 'ios-e2e-work'))
    ap.add_argument('--note', action='store_true', help='also write a note on the PC and wait for it on the phone')
    ap.add_argument('--timeout', type=int, default=300)
    a = ap.parse_args()
    key = os.environ.get('BMB_E2E_FULL_KEY', '')
    if not key:
        say('set BMB_E2E_FULL_KEY to the full node\'s internal key')
        return 2
    os.makedirs(a.work, exist_ok=True)
    if a.device:
        return on_device(a, key)

    # 1. the app
    simctl('boot', a.sim)
    run(['xcrun', 'simctl', 'bootstatus', a.sim, '-b'], timeout=600)
    r = simctl('install', a.sim, a.app)
    if not check('the app installs', r.returncode == 0, r.stderr.strip()[-200:]):
        return 1
    restart(a.sim)
    time.sleep(15)

    # 2. the phone code, from the QR code on the screen
    shot = os.path.join(a.work, 'phone-code.png')
    simctl('io', a.sim, 'screenshot', shot)
    decoded = run(['swift', os.path.join(HERE, 'qr_decode.swift'), shot]).stdout
    with open(shot, 'wb'):
        pass  # the code carries the phone's secret: the picture is emptied, not kept
    phone = next((l for l in decoded.splitlines() if l.startswith('bmb-blind-phone:')), None)
    if not check('the first start shows the phone code as a QR code', phone is not None, decoded[:200]):
        return 1

    # 3. the PC pairs the phone and answers with the call code
    call = pair_on_pc(a, key, phone)
    phone = None
    if not call:
        return 1

    # 4. the call code, handed over at launch
    env = dict(os.environ, SIMCTL_CHILD_BMB_BLIND_E2E_CALL_CODE=call)
    check('the app starts with the call code', restart(a.sim, env).returncode == 0)
    paired = wait_for(lambda: state(a.sim).get('bmb.blind.call_code', '').startswith('bmb-blind-call:'), 60)
    check('the app accepts the call code', bool(paired))

    # 5. the first load and a sync
    loaded = wait_for(lambda: state(a.sim).get('bmb.blind.initial_load_done') == '1', a.timeout)
    check('the first load finishes (the listener\'s package is downloaded, verified and applied)', bool(loaded))
    synced = wait_for(lambda: state(a.sim).get('bmb.blind.last_sync'), 120)
    check('a sync round follows', bool(synced))
    count = notes(a.sim)
    check('the copy holds notes', count > 0, 'notes=%s' % count)
    simctl('io', a.sim, 'screenshot', os.path.join(a.work, 'paired.png'))

    # 6. a later note reaches the phone
    if a.note:
        s, _ = http('POST', a.full_url + '/api/articles', key,
                    {'title': 'ios e2e %d' % int(time.time()), 'treePath': '/ios-e2e', 'content': 'written by ios_e2e.py ' + 'z' * 400})
        check('the PC writes a note', s in (200, 201), str(s))

        def arrived():
            restart(a.sim)
            time.sleep(25)
            return notes(a.sim) > count
        check('the note reaches the phone (through the listener)', bool(wait_for(arrived, a.timeout, every=30)),
              'notes=%s' % notes(a.sim))
        simctl('io', a.sim, 'screenshot', os.path.join(a.work, 'after-note.png'))

    say('RESULT: %s' % ('ALL PASS' if not FAILS else 'FAILED: ' + '; '.join(FAILS)))
    return 1 if FAILS else 0


def pair_on_pc(a, key, phone):
    s, b = http('GET', a.full_url + '/api/blind-nodes/android/listeners', key)
    listeners = json.loads(b) if s == 200 else []
    listener = next((l for l in listeners if l.get('nodeId') == a.listener), None) if a.listener else \
        next((l for l in listeners if l.get('isBlind')), listeners[0] if listeners else None)
    if not check('the PC has a listener to offer the phone', listener is not None, '%s %s' % (s, b[:200])):
        return None
    s, b = http('POST', a.full_url + '/api/blind-nodes/android', key, {'code': phone, 'listenerId': listener['nodeId']}, timeout=300)
    call = json.loads(b).get('callCode') if s == 200 else None
    check('the PC pairs the phone', call is not None, '%s %s' % (s, b[:200]))
    return call


def on_device(a, key):
    phone = Device(a.device, a.work)
    r = phone.install(a.app)
    if not check('the app installs on the iPhone', r.returncode == 0, (r.stdout + r.stderr).strip()[-300:]):
        return 1
    code = phone.phone_code()
    if not check('the app prints its phone code to the console (E2E build)', code is not None):
        return 1
    call = pair_on_pc(a, key, code)
    code = None
    if not call:
        return 1
    check('the app starts with the call code', phone.launch({'BMB_BLIND_E2E_CALL_CODE': call}).returncode == 0)
    check('the app accepts the call code',
          bool(wait_for(lambda: phone.state().get('bmb.blind.call_code', '').startswith('bmb-blind-call:'), 90, 10)))
    check('the first load finishes (allow "local network" on the iPhone if it asks)',
          bool(wait_for(lambda: phone.state().get('bmb.blind.initial_load_done') == '1', a.timeout, 15)))
    check('a sync round follows', bool(wait_for(lambda: phone.state().get('bmb.blind.last_sync'), 180, 15)))
    count = phone.notes()
    check('the copy holds notes', count > 0, 'notes=%s' % count)
    if a.note:
        s, _ = http('POST', a.full_url + '/api/articles', key,
                    {'title': 'ios e2e device %d' % int(time.time()), 'treePath': '/ios-e2e', 'content': 'written by ios_e2e.py ' + 'z' * 400})
        check('the PC writes a note', s in (200, 201), str(s))

        def arrived():
            phone.launch()
            time.sleep(30)
            return phone.notes() > count
        check('the note reaches the iPhone (through the listener)', bool(wait_for(arrived, a.timeout, every=30)),
              'notes=%s' % phone.notes())
    say('RESULT: %s' % ('ALL PASS' if not FAILS else 'FAILED: ' + '; '.join(FAILS)))
    return 1 if FAILS else 0


if __name__ == '__main__':
    sys.exit(main())
