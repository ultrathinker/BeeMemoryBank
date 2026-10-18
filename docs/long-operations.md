# Long operations in the UI and on the server

A button that starts something that takes time must not be pressable twice, must say that it is working, and must say how it ended in the place where the button is. This page describes the one rule, the one helper per UI technology that keeps it, the server-side guard that refuses a second identical request, and the tests that fail when a new button bypasses it. (BMB-311.)

## The rule

| Kind | What the user sees |
|---|---|
| **Short** (a second or two: sign in, rename, delete a row, a switch) | The button is disabled at once and shows a spinner. The result (success, or the full text of an error) is written beside the button. |
| **Long** (seconds to minutes, or keys and data in bulk: the first copy of a baseline, encrypting a node or a phone, a backup, a restore, a replication, an update, a key change, an activation, an import or export, a snapshot, a wipe) | The same, and a modal pop-up with a loader and "please wait" covers the screen. Nothing else can be clicked. The pop-up closes when the work ends and the result (success, or the full text of the error) is shown where the button was. |

Work that the node runs on its own after an answer (a backup job on a blind phone or on the blind console, a sync) is followed to its end: the pop-up (or, on the blind console page, the job lock on the buttons) stays until the job is over, so a second click cannot start another one.

### The lock lasts as long as the work does

A time limit may change what the screen SAYS; it never gives a control back. A lock that let go on a timer would allow a second start of something that is still running.

- **Web.** `bmb-busy.js` keeps a button, a form and the pop-up locked until the answer comes or the request fails. After two minutes (a short operation) or half an hour (a long one) it says "Still waiting for the node ... do not start it again. Reload this page later to see whether it has finished." and keeps the lock. Back-button restores (`pageshow`) and a page that is replaced by the single-page navigation (below) are the only other ways out. The old second copy of this logic in `site.js` (it let a form go after two minutes) is gone: `bmb-busy.js` is the one owner of the busy state of a form.
- **A form is reserved in the task of its first submit.** A second submit in the same task (a script, a confirmation callback) is refused before anything is sent; a declined confirmation ends the reservation. The first submit event is also SEEN as busy at once (the buttons spin, the "working" note and the pop-up of a long operation are up); only disabling the submit buttons waits until the event has ended, because a disabled submit button is not part of the data a form sends, and a declined confirmation undoes all of it.
- **What the node said reaches the person.** The error text is taken from the answer in a fixed order - ProblemDetails `detail`, then `error`, `message`, `title`, a JSON string, and last the text of the answer (never an HTML error page) - and is always put into the page as text. The text is kept WHOLE: a message longer than 1000 characters shows its first part in the line and all of it in an area that opens ("Show the whole message").
- **Leaving the page.** When the single-page navigation replaces the content while an operation runs, the pop-up of an operation whose control is gone closes, the request itself goes on (it cannot be taken back), and its result is said once as a notice instead of being drawn into the page that replaced it. A control that stays (the sync status in the header) keeps its pop-up and its result line.
- **The pop-up is a dialog.** `role="alertdialog"`, `aria-modal`, named by its headline (`aria-labelledby`) and described by its line (`aria-describedby`); the focus moves into it and goes back to the control that was used.
- **Blind phones (Android, iPhone) and the blind desktop** wait for a job by the STATE of the phone (`BlindJobWait.Judge`): the job is open while it runs or is queued by the system, and ends when its mark appears (the first load is done; the time of the last sync or backup moved), when its failure is recorded, or - after a two-minute grace for the queue - when nothing was ever seen of it (the screen says "did not report", it does not say "finished"). There is no limit on how long a running job may take; after half an hour the pop-up says that it is still running and not to start it again. "Sync now" is also off while any job runs.
- **The MAUI helper and a page that goes away.** A rotation re-creates the activity, a page can be replaced or disposed while the work goes on: the spinner stops with its control, every touch of a control that may be gone is guarded (the end of the work must free the key), a result is not written into a label of a disposed page (a failure is said in a dialog of the page that is shown), and an old pop-up under another page closes the moment it is shown again.

## The helpers

There is exactly one helper per UI technology. A new button uses it; it does not build its own spinner.

