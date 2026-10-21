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

## Start from the ready-made image

The image is on GitHub's container registry, `ghcr.io/ultrathinker/beememorybank-blind`, for `linux/amd64` and
`linux/arm64` (a Raspberry Pi 4/5 included). No clone, no build — in an empty folder, naming the host's LAN address (the
sync port is published on that address only, and compose refuses to start without it):

```bash
curl -fsSL -o compose.yaml https://raw.githubusercontent.com/ultrathinker/BeeMemoryBank/main/docker/blind/compose.image.yaml
BMB_LAN_ADDR=192.168.1.20 docker compose up -d
```

(or put `BMB_LAN_ADDR=…` into a `.env` next to it; `BMB_VERSION=2.4` there stays on one release line). Update with
`docker compose pull && docker compose up -d`. `compose.image.yaml` is `compose.yaml` with the published image instead of
the build: the same container, volumes and ports, so a node started either way keeps its data when switched to the other.
Tags: `X.Y.Z`, `X.Y`, `latest`; a pre-release gets only its own tag.

## Build and start from source

From the repository root, naming the host's LAN address — the sync port is published on that
address only, and compose refuses to start without it:

```bash
BMB_LAN_ADDR=192.168.1.20 docker compose -f docker/blind/compose.yaml up -d --build
```

(or put `BMB_LAN_ADDR=…` into `docker/blind/.env`).

The image downloads restic (0.17.3, amd64 or arm64) at build time and refuses to build if the
download does not match the checksum pinned in the Dockerfile. Only the sync port 5610 is declared
as exposed in the image; the console's 5611 is published by the compose files on the host's
loopback only.

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

The console's own state file (`blind/console.json` in the data volume, which holds the console password's
hash and the sign-in log) is read before any change. A missing file is a node with no console password yet, so
the first one may be set without a current one. A file that cannot be read, or is damaged, refuses every sign-in
and every change to the console password; the change is refused with the reason. Fix the file or restore it, then try again.

The restic password defaults to the console password: when `init` (or the console page) sets or
changes the console password on a node that has no restic password of its own and no repository
yet, the same value becomes the restic password. A different one can be entered before the first
backup (`restic_password` here, or the field in the console's backup settings) and then always
wins. Once the repository exists (a backup has run) a console password change no longer touches
it: the repository stays encrypted under the password it was created with, so the settings refuse
a different restic password from then on (to use another one, point the backup at a new
repository, another folder or S3 prefix, and enter its password in the same save). A remote (S3)
repository counts as existing as soon as one was saved: this node cannot look inside a bucket, so
the console password is never taken as the restic password after that. A folder repository is
recognised by its `config` file (a repository made by 1.0.12, another spelling of the folder or a
symlink to it needs no migration); a folder whose `config` cannot be inspected (no permission, an I/O
error) counts as a repository too, only a folder that is explicitly empty or missing does not. A bucket settings file from 1.0.12 that has a password counts
as in use until the backup is pointed at another location. Pointing the backup at another S3 prefix
together with that repository's password in the same save is the supported way to switch: the node
never initialises a repository it cannot open, so a prefix that already holds one and a wrong password
stop the backup with restic's own error and change nothing there (only restic's "repository does not
exist" leads to `restic init`, which also refuses to overwrite an existing repository). Keep a copy of the
restic password: without it the repository cannot be opened.

`init` is also how a node that is already set up gets a different backup target: it keeps the
console password it finds (pass `current_console_password` in the secrets to change that too) and
updates the settings. The endpoint needs the current password for a *password change*; the rest of
the run is the local administrator's, which is what holding the node's internal key means.

Open the console on the host at <http://127.0.0.1:5611>, or from another machine through a tunnel:
`ssh -L 5611:127.0.0.1:5611 <host>`. Five wrong passwords lock the page for 15 minutes; every
attempt is in the login journal. The page's "Console password" section changes the password (it
asks for the current one, counted like a sign-in) and signs the other browsers out.

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

### Join a full node with its invitation (2.7)

