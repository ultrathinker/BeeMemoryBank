"""Upgrade and rollback rehearsal on a COPY of a real blind node's data volume.

  python tools/blind-e2e/fixture_rehearsal.py --data <extracted copy of the node's /app/data> --old <old image> --new <new image> --work <scratch dir>

The copy is taken from the live node (a read-only mount of its volume streamed out with tar); this script only ever
works on a private docker volume made from that copy. The images are started on that ONE volume in turn:

    old  ->  new  ->  old again

(the old image as the node runs today, the upgrade, and the ROLLBACK - the old image on a volume the new one has touched),
on a network of `none`, so a copy of a REAL node (same identity, same peers, same backup targets) cannot dial anyone,
push anywhere or write to the real backup repository. After each step the node is stopped cleanly and its data directory
is read back:

  * the node starts and becomes healthy, keeps its NodeId and the TLS pin;
  * the migration ledger is unchanged (the migration runner deletes ledger rows it has no SQL for: a build that lost
    a migration would show here);
  * the database passes `PRAGMA integrity_check` and keeps its event and article counts;
  * no file of the data directory disappears; the identity key, TLS material and settings stay byte-identical.

Objects it creates are named bmb-fixture-<run id>; it removes only those, and only when its own run is over.
"""
import argparse
import hashlib
import os
import re
import sqlite3
import subprocess
import sys
import time
import uuid

FAILS = []


def say(m):
    print(time.strftime('%H:%M:%S') + ' ' + m, flush=True)


def check(name, ok, detail=''):
    say(('PASS ' if ok else 'FAIL ') + name + ((' - ' + detail) if detail else ''))
    if not ok:
        FAILS.append(name)


def run(args, **kw):
    return subprocess.run(args, capture_output=True, text=True, encoding='utf-8', errors='replace', **kw)


def facts(db_path):
    c = sqlite3.connect(db_path)
    try:
        return {
            'integrity': c.execute('pragma integrity_check').fetchall(),
            'ledger': [r[0] for r in c.execute('select version from tbl_migration order by version')],
            'events': c.execute('select count(*) from tbl_event').fetchone()[0],
            'articles': c.execute('select count(*) from tbl_article').fetchone()[0],
            'node': [str(r[0]) for r in c.execute('select node_id from tbl_node_identity')],
        }
    finally:
        c.close()


def manifest(root):
    """Every file of the data directory (recursively, relative paths) minus what a running node rewrites by itself, and
    a digest of the files that ARE the node's identity and configuration."""
    files, digests = [], {}
    for r, _d, fs in os.walk(root):
        for f in fs:
            rel = os.path.relpath(os.path.join(r, f), root).replace(os.sep, '/')
            if rel.startswith(('blind-tmp/', 'temp/')) or rel.endswith(('-wal', '-shm')):
                continue
            files.append(rel)
            if rel in ('node-identity.key', '.internal-key') or rel.startswith('tls/') or (rel.startswith('blind/') and 'settings' in rel):
                with open(os.path.join(r, f), 'rb') as fh:
                    digests[rel] = hashlib.sha256(fh.read()).hexdigest()
    return sorted(files), digests


def populate(vol, pristine, image):
    # A copy extracted on Windows arrives with every file world-accessible; the node refuses a key file like that
    # (and so it should), so the permissions a real volume has are put back on the private copy.
    r = run(['docker', 'run', '--rm', '--entrypoint', 'sh', '-v', vol + ':/d', '-v', pristine + ':/src:ro', image,
             '-c', 'cp -a /src/. /d/ && chmod 600 /d/node-identity.key /d/.internal-key && chmod -R go-rwx /d/tls'])
    return r.returncode == 0, r.stderr[:300]


