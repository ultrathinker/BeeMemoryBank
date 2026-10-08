"""End-to-end check of the iPhone full app (mobile/BeeMemoryBank.FullIos) on a simulator, against a throwaway full node on this Mac.

  python3 tools/ios-e2e/ios_full_e2e.py --app <BeeMemoryBank.FullIos.app> --api <folder of BeeMemoryBank.Api.dll> --work <dir>
         [--sim <udid> | --new-sim <name>] [--lan <this Mac's LAN address>] [--port 16700]

The app is a Debug build or one made with -p:BmbE2E=true: a script cannot tap a simulator, so each step is handed to the app at launch
(BMB_E2E_* variables, see mobile/BeeMemoryBank.FullIos/FullE2E.cs) and runs through the same services its pages call; the app then shows
the screen this script photographs. The computer it joins is a full node started here (BeeMemoryBank.Api, published for osx-arm64),
listening on loopback for this script and with https on the LAN address for the phone, under a self-signed key whose pin goes into the
join code - the code a computer's "Connect a device" shows.

What it checks:
  1. the self-check on the simulator (SQLite FTS5 and WAL, Argon2id, a scratch vault);
  2. join by code: the master password goes only to the pinned key, the whole vault arrives (the computer's notes are on the phone);
  3. a note written on the phone reaches the computer; a note written on the computer reaches the phone;
  4. a conflict (the same note changed on both sides between syncs) ends the same on both, the loser kept as a conflict version;
  5. lock and unlock: a wrong password is refused, the right one opens, a locked app shows the unlock page;
  6. Face ID (the simulator's enrolled face): turned on, a matching face opens the vault, a face that does not match opens nothing;
  7. leaving the app (Settings opened over it): the last sync round runs and the vault locks; back in front the unlock page shows;
  8. the computer's log holds no rejected or quarantined event of the phone's; screenshots in light and dark throughout.

Throwaway secrets (the vault's password, the node's internal key) exist only in this process and the processes it starts; nothing
is written but the node's TLS key and data under --work. Nothing is removed: every run uses a new data folder of its own in the app and
on the node, and the node is stopped at the end. Every step prints PASS/FAIL.
"""
import argparse
import base64
import hashlib
import json
import os
import secrets
import shutil
import sqlite3
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

BID = 'com.beememorybank.mobile'
FAILS = []


def say(m):
    print(m, flush=True)


def check(name, ok, detail=''):
    say(('PASS ' if ok else 'FAIL ') + name + ((' - ' + detail) if detail else ''))
    if not ok:
        FAILS.append(name)
    return ok


def run(args, timeout=300, **kw):
    return subprocess.run(args, capture_output=True, text=True, timeout=timeout, **kw)


def simctl(*args):
    return run(['xcrun', 'simctl', *args])


