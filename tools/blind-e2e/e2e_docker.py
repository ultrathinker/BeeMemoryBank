"""End-to-end check of the blind node image against a REAL full node (not a test host).

  python tools/blind-e2e/e2e_docker.py --image bmb-blind:v2-dev --full-exe <path to BeeMemoryBank.Api.exe> --work <scratch dir>

What it does (everything under --work and under objects named bmb-e2e-*; nothing else on the machine is touched):
  1. starts the blind image in a container with its own volumes, published on loopback only;
  2. starts a full node (the Api binary you give it) with its own data directory, initializes it and writes articles;
  3. pairs the two (pair code from `bmb blind pair-code`, added through POST /api/blind-nodes), waits for the seed,
     writes one more article and waits for it to arrive by ordinary sync;
  4. checks the console answers, the restore package is served for a restore code, an unauthenticated caller reaches
     only the public surface, the container is healthy, and that the node keeps its identity across `docker restart`.

It prints one PASS/FAIL line per check and exits non-zero on any FAIL. It never removes anything; the objects it made
are listed at the end for the owner (or `--cleanup` removes exactly those NAMED containers/volumes it created).
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

FAILS = []


def say(msg):
    print(time.strftime('%H:%M:%S') + ' ' + msg, flush=True)


def check(name, ok, detail=''):
    say(('PASS ' if ok else 'FAIL ') + name + ((' - ' + detail) if detail else ''))
    if not ok:
        FAILS.append(name)
    return ok


def run(args, **kw):
    return subprocess.run(args, capture_output=True, text=True, encoding='utf-8', errors='replace', **kw)


def http(method, url, headers=None, body=None, insecure=False, timeout=30):
    data = None
    h = dict(headers or {})
    if body is not None:
        data = json.dumps(body).encode()
        h.setdefault('Content-Type', 'application/json')
    req = urllib.request.Request(url, data=data, headers=h, method=method)
    ctx = ssl._create_unverified_context() if insecure else None
    try:
        with urllib.request.urlopen(req, timeout=timeout, context=ctx) as r:
            return r.status, r.read()
    except urllib.error.HTTPError as e:
        return e.code, e.read()
    except Exception as e:  # connection refused etc.
        return 0, str(e).encode()


def wait_for(fn, what, seconds=120, every=2):
    end = time.time() + seconds
    last = None
    while time.time() < end:
        last = fn()
        if last:
            return last
        time.sleep(every)
    say('timeout waiting for ' + what)
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--image', required=True)
    ap.add_argument('--full-exe', required=True)
    ap.add_argument('--work', required=True)
    ap.add_argument('--blind-https-port', type=int, default=15610)
    ap.add_argument('--blind-console-port', type=int, default=15611)
    ap.add_argument('--full-port', type=int, default=15700)
    ap.add_argument('--address', default='127.0.0.1')
    ap.add_argument('--first-image', help='start the node from this (older) image, pair and seed it, then swap the '
                    'container to --image on the SAME volumes: the upgrade rehearsal')
    ap.add_argument('--cleanup', action='store_true')
    a = ap.parse_args()

    os.makedirs(a.work, exist_ok=True)
    name = 'bmb-e2e-blind'
    vols = ['bmb-e2e-data', 'bmb-e2e-backups']
    full_key = 'e2e-full-internal-key'
    full_url = 'http://127.0.0.1:%d' % a.full_port
    fh = {'X-Internal-Key': full_key, 'X-User-Role': 'superadmin'}

    # 1. the blind container
    def start_container(image):
        return run(['docker', 'run', '-d', '--name', name, '--hostname', 'bmb-blind',
                    '-p', '127.0.0.1:%d:5610' % a.blind_https_port, '-p', '127.0.0.1:%d:5611' % a.blind_console_port,
                    '-v', vols[0] + ':/app/data', '-v', vols[1] + ':/backups',
                    '-e', 'ASPNETCORE_ENVIRONMENT=Production',
                    '-e', 'BMB_PUBLIC_ADDRESS=https://%s:%d' % (a.address, a.blind_https_port),
                    image])

    r = start_container(a.first_image or a.image)
    if not check('container starts', r.returncode == 0, r.stderr.strip()[:200]):
        return 1
    ok = wait_for(lambda: run(['docker', 'inspect', '-f', '{{.State.Health.Status}}', name]).stdout.strip() == 'healthy',
                  'healthy container', 120)
    check('container becomes healthy', bool(ok))

    def bmb(*args):
        return run(['docker', 'exec', name, 'bmb', 'blind', *args])

    st = bmb('status')
    check('bmb blind status works', st.returncode == 0 and 'blind' in st.stdout.lower(), st.stdout.strip()[:200].replace('\n', ' | '))
    node_id_before = re.search(r'[0-9a-f]{8}-[0-9a-f-]{27}', st.stdout)

    # 2. the full node
    data = os.path.join(a.work, 'full-data')
    os.makedirs(data, exist_ok=True)
    env = dict(os.environ, BMB_DATA_PATH=data, ASPNETCORE_URLS=full_url, BMB_INTERNAL_KEY=full_key,
               ASPNETCORE_ENVIRONMENT='Production', BMB_MDNS_ENABLED='false', BMB_SYNC_INTERVAL_SECONDS='5')
    log = open(os.path.join(a.work, 'full-node.log'), 'w', encoding='utf-8')
    full = subprocess.Popen([a.full_exe], env=env, stdout=log, stderr=subprocess.STDOUT, creationflags=0x08000000,
                            cwd=os.path.dirname(a.full_exe))
    try:
        up = wait_for(lambda: http('GET', full_url + '/health')[0] == 200, 'full node up', 90)
        if not check('full node starts', bool(up)):
            return 1
        s, b = http('POST', full_url + '/api/init/standalone', fh,
                    {'adminUsername': 'admin', 'displayName': 'E2E full', 'password': 'E2e-test-password-1'})
        check('full node initialized', s in (200, 201), '%s %s' % (s, b[:120]))
        s, b = http('POST', full_url + '/api/session/unlock', fh, {'password': 'E2e-test-password-1'})
        check('full node unlocked', s == 200, '%s %s' % (s, b[:120]))
        for i in range(3):
            s, b = http('POST', full_url + '/api/articles', fh,
                        {'title': 'e2e article %d' % i, 'treePath': '/e2e', 'content': 'body %d ' % i + 'x' * 2000})
            check('article %d written' % i, s in (200, 201), '%s %s' % (s, b[:100]))

        # 3. pairing
        pc = bmb('pair-code')
        m = re.search(r'BMBBLIND1\.[A-Za-z0-9_=.\-]+', pc.stdout)
        code = m.group(0) if m else None
        check('pair code issued', pc.returncode == 0 and code is not None, pc.stdout.strip()[:160].replace('\n', ' | ') + pc.stderr.strip()[:200])
        s, b = http('POST', full_url + '/api/blind-nodes', fh, {'code': code})
        check('full node pairs with the blind node', s == 200, '%s %s' % (s, b[:300]))

        def stored():
            o = bmb('status')
            m = re.search(r'articles[^0-9]*(\d+)', o.stdout)
            return int(m.group(1)) if m else -1

        got = wait_for(lambda: stored() >= 3, 'seed to arrive', 150, 3)
        check('seed delivered: the blind node stores the 3 articles', bool(got), 'stored=%s' % stored())

        s, b = http('POST', full_url + '/api/articles', fh,
                    {'title': 'e2e after pairing', 'treePath': '/e2e', 'content': 'late ' + 'y' * 1000})
        got = wait_for(lambda: stored() >= 4, 'ordinary sync', 90, 3)
        check('article written after pairing reaches the blind node by sync', bool(got), 'stored=%s' % stored())

        if a.first_image:
            def renewed_spki():
                o = run(['docker', 'exec', name, 'sh', '-c',
                         'curl -s -X POST -H "X-Internal-Key: $(cat /app/data/.internal-key)" '
                         '-H "X-User-Role: superadmin" http://127.0.0.1:5612/api/blind/pair-code/renew'])
                m = re.search(r'BMBBLIND1\.[A-Za-z0-9_=.\-]+', o.stdout)
                if not m:
                    return None
                raw = m.group(0).split('.', 1)[1]
                return json.loads(base64.urlsafe_b64decode(raw + '=' * (-len(raw) % 4))).get('tls_spki')

            def data_listing():
                return run(['docker', 'exec', name, 'sh', '-c', 'cd /app/data && ls -A | sort']).stdout.split()

            spki_before = renewed_spki()
            files_before = data_listing()
            say('upgrade rehearsal: swapping image %s -> %s on the same volumes' % (a.first_image, a.image))
            run(['docker', 'rm', '-f', name])          # the CONTAINER only; the volumes stay
            r2 = start_container(a.image)
            check('new image starts on the old volumes', r2.returncode == 0, r2.stderr.strip()[:200])
            wait_for(lambda: run(['docker', 'inspect', '-f', '{{.State.Health.Status}}', name]).stdout.strip() == 'healthy',
                     'healthy after the swap', 120)
            st3 = bmb('status')
            n3 = re.search(r'[0-9a-f]{8}-[0-9a-f-]{27}', st3.stdout)
            check('identity unchanged by the image swap',
                  bool(node_id_before and n3 and n3.group(0) == node_id_before.group(0)))
            spki_after = renewed_spki()
            check('TLS pin unchanged by the image swap (paired peers keep trusting it)',
                  bool(spki_before) and spki_before == spki_after, '%s -> %s' % (spki_before, spki_after))
            check('stored articles kept across the swap', stored() >= 4, 'stored=%s' % stored())
            files_after = data_listing()
            check('no file of the data directory is lost by the swap',
                  set(files_before) <= set(files_after), 'lost: %s' % sorted(set(files_before) - set(files_after)))
            say('data dir before: %s' % ' '.join(files_before))
            say('data dir after : %s' % ' '.join(files_after))
            http('POST', full_url + '/api/articles', fh,
                 {'title': 'e2e after the swap', 'treePath': '/e2e', 'content': 'swap ' + 'z' * 1000})
            got = wait_for(lambda: stored() >= 5, 'sync after the swap', 90, 3)
            check('the paired full node still syncs to the swapped node, no re-pair', bool(got), 'stored=%s' % stored())

        # 4. surfaces
        s, b = http('GET', 'http://127.0.0.1:%d/' % a.blind_console_port)
        check('console page answers', s == 200 and b'<html' in b.lower(), str(s))
        s, b = http('GET', 'https://127.0.0.1:%d/health' % a.blind_https_port, insecure=True)
        check('sync port answers /health over TLS', s == 200, str(s))
        s, b = http('GET', 'https://127.0.0.1:%d/api/articles' % a.blind_https_port, insecure=True)
        check('no article API on a blind node', s == 404, str(s))
        s, b = http('GET', 'https://127.0.0.1:%d/api/blind/status' % a.blind_https_port, insecure=True)
        check('keyless caller gets no blind status', s in (401, 403, 404), str(s))
        s, b = http('GET', 'http://127.0.0.1:%d/mcp' % a.blind_console_port)
        check('no MCP', s == 404, str(s))

        rc = bmb('restore-code')
        m = re.search(r'BMBRESTORE1\.[A-Za-z0-9_=.\-]+', rc.stdout)
        rcode = m.group(0) if m else None
        check('restore code issued', rcode is not None, rc.stdout.strip()[:160].replace('\n', ' | ') + rc.stderr.strip()[:200])
        if rcode:
            # the routes take the one-time SECRET carried inside the code, not the whole code
            raw = rcode.split('.', 1)[1]
            secret = json.loads(base64.urlsafe_b64decode(raw + '=' * (-len(raw) % 4)))['secret']
            s, b = http('GET', 'https://127.0.0.1:%d/api/blind/restore/package' % a.blind_https_port,
                        {'X-Restore-Code': secret}, insecure=True, timeout=120)
            check('restore package served for the code', s == 200 and len(b) > 1000, '%s %d bytes' % (s, len(b)))

        # identity survives a restart
        run(['docker', 'restart', name])
        wait_for(lambda: run(['docker', 'inspect', '-f', '{{.State.Health.Status}}', name]).stdout.strip() == 'healthy',
                 'healthy after restart', 120)
        st2 = bmb('status')
        n2 = re.search(r'[0-9a-f]{8}-[0-9a-f-]{27}', st2.stdout)
        check('node identity survives docker restart', bool(node_id_before and n2 and node_id_before.group(0) == n2.group(0)))
        check('data still there after restart', stored() >= 4, 'stored=%s' % stored())
        logs = run(['docker', 'logs', name]).stdout + run(['docker', 'logs', name]).stderr
        bad = [l for l in logs.splitlines() if re.search(r'\b(fail|crit|unhandled)\b', l, re.I)]
        check('no failure lines in the container log', not bad, ' | '.join(bad[:3])[:300])
    finally:
        full.terminate()
        try:
            full.wait(timeout=20)
        except Exception:
            full.kill()

    if a.cleanup:
        run(['docker', 'rm', '-f', name])
        for v in vols:
            run(['docker', 'volume', 'rm', v])
        say('cleaned the named container and volumes it created')
    else:
        say('left in place for inspection: container %s, volumes %s, folder %s' % (name, ', '.join(vols), a.work))
    say('RESULT: %s' % ('ALL PASS' if not FAILS else 'FAILED: ' + '; '.join(FAILS)))
    return 1 if FAILS else 0


if __name__ == '__main__':
    sys.exit(main())