| Technology | Helper | How a button uses it |
|---|---|---|
| Web, Razor forms | `server/BeeMemoryBank.Web/wwwroot/js/bmb-busy.js` | `<form method="post" data-busy="short">` or `data-busy="long" data-busy-title="Encrypting this node" data-busy-text="..."`. A form that cannot use it says why: `data-busy-exempt="reason"`. |
| Web, scripts | the same file, `window.bmbBusy` | `bmbBusy.run(button, () => bmbBusy.request(url, init), { long, title, text, success, quiet, error, scope })`. `request` is `fetch` that throws an `Error` with the node's own message (and `status`, `body`) on a non-2xx answer. `bmbBusy.submit(form)` sends a form from a script. A send that cannot use it carries `// bmb-busy-exempt: reason` just above it (a streamed chat answer, a file dropped on a drop zone, a read). |
| Desktop shell (Avalonia) | `desktop/BeeMemoryBank.Desktop/Views/BusyAction.cs` | `await BusyAction.RunAsync(owner, button, work, new BusyOptions { Long = true, Title = "...", ... })`. |
| Blind desktop | `MainViewModel` busy state (`IsBusy`, `BusyTitle`, `BusyText`) and the overlay in `MainWindow.axaml` | A command's `CanExecute` includes `!IsBusy`; a job-driven command stays busy until the node reports the job over. |
| Android and iPhone apps (MAUI) | `mobile/BeeMemoryBank.Mobile/Services/BusyAction.cs`, linked into `BlindMobile`, `BlindIos` and `FullIos` | `await BusyAction.RunAsync(this, work, new BusyOptions { Long = true, Title = "...", Message = "..." })`; `work` may take a `BusyProgress` to change the pop-up's text as it goes. A handler that cannot use it carries `// busy-exempt: reason` just above it (opening a page, copying to the clipboard). |
| Blind node console page (`server/BeeMemoryBank.BlindConsole/wwwroot/index.html`) | the page's own `busy(button, work, { long, title, text, out })` | A standalone page, so a few lines of the same rule live in it. The buttons that start a job are also off while a job runs. |

Each helper restores the button's text and enabled state when the work did not change them itself, so a page can re-label or keep a button off (for example after "Encrypt this node" succeeded) without the helper undoing it.

## The server: a second identical request gets 409

The pop-up and the disabled button stop an honest double click. They do not stop a second tab, a retry after a timeout, or a script. For that the server refuses a second identical long request with

```
409 { "error": "<what> is already running. Wait until it has finished.", "code": "already_running" }
```

`server/BeeMemoryBank.Api/Helpers/LongOperationGate.cs` is an in-process keyed lease, one gate per node (a singleton of the host, so several nodes hosted by one process never refuse each other): `.SingleFlight(key, what)` on an endpoint takes the lease for the length of the handler. The routes that carry it:

| Route | Key |
|---|---|
| `POST /api/blind-nodes/` (Add a blind node) | `blind-add` |
| `POST /api/blind-nodes/android/` (pair a blind phone) | `blind-phone-pair` |
| `POST /api/blind-nodes/{nodeId}/reseed` | `blind-reseed:{nodeId}` (another node's reseed runs next to it) |
| `POST /api/snapshots/` | `snapshot-create` |
| `POST /api/snapshots/restore`, `/restore-network` | `snapshot-restore` |
| `POST /api/snapshots/upload` | `snapshot-upload` |
| `POST /api/import/bee`, `/api/import/obsidian` | `import` |
| `POST /api/init/standalone`, `/join`, `/reset` | `node-init` |
| `POST /api/keys/change-password` | `master-password-change` |
| `POST /api/admin/concept-tag-edge/rebuild` | `graph-rebuild` |
| `POST /api/admin/search/embeddings/backfill` | `embeddings-backfill` (the lease is held by the background drain, not by the request, because the request answers 202 at once) |
| `POST /api/sync/activation` | `metadata-activation` |
| `POST /api/downloads/prepare` | `export-folder:{path}` for a folder (per folder), `export-all` for the whole vault; one article is a short export and takes no lease |
| `POST /api/folders/{id}/copy` | `copy-folder:{id}` |
| `POST /api/hard-delete/folder` | `hard-delete-folder:{path}` |
| `POST /api/hard-delete/article/{id}` | `hard-delete-article:{id}` |
| `POST /api/hard-delete/restore/folder/{id}`, `/restore/article/{id}` | `restore-folder:{id}`, `restore-article:{id}` |
| `POST /api/users/{id}/change-password`, `POST /api/users/me/change-password` | `user-password:{id}` - the same key for both, so that the user and a superadmin never race over one key slot (Argon2 and a rewrite of the slot) |

Every key is per operation and per resource (a route value, the body, or the caller), and a lease is per node: the gate is a singleton of the host. A route whose key is not in its route values says how to read it from the request (`SingleFlight(template, what, select)`); a request that is not a long one (an article export) returns no key and runs without a lease.

**How a lease ends.** When the handler returns or throws - or, for an operation the handler only starts, when the background work reaches a terminal state: the network restore (`POST /api/snapshots/restore-network`) answers 202 and goes on in the background, so its handler hands the lease over (`HttpContext.DetachSingleFlightLease()`) and the background task gives it back when the restore is over, whichever way it ends. A second request meanwhile is refused with 409 before it can append a second restore event. There is deliberately **no time limit** on a lease: one that expired while the work still ran would allow two active restores. The background work of the network restore is tracked by the restore service (one task), linked to the application's stopping token, so that a node that shuts down cancels it before anything is replaced: the token reaches the pre-restore backup, the lookup of the seeder, every HTTP call and read and the copy of the file, up to the last point before the apply, which is not interrupted; a cancelled restore is the terminal step `Cancelled` (`"cancelled"` on the status route) and gives the lease back at that moment. The copy, restore-from-trash and password routes are multi-row writes, and they stop only where stopping is safe: each hands its service a token linked to the request and to the application's stopping (`RequestCancellation.Linked`), and the service looks at it before its first write and, for a copy, before each folder, article and media record (a stop is a failure like any other, so what the copy created is rolled back and the vault is as it was; the rollback does not look at the token). A restore of an article is one write after its check; a restore of a folder that has created its root writes the whole subtree; a password change or an admin reset stops while it is only computation (the new login hash, the new key) and never once the key slot is being rewritten, which runs to its consistent end with the security stamp, the remote tokens and the doors. The route keeps its lease until the handler returns, that is until the work is done or stopped and undone. `CancellableWritesTests` calls every one of these routes as a client that has already gone (the handler runs with `RequestAborted` cancelled) and shows that nothing changed, and stops each service at its checkpoints. A work that hangs is ended by cancelling it: a client that goes away cancels what the work passes its token to (the snapshot create and its pre-restore backup, restore staging, exports, hard deletes, copies, restores from the trash, password changes); a restart of the node clears the gate. And `GET /api/admin/operations` (superadmin, never an agent) lists what holds a lease and since when, so that a stuck operation is seen and not guessed at. A restore that was accepted from a peer holds the service's own lock; the initiation check of `restore-network` sees that too.

These already refuse a second request on their own and were left as they are: the backup jobs (409 from `BlindJobManager`), a restore in progress (`RestoreProgress.TryStart`), an update (single flight in `UpdateService`), compaction (`ConflictException`), "Encrypt this node" (`SealMigrationService`), a data-key change (409 on propose). `HeavyOperationLock` and `EventWriteGate` are unrelated: they keep two *different* operations off the same data; the gate keeps *one* operation from starting twice.

The Web app turns the 409 into a plain sentence for "Add a blind node" and "Reseed" (`BlindNodeErrors`), so the card says "Adding a blind node is already running. Wait until it has finished." instead of a status code.

## The guard tests

`tests/BeeMemoryBank.Integration.Tests/BusyHelperGuardTests.cs` fails the build when a new asynchronous button bypasses the helper of its technology:

- every `<form method="post">` under `Pages` has `data-busy="short|long"` (a long one with a title) or `data-busy-exempt="reason"`;
- every POST/PUT/PATCH/DELETE, XHR or `form.submit()` in the Web scripts sits inside `bmbBusy.run(...)` or has a `// bmb-busy-exempt: reason` comment just above it;
- every `async` handler of the desktop shell, the blind desktop and the MAUI apps for a click, a tap, a menu item, the Enter key of a field (`Completed`), a search, a switch (`Toggled`) or the pull of a list (`Refreshing`) reaches `BusyAction.RunAsync` (in its own body or in any method of the same file it calls, however deep) or has a `// busy-exempt: reason` comment (a search reads, a local setting is switched, a page is opened);
- every `RelayCommand` of the blind desktop's view models has a can-execute that looks at the busy state, or a `// busy-exempt: reason` comment (copying a code, opening the confirmation panel, quitting);
- every change made by the blind console page sits inside its `busy(...)`;
- the shared helper source is linked into all four MAUI apps.

`LongOperationGateTests.cs` holds the gate's own tests, a 409 test for each of Add a blind node, Reseed, snapshot, the embeddings backfill and every route of the fix round with the lease held, and a test that lists every route carrying `SingleFlightMetadata`, so a route cannot gain or lose the rule unnoticed (`docs/full-node/ROUTES.golden.txt` shows it too).

`SingleFlightConcurrencyTests.cs` proves the gate with REAL concurrent requests and a latch that holds the work: on a small host, exactly one of many concurrent requests runs and the rest are refused at once; the lease comes back after an exception, after the client went away, and after a background operation the handler handed it to; end to end, two network-restore requests (the first answers 202 and keeps running; the second is refused before it appends an event; the third, after the first ended, is a new restore) and two "Add a blind node" requests (one call reaches the blind node).

`BusyHelperBehaviorTests.cs` runs `bmb-busy.js` itself in a small fake page (`JsHarness/bmb-busy-harness.js`, node, no packages, timers that move only when the test says so): a long operation is still locked after 31 minutes, a form after two minutes, two submits in one task send once, the error text of the node reaches the person, an operation of a replaced page closes its pop-up, the dialog is named and gives the focus back. They need node; where there is none they are skipped, and `BMB_REQUIRE_NODE=1` makes that a failure.

An exemption needs a reason of its own: the scanners do not accept an empty one. Reasons that exist today are opening a page, copying to the clipboard, a streamed answer with its own Stop button, files dropped on a drop zone, and reads that happen to use POST.

## Not covered by a compiler

The iPhone apps (`BlindIos`, `FullIos`) cannot be built on a Windows machine. Their changes were checked by the guard scanner (which reads the source text) and by review, and need a build on a Mac before a release.
