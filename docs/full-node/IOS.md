# The iPhone app (full node)

`mobile/BeeMemoryBank.FullIos`, "Bee Memory Bank" for iPhone: the whole memory bank on the phone, opened with the master password,
read, searched and edited there, and synced with the owner's computers and servers. The same product as the Android app
(`mobile/BeeMemoryBank.Mobile`), on the same libraries. **In development, not distributed yet:** it builds and passes its end-to-end
check on the iOS simulator; it has not run on a real iPhone, and there is no TestFlight or App Store build.

The iPhone *blind* copy is a different app ([docs/blind-node/IOS.md](../blind-node/IOS.md)): it holds only ciphertext and never sees the
master password. The two never share code that touches keys: the blind app's boundary tests keep the vault out of it, and this app's
golden list keeps the blind libraries out of this one.

## What a full node on an iPhone is, and is not

* **A node that calls, never one that is called.** iOS suspends an app soon after it leaves the screen and gives it no way to keep a
  socket open, so the phone cannot be a hub. It is a sync client: it calls each of its peers (`SyncClient`, the same push, pull and blob
  exchange as every node, sync protocol 3 unchanged) while the app is open and the vault unlocked - at once, after every change, every
  minute - and once more in the seconds iOS grants when the app leaves the screen. Peers never call it (its row has no address), so
  changes made elsewhere arrive the next time the phone is opened.
* **In process, no server.** The libraries the Android app uses (Core, Crypto, Search, Storage, Sync, Vault) run inside the app. No
  Kestrel, no web pages, no MCP server: AI agents keep using a computer or a server node.
* **No search by meaning on the phone.** Titles and folders are searched through the full-text index (SQLite FTS5), the notes' text by
  decrypting them in batches (the existing `SearchService` paths). The model (118 MB) and ONNX Runtime (~25 MB more in the executable)
  would triple the download and cost minutes of battery to index a vault; ONNX Runtime does ship an iOS build, so an optional download
  later is possible.
* **No photo upload yet** (no `BeeMemoryBank.Media`), no tag pages, no comments page. Notes with pictures show the text; the pictures stay
  on the other nodes' pages.

## Setting it up

* **Join** (the usual way): on a computer open Admin → Connect a device, then on the phone paste the code that starts with `bmb-join:`
  (or scan the code's QR with the Camera: the app registers the `bmb-join:` link and opens the join page with the code filled in; nothing
  is sent before the master password is typed). The code carries the computer's address and the pin of its TLS key: the master password
  goes only to a server holding that key, the whole vault is downloaded as a snapshot over the same pinned connection, and the computer
  is recorded with that pin, so every later sync is pinned to it as well. A server with a public certificate can be joined by typing its
  `https://` address; plain `http` is refused (the master password would cross the network in the clear).
  The first time an app reaches the local network iOS asks "find and connect to devices on your local network?", and every connection
  fails while the question is open. Before a join to a node on the local network the app therefore waits (up to 45 s) until a plain TCP
  connection to it opens - nothing is sent over it - so the request that carries the password is never the one that fails.
* **Create** a new memory bank on the phone: a name, the master password twice (the product's rule: 8 characters, upper case, lower case,
  a digit), then a **recovery key, shown once**. The app does not keep it; "Copy" puts it on this phone's clipboard only (not Universal
  Clipboard) for two minutes. Computers can join the phone's vault only through a node that listens - in practice the phone joins a
  computer, not the other way round.

## Keys, data and locking

| What | Where | Protected by |
|---|---|---|
| Master password | nowhere | used for the Argon2id derivation and forgotten |
| Master key (DEK) | memory, only while unlocked | the vault's key slot (Argon2id of the password), as on every node; wiped on lock |
| Node identity key | the database, wrapped under the master key | as on every full node (`NodeIdentityVault`) |
| Face ID unlock key | Keychain `com.beememorybank.mobile.quick-unlock` / `unlock-key-v1` | `BiometryCurrentSet` + `WhenPasscodeSetThisDeviceOnly`, not synchronisable: iOS releases it only after Face ID / Touch ID, drops it when faces or fingers change or the passcode is removed, never backs it up |
| Master key wrapped under that key | Keychain, same service, `unlock-slot-v1` | `WhenUnlockedThisDeviceOnly`, not synchronisable; the key-slot format of `MasterKeyManager` |
| Notes, titles, folders, events | `Library/Application Support/BeeMemoryBank` in the app's container | content: the vault's encryption; the files: iOS file protection "until first user authentication"; excluded from iCloud and computer backups |

* **Face ID / Touch ID** is offered after the first unlock and can be switched in Settings. It works like the desktop's OS auto-unlock: a
  random 256-bit key wraps the master key the way a password-derived key does in a key slot. A key that unwraps to anything but this
  vault's master key (checked against the vault's sentinel) is never installed: Face ID unlock turns itself off and the password is
  asked for (this happens after a master-key rotation on another node).