def step(image, label, vol, suffix, work):
    """Run `image` on the volume, stop it cleanly, read the data directory back."""
    name = 'bmb-fixture-node-%s-%s' % (suffix, label)
    r = run(['docker', 'run', '-d', '--name', name, '--hostname', 'bmb-blind', '--network', 'none',
             '-v', vol + ':/app/data', '-e', 'ASPNETCORE_ENVIRONMENT=Production',
             '-e', 'BMB_PUBLIC_ADDRESS=https://fixture.invalid:5610', image])
    if r.returncode != 0:
        say('start failed: ' + r.stderr[:300])
        return None
    end = time.time() + 180
    health = ''
    while time.time() < end:
        health = run(['docker', 'inspect', '-f', '{{.State.Health.Status}}', name]).stdout.strip()
        if health == 'healthy' or run(['docker', 'inspect', '-f', '{{.State.Running}}', name]).stdout.strip() != 'true':
            break
        time.sleep(2)
    status = run(['docker', 'exec', name, 'bmb', 'blind', 'status']).stdout
    logs = run(['docker', 'logs', name])
    run(['docker', 'stop', '-t', '30', name])
    # the stopped container's volume is still readable with docker cp; the WAL is checkpointed by the clean stop
    out = os.path.join(work, '%s-%s' % (label, suffix))
    os.makedirs(out)          # fresh by construction; raises if it somehow exists, never clears one
    run(['docker', 'cp', name + ':/app/data/.', out])
    files, digests = manifest(out)
    f = facts(os.path.join(out, 'beememorybank.db'))
    run(['docker', 'rm', '-f', name])
    return dict(health=health, status=status, files=files, digests=digests, facts=f, log=logs.stdout + logs.stderr)


def compare(a, b, what):
    check('%s: migration ledger identical' % what, a['facts']['ledger'] == b['facts']['ledger'],
          '%d vs %d rows' % (len(a['facts']['ledger']), len(b['facts']['ledger'])))
    check('%s: same node identity' % what, a['facts']['node'] == b['facts']['node'], '%s vs %s' % (a['facts']['node'], b['facts']['node']))
    check('%s: same events and articles' % what,
          (a['facts']['events'], a['facts']['articles']) == (b['facts']['events'], b['facts']['articles']),
          '%s vs %s' % ((a['facts']['events'], a['facts']['articles']), (b['facts']['events'], b['facts']['articles'])))
    lost = sorted(set(a['files']) - set(b['files']))
    check('%s: no file of the data directory lost (recursive)' % what, not lost, 'lost: %s' % lost)
    check('%s: identity key, TLS material and settings byte-identical' % what,
          a['digests'] == b['digests'] and len(a['digests']) > 0, '%d vs %d digests' % (len(a['digests']), len(b['digests'])))
    na = re.search(r'[0-9a-f]{8}-[0-9a-f-]{27}', a['status'])
    nb = re.search(r'[0-9a-f]{8}-[0-9a-f-]{27}', b['status'])
    check('%s: status shows the same node id' % what, bool(na and nb and na.group(0) == nb.group(0)))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--data', required=True, help='pristine copy of the node data directory')
    ap.add_argument('--old', required=True)
    ap.add_argument('--new', required=True)
    ap.add_argument('--work', required=True)
    a = ap.parse_args()
    os.makedirs(a.work, exist_ok=True)

    # Names unique to this run: only objects this run created are ever removed, never an earlier run's or anyone else's.
    suffix = uuid.uuid4().hex[:8]
    vol = 'bmb-fixture-data-' + suffix
    run(['docker', 'volume', 'create', vol])
    results = {}
    try:
        ok, err = populate(vol, a.data, a.old)
        check('private volume made from the copy', ok, err)
        if not ok:
            return 1
        for label, image in (('old', a.old), ('new', a.new), ('rollback', a.old)):
            say('--- %s: %s' % (label, image))
            res = step(image, label, vol, suffix, a.work)
            results[label] = res
            if res is None:
                check(label + ' image runs on the volume', False)
                break
            check(label + ': healthy on a copy of the real data', res['health'] == 'healthy', res['health'])
            check(label + ': database passes integrity_check', res['facts']['integrity'] == [('ok',)])
            say(label + ' status: ' + ' | '.join(l.strip() for l in res['status'].splitlines()[:6]))
            say(label + ' ledger rows: %d (max %s), events %d, articles %d, %d data files' % (
                len(res['facts']['ledger']), max(res['facts']['ledger'] or [0]), res['facts']['events'], res['facts']['articles'], len(res['files'])))
            bad = [l for l in res['log'].splitlines() if re.search(r'\b(fail|crit|unhandled|exception)\b', l, re.I)]
            check(label + ': no failure lines in the log', not bad, ' | '.join(bad[:3])[:300])
        if results.get('old') and results.get('new'):
            compare(results['old'], results['new'], 'upgrade (old -> new)')
        if results.get('new') and results.get('rollback'):
            compare(results['new'], results['rollback'], 'rollback (new -> old again)')
    finally:
        run(['docker', 'volume', 'rm', vol])
    say('RESULT: %s' % ('ALL PASS' if not FAILS else 'FAILED: ' + '; '.join(FAILS)))
    return 1 if FAILS else 0


if __name__ == '__main__':
    sys.exit(main())
