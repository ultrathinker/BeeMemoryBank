# Blind node (Docker)

A blind node stores and serves the mesh's encrypted data and backs it up with restic, without
ever holding the master key: it cannot read articles, attachments or comments. It is one
container with two processes — the Api in the blind role and a small local management page (the
console).

| Port | Published as | What |
|------|--------------|------|
| 5610 | `${BMB_LAN_ADDR}:5610` (LAN address only) | Sync surface for the mesh's full nodes, in **HTTPS** — the node's own self-signed certificate, pinned by each peer from the pair code. Keyless callers reach only the peer routes (`PublicSurface`); everything else answers 404. |
| 5611 | `127.0.0.1:5611` (host loopback only) | The console. Nothing on the LAN can reach it. |
| 5612 | not published | Plain-HTTP loopback port *inside* the container, where the console process and `docker exec … bmb` reach the Api (`BMB_BLIND_LOCAL_PORT`). Only local tools use it: the TLS listener above is for the mesh. |

The pair code the console prints carries `https://<BMB_LAN_ADDR>:5610` (`BMB_PUBLIC_ADDRESS`), so a
PC dials the node the same way every other device does. A code with an `http://` address is refused
by the PC, which is why the two are set together in `compose.yaml`.

Volumes: `bmb-blind-data` (the node's whole state: database, media, settings, console password,
job history, restic cache) and `bmb-blind-backups` (mounted at `/backups`, for a folder repository).

## Build and start

From the repository root, naming the host's LAN address — the sync port is published on that
address only, and compose refuses to start without it:

```bash
BMB_LAN_ADDR=192.168.1.20 docker compose -f docker/blind/compose.yaml up -d --build
```

(or put `BMB_LAN_ADDR=…` into `docker/blind/.env`).

The image downloads restic (0.17.3, amd64 or arm64) at build time and refuses to build if the
download does not match the checksum pinned in the Dockerfile.

## First setup

Secrets (console password, restic password, S3 secret key) are never command-line arguments —
`ps` and `docker inspect` show those to anyone on the host. The CLI prompts for them without
echo (`docker exec -it`), or reads `key=value` lines from stdin or from a `--secrets-file` that
must be `chmod 600`:

```bash
# Interactive: prompts for the console password (min 8 characters) and the restic password.
docker exec -it bmb-blind bmb blind init --repo-folder /backups/restic

# Scripted: secrets on stdin (keys: console_password, restic_password, s3_secret_key).
docker exec -i bmb-blind bmb blind init \
    --s3-endpoint https://s3.example:9000 --s3-bucket bmb --s3-prefix blind-repo \
    --s3-access-key AKIA… < ./blind-secrets.env      # the file: chmod 600, then delete it
```

The restic password will arrive from the pairing Windows node once pairing ships; until then it is
set here or on the console page. Keep a copy: without it the repository cannot be opened.

`init` is also how a node that is already set up gets a different backup target: it keeps the
console password it finds (pass `current_console_password` in the secrets to change that too) and
updates the settings. The endpoint needs the current password for a *password change*; the rest of
the run is the local administrator's, which is what holding the node's internal key means.

Open the console on the host at <http://127.0.0.1:5611>, or from another machine through a tunnel:
`ssh -L 5611:127.0.0.1:5611 <host>`. Five wrong passwords lock the page for 15 minutes; every
attempt is in the login journal.

## Backups

- Daily at the configured UTC time, moved by a random ±15 minutes; a slot missed while the node
  was down is caught up within 12 hours. After the Sunday backup, `restic check
  --read-data-subset=5%` runs; a full check is a button (`bmb blind backup verify --full`).
- The schedule turns itself on as soon as the repository and its password are in place — a
  configured node backs up without a further step, and one line in the log says so. Turning the
  console toggle off (or writing `"scheduleEnabled": false` in `settings.json`) is the operator's
  answer and is never overruled by that default.
- Each backup: `VACUUM INTO` a consistent copy of the database (needs 2.5× its size free),
  `restic backup` of that copy and the media folder, then `forget --prune` with the retention
  (default 7 daily, 4 weekly, 12 monthly, 3 yearly).
- Next to the repository — never inside it — the node writes `<repo>.recovery-set.json`
  (`/backups/restic.recovery-set.json`, or the object `<prefix>.recovery-set.json` in the
  bucket). A restore needs it before it can open the backup.
- On S3, restic needs delete rights (prune, locks). Protect the bucket with Object Lock or
  versioning.
- A hard delete in the mesh does not remove an article from backups that were already made.

`bmb blind backup now | list | verify [--full]`, `bmb blind jobs`, `bmb blind status`.

### Save a copy to…

A one-off copy of the repository into another folder — a USB disk or a NAS share mounted into
the container (add a volume line such as `- /media/usb:/backups/usb`), from the console or with
`bmb blind backup copy --to /backups/usb/bmb`. It is `restic copy` into a restic repository in
that folder (created on first use with the source's chunker parameters, so repeated copies are
incremental), under the same repository password, with `<folder>.recovery-set.json` next to it.
Nothing is decrypted or exported in the clear. The destination must be outside the node's data
folder and apart from the main repository.

## CPU modes

"Economy" (default: restic under `ionice -c 3 nice -n 19`, `GOMAXPROCS=1`), "Fast" (about
90 % of the cores, normal priority), "Pause" (the running restic is stopped with SIGSTOP and
resumes where it was). Pause acts at once; Economy/Fast apply from the next restic step of the
job, because a running restic cannot change its thread count. The mode survives a restart.
restic's memory is capped with `GOMEMLIMIT` (default 512MiB); the host needs at least 1 GB of RAM
plus swap, or 2 GB.

## Disconnect and erase

Console "Danger zone" or `docker exec -it bmb-blind bmb blind wipe --name <node name>` (asks for the
console password): clears the database, media and the node's backup settings (the restic
password with them). The restic repository and
the recovery-set are **not** touched, and the console password stays so the node can be paired
again. The wipe is written to `wipe-audit.log` in the data volume.