class Node:
    """The throwaway computer: BeeMemoryBank.Api with its own data folder, loopback http for this script, LAN https for the phone."""

    def __init__(self, api, work, lan, port):
        self.api, self.work, self.lan, self.port = api, work, lan, port
        self.data = os.path.join(work, 'node-data')
        self.key = secrets.token_urlsafe(32)
        self.password = 'E2e-' + secrets.token_urlsafe(12) + '-9a'
        self.local = 'http://127.0.0.1:%d' % port
        self.public = 'https://%s:%d' % (lan, port + 1)
        self.log = os.path.join(work, 'node.log')
        self.pin = None

    def tls(self):
        cert, key = os.path.join(self.work, 'node-cert.pem'), os.path.join(self.work, 'node-key.pem')
        r = run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '7',
                 '-keyout', key, '-out', cert, '-subj', '/CN=bmb-e2e-node', '-addext', 'subjectAltName=IP:' + self.lan])
        if r.returncode != 0:
            raise SystemExit('openssl: ' + r.stderr)
        os.chmod(key, 0o600)
        der = run(['sh', '-c', 'openssl x509 -in "$0" -pubkey -noout | openssl pkey -pubin -outform der | base64', cert]).stdout
        spki = base64.b64decode(der)
        self.pin = base64.urlsafe_b64encode(hashlib.sha256(spki).digest()).decode().rstrip('=')
        return cert, key

    def start(self):
        os.makedirs(self.data, exist_ok=True)
        cert, key = self.tls()
        env = ['BMB_DATA_PATH=' + self.data, 'ASPNETCORE_URLS=%s;%s' % (self.local, self.public),
               'Kestrel__Certificates__Default__Path=' + cert, 'Kestrel__Certificates__Default__KeyPath=' + key,
               'BMB_INTERNAL_KEY=' + self.key, 'ASPNETCORE_ENVIRONMENT=Production', 'BMB_MDNS_ENABLED=false',
               'BMB_SYNC_INTERVAL_SECONDS=10', 'DOTNET_ROOT=' + os.environ.get('DOTNET_ROOT', os.path.expanduser('~/.dotnet'))]
        dotnet = shutil.which('dotnet') or os.path.expanduser('~/.dotnet/dotnet')
        # Inside the desktop session: on macOS a TLS server's key lives in a keychain, which a process of a plain ssh session cannot use.
        uid, user = str(os.getuid()), os.environ.get('USER') or run(['id', '-un']).stdout.strip()
        cmd = ['sudo', '-n', 'launchctl', 'asuser', uid, 'sudo', '-n', '-u', user, '-H', 'env', *env, dotnet, 'BeeMemoryBank.Api.dll']
        self.proc = subprocess.Popen(cmd, cwd=self.api, stdout=open(self.log, 'a'), stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL)
        for _ in range(90):
            try:
                if self.http('GET', '/health')[0] == 200:
                    return True
            except Exception:
                pass
            time.sleep(1)
        return False

    def stop(self):
        # The dotnet process itself (sudo and env exec into it, but sudo stays as its parent): found by this run's data folder.
        pids = run(['pgrep', '-f', 'BeeMemoryBank.Api.dll']).stdout.split()
        for pid in pids:
            env = run(['ps', '-E', '-p', pid, '-o', 'command=']).stdout
            if self.data in env:
                run(['kill', '-TERM', pid])
        try:
            self.proc.wait(timeout=20)
        except Exception:
            self.proc.kill()

    def http(self, method, path, body=None, timeout=120):
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(self.local + path, data=data, method=method,
                                     headers={'X-Internal-Key': self.key, 'X-User-Role': 'superadmin', 'Content-Type': 'application/json'})
        try:
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.status, r.read().decode()
        except urllib.error.HTTPError as e:
            return e.code, e.read().decode()

    def note(self, title, folder, text):
        s, b = self.http('POST', '/api/articles', {'title': title, 'treePath': folder, 'content': text})
        return json.loads(b)['id'] if s in (200, 201) else None

    def find(self, title):
        s, b = self.http('GET', '/api/articles')
        if s != 200:
            return None
        hits = [a for a in json.loads(b) if a.get('title') == title]
        return hits[0]['id'] if hits else None

    def text(self, article_id):
        s, b = self.http('GET', '/api/articles/%s/content' % article_id)
        return json.loads(b).get('content') if s == 200 else None

    def conflict_versions(self):
        db = os.path.join(self.data, 'beememorybank.db')
        try:
            with sqlite3.connect('file:%s?mode=ro' % db, uri=True) as c:
                return c.execute('select count(*) from tbl_conflict_version').fetchone()[0]
        except sqlite3.Error as e:
            return 'unreadable (%s)' % e

    def join_code(self):
        return 'bmb-join:?a=%s&s=%s' % (urllib.parse.quote(self.public, safe=''), self.pin)


class Phone:
    """The app on a simulator: each launch carries its steps, prints BMB-E2E lines, and ends on a screen that is photographed."""

    def __init__(self, sim, work, run_id):
        self.sim, self.work, self.run_id = sim, work, run_id
        self.shots = os.path.join(work, 'shots')
        os.makedirs(self.shots, exist_ok=True)

    def biometry(self, event):
        """The simulator's Face ID: 'enroll', 'match' (a matching face) or 'nomatch'."""
        if event == 'enroll':
            simctl('spawn', self.sim, 'notifyutil', '-s', 'com.apple.BiometricKit.enrollmentChanged', '1')
            simctl('spawn', self.sim, 'notifyutil', '-p', 'com.apple.BiometricKit.enrollmentChanged')
        else:
            simctl('spawn', self.sim, 'notifyutil', '-p', 'com.apple.BiometricKit_Sim.pearl.' + event)

    def launch(self, shot, appearance='light', seconds=180, face=None, background=False, **steps):
        simctl('terminate', self.sim, BID)
        simctl('ui', self.sim, 'appearance', appearance)
        env = dict(os.environ)
        env['SIMCTL_CHILD_BMB_E2E_DATA'] = self.run_id
        for k, v in steps.items():
            env['SIMCTL_CHILD_BMB_E2E_' + k.upper()] = v
        log = os.path.join(self.work, 'phone-%s.log' % shot)
        with open(log, 'w') as out:
            p = subprocess.Popen(['xcrun', 'simctl', 'launch', '--console-pty', self.sim, BID], stdout=out, stderr=subprocess.STDOUT, env=env)
            if face:
                # Answer the Face ID prompt the app is about to show.
                threading.Timer(6, self.biometry, [face]).start()
            deadline = time.time() + seconds
            while time.time() < deadline and 'BMB-E2E done' not in open(log).read():
                time.sleep(1)
            time.sleep(3)
            if background:
                # Leave the app for another one (Settings), come back after a while: the vault must be locked by then.
                simctl('io', self.sim, 'screenshot', os.path.join(self.shots, shot + '-before.png'))
                simctl('launch', self.sim, 'com.apple.Preferences')
                time.sleep(10)
                simctl('launch', self.sim, BID)
                time.sleep(4)
            simctl('io', self.sim, 'screenshot', os.path.join(self.shots, shot + '.png'))
            p.terminate()
            try:
                p.wait(timeout=10)
            except subprocess.TimeoutExpired:
                p.kill()
        lines = [l.strip() for l in open(log) if 'BMB-E2E' in l or 'BMB-SELFCHECK' in l]
        result = {}
        for l in lines:
            l = l[l.index('BMB-'):]
            say('    ' + l[:300])
            if l.startswith('BMB-E2E step='):
                name, rest = l[len('BMB-E2E step='):].split(' ', 1)
                result[name] = rest
            elif l.startswith('BMB-SELFCHECK '):
                k, _, v = l[len('BMB-SELFCHECK '):].partition('=')
                result['selfcheck.' + k] = v
            elif l.startswith('BMB-E2E event='):
                result.setdefault('events', []).append(l[len('BMB-E2E event='):])
        result['done'] = any(l.endswith('BMB-E2E done') for l in lines)
        return result


