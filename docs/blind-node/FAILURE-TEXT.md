# Blind app: what a failure says

One rule for the reviewed paths of the shared blind core (`libs/BeeMemoryBank.Blind.AppCore`, `libs/BeeMemoryBank.Blind.PhoneClient`): **a failure these paths record is told by its KIND, never by the
message of the exception.** The message of an exception carries what the person must not see on a screen, in a notification, in a screenshot or in a log that a support request will include: the
path of a file, the alias of a key, the address that was dialled (and a token in its query), the other side's reason phrase, a library's own words. The rule is the blind apps' form of the one in
[../sync.md](../sync.md) ("What a failure says"); the formatter is `BlindFailureText` (`Services/Blind/BlindFailureText.cs`).

**What this document does NOT promise.** It is not a guarantee for every error or log line of every app.
- The reviewed core paths sanitize what they record (the table below), and filter what an older version stored when it is read back.
- A head must format an exception itself (`BlindFailureText.Say`, or the head's own fixed sentence); the core cannot do it for a head.
- An exception that only propagates out of the core, or a platform call a head makes, keeps the words it was thrown with until a head formats it.
- A log that is explicitly diagnostic is not covered by "told by its kind": it records the TYPE of the exception and its stack, not the message (see "Diagnostic logs").

## What is said

`BlindFailureText.Say(ex)` decides by the TYPE and the PROVENANCE of the failure, never by trusting its text:

- anything in the chain that is a failure of a call to the other computer (HTTP, sockets, TLS, JSON, timeouts, the typed refusals of a peer) is the kind from the closed set of
  `PeerContactLog.Describe` ("no answer: connection refused", "it answered 401: its door is closed to this node ...", "no answer: its certificate was not accepted"), however many exceptions wrap it;
- a local failure is said by its **own sentence** ONLY when the exception carries the mark that this code threw it on purpose, to be read (`IBlindOwnSentence`, or the data key of
  `BlindOwnSentence.Mark`): then its message is passed on, cut to one line of at most 160 characters, and still not if it names a place (a `\`, a `/`, an address, a key alias). The TYPE of an exception
  proves nothing about its message: a framework, a library or a peer-influenced path throws `InvalidOperationException`, `ArgumentException` and `UnauthorizedAccessException` with a URL, a path, an
  account id or a token in the words, so **none of those types is trusted by itself**;
- every other local failure is told as its kind: a file or the storage ("a file or the storage of this device could not be read or written"), access ("access was denied"), a database, a key or
  encrypted data, a package or file that could not be read or verified, stored data that could not be read, or "an unexpected problem (TypeName)" - the name of the type at most. A wrapper that is not
  itself marked says its kind and what it wraps is not read.

The sentences meant to be shown carry the mark. For an `InvalidOperationException` it is `BlindSentenceException` (the sentences a start error shows: "This account's identity is in its database, but its
Keystore seed is missing or does not match. Remove this account from the accounts list and add it again.", "This phone has no blind identity.", the sync clients' "The blind phone identity has not been
recorded."), thrown by this code on purpose. For an I/O failure it is `BlindFileSentenceException` (the host's refusals about an account's folder: "... is a link; it was not followed or deleted.
Remove the link by hand."), `BlindRegistryDamagedException` (the registry's refusals) and `BlindNotEnoughRoomException` (two sizes: "Not enough room for a backup: about 120 MiB are needed, 80 MiB are
free."). A type that cannot be replaced because its callers catch it by type (`ArgumentException` for a name, the sealed `InvalidDataException` of the install waiting out its back-off or of a file that is
not a backup, `FileNotFoundException`) gets the mark in its data: `BlindOwnSentence.Mark(ex)`. `BlindFeaturePendingException` and the pause exception (`BlindPausedException`) carry it too. To show a new
sentence, throw one of these; any other exception is the kind of failure, whatever its message says.

`Typed(ex)` puts the type's name in front for the places read by whoever diagnoses a problem (the scheduler's `LastError`, the secret store's reason); `Sentence(ex)` makes it end in a full stop.

## Where it is said: at the point where the failure is recorded

So that whatever a head reads later is already clean, the kind is chosen where the failure is **recorded**, not where a head prints it:

| Where | What is recorded |
|---|---|
| `BlindRunReport.Reason` / `RecordFailure` | the log line of a failed sync and of an unexpected error of a run |
| `BlindHeavyWork` | `LastFailure.Message` and the log line of a failed first load, baseline update or backup |
| `BlindPhoneBackupRunner` | the outcome and the log line of a failed backup |
| `BlindAppController` | `StartError` and its log line, the key store's reason (`KeyStoreUnavailable`), the sentence of a run that stopped |
| `BlindAccountHost.FailureWords` | the problem of a card: an account that could not be started, removed, repaired, or an invitation that could not be kept |
| `BlindDeviceScheduler` | `LastError` and the sentence handed to the device log |
| `BlindPhoneReset`, the removal of an account | the exception a head gets: its message names the step and the kind, and **it carries no inner exception** (a head that tells "the whole story" of an exception, as `FailureText.Story` does, would otherwise read the path of the file that could not be removed) |

## What is read back from storage

What a stored text SAYS cannot tell the text of this version from the raw message that 2.6.4 stored (a path, a key alias, a request address, or just `Parameter 'filePath' token=... account 8f3a-...`, which has
no marker at all), so what is shown of a stored line is decided by **where it came from**, not by how it looks:

- **The stamp.** A line of the log file `blind-log.jsonl` that this version writes carries `"v":2` (`BlindFailureText.StoredVersion`; `BlindPhoneLog.Add` is the only writer, and the lines it is given are a failure
  already told by `BlindFailureText` or a sentence this code composed). A line **without** the stamp - everything 2.6.4 wrote - keeps its time and its kind and shows only the fixed sentence "The details were
  written by an older version of this app and are not shown." (`BlindFailureText.StoredTextHidden`). That holds for every kind and every text, the harmless-looking lines included. The file is **not rewritten**, and
  an account's old log still loads: only the words are not shown. `BlindPhoneLog.LatestStored` gives the lines as they are, for the code that compares a line it is about to write with the last one; a screen never gets those.
- **The `pairing` kind is no bypass.** The kind is stored data and proves nothing. A `pairing` line is shown only if it is stamped AND is, character for character, one of the shapes this code writes
  (`BlindPairingLine`): the fixed sentences, `Blind identity created: <node id>`, `Recovered existing blind identity: <node id>`, or `Paired: calls <address> (node <node id>).` where the node id is a GUID and
  the address is an `https://host[:port]` origin (no user, no path, no query, no fragment: the form of a call code). Anything else of that kind shows the fixed sentence.
- **The location check stays as a second layer.** A stamped line still goes through `BlindFailureText.ForDisplay(string)`: one line, no control characters, at most 800 characters, and a text that looks like a
  path, an address or a key alias (`\`, `/`, `://`, `bmb_blind_`) shows the fixed sentence. It is a cheap check and not the rule: it is also what is applied to the texts that exist only in memory (the status's
  `LastFailure`, `StartError`, `KeyStoreUnavailable`), which are made in this process by `Say` and are never read from a file.
- The rule is applied in `BlindPhoneLog.Latest` (so every head that reads `BlindAppStatus.RecentLog`, the desktop's account log among them, gets the shown text), and again in `BlindHomeView`, for a status that was
  made by hand. `BlindFailureText.ForDisplay(string, int)` is the same rule for any stored text with its stamp.
- **What the stamp is not.** It is provenance, not authentication: a person who can write the app's private folder can write `"v":2` too; it protects against the text an older version left behind and against a
  forgotten code path, not against someone who already has that folder.

## What a head does

Use `BlindFailureText.Say(ex)` (or `Typed`/`Sentence`) in your own `catch`; do not print `ex.Message`, `FailureText.Story(ex)` or `BusyAction.Describe(ex)` of an exception that came out of the shared
core. What the core builds itself (the removal and the wipe) is clean all through, but an exception that only propagates (a failed write of the registry, a platform store that throws) keeps the
words it was thrown with, so the head that catches it formats it. A head that stores its own text must stamp it the way `BlindPhoneLog` does, and must not show a stored text without its stamp.

## Diagnostic logs

**Diagnostic logs record the type and the stack, not the message.** A local file that is there for whoever diagnoses a problem (the desktop app's `ErrorLog`, the headless self-check) may keep the TYPE of the
exception and its stack frames, which name code and not data; it must not keep the message, because the message is where a path, a token or a key alias is. The desktop `ErrorLog` and the headless self-check use
`DiagnosticText.Describe(ex)`, which records the type and bounded stack frames without reading the message; they are not read back into any screen.

A platform's secret store throws its own sentences the same way, so that the key-store line of a card (`KeyStoreUnavailable`, read by the shared controller through `Typed`) says what the person needs
and not just "a file or the storage ...": the macOS `BlindSecretStoreException` is marked (`IBlindOwnSentence`) and says the secret by its KIND ("the backup key"), the system's status number and the way
out - never the Keychain item's name (`<account id>/backup-key-v1`: it holds a `/`, so the formatter would hide the whole sentence); the Windows stores throw `BlindFileSentenceException` for a file that
cannot be read for now ("The secret store did not answer: the file cannot be read (access denied)."), with the framework's own failure (which names the path) only as its inner exception. Any other
exception of a store is told by its kind. A new sentence of a platform must carry the mark AND name no place (`LooksLikeALocation`), or it is not shown.

## What a diagnostic of the desktop head records

The error log file of the desktop head (`ErrorLog`: start-up failures, a window command that failed) and the console output of `--self-check` are diagnostics, and a support request attaches them. They follow the same rule as a screen, with
a different content: **the type and the frames, not the message.** `DiagnosticText.Describe(ex)` writes `IOException at FileStream.Open < Store.Load < App.Start | inner UnauthorizedAccessException at ...` - the type of the exception, up to 8
frames as `Type.Method` (taken without file information, so no build path), the same for the inner exception or the failures of an aggregate (up to 6 exceptions), cut at 800 characters. It never reads `Message` (a test throws from a
`Message` getter to prove it), so an inner message cannot get in either. A finding the self-check makes itself ("the lock could not be taken", "the data folder does not exist") is a fixed sentence of its own and names no folder. The scheduler's own
problem line (`BlindTimerScheduler.Problem`) gives the type's name only, like `BlindDeviceScheduler`'s.

## Tests

`BlindStoredLineProvenanceTests` (the stamp, the plain hostile lines of the review - a parameter name, a token, an account id, an alias without the prefix - and a forged `pairing` line, through the log, `BlindHomeView`,
the account cards and `BlindJobWait`; a 2.6.4 log loads and is not rewritten), `BlindFailureTextRuleTests` (the rule and the filter), `BlindAppControllerBehaviorTests.FailureText.cs` (an adversarial exception through the run report, the heavy job, the sync round, the start,
the secret store's answer, the job wait, the home view and the wipe; a log written by 2.6.4), `BlindAccountFailureTextTests` (the host's removal, start and add-from-invitation paths), and the scheduler
test: each pushes the same adversarial exceptions - an `IOException` with a path and a key alias, an `HttpRequestException` with a URI and a token, wrapped and aggregate ones, a `SqliteException`, a
`CryptographicException`, an `UnauthorizedAccessException`, an `InvalidOperationException` that names a path - and asserts that none of the canaries is in any string of a view record, the log, the job
outcome, the exception a head gets (with everything inside it) or the files the run leaves. `ExceptionTextSitesInventoryTests` (`docs/design/exception-text-sites.golden.txt`) lists every file that uses
an exception's message; `NetworkFailureTextArchitectureTests` keeps an exception's message out of log calls and status members of these projects.
