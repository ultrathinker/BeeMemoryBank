# Dream (sleep-time advice)

Dream is a maintenance pass over the notes of a full node. It looks for exact duplicates, notes that
sit in the wrong folder, tags spelled several ways, unused tags, empty folders, leftovers of testing,
and — when a model is switched on — pairs and folders worth a human look. What it finds it writes
down as **advice**: one card with a score from 0 to 100, what is proposed, why, and buttons.

Dream never changes the bank by itself. A note, tag or folder is moved only when a person presses a
button on a card, and the change is made by the ordinary article, tag and folder services, with that
person's rights: version history, the trash (30 days), sync events and the activity log work exactly
as for a hand edit. Dream is visible to the superadmin only (the header item right after **Search**);
there is no Dream over MCP, and agents never see it. Its settings are in **Admin → Search and AI →
Dream**.

Everything Dream keeps belongs to this node alone: runs, advice and dismissals are never
synchronised, never part of a snapshot, and a blind node has no Dream at all.

## Runs

A run is started by hand (the **Start** button on the Dream page) or by the nightly schedule. One run
at a time per node; a second start is refused with `409 already_running`. **Stop** takes effect
between candidates. A run goes through five phases in order:

1. **embeddings** — notes whose search vector is still missing are embedded first, so the
   similarity steps have vectors to work with;
2. **detectors** — every rule that needs no model runs here (duplicates, misplaced notes, tag
   variants, unused tags, empty folders, test leftovers);
3. **pairs** — with the model on, pairs of similar notes are judged one by one;
4. **folders** — with the model on, busy folders are reviewed;
5. **tree** — with the model on, the shape of the whole tree is looked at, at most once in 7 days
   and only when the tree changed.

**Two switches** on the start form decide what a manual run does: **Built-in rules** (on by default)
runs the detectors phase, and **AI model: <name>** runs the pairs, folders and tree phases. They are
independent: either one alone, or both. With the rules off the detectors make no advice, but the
embeddings phase still runs (the model steps need the vectors); no model step depends on what the
detectors found. At least one switch must be on: the page greys out Start and says why, and the
node refuses `{ "useRules": false, "useModel": false }` (or `useRules: false` while the model is not
ready) with `400`. When no text model is set up, or no usable OpenRouter key, the model switch is
shown off and disabled, with the reason (`modelProblem` in `GET /api/dream/status`: `no_model` or
`no_key`) and links to **Admin → AI / Chat** (models and keys) and **Admin → Dream** (the model Dream
uses). The run records `useRules` next to `useModel`; the history row shows "Rules + AI", "AI only"
or "Rules only", and the comparison of trial runs shows it beside the model's name. **The nightly run
always runs the rules** (and the model when the settings switch it on); the start form's switches
are for manual runs only.

**View all rules** next to Start (and a link in "What Dream reads") opens a dialog that describes
every detector and every model step: the kinds of advice it makes, what it finds with an example,
how it decides with its thresholds, how the score is set, what Apply does and never does, what it
skips; for a model step, which candidates and how many, what the model is asked, what it may
answer, what the node checks and its output limits; then the limits of a run and the score bands.
It is `GET /api/dream/rulebook`, built at request time from the detectors and tasks the node has
and from the numbers in the code and the settings, so it cannot drift from what runs
(`DreamRuleBookTests` fails when a detector, a model step or a kind of advice has no description).

A nightly run stops after the first two tiers of the pairs phase; a manual run works through
everything and can
be continued by the next manual run — what was already examined is remembered, so it is not paid for
twice. When a run finishes, it records what ended it: the candidates ran out (`completed`), the
advice limit, the time limit or the model budget was reached (`advice_limit`, `time_limit`,
`budget_limit`), it was stopped (`stopped`), the vault was locked mid-run (`locked`), a heavy
operation began (`heavy_operation`), the model provider kept failing (`provider_error`), the process
went away (`interrupted`), something unexpected broke (`failed`), or the nightly window passed
without a start (`skipped`, with the reason).

While a run works, the page polls a live feed every two seconds: advice cards appear one by one, and
the run's counters grow.

## What Dream reads

One run builds one picture of the vault and reads only from it:

- **active notes** of this node (notes in the trash are not read either) — except notes locked with
  a second password (they are never read
  and never sent; the flag is checked twice: when the picture is built and again before any single
  body is read), except notes subscribed from another node, and except the subtrees of folders you
  excluded in the settings;
- **folders** (empty ones included) with the notes directly in them and in their subtree;
- **tags** with how many active notes carry each;
- for single notes, when a step needs it: the note's **text**, its **vector neighbours** (cosine
  similarity, scored by how far the neighbour stands above the note's own typical similarity), its
  tags, and **how many other notes name its id** in their text (used to keep the trash suggestion
  for an exact duplicate safe).

An encrypted node stores note text encrypted; Dream reads through the same services everything else
reads through, and only while the vault is unlocked. It never unlocks anything itself, and it never
enables a server's auto-unlock for its own sake.

## What leaves the node, and when

**With the model off (the default), nothing leaves the node.** The detectors are local rules; no
model is called.

**With "Use a model" switched on** in the settings, the model steps send fixed inputs through
OpenRouter to the model chosen there (any text model of the chat's model catalogue; empty = the
chat's default text model), using the chat's own stored OpenRouter keys:

- **a pair** of notes: for each, the title, path, tags, dates and up to 8000 characters of text (the
  first 6000 and the last 2000);
- **a folder review**: the folder path, the names of neighbouring and child folders (up to 40), and
  up to 80 notes (label, title, up to 5 tags, the two dates, the first 200 characters);
- **a tree review**: a map of up to 500 lines "folder path, note count", four levels deep. No note
  text at all.

Notes under a second password and notes in excluded folders are never in any of these inputs. The
model has no tools and cannot read anything else. Its answer must be one JSON object; it is checked
against a schema (types, allowed values, lengths), every identifier in it must be one that was in
the input, paths must be canonical, and long fields are cut (summary to 400 characters, quotes to
200). A bad answer gets one retry with the error said back; after that the candidate is recorded as
failed. Note text inside the input is wrapped in markers and the prompts say in so many words that
what is inside is data, not instructions — a note that contains "ignore the above" is just a note.

Calls are strictly sequential, at temperature 0, with the answer length capped. The run's budget
counts **every outgoing request**: a request is sent only if its estimated size (characters / 3 plus
the output cap) still fits the token budget, and is then charged what the provider reports (a
refused request costs a call slot but no tokens; a request with an unknown outcome is charged the
full estimate, because the provider may have run it). The token limit is therefore an estimate, not
an exact bill — the last request of a run can take it slightly past the number. If every key fails,
the client waits 30, 60 and 120 seconds (Stop works during the waits) and then ends the run
`provider_error`. A key refused with 401 is disabled, a key paused by 402 or 429 rests for five
minutes — the same key handling the chat has. No key, request text or reply text is ever written to
a log line or an exception message.

Free text the model writes is kept inside the advice card (the summary and, for pairs, up to two
short quotes). It is never inserted into the text of an action prompt, never handed to Apply, and
never stored anywhere in the clear.

## What Dream may change

Nothing, until you press a button. Every card carries the buttons its advice allows:

- **Apply** makes the change right away, in your own session, through the ordinary services:
  - `move_article` — `ArticleService.MoveAsync` (the note keeps its id; links that go by path do not
    follow, and the new folder's access rules apply — the confirmation dialog says both);
  - `move_articles` — create the target folder if needed, then move each note; a note that changed
    since the advice stays where it is and is listed, the others still move;
  - before either move, the target and the folder each note is in now are checked against the tree
    and the settings as they are at the click, not as they were when the advice was made: the target
    must be a valid path, not a system folder or inside one, and neither may be at or below a folder
    Dream leaves alone now (an excluded folder, found by its id under its current name, or a remote
    copy). If any of that fails, nothing moves at all and the advice expires with the reason. The
    confirmation dialog lists every note the action moves, with the folder it is in;
  - `trash_article` — a soft delete, restorable for 30 days. An exact duplicate is offered this way
    only when no other note names the kept id in its text, or when the reference search is
    unavailable; otherwise the card offers the chat instead. Right before the copy goes to the
    trash the search runs again, fresh (not from the search's 30-second cache); a link written in
    the meantime hands the advice to the chat instead;
  - `merge_tags`, `delete_tags` (only tags that are still unused at the moment of the click),
    `delete_folder` (only folders that are still empty). Each tag and the folder are looked at again
    the moment before they are deleted: a tag given to a note in between is left alone, a folder
    that got a note in between is not deleted and the advice expires.
  - A move of several notes, a tag merge and a deletion of several tags work item by item. If one
    stops part-way (the vault locks, something fails), what it had already done, skipped or failed
    is kept in the advice's last error and shown; the advice is new again, and pressing Apply once
    more does the rest (the items already done are skipped).
- **Open in AI chat** opens the built-in chat in a new browser tab (`/AI?dreamAdvice=<advice id>`);
  the Dream tab stays as it was. Only the advice id goes in the address: the chat page asks the
  node for the task (`GET /api/dream/advice/{id}/prompt?target=chat`) and shows it in the
  conversation as a draft marked *not sent yet*, with the advice title and a link back to
  `/Dream?advice=<id>`. Nothing reaches the model and nothing is saved in the chat history until
  you act: **Run** sends exactly that text as your message, in a new conversation; **Edit** puts it
  into the message box (the box grows up to part of the window while it holds the task, then the text
  scrolls inside it) and it goes when you send it
  the ordinary way; **Cancel** removes the draft. The advice is marked *in chat* only then, after the
  node has checked that it still stands, so opening the tab and closing it changes nothing. If the
  advice was applied, dismissed or expired in the meantime (when the tab loads, or when you press
  Run or send the edited text), a notice in the node's words takes the place of the draft and
  nothing is sent. While the node checks the advice after Run or Send, Cancel, Edit and the control
  that unties the text from the advice wait; a check that comes back after the draft was cancelled
  anyway sends nothing and puts the advice back to new. If the node refuses the message outright (an
  error answer it gives before saving the message: the vault is locked, no model is set, or a sign-in
  is needed), the advice goes back to new and the draft comes back. Any other answer that is not a
  stream counts as a broken send, as below. If the connection breaks, or you press Stop, before the chat has
  confirmed the message, it may already be saved: the advice stays *in chat*, nothing offers to send
  it again, and the text stays on the page with "The connection broke while sending. Check the
  conversation list before sending again." The parameter is
  taken out of the address as soon as it is read, so a reload or a copied link shows a plain chat; a
  malformed id is ignored, and the homepage chat never shows a draft. A link from elsewhere can at
  most show a draft of one of your own advice, written by your node and waiting for Run. The chat
  makes its changes through its usual confirmation cards: the hand-over adds no permission of its
  own.
- **Show prompt** gives a ready text for your own agent: what to do, the ids, titles and paths, the
  stop condition ("if the note changed after that time, do nothing and say so"), and "use only
  BeeMemoryBank tools; touch nothing else; list in the end what you changed". It opens in a dialog
  as a read-only field with a Copy button and changes nothing. The prompt for an
  exact duplicate always has the agent look for notes that link to the copy, and re-point them,
  before it trashes the copy; the number Dream found, fresh, when the prompt was made is a hint.
  On an advice that was already applied or dismissed the prompt is still given, for reference
  (`forReference: true` in the answer; the dialog says so) and without checking the notes again; an
  expired advice has none.
- **Dismiss** hides the advice and records a rule so it does not come back: this advice only,
  anything of this kind about the same note, this kind in a folder's subtree, or this kind
  everywhere; 180 days by default, or without end. The folder of "this kind in a folder" is the
  folder the advice is about (where the note is, the folder to rename or delete), never where it
  would send something: moving a note from elsewhere into that folder can still be suggested. The rules are listed in the settings page and can
  be deleted there.
- **Mark done** — for something you already did by hand.

Renaming or merging folders and free-form structure notes are prompt-only on purpose: outside tools
and agents write into folders by path, so the product never renames a folder itself.

**An advice closed in another tab.** Every button on an advice that is no longer open (applied,
dismissed, marked done, expired, or being applied right now) answers `409` with the usual `error`
and `code` (`expired` for an expired advice and for one that counts as done after the chat,
`not_applicable` otherwise) plus `status`, `statusAt` and `statusNote`, and a sentence that says
what happened and when: "This advice was already applied on 9 Nov 2026, 18:30 UTC.", "... dismissed
on ...", "... marked done on ...", "This advice expired on ...: <reason>", "A note it is about
changed after it went to the chat, so it counts as done (...)". The page says it at the button in
the person's own time and redraws the card. When the tab becomes visible again the page reads the
state, the open advice and the history again (at most once in five seconds), redrawing only what
changed, so an open dialog or an open history row stays as it was. Apply and **Open in AI chat** are
never offered on a closed card.

**A link to one advice.** `/Dream?advice=<id>` (the chat links back this way) shows that advice at
the top of the page, marked and scrolled into view, whatever its status, with **Show all advice** to
go back to the whole page. An id Dream does not know gives a short notice instead.

Before anything uses an advice — the list, Apply, a prompt, the chat hand-over — Dream re-checks its
**preconditions**: the notes it names still exist and have not changed since (by their sync
timestamps), folders still exist and are still empty, tags still exist and are still unused. A note
that changed makes the advice **expired** with the reason on the card. Applying twice cannot apply
twice: the card is held while the first click runs, by that click alone (a token of the attempt). A run never repeats itself either: two identical
proposals collapse into the advice already on the list.

## Where Dream keeps its records

Six tables of their own in `chat.db`, next to the chat's tables — `chat.db` because it is local to
the node, never in a snapshot or a sync event, and sealed page by page on an encrypted node. The
vault database and its schema number are not touched, and blind nodes have none of it.

| Table | What is in it | Sealed? |
|---|---|---|
| `dream_settings` | the one settings row | no (numbers and switches only) |
| `dream_run` | one row per run: trigger, mode, state, outcome, phase, counters, limits | no (numbers, ids and enumerations; an error text never carries a title or note text) |
| `dream_advice` | one row per advice; **the payload is sealed**: titles, paths, tag names, the explanation, quotes, the fingerprint | yes, under the chat key |
| `dream_rule` | dismiss rules (what they suppress, the reason in your words) | yes |
| `dream_run_event` | the detailed journal of a run (phases, candidates, calls, skips, errors) | yes, kept **7 days**, never note text |
| `dream_examined` | "already examined, do not pay twice": a hash of the task and the note ids, a hash of the content it covered, the verdict | no (hashes of ids only, nothing secret) |

Sealed means sealed with the chat's row scheme under the **chat key** — the same key the chat
transcripts use (see [encryption.md](encryption.md), "Node data keys and chat.db"). On an unencrypted
node `chat.db` is a plain file; that is exactly why everything a person could be named by (titles,
paths, tag names, explanations) sits only in sealed columns, and the plaintext rows hold nothing but
numbers and ids.

A sweep runs every five minutes and keeps the tables from piling up: the journal older than 7 days
is removed; advice and journal lines that name a **hard-deleted** note (gone for good) are removed;
advice stuck in "applying" for more than 10 minutes goes back to new, and the attempt that held it
can no longer change anything or mark the advice (it stops at its next step); a run whose process died (no
heartbeat for 3 minutes) is marked `interrupted`; and rows the current chat key cannot open are
deleted — advice is derived data, and one unreadable row would block the pre-check of an offline key
change.

## The limits

All of these are in the settings page, and all of them always apply:

| Limit | Default | Allowed |
|---|---|---|
| New advice per nightly run | 10 | 1–500 |
| New advice per manual run | 50 | 0 = no limit, up to 1000 |
| Open advice before a nightly run is skipped | 100 | 1–10000 |
| Lowest score kept | 40 | 0–100 |
| Manual run time limit | 30 min | 0 = no limit, up to 24 h |
| Model calls per run | 200 | 1–10000 |
| Tokens per run (input + output, an estimate) | 1,500,000 | 1000–100,000,000 |
| Nightly window length | 120 min | 15–720 |
| Excluded folders | none | up to 200 |

One run at a time; the model calls are sequential; the open-advice cap stops a nightly run from
piling work on top of an unworked list. The score bands on the page: **high** from 80, **medium**
60–79, **low** below 60; the advice below `min_score` is not kept at all.

## The nightly schedule

Off by default, and it is switched on per node. When it is on, Dream starts once inside the daily
window (node-local time, 03:00 by default) if: the vault is unlocked, no run is active, no heavy
operation is going on, and the list of open advice is shorter than the cap. A window that passes
without a start writes one `skipped` record with the reason, so the history says why. With several
full nodes, switch the schedule on on **one** of them: each node that runs it spends its own budget
and keeps its own list.

## Key change, node reset, snapshot, restore

- **Master-key rotation (DEK rotation).** Dream rows are sealed under the chat key, not the master
  key; a rotation re-wraps that key and reads Dream on unchanged. A run that is working when a
  rotation, a restore, a compaction or a node encryption begins ends `heavy_operation`.
- **Offline key change** (the recovery-key re-key). Every dream table is listed for the re-key and
  its sealed rows are re-encrypted under the new chat key, the same way the chat's own rows are.
- **A lost chat key** (restoring a snapshot old enough to predate the key, or a damaged key row —
  the node creates a fresh one): dream rows under the lost key cannot be opened; the sweep deletes
  them, for the reasons above.
- **Node reset** (wipe). The five data tables are emptied and the schedule is switched off. The
  settings row itself survives (limits, model choice, excluded folders) — harmless while the
  schedule is off, and a reset node is a different vault.
- **Snapshots and restore.** `chat.db` is not part of a snapshot, so a restored node brings no
  Dream records back. Advice about notes the restore removed or changed expires through its
  preconditions; the examined registry hashes the content it covered, so a changed note is worth
  another look and is re-examined instead of being skipped. Nothing Dream holds ever reaches
  another node.

## Related articles (better for everyone)

The Related block on an article page, the REST endpoint `GET /api/articles/{id}/related` and the
MCP tool `bee_get_related` give a short, explainable list instead of the old count of shared tags:

- a shared tag counts by how **rare** it is, `ln((N + 1) / (df + 1))`; a tag carried by more than
  `max(20, 5% of the vault)` notes is a commonplace and weighs nothing;
- notes that stand out by **meaning** (the 20 nearest vectors that are far enough above the note's
  own typical similarity) are included even without tags;
- the two are combined as `0.6 * tags + 0.4 * meaning`, each scaled by the best row of the list;
  `strength` is 0–100;
- every row says **why**: up to three rare shared tags, "similar meaning: 87%", and a
  "possible duplicate" mark at 95% similarity or more;
- the list is 7 rows by default, 20 at most (`limit`); rows with nothing behind them are dropped;
  the caller's folder restrictions are applied before the list is cut, so a note someone may not
  read never fills a slot or shows a title; notes under a second password have no vector and are
  never meaning neighbours; while the vault is locked the meaning part is skipped (tags only), the
  same rule ranked search follows.

`bee_get_article`'s `relatedCount` and `relatedStrength` remain a cheap tags-only summary;
`bee_get_related` is the ranked list. `bee_list_tags` without a filter sorts by note count, as its
description always promised.

## Trial runs and comparing models

A **trial run** is for comparing models, not for work: it ignores the examined registry in both
directions (it re-looks at candidates a previous run already judged, and records nothing as
examined), and its advice stays out of the open list. Its advice is otherwise a full citizen: it can
be applied, dismissed or handed to the chat.

A trial run may carry a **sample**: a seed and optional caps for pairs and folders. The seed fixes a
deterministic order of the candidates that does not depend on the model or the time, so two or three
trial runs of different models look at the same candidates in the same order and the fast model
simply gets further. The **Compare trial runs** section of the Dream page lays two or three of them
side by side (columns A, B, C; the model names hidden until you ask): per common candidate each
run's verdict and score, the share of candidates they agreed on, and each run's calls, tokens and
advice. The comparison reads the runs' journals, so it is available as long as those live — 7 days.

## Where the code lives

`server/BeeMemoryBank.Api/Dream/` — contracts (`Contracts`), the six-table store (`Storage`), the
run engine and the scheduler (`Engine`), the rules without a model (`Detectors`), the model client
and the pair/folder/tree tasks (`Model`, with the versioned prompts in `Dream/Prompts/*.md`), and
the apply/validate/prompt building (`Apply`). The routes are `/api/dream`
(superadmin-only, never an agent); the pages are `/Dream` and `/Admin/Dream` in the Web project.
Nothing of it exists in `libs/BeeMemoryBank.Sync`, in snapshots, or on a blind node.