def ok(result, step, contains=''):
    v = result.get(step, '')
    return v.startswith('ok') and contains in v


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--app', required=True)
    ap.add_argument('--api', required=True, help='folder of a published BeeMemoryBank.Api (osx-arm64)')
    ap.add_argument('--work', required=True)
    ap.add_argument('--sim')
    ap.add_argument('--new-sim', help='create a new simulator with this name (iPhone 17, newest iOS) and use it')
    ap.add_argument('--lan', default=run(['ipconfig', 'getifaddr', 'en0']).stdout.strip())
    ap.add_argument('--port', type=int, default=16700)
    a = ap.parse_args()

    run_id = time.strftime('%m%d%H%M%S')
    work = os.path.join(os.path.abspath(a.work), run_id)
    os.makedirs(work)
    say('run %s, work %s, LAN %s' % (run_id, work, a.lan))

    sim = a.sim
    if a.new_sim:
        runtimes = [l for l in simctl('list', 'runtimes').stdout.splitlines() if l.startswith('iOS ')]
        runtime = runtimes[-1].split(' - ')[-1].strip()
        sim = simctl('create', a.new_sim, 'com.apple.CoreSimulator.SimDeviceType.iPhone-17', runtime).stdout.strip()
        say('new simulator %s (%s)' % (a.new_sim, sim))
    simctl('bootstatus', sim, '-b')
    check('app installs', simctl('install', sim, a.app).returncode == 0)

    node = Node(a.api, work, a.lan, a.port)
    phone = Phone(sim, work, run_id)
    try:
        if not check('the computer (throwaway full node) starts', node.start(), node.log):
            return
        s, b = node.http('POST', '/api/init/standalone', {'adminUsername': 'admin', 'displayName': 'e2e computer', 'password': node.password})
        check('the computer gets a vault', s == 200, b[:200])
        s, b = node.http('POST', '/api/session/unlock', {'password': node.password})
        check('the computer unlocks', s == 200, b[:200])
        node.note('From the computer', '/e2e', 'Written on the computer before the phone joined.')
        shared = node.note('Shared note', '/e2e', 'first version')

        say('1. self-check')
        r = phone.launch('01-selfcheck', selfcheck='1')
        check('self-check passes on the simulator', r.get('selfcheck.result') == 'ok',
              'argon2id %s ms, unlock %s ms, fts5 %s, journal %s' % (r.get('selfcheck.argon2id_again_ms'), r.get('selfcheck.unlock_ms'),
                                                                  r.get('selfcheck.sqlite_fts5'), r.get('selfcheck.sqlite_journal')))

        say('2. join by code')
        r = phone.launch('02-joined', password=node.password, name='iPhone (e2e)', join=node.join_code(), sync='1',
                         find='From the computer', show='notes')
        check('the phone joins by code (pinned key, master password)', ok(r, 'join'), r.get('join', 'no join step'))
        check('the first sync after the join reaches the computer on its pinned key', ok(r, 'sync', 'failed=0'), r.get('sync', ''))
        check("the computer's note is on the phone", ok(r, 'find', 'present'), r.get('find', ''))

        say('3. both directions')
        r = phone.launch('03-phone-note', password=node.password, unlock='1', write='Written on the iPhone|/e2e|Hello from the **phone**.',
                         sync='1', show='note:Written on the iPhone')
        check('the phone unlocks with the master password', ok(r, 'unlock', 'True'), r.get('unlock', ''))
        pid = node.find('Written on the iPhone')
        check("the phone's note reaches the computer", pid is not None and 'Hello from the **phone**.' in (node.text(pid) or ''),
              'not on the computer' if pid is None else '')
        node.note('Written on the computer later', '/e2e', 'A second note from the computer.')
        r = phone.launch('04-sync-page', password=node.password, unlock='1', sync='1', find='Written on the computer later', show='sync')
        check("a later note of the computer reaches the phone", ok(r, 'find', 'present'), r.get('find', ''))

        say('4. conflict')
        s, _ = node.http('PUT', '/api/articles/%s' % shared, {'content': 'changed on the computer'})
        check('the computer changes the shared note', s == 200)
        r = phone.launch('05-conflict', password=node.password, unlock='1', write='Shared note|/e2e|changed on the iPhone', sync='1',
                         find='Shared note', show='note:Shared note')
        on_phone = (r.get('find', '').split(' text=', 1) + [''])[1]
        time.sleep(2)
        r2 = phone.launch('06-after-conflict', password=node.password, unlock='1', sync='1', find='Shared note', show='note:Shared note')
        on_phone = (r2.get('find', '').split(' text=', 1) + [''])[1] or on_phone
        on_computer = node.text(shared)
        check('the conflict ends the same on both sides', on_phone != '' and on_phone == on_computer,
              'phone=%r computer=%r' % (on_phone, on_computer))
        check('the losing version is kept on the computer (tbl_conflict_version)', isinstance(node.conflict_versions(), int) and node.conflict_versions() >= 1,
              str(node.conflict_versions()))

        say('5. lock and unlock')
        r = phone.launch('07-wrong-password', password='Wrong-password-1', unlock='1')
        check('a wrong password is refused', r.get('unlock', '').startswith('ok False'), r.get('unlock', ''))
        r = phone.launch('08-locked', password=node.password, unlock='1', lock='1')
        check('lock closes the vault (the unlock page shows)', ok(r, 'lock', 'True'), r.get('lock', ''))
        r = phone.launch('09-unlocked-dark', appearance='dark', password=node.password, unlock='1', show='note:Written on the iPhone')
        check('the right password opens it again (dark appearance)', ok(r, 'unlock', 'True'), r.get('unlock', ''))
        phone.launch('10-search-dark', appearance='dark', password=node.password, unlock='1', show='search:computer')
        simctl('ui', sim, 'appearance', 'light')

        say('6. Face ID')
        phone.biometry('enroll')
        r = phone.launch('11-faceid-enabled', password=node.password, unlock='1', quick_enable='1', show='settings')
        check('Face ID unlock can be turned on (the Keychain takes the biometry-protected key)', ok(r, 'quick-enable', 'enabled=True'),
              r.get('quick-enable', ''))
        r = phone.launch('12-faceid-unlocked', face='match', quick_unlock='1', show='notes')
        check('a matching face opens the vault (no password)', ok(r, 'quick-unlock', 'result=Unlocked unlocked=True'), r.get('quick-unlock', ''))
        # iOS keeps its "Face not recognised - try again / cancel" prompt up, which a script cannot answer: within the wait the vault
        # must not have opened (the screenshot shows the prompt over the app's privacy cover).
        r = phone.launch('13-faceid-refused', face='nomatch', quick_unlock='1', seconds=30)
        check('a face that does not match opens nothing', 'unlocked=True' not in r.get('quick-unlock', ''), r.get('quick-unlock', 'prompt still up'))

        say('7. leaving the app')
        r = phone.launch('14-back-from-background', password=node.password, unlock='1', write='Written just before leaving|/e2e|left at once',
                         show='notes', background=True)
        events = r.get('events', [])
        check('leaving the app runs a last sync round', any(e.startswith('background-round reached=1 failed=0') for e in events), str(events))
        check('leaving the app locks the vault (default: immediately)', 'locked' in events, str(events))
        check('the note written just before leaving is on the computer', node.find('Written just before leaving') is not None)

        say('8. what the computer saw')
        log = open(node.log, errors='replace').read()
        bad = [l for l in log.splitlines() if any(w in l.lower() for w in ('reject', 'quarantin', 'invalid signature', 'signature verification failed'))]
        check('no event of the phone was rejected or quarantined', not bad, ' | '.join(bad[:3]))
        say('screenshots: ' + phone.shots)
    finally:
        node.stop()
        simctl('terminate', sim, BID)

    say('RESULT: ' + ('ALL PASS' if not FAILS else 'FAILED: ' + ', '.join(FAILS)))
    sys.exit(1 if FAILS else 0)


if __name__ == '__main__':
    main()