* **Locking.** The vault locks when the app leaves the screen - immediately by default; "after 1 minute" and "after 5 minutes" are
  choices, judged when the app comes back, because Argon2id costs seconds on a phone - and after 5 minutes on the screen without a touch
  or a keystroke (2 and 15 are choices). Locking throws away every page that showed a note. While the app is not active (app switcher,
  a system prompt) a plain cover hides it, so the picture iOS keeps for the app switcher shows no note.
* **The data folder** is excluded from backups: a backup restored on another phone would be a database without its Keychain items, and the
  vault is already copied on every node of the network. The titles and folder paths are plaintext in the database, as on every node
  (ADR-0005); the file protection keeps them unreadable while the phone is off or not yet unlocked since it started. "Complete" protection
  was not chosen: SQLite keeps its files open across the moment the phone locks during the last sync round.

## Notes

Tabs at the bottom - Notes, Search, Sync, Settings - and the main action of each page at the bottom too (the round **+** for a new note,
Save, Sync now, Lock now). Folders first, then notes; swipe a note left to move it to the trash; the trash restores a note as a copy
"[RESTORED] title" at the top level (the rule of every node). A note is shown as Markdown rendered on the phone with raw HTML displayed
as text and a Content-Security-Policy that loads nothing: no script, no remote picture, so opening a note tells nobody on the network. A
link opens in the browser after a question. Notes protected by their own passphrase open after the passphrase and are saved sealed
again. Light and dark follow the system.

## Measured on the simulator

The app's self-check (`Services/FullSelfCheck.cs`, `BMB_E2E_SELFCHECK=1`) on an iOS 26.5 simulator of a MacBook Air M1 (JIT):
SQLite 3.53.3 with FTS5 and WAL; Argon2id at the vault's parameters (64 MiB, t=3, p=4) about 1.9–2.6 s; creating a vault about 5 s (two
derivations), unlocking about 2 s, a note written in 7 ms, both searches under 30 ms on 40 notes, an Ed25519 signature 1.8 ms; peak
memory footprint 170 MB. An iPhone 11 Pro (A13, ahead-of-time compiled) is expected at 3–6 s per Argon2id derivation - the reason for
Face ID unlock and the lock grace - and is not measured yet.

## Building, testing, running

On a Mac with Xcode and the .NET `ios` and `maui-ios` workloads:

```bash
scripts/build-ios-full.sh simulator            # -> mobile/BeeMemoryBank.FullIos/bin/Debug/net10.0-ios/iossimulator-arm64/BeeMemoryBank.FullIos.app
BMB_IOS_CODESIGN_KEY="Apple Development: …" BMB_IOS_PROVISION="iOS Team Provisioning Profile: *" \
  scripts/build-ios-full.sh device Release     # signed, for xcrun devicectl device install app (inside the desktop session)
BMB_REQUIRE_IOS_SCAN=1 dotnet test tests/BeeMemoryBank.FullIos.Tests   # also scans the built .app against the golden list
```

The simulator build runs on the JIT; a device build is compiled ahead of time with the interpreter kept for code made at run time
(Dapper's row readers), as the blind app (whose run on an iPhone 11 Pro showed Dapper working that way). Build a device app from a fresh
`obj/` after a change to trimming: the incremental build does not compile again the assemblies the trimmer copies unchanged, and the app
then aborts at launch (found with the blind app, see [docs/blind-node/IOS.md](../blind-node/IOS.md)). A Release build keeps the runtime's
messages (`UseSystemResourceKeys=false`), so a failed join or sync is explained in words, not as a resource key.

`tests/BeeMemoryBank.FullIos.Tests` runs anywhere: the app's platform-free services (`Services/*.cs`: vault, Face ID logic with an
in-memory Keychain, auto-lock, notes, sync client, note rendering, the self-check) on real vaults, the boundary
(`docs/full-node/IOS-APP.golden.txt`), and what the app declares to iOS.

`tools/ios-e2e/ios_full_e2e.py` is the end-to-end check on a simulator: it starts a throwaway full node on the Mac (a published
`BeeMemoryBank.Api`, its own data folder, https on the LAN address under a self-signed key whose pin goes into a `bmb-join:` code), then
drives a Debug or `-p:BmbE2E=true` build of the app through environment variables at launch (a script cannot tap a simulator; the hooks,
`FullE2E.cs`, are compiled out of Release builds): self-check, join by code, a note each way, a conflict, a wrong and a right password,
lock, Face ID (the simulator's enrolled face, matching and not), leaving the app; screenshots of each screen, light and dark.

```bash
python3 tools/ios-e2e/ios_full_e2e.py --app <BeeMemoryBank.FullIos.app> --api <folder of BeeMemoryBank.Api.dll> --work <dir> --new-sim BMB-e2e
```

## Before it can be distributed

An App ID (`com.beememorybank.mobile`, the Android app's id), an Apple Distribution certificate and an App Store profile, an App Store
Connect record, the export-compliance answer (standard algorithms: AES-GCM, Ed25519, Argon2id, TLS), privacy labels (no data collected;
the privacy manifest is in the app), and a demo network for App Review (the app is useful alone after creating a vault, which helps).
A run on a real iPhone first: Argon2id speed, the ahead-of-time build with Dapper, Face ID with a real face, and the local-network prompt.
