"""End-to-end check of the Android blind app against the blind node image, on a REAL test phone.

  python tools/blind-e2e/phone_e2e.py --image bmb-blind:dev --full-exe <BeeMemoryBank.Api.exe> --work <scratch dir> \
         --serial <adb serial of the TEST phone> --apk <com.beememorybank.blind-Signed.apk>

What it does (the phone is the test phone you name; only the blind app `com.beememorybank.blind` is touched):
  1. starts the blind node image (the listener the phone will call) and a full node (the "PC" of the pairing), pairs them;
  2. installs the APK, clears the blind app's data, starts it, answers the notification dialog;
  3. reads the phone's code off the screen, asks the PC to pair the phone to the blind node (POST /api/blind-nodes/android),
     types the call code it gets back into the phone and taps Connect (adb reverse makes the blind node's port the phone's
     127.0.0.1);
  4. waits for the phone's first load (it downloads the node's package, verifies it and applies it) and checks the screen;
  5. writes one more article on the PC, waits for it on the blind node, and (optionally) for the phone's next sync.

Every step prints PASS/FAIL. It removes only the objects named bmb-e2e-* it created; `adb reverse` is removed at the end.
"""
import argparse
import base64
import json
import os
import re
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET

FAILS = []


def say(m):
    print(time.strftime('%H:%M:%S') + ' ' + m, flush=True)


def check(name, ok, detail=''):
    say(('PASS ' if ok else 'FAIL ') + name + ((' - ' + detail) if detail else ''))
    if not ok:
        FAILS.append(name)
    return ok


def run(args, **kw):
    return subprocess.run(args, capture_output=True, text=True, encoding='utf-8', errors='replace', **kw)


def http(method, url, headers=None, body=None, insecure=False, timeout=60):
    data = json.dumps(body).encode() if body is not None else None
    h = dict(headers or {})
    if body is not None:
        h.setdefault('Content-Type', 'application/json')
    req = urllib.request.Request(url, data=data, headers=h, method=method)
    ctx = ssl._create_unverified_context() if insecure else None
    try:
        with urllib.request.urlopen(req, timeout=timeout, context=ctx) as r:
            return r.status, r.read()
    except urllib.error.HTTPError as e:
        return e.code, e.read()
    except Exception as e:
        return 0, str(e).encode()


def wait_for(fn, what, seconds=120, every=2):
    end = time.time() + seconds
    while time.time() < end:
        v = fn()
        if v:
            return v
        time.sleep(every)
    say('timeout waiting for ' + what)
    return None