The owner of the full node makes an invitation ("Invite a blind copy", a line that begins with `bmb-invite:`); you run one command on the machine of this node. The node must be running, with
`BMB_PUBLIC_ADDRESS` set (the full node dials that address to send the data):

```bash
docker exec -i bmb-blind bmb blind join - --yes < invitation.txt      # the invitation from a file: not in `ps` or the shell history
docker exec -it bmb-blind bmb blind join 'bmb-invite:?v=1&…'          # or typed: it shows who made it, asks, then prints the match code
docker compose exec -T blind bmb blind join - --yes < invitation.txt  # from the folder of a compose file
```

Read the **match code** it prints out to the owner; they compare it with the one on their Blind nodes page and confirm. The command waits for the answer (`--no-wait` does not; `bmb blind join --status`
shows it later; `--cancel` gives up). The old way - `bmb blind pair-code`, pasted on the full node's Blind nodes page - still works; a node does one of the two at a time. A full node older than 2.7 answers
"upgrade the full node first". If the full node's answer gets lost (a dropped connection, a timeout) the node keeps its record and its secret: run the same command again with the same invitation, or look at `--status`; `--cancel` ends it. Details: [docs/blind-node/README.md](../../docs/blind-node/README.md#a-server-blind-node-joins-with-bmb-blind-join-invitation-27).

### Save a copy to…

A one-off copy of the repository into another folder — a USB disk or a NAS share mounted into
the container (add a volume line such as `- /media/usb:/backups/usb`), from the console or with
`bmb blind backup copy --to /backups/usb/bmb`. It is `restic copy` into a restic repository in
that folder (created on first use with the source's chunker parameters, so repeated copies are
incremental), under the same repository password, with `<folder>.recovery-set.json` next to it.
Nothing is decrypted or exported in the clear. The destination must be outside the node's data
folder and apart from the main repository.

### Limits (2.7)

The person who hosts the node can cap what it keeps and moves for the mesh. Both are optional, in `compose.yaml` under `environment:` or in the console's **Limits** section (there a size can be written as `500 MB` or `2 GB`):

```yaml
      - BMB_BLIND_MAX_STORED_BYTES=2147483648       # at most 2 GB stored (database, media files, sealed baselines)
      - BMB_BLIND_MAX_TRAFFIC_PER_DAY=209715200     # at most 200 MB a day, counted over the node's local day
```

An empty or unset variable is no limit; `0` pauses that kind of transfer. A daily traffic limit below 10 MB (other than 0) is refused, and so is a value that is not a number: **the node does not start** with it. A variable that is set is applied again at every start, so a value changed in the console under it comes back; to keep a console value, leave the variable out.

A reached limit - of either kind, reads as well as writes - answers the routes that carry the mesh's data (events, blobs, the seed, the replica and restore packages) with `503`, `X-BMB-Paused: disk|traffic` and `Retry-After` (the next local midnight for traffic, an hour for storage) and writes nothing; the identity, sign-in, status and console routes still answer. A current full node shows the node as *paused by the blind node's owner* and asks again at the named time; an older full node treats it as an ordinary failure and backs off. A node that is only busy with other transfers answers a one-minute `X-BMB-Paused: busy`, which a full node shows as busy and not as your pause. `bmb blind status` and the console show both limits, what the node holds and what it moved today.

The limits are the host's agreement with the node's owner, not an operating-system quota: anything that can write the data volume can change the counts, and a request that is already admitted finishes, so a limit can be passed by what the requests in flight moved. The stored size includes what peers make the node stage, so a seed needs room for the old and the new data, and the volume itself needs about three times the package free. Wrong restore codes are counted per source address (five in five minutes, then `429`), never against the codes. This node has no app accounts and no device-wide limit. How exactly a cap holds (reservations, the 1 MiB minimum, the 256 KiB and 16 KiB small bodies, the lifetime of a request, the grace for a stuck one, the 200-character name of a restored computer): [docs/blind-node/README.md](../../docs/blind-node/README.md#limits-of-a-server-blind-node-27).

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
