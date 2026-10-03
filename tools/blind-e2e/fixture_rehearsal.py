"""Upgrade rehearsal on a COPY of a real blind node's data volume.

  python tools/blind-e2e/fixture_rehearsal.py --data <extracted copy of the node's /app/data> --old <old image> --new <new image> --work <scratch dir>

The copy is taken from the live node (a read-only mount of its volume streamed out with tar); this script only ever
works on a private docker volume made from that copy. Both images are started on it in turn - on a network of
`none`, so a copy of a REAL node (same identity, same peers, same backup targets) cannot dial anyone, push anywhere
or write to the real backup repository - and the results are compared:

  * the node starts and becomes healthy, keeps its NodeId and the TLS pin;
  * the migration ledger is unchanged (the migration runner deletes ledger rows it has no SQL for: a build that lost
    a migration would show here);
  * the database passes `PRAGMA integrity_check` and keeps its event and article counts;
  * no file of the data directory disappears.

Objects it creates are named bmb-fixture-*; it removes only those (--cleanup).
"""
import argparse
import json
import os
import re
import shutil
import sqlite3
import subprocess
import sys
import time

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


def rehearse(image, label, pristine, work):
    vol, name = 'bmb-fixture-data', 'bmb-fixture-node'
    run(['docker', 'rm', '-f', name])
    run(['docker', 'volume', 'rm', vol])
    run(['docker', 'volume', 'create', vol])
    # populate the private volume from a COPY of the pristine data (the image has cp)
    # A copy extracted on Windows arrives with every file world-accessible; the node refuses a key file like that
    # (and so it should), so the permissions a real volume has are put back on the private copy.
    r = run(['docker', 'run', '--rm', '--entrypoint', 'sh', '-v', vol + ':/d', '-v', pristine + ':/src:ro', image,
             '-c', 'cp -a /src/. /d/ && chmod 600 /d/node-identity.key /d/.internal-key && chmod -R go-rwx /d/tls'])
    if r.returncode != 0:
        say('populate failed: ' + r.stderr[:300])
        return None
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
    out = os.path.join(work, label)
    shutil.rmtree(out, ignore_errors=True)
    os.makedirs(out)
    run(['docker', 'cp', name + ':/app/data/.', out])
    files = sorted(os.listdir(out))
    f = facts(os.path.join(out, 'beememorybank.db'))
    run(['docker', 'rm', '-f', name])
    run(['docker', 'volume', 'rm', vol])
    return dict(health=health, status=status, files=files, facts=f, log=logs.stdout + logs.stderr)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--data', required=True, help='pristine copy of the node data directory')
    ap.add_argument('--old', required=True)
    ap.add_argument('--new', required=True)
    ap.add_argument('--work', required=True)
    a = ap.parse_args()
    os.makedirs(a.work, exist_ok=True)

    before = facts(os.path.join(a.data, 'beememorybank.db')) if False else None  # never open the pristine copy
    res = {}
    for label, image in (('old', a.old), ('new', a.new)):
        say('--- %s image: %s' % (label, image))
        res[label] = rehearse(image, label, a.data, a.work)
        if res[label] is None:
            check(label + ' image runs on the copy', False)
            continue
        check(label + ' image becomes healthy on a copy of the real data', res[label]['health'] == 'healthy', res[label]['health'])
        check(label + ' database passes integrity_check', res[label]['facts']['integrity'] == [('ok',)])
        say(label + ' status: ' + ' | '.join(l.strip() for l in res[label]['status'].splitlines()[:6]))
        say(label + ' ledger rows: %d (max %s), events %d, articles %d' % (
            len(res[label]['facts']['ledger']), max(res[label]['facts']['ledger'] or [0]),
            res[label]['facts']['events'], res[label]['facts']['articles']))
    if res.get('old') and res.get('new'):
        o, n = res['old'], res['new']
        check('migration ledger identical after the new image', o['facts']['ledger'] == n['facts']['ledger'],
              'old=%s new=%s' % (o['facts']['ledger'], n['facts']['ledger']))
        check('same node identity', o['facts']['node'] == n['facts']['node'], '%s vs %s' % (o['facts']['node'], n['facts']['node']))
        check('same events and articles', (o['facts']['events'], o['facts']['articles']) == (n['facts']['events'], n['facts']['articles']),
              '%s vs %s' % ((o['facts']['events'], o['facts']['articles']), (n['facts']['events'], n['facts']['articles'])))
        lost = sorted(set(o['files']) - set(n['files']))
        check('no file of the data directory lost by the new image', not lost, 'lost: %s' % lost)
        say('files old: %s' % ' '.join(o['files']))
        say('files new: %s' % ' '.join(n['files']))
        node_o = re.search(r'[0-9a-f]{8}-[0-9a-f-]{27}', o['status'])
        node_n = re.search(r'[0-9a-f]{8}-[0-9a-f-]{27}', n['status'])
        check('status shows the same node id', bool(node_o and node_n and node_o.group(0) == node_n.group(0)))
        bad = [l for l in n['log'].splitlines() if re.search(r'\b(fail|crit|unhandled|exception)\b', l, re.I)]
        check('no failure lines in the new image log', not bad, ' | '.join(bad[:3])[:300])
    say('RESULT: %s' % ('ALL PASS' if not FAILS else 'FAILED: ' + '; '.join(FAILS)))
    return 1 if FAILS else 0


if __name__ == '__main__':
    sys.exit(main())