class Phone:
    def __init__(self, serial):
        self.s = serial
        self.w, self.h = 720, 1600
        m = re.search(r'(\d+)x(\d+)', self.adb('shell', 'wm', 'size').stdout)
        if m:
            self.w, self.h = int(m.group(1)), int(m.group(2))

    def install(self, apk, seconds=300):
        """adb install, answering Google Play Protect's 'send this app for a security check?' with 'Don't send' (the test build is
        ours; nothing is sent to Google)."""
        proc = subprocess.Popen(['adb', '-s', self.s, 'install', '-r', apk], stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                text=True, encoding='utf-8', errors='replace')
        end = time.time() + seconds
        while proc.poll() is None and time.time() < end:
            time.sleep(4)
            if proc.poll() is not None:
                break
            node = self.find(lambda n: n.get('text') in ("Don't send", 'Don’t send'))
            if node is not None:
                self.tap(node)
        if proc.poll() is None:
            proc.kill()
        return proc.communicate()[0] or ''

    def swipe_up(self):
        x = self.w // 2
        self.sh('input swipe %d %d %d %d 300' % (x, int(self.h * 0.8), x, int(self.h * 0.3)))

    def adb(self, *args, timeout=60):
        return run(['adb', '-s', self.s, *args], timeout=timeout)

    def sh(self, cmd, timeout=60):
        return self.adb('shell', cmd, timeout=timeout)

    def dump(self):
        self.sh('uiautomator dump /sdcard/e2e_ui.xml')
        out = self.adb('exec-out', 'cat', '/sdcard/e2e_ui.xml').stdout
        self.sh('rm /sdcard/e2e_ui.xml')
        try:
            return ET.fromstring(out[out.index('<'):])
        except Exception:
            return None

    def nodes(self):
        root = self.dump()
        return [] if root is None else list(root.iter('node'))

    def texts(self):
        return [n.get('text') for n in self.nodes() if n.get('text')]

    @staticmethod
    def center(node):
        m = re.match(r'\[(\d+),(\d+)\]\[(\d+),(\d+)\]', node.get('bounds'))
        x1, y1, x2, y2 = map(int, m.groups())
        return (x1 + x2) // 2, (y1 + y2) // 2

    def find(self, pred):
        for n in self.nodes():
            if pred(n):
                return n
        return None

    def scroll_to(self, pred, tries=8):
        """The node matching `pred`, scrolling the page up (finger swipe) until it shows; None if it never does."""
        for _ in range(tries):
            n = self.find(pred)
            if n is not None:
                return n
            self.swipe_up()
            time.sleep(1)
        return self.find(pred)

    def to_top(self):
        """Scroll the page back to its top (finger swipes downwards) so the status header is on the screen."""
        for _ in range(6):
            self.sh('input swipe %d %d %d %d 200' % (self.w // 2, int(self.h * 0.3), self.w // 2, int(self.h * 0.8)))

    def tap(self, node):
        x, y = self.center(node)
        self.sh('input tap %d %d' % (x, y))

    def launch(self, package):
        """Start the app's launcher activity (`monkey` blocks adb's pipes on some phones; `am start` returns at once)."""
        out = self.sh('cmd package resolve-activity --brief -c android.intent.category.LAUNCHER ' + package).stdout.split()
        component = next((t for t in reversed(out) if '/' in t), package)
        self.sh('am start -n ' + component)

    def wake(self):
        self.sh('input keyevent 224')
        self.sh('wm dismiss-keyguard')
        self.swipe_up()

    def type_text(self, text):
        """Type into the focused field with the soft keyboard open. This phone's `input text` and key events lose every u and U, so
        runs of other characters go through `input text` and each u / U is a TAP on the Gboard key (position relative to the screen:
        u at 64.6 % x / 70.9 % y, shift at 7.9 % x / 83.8 % y), which is how a person would type it."""
        run_chars = []

        def flush():
            if run_chars:
                chunk = ''.join(run_chars)
                self.sh("input text '%s'" % chunk.replace("'", "'\''").replace(' ', '%s'))
                run_chars.clear()

        def tap_rel(fx, fy):
            self.sh('input tap %d %d' % (int(self.w * fx), int(self.h * fy)))

        for ch in text:
            if ch in 'uU':
                flush()
                if ch == 'U':
                    tap_rel(0.079, 0.838)
                tap_rel(0.646, 0.709)
            else:
                run_chars.append(ch)
                if len(run_chars) >= 120:
                    flush()
        flush()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--image', required=True)
    ap.add_argument('--full-exe', required=True)
    ap.add_argument('--work', required=True)
    ap.add_argument('--serial', required=True)
    ap.add_argument('--apk', required=True)
    ap.add_argument('--blind-port', type=int, default=15610)
    ap.add_argument('--console-port', type=int, default=15611)
    ap.add_argument('--full-port', type=int, default=15700)
    a = ap.parse_args()
    os.makedirs(a.work, exist_ok=True)

    name, vols = 'bmb-e2e-blind-phone', ['bmb-e2e-phone-data', 'bmb-e2e-phone-backups']
    full_key = 'e2e-phone-full-key'
    full_url = 'http://127.0.0.1:%d' % a.full_port
    fh = {'X-Internal-Key': full_key, 'X-User-Role': 'superadmin'}
    phone = Phone(a.serial)
    full = None
    try:
        # 1. the listener and the PC
        r = run(['docker', 'run', '-d', '--name', name, '--hostname', 'bmb-blind',
                 '-p', '127.0.0.1:%d:5610' % a.blind_port, '-p', '127.0.0.1:%d:5611' % a.console_port,
                 '-v', vols[0] + ':/app/data', '-v', vols[1] + ':/backups',
                 '-e', 'ASPNETCORE_ENVIRONMENT=Production',
                 '-e', 'BMB_PUBLIC_ADDRESS=https://127.0.0.1:%d' % a.blind_port, a.image])
        if not check('blind node starts', r.returncode == 0, r.stderr.strip()[:200]):
            return 1
        wait_for(lambda: run(['docker', 'inspect', '-f', '{{.State.Health.Status}}', name]).stdout.strip() == 'healthy', 'healthy', 120)

        data = os.path.join(a.work, 'full-data')
        os.makedirs(data, exist_ok=True)
        env = dict(os.environ, BMB_DATA_PATH=data, ASPNETCORE_URLS=full_url, BMB_INTERNAL_KEY=full_key,
                   ASPNETCORE_ENVIRONMENT='Production', BMB_MDNS_ENABLED='false', BMB_SYNC_INTERVAL_SECONDS='5')
        full = subprocess.Popen([a.full_exe], env=env, stdout=open(os.path.join(a.work, 'full-node.log'), 'w'),
                                stderr=subprocess.STDOUT, creationflags=0x08000000, cwd=os.path.dirname(a.full_exe))
        check('PC (full node) starts', bool(wait_for(lambda: http('GET', full_url + '/health')[0] == 200, 'full node', 90)))
        http('POST', full_url + '/api/init/standalone', fh, {'adminUsername': 'admin', 'displayName': 'E2E PC', 'password': 'E2e-test-password-1'})
        s, _ = http('POST', full_url + '/api/session/unlock', fh, {'password': 'E2e-test-password-1'})
        check('PC unlocked', s == 200)
        for i in range(3):
            http('POST', full_url + '/api/articles', fh, {'title': 'phone e2e %d' % i, 'treePath': '/e2e', 'content': 'body %d ' % i + 'x' * 3000})
        pc = run(['docker', 'exec', name, 'bmb', 'blind', 'pair-code']).stdout
        m = re.search(r'BMBBLIND1\.[A-Za-z0-9_=.\-]+', pc)
        s, b = http('POST', full_url + '/api/blind-nodes', fh, {'code': m.group(0) if m else ''})
        check('PC pairs with the blind node', s == 200, '%s %s' % (s, b[:160]))
        blind_id = json.loads(b).get('nodeId') if s == 200 else None

        # 2. the phone
        run(['adb', 'start-server'])
        r = phone.install(a.apk)
        check('APK installs', 'Success' in r, r.strip()[-200:])
        phone.adb('reverse', 'tcp:%d' % a.blind_port, 'tcp:%d' % a.blind_port)
        phone.sh('pm clear com.beememorybank.blind')
        phone.adb('logcat', '-c')
        phone.wake()
        for _ in range(4):
            phone.launch('com.beememorybank.blind')
            time.sleep(6)
            focus = phone.sh('dumpsys window').stdout
            if re.search(r'mCurrentFocus=.*(com\.beememorybank\.blind|permissioncontroller)', focus):
                break
            phone.sh('input keyevent 3')   # home, then try again: another app (or an installer dialog) was in front
        allow = phone.find(lambda n: n.get('text') == 'Allow')
        if allow is not None:
            phone.tap(allow)
            time.sleep(3)
        screen = phone.texts()
        code_node = next((t for t in screen if t.startswith('bmb-blind-phone:')), None)
        check('first start shows the identity and the phone code', code_node is not None and any(t.startswith('Node ') for t in screen),
              ' | '.join(t[:50] for t in screen[:6]))
        crash = phone.adb('logcat', '-d', '-s', 'AndroidRuntime:E').stdout.strip()
        check('no crash on first start', not crash, crash[:200])
        if code_node is None:
            return 1

        # 3. pairing: the PC tells the phone whom to call
        s, b = http('GET', full_url + '/api/blind-nodes/android/listeners', fh)
        listeners = json.loads(b) if s == 200 else []
        check('the blind node can be offered to the phone as the listener', any(l.get('nodeId') == blind_id for l in listeners), str(listeners)[:200])
        s, b = http('POST', full_url + '/api/blind-nodes/android', fh, {'code': code_node, 'listenerId': blind_id})
        check('PC pairs the phone', s == 200, '%s %s' % (s, b[:200]))
        call = json.loads(b).get('callCode') if s == 200 else None
        if not call:
            return 1
        edit = phone.scroll_to(lambda n: n.get('class') == 'android.widget.EditText')
        check('the phone has a field for the call code', edit is not None)
        if edit is None:
            return 1
        phone.tap(edit)
        time.sleep(1)
        phone.type_text(call)
        phone.sh('input keyevent 4')   # hide the keyboard
        time.sleep(1)
        connect = phone.scroll_to(lambda n: n.get('text') == 'Connect')
        check('the phone has a Connect button', connect is not None)
        if connect is None:
            return 1
        phone.tap(connect)
        time.sleep(4)
        phone.to_top()
        screen = phone.texts()
        check('the phone accepts the call code', any(t.startswith('Paired') for t in screen), ' | '.join(t[:70] for t in screen[:8]))

        # 4. the first load: the package of the blind node, verified and applied on the phone
        def first_load_done():
            phone.to_top()
            t = ' | '.join(phone.texts())
            return 'First load: done' in t
        done = wait_for(first_load_done, 'first load', 360, 10)
        check('first load finishes (the node\'s package is downloaded, verified and applied)', bool(done), ' | '.join(phone.texts())[:300])
        shot = phone.adb('exec-out', 'screencap', '-p')
        crash = phone.adb('logcat', '-d', '-s', 'AndroidRuntime:E').stdout.strip()
        check('no crash during pairing and first load', not crash, crash[:200])
        log_lines = phone.adb('logcat', '-d', '-s', 'DOTNET').stdout
        bad = [l for l in log_lines.splitlines() if re.search(r'unexpected error|Exception', l)]
        check('no unexpected error in the app log', not bad, ' | '.join(bad[:2])[:300])

        # 5. a later article reaches the blind node by sync (the phone follows with its scheduled worker)
        http('POST', full_url + '/api/articles', fh, {'title': 'phone e2e after pairing', 'treePath': '/e2e', 'content': 'late ' + 'y' * 2000})

        def stored():
            o = run(['docker', 'exec', name, 'bmb', 'blind', 'status']).stdout
            m2 = re.search(r'(\d+) articles', o)
            return int(m2.group(1)) if m2 else -1
        check('a new article reaches the blind node', bool(wait_for(lambda: stored() >= 4, 'sync', 90, 3)), 'stored=%s' % stored())
        with open(os.path.join(a.work, 'phone_after.png'), 'wb') as f:
            f.write(subprocess.run(['adb', '-s', a.serial, 'exec-out', 'screencap', '-p'], capture_output=True).stdout)
    finally:
        if full is not None:
            full.terminate()
            try:
                full.wait(timeout=20)
            except Exception:
                full.kill()
        phone.adb('reverse', '--remove', 'tcp:%d' % a.blind_port)
        say('left in place: container %s, volumes %s, folder %s (remove by name when done)' % (name, ', '.join(vols), a.work))
    say('RESULT: %s' % ('ALL PASS' if not FAILS else 'FAILED: ' + '; '.join(FAILS)))
    return 1 if FAILS else 0


if __name__ == '__main__':
    sys.exit(main())
