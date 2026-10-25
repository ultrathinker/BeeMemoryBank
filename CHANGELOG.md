# Changelog

All notable changes to BeeMemoryBank will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

#### 2.7.3 (2026-10-25)

A small fix release. Nothing is added to the data, the protocol or the database schema (it stays at version 42), and no action is needed for databases or blind copies.

### Fixed

- **Desktop apps: the connection snippet for an AI assistant printed an address that does not work.** In the Windows app, the Windows service and the macOS app, the Profile page showed the port of the app's internal web process (a random port) in the snippet that connects Claude Code or another assistant to the node over MCP. That process has no `/mcp`, so the assistant reported 404. The page now prints the address the browser used, which is where the app answers (port 5310 by default). A snippet you already pasted is repaired by replacing the port in it with 5310, or by creating the snippet again. Docker and reverse-proxy setups are not affected.
- **Admin > Blind nodes: the address a friend's device will call.** The invitation form started from the same internal address and now starts from the address the browser used.

#### 2.7.2 (2026-10-23)

A fix and tidy-up release for the web pages of Admin. Nothing is added to the data, the protocol or the database schema (it stays at version 42), and no action is needed for databases or blind copies. Two things change on the screen. First, an Admin page that shows a wide table, such as Trusted Nodes with its nine columns, was held to the width of the reading column (880 pixels), so the table scrolled sideways inside its card even on a wide screen; such a page now uses the width the window has, up to 1240 pixels, while text and the other cards keep their width. Second, six functions that opened a second page from a button now live on their own Admin page: you no longer click through to another page to do the work.

### Fixed

- **Admin: a wide table fits on the page.** A page whose table sits directly in a card is no longer capped at 880 pixels; it takes up to 1240 pixels or the width of the window, whichever is less. In a window narrower than the table, the table still scrolls inside its card.
- **Admin: the OS auto-unlock switch is in one place.** It was on two pages (Database encryption and OS Auto-Unlock); it is now only on OS Auto-Unlock. The Database encryption page no longer repeats an element id.

### Changed

- **Admin: no second page behind a button.** Blind nodes (with the Android blind copy card), Connect a device, Hard Delete, Database encryption (Storage protection) and Trusted Nodes (granting superadmin to devices that joined with the password, now a card on that page) each hold their whole function on their own Admin page. The old addresses (/BlindNodes, /AndroidBlindCopy, /Connect, /HardDelete, /VaultEncryption, /PromotePasswordPeers) still work and send you to the new page.
- An error on the Blind nodes page lists the devices to blame under the page title.

#### 2.7.1 (2026-10-22)

A fix release. Two processes that start at the same moment on a database that still has to be moved to the 2.7 schema (for example a node and a command-line tool started together by a script) could both write the same step into the migration ledger, and one of them stopped with an error. The runner now reads the ledger again once it holds the write lock, so the second process finds the work done and goes on. Nothing else changes: no new feature, no new step in the schema (it stays at version 42), and no action is needed for databases or blind copies.

### Fixed

- **The migration runner is safe when two processes start together.** After it takes the write lock it asks the ledger again, so a step that another process has just applied is skipped instead of failing on the ledger's unique key. A test starts several runners at once on a database that needs steps 041 and 042.

### Changed

- Tests only: the rows of the Admin template guard have distinct names, the check of the OS auto-unlock words runs on a Mac only, and every blind hub test starts with a full challenge budget.

#### 2.7.0 (2026-10-21)

The blind copy (Android, iPhone, Windows, macOS) can hold the copies of several memory banks, is added from a one-time invitation, and can be limited. **A blind copy you already have keeps working as it is:** it becomes the first account of the app, in place, with no limits and any network, until you set some. A server blind node (Docker, service) gets two limits of its own and can join with the same kind of invitation by one command. The database schema goes to version 42 (see "Notes for upgrading"). What this release does not do is at the end of this block.

### Added

- **One blind app can hold the encrypted copies of several memory banks.** Each memory bank (a friend's, a family member's, your own second one) is an **account** with its own folder, database, keys, sync state and log; one account never reads or writes the data or keys of another. The device still shares the list of accounts, its clock, its network address and its own device-wide limits. A device holds up to 8 accounts. On Android and iPhone the start page lists the accounts, on Windows and macOS they are on the left of the window; each account can be paused, renamed and removed, and shows its state in one line ("Synced 4 min ago", "Paused: ...", "Waiting for ... to confirm"). On Windows and macOS the tray or menu-bar tooltip sums them up ("3 copies - 1 paused - 1 needs attention").
- **Invite a friend's device from the Blind nodes page.** On your full node open Admin, Blind nodes, then **Blind nodes**, and use **Invite a blind copy**: a note for yourself, the name your friend will see, the address their device will call, and how long it works (15, 30 or 60 minutes; 30 by default). The page shows one line of text once, with a Copy button and a QR code, and you can withdraw it until it is used (at most 20 are open at once). Before it makes one, the page calls its own address the way a blind copy will and says what is wrong if that fails (for example a router that does not loop the public address back). An invitation does not need the node's switch "Let blind copies call this node" (Admin, Trusted Nodes): that switch is for pairing with codes, for the list of nodes a copy may call and for the network's record of the node's certificate.
- **The invitation is a one-time code, not an encrypted message.** It works for one device, for the time you chose, and carries no vault password, no master key and no reusable token. It does carry your node's public key and a one-time secret. It names your node and signs that with your node's key, so the app calls only the node you meant and refuses a line that was edited or cut. Anyone who gets the line within its time can ask to join; that is why you confirm before anything of your memory bank is sent. Until you confirm, no vault content and no usable key of your memory bank reaches the device. Asking to join already sends your node the friend's device id, public key, name, platform, version and the key of its own backup; your node keeps that backup key encrypted until you decide.
- **You confirm each new device with a match code and your master password.** The friend's app shows a short code such as `K7F-2QX`; the Blind nodes page shows the same code under "Waiting for your confirmation". Confirm only when your friend reads out exactly that code, with your master password (or refuse). A device you did not expect is one click away from refused. You have 72 hours to answer. The same device may ask again and gets the same pending answer only while its request is still waiting for you; once you have decided, or the 72 hours are over, it uses its signed status request instead. If the confirmation is interrupted (a restart, a locked node), your page shows the device as being added; the next time the node is unlocked it finishes the job or takes it back.
- **Add a copy from an invitation.** On the **Add account** screen paste the line (on a phone, opening a `bmb-invite:` link does the same, and the Camera app opens the QR code). The app shows whose copy it is, which limits it will start with and how long the invitation still works, and adds nothing until you press Add. It checks that the node at the address is the one named before it sends the one-time secret. If your node is locked or busy, the app waits and tries again until the invitation runs out. If an answer is lost on the way, the app asks again with the same identity while the request still waits for your decision, and otherwise asks for the state of its request, so the invitation is not lost. An invitation to a node older than 2.7 is refused with a sentence that says so.
- **Limits for each copy and for the device.** An account can limit what it stores, what it transfers a day and how many requests it makes a day, can be set to "only on Wi-Fi or other unmetered networks" and "pause on battery saver", and can be paused by hand. The device has its own limits for storage and traffic a day over all accounts. An empty box is no limit and **0 pauses**; fewer than 200 requests or 10 MB a day is refused, because a sync every 15 minutes could not keep up. A day is the device's local day, and a clock set back does not give a day back. A limit holds at the moment a download arrives; a download that cannot fit is refused before it starts (what the app itself writes to disk can pass a storage limit by one batch). A new invited account starts with 2 GB of storage and 200 MB a day and no limit on requests; on a phone it also starts with both switches on, on a computer with both off.
- **A reached limit is a pause, not a failure.** The friend sees "Paused: daily traffic limit reached (200 MB). Resumes at 00:00." and nothing is retried in a loop; a download usually goes on where it stopped (a pause in the middle of a complete download starts that part again from its beginning). A daily traffic or request pause ends at the device's next local midnight; a storage pause lasts until the limit or the room changes (pressing Resume does not clear it); a manual pause lasts until Resume. You see it too: the Blind nodes page and the network map say "Paused by the device's owner: daily traffic limit (until 21:00 UTC)" instead of "silent", as a banner and never as a notification. Only the reason and the end are sent, never the friend's numbers, and a copy that only waits for Wi-Fi or for the battery is not reported. If the usage counts of a device are damaged or locked, the account is paused with that reason instead of getting a fresh day; **Start new counts** keeps the damaged file aside and starts again.
- **The two switches read what the device says.** Android asks for a metered network and battery saver, an iPhone for an expensive or constrained network and Low Power Mode, Windows for the connection cost and Energy Saver, macOS for an expensive or constrained network and Low Power Mode. A device that cannot say counts as "yes" and the account waits.
- **Remove an account.** The confirmation names what goes; removing deletes the copy, its keys and its state from the device and tells your node when the device is online, which then shows "removed from the device by its owner" with a **Revoke** button (it does not revoke by itself). A removal that the app's end cut off is finished at the next start; if a key or a setting cannot be deleted, the account says what is left and can be removed again.
- **A copy you removed from the network says so.** When your node stops knowing a device, its copy shows "The owner removed this device from the network. The copy stays until you remove it." instead of failing again and again. It says this only after your node has refused it **twice**, at least 15 minutes apart and within 24 hours. The first refusal is shown as "The source refused this device once; asking again." and the copy goes on at its normal rhythm; any contact that goes through clears it (a finished sync round, a finished first load, or a check for a newer baseline that your node answered; "Try again" and a new pairing start from nothing). So a refusal that does not come from your node (a proxy or firewall in front of it that says no for a moment) does not end a copy's work, and if it refuses twice, **Try again** puts a healthy copy back. The count uses the device's clock: a clock corrected forward by 15 minutes or more between two refusals counts them as two.
- **The app refuses a data folder it cannot trust.** A symbolic link, a junction or a file where a folder belongs is not followed: the app looks before it opens, says so and starts nothing from it (an account whose own folder is a link is marked "Needs repair"; a swap made between the look and the opening is not detected). Only one copy of the app can use a data folder at a time. If the list of accounts and the folders on the device disagree, nothing is started and the app lists the differences with one explicit repair for each.
- **A server blind node (Docker, service) can be given two limits by its owner.** `BMB_BLIND_MAX_STORED_BYTES` caps what the node stores for the network and `BMB_BLIND_MAX_TRAFFIC_PER_DAY` caps what it moves in the node's local day; both can also be edited on the node's local console, and `bmb blind status` shows them with what the node holds and moved today. An empty or unset variable is no limit and **0 pauses**; a daily traffic limit below 10 MB (other than 0) is refused. A node that is started with such a value, or with one that is not a number, does not start, rather than serve without the cap its owner believes in. A variable that is set is applied again at every start, so it wins over a console edit; a variable left unset leaves a limit set on the console alone. The node still has no app accounts and no device-wide limit.
- **A reached limit of a server blind node is a pause.** The node answers the requests that move the network's data, reads as well as writes, with a "paused" answer (`503`) that names the reason, storage or traffic, and when to ask again (the next local midnight for traffic, an hour for storage), and nothing is written. Its identity, sign-in, status and console still answer. A current full node takes that as a pause and not a failure: its page and map say "Paused by the blind node's owner: storage limit" (or "daily traffic limit"), it asks again when told and does not raise an alarm. **An older full node does not know the pause**: it sees an ordinary 503, backs off as for a failing node and may show the node as unreachable until the pause ends. A refusal that only rests on other requests' reservations is a short "busy" answer (a minute), not "reached until midnight", and a full node shows it as busy, not as the owner's pause.
- **How a server node's limits hold.** They are an agreement with the person who hosts the node, not an operating-system quota: a process that can write the node's data volume can change the data or the counts, and a request that is already admitted finishes, so a cap can be passed by the bytes that the requests in flight when it was reached move beyond what was reserved for them. A request reserves its announced length (1 MiB when it announced none) once the node knows who is asking. A client that has not authenticated holds nothing, and no body is read before that except the small ones of three routes, bounded first (256 KiB for a list of blob hashes, 16 KiB for a restore claim, `413` above). Every request that holds anything has a lifetime: half an hour plus the time its size needs at 64 KiB/s, counted from when it was authenticated. After that it is aborted and keeps counting until it has ended, and a handler that is still stuck two minutes after the abort is settled without it, never left to go on uncounted. The stored size includes what peers make the node stage: a seed is judged as a whole at its first part against the room that is left (a reseed needs room for the old and the new data), one that would take the node past its storage limit is held back as a pause before a byte is staged, and an interrupted upload is dropped after an hour. A current full node shows "paused by the blind node's owner" instead of a failed seed and does not build and upload the package again before the time the node named.
- **Wrong restore codes no longer revoke the blind node's open codes.** A blind node used to count every wrong code against every open restore code and revoke them all at the fifth, so anybody who could reach it could burn the code of a device that was about to restore, with five requests and no token. Wrong codes are now counted per source address: five in five minutes, then `429` with `Retry-After`, before the next code is looked at (the same on the package, events and claim routes; a claim whose body is not valid counts too). A right code costs nothing. The name of a restored computer is at most 200 characters (the setup page, the restore client and the endpoint hold to it), so the claim after the restore has committed cannot be too large for the blind node.
- **A server blind node joins a full node with `bmb blind join <invitation>`.** Instead of copying a pair code from the node to the full node, the friend who runs the node pastes the owner's invitation into one command on the node's machine (`-` reads it from standard input, so it is not in the process list). The command shows who made the invitation, where it points and how long it works, asks, and then prints the match code; the owner sees the same code on the Blind nodes page, in a row marked "Invited (server)", and confirms with the master password, which adds and seeds the node. The command waits for the answer (`--status` shows it later, `--cancel` gives the join up). The node's pair code travels inside the request instead of a backup key, and the full node keeps it encrypted only until the owner decides (72 hours at most). If the seed fails the page keeps the row and the full node repeats the step by itself; if it never works, the node is taken back out and nothing stays half done. The old pairing with a pasted code still works; a node uses one of the two at a time, and a node that already holds a full node's seed refuses to join. A full node older than 2.7 refuses the request and the command says to update the full node first. If an answer gets lost (a dropped connection, a timeout) the join is still there to confirm: run the same command again with the same invitation, or look at `--status`. A wipe stops a join that is waiting and a seed that is being received, and the join commands are closed to agent keys.

### Changed

- **"Disconnect and wipe" is now "Remove this account"** in the blind apps. With several accounts nothing restarts the app, and an account made by an invitation cannot be made again by wiping it.
- **The notification of a long Android job does not name the copy on a lock screen that hides sensitive content.** There it shows "Blind copies" with no name and no figure; unlocked, it names the copy. A scheduled pass with nothing to do no longer shows a notification at all.
- **On an iPhone each account syncs less often in the background.** iOS gives the app a short time per wake; each wake serves the accounts in turn, the one that synced longest ago first, and one background round runs at a time, never beside the open app. The open app syncs all of them. The "no contact" reminder is one per account and repeats every day while the silence lasts.
- **Database schema version 42.** Two additive steps: migration 041 adds a table on the full node for the invitations, and migration 042 adds two columns to it that say whether an invitation is for an app or for a server node and hold a server node's pair code until the owner decides. The table is never synced and is not in a snapshot or a package.
- **The Admin page is a list, and every block of it is a page of its own.** `/Admin` used to be one very long page with about two dozen blocks, all loaded at once. It now shows only a list of 25 items under six headings (Nodes and network, Backups and history, Security, Search and AI, Software and appearance, Danger zone), each with an icon, a title and a line on what it is for. Click an item to open its page. Every page is built the same way: the title and an icon, then a description of what the thing is and what it is for (as long as it needs to be; several paragraphs for the dangerous ones, such as Reset This Node, Hard Delete, DEK Rotation and Metadata encryption), then its buttons or its information, and a **Back** button that returns to the list. Every button works as before and tells you what came of it on its own page, right under the title. The warnings that used to sit at the top of the long page (another node changed the master password, other nodes' data-key rotations waiting for you, devices a restore left waiting for your confirmation) are on the list, and on the page where you deal with them. Each page loads only its own data, so the list opens at once. The recovery key and the tag-graph check, which were tucked inside other blocks, have pages of their own, and the long explanation of event-log compaction moved from a pop-up to its page. Old bookmarks such as `/Admin#card-snapshots` still lead to the right page. Where texts and docs said "Admin, Nodes" or "Admin, Security", they now name the page: the messages after a failed join (Admin -> Trusted Nodes), the note after `bmb user reset-password` (Admin -> Master Password), the blind-node error text (Admin -> Search diagnostics) and the firewall text of the Windows installer (Admin -> Devices on my network). The Lock Server page no longer says that only a superadmin signing in opens the vault again: it names the other two ways, an enabled agent key and OS auto-unlock after a restart. The confirmation of OS auto-unlock names the account of the system the node runs on (macOS or Windows), not Windows alone.
- **A wrong master password at Reset This Node says so in words.** The page used to show the raw reply of the server (`{"error":"Invalid master password"}`).

### Fixed

- **Resetting a blind copy now also removes its stored baselines and usage counts.** They stayed behind after a reset.
- **Removing or wiping a blind copy no longer races its own work.** A long job of a blind copy (the first load, a baseline update, a backup) could still be writing its closing line to the log when the copy was wiped; the wipe now waits until the line is written. And when an account was removed from the app, the work it had running for its invitation was stopped only a moment after the removal returned, so a request to join that was already on its way could still use the invitation of the account being removed; it is stopped as the removal is made now. A removal whose first write to the list of accounts fails because the file is held for a moment is tried again instead of leaving a half-made account, and an iPhone background wake no longer writes its outcome into an account that is being wiped.
- **The "removed by the owner" rule starts from nothing when it should.** "Try again" and a new pairing clear a remembered first refusal; a refusal that reaches a copy your node has already removed is no longer remembered; a check for a newer baseline that your node answers counts as a contact; and the account card shows a first load that failed after a refusal instead of "asking again". A burst of refused first-load retries writes one log line.

### Notes for upgrading

- **Update the blind nodes of a network together with its full nodes.** A 2.6 node asked to join from a 2.7 node, and a 2.6 server blind node asked to take a seed from a 2.7 node, answer "upgrade this node first". A full node older than 2.7 cannot make invitations (the old pairing with two codes still works).
- Make a snapshot (Admin, Snapshots) before updating. After the first start of 2.7.0 a database holds migrations 041 and 042 that a 2.6 program does not know and refuses; to go back, restore the snapshot made before the update.
- A blind app you already have becomes account 1 with no limits and any network. Nothing is asked.

### What this release does NOT do (known limits)

- **Phones.** The Android app has no scanner of its own in the default build: paste the line, open the link, or scan the QR code with the Camera app; Windows and macOS take pasted text only. The iPhone app is still source-only, and it was not tested on a real iPhone for this release (only on the simulator).
- **A limited copy goes stale.** A copy that is paused for a long time falls behind, so do not count a friend's limited copy as your only copy off site. The device's limits are counting inside the app, not an operating-system quota: a program that can write the app's folder can reset them.
- **A server blind node** still serves one memory bank per container (run one container for each) and has no app accounts or device-wide limit; its limits are an agreement with the host, not a quota, and if its settings file is damaged or unreadable the limits count as none until the file is repaired.
- **Aborting a slow request** was proven with a test server, not on a real Docker node; run one manual test before relying on it. A request that hangs for good still costs a connection and a thread until it returns or the node restarts.
- **Guessing a restore code** is limited per source address, not per person: behind a proxy that hides the client's address the callers share one budget, and the counts are lost at a restart (a code is 80 random bits). The wait before a held-back seed is tried again is kept in memory, so a restart of the full node ends it.
- **An invitation is a one-time code** that works for whoever holds the line within its minutes; the match code and your master password are what stand between that and a copy. A friend who pastes a line someone else wrote joins that person's memory bank.
- **Rare start problems under load.** A sealed vault folder held for a moment by a virus scanner or backup program is reported like a foreign or damaged one ("cannot start"), and one "Encrypt this node" failed once after waiting 30 seconds for background work on a very busy machine; start again.
- **Older programs:** an older full node shows a server node's pause as a failure; a 2.6 program cannot open a 2.7 database.

#### 2.6.4 (2026-10-20)

The header's Sync Status opens a network map, a protected article can stay unlocked for a time you choose, and a browser can no longer keep the page scripts of an older version. The database schema does not change from 2.6.3.

### Added

- **A network map.** Click **Sync Status** at the top of the sync dropdown in the header to open a new **Network** page: this node and every node it knows, on a circle (two on a line, three a triangle, four or more a circle), with an arrow for every call between them, one-way or two-way. Point at an arrow to see the address and port it goes to (and, for a call to this node, where it came from); point at a node to see what this node knows about it: what it runs on, its address, version, how it stands, how many notes it holds (or how many changes are still to be delivered to it), when it was last heard from. A node is drawn as what it runs on - a Windows PC, a Mac, a Linux server, an Android phone or an iPhone - with a Docker badge for a node in a container and an eye badge for a blind node. Click a node or an arrow to keep its card open. The arrows this node measures itself (the calls it makes, the calls it receives) are solid and coloured by how they go (answering, no answer, not heard lately); the calls between two other nodes cannot be seen from here and are dotted, "expected by the network's rule". Nodes tell what they run on in their sync identity and when they call (`platform`, `inContainer`; additive, so an older node simply is drawn as a server or a device until it runs 2.6.4). Superadmin only, like the sync status; `GET /api/sync/network`.

### Changed

- **The Favorites block in the web sidebar reads as a folder.** Its header now sits where a top-level folder row does, and the starred articles are indented one level, in the same columns the tree gives an article inside a folder, so a protected article no longer looks like a sibling of the block. The block also has some room above its header and more below the last article.

- **The sync status shows two lines a node.** In the header dropdown each node is its name (with ONLINE, or a red NOT ANSWERING) and when it was last heard from; everything else (what the node answered, how many changes are waiting, the delivery bar, Check now) is under a chevron. A node you ask with Check now (or a double-click) opens to show the answer. The line that explained how to check a node is gone. A device that has no address and only calls this node is said to have changes "waiting for it to call", not "not delivered".

- **The editor of a protected article shows how long its password is still remembered.** Over the text it says Temporarily unlocked · locks automatically in N min, counting down as the article page does, and when the time is out it says that saving may ask for the password again and that what you wrote stays. The time is 15 minutes unless the new setting BMB_PROTECTED_UNLOCK_SECONDS (30 to 3600) says another.

- **You can choose how long one protected article stays unlocked.** The strip over the article (in the editor and on the article page) has a **change** link next to the time. It opens a slider from 15 minutes to 8 hours, in steps of 15 minutes; when you let it go the time applies at once, the slider goes away and the strip counts the new time. The time belongs to that one article, is kept when you go from the article page to the editor and back and when you save, and ends when the article locks (Re-lock, the time running out, locking the vault): the standard 15 minutes apply again. It is for the web and the desktop apps, not for the phone apps.

- **A protected article's editor says what such an article cannot do.** Instead of the attachments block simply not being there, a list under the editor says: no attachments or pictures (files are kept under the vault key, not under the article's password); comments are allowed but sealed with the password and readable and writable only while the article is unlocked; search does not look into the text; AI assistants and MCP tools cannot read or change it; export leaves the text out; earlier versions are not shown; no draft autosave; the password cannot be recovered. A new article shows the same list as soon as a password is typed.

### Fixed

- **After an update the browser could keep running the page scripts of the version before.** The files of the installed app all carry the same old "last modified" date, so a browser told nothing about freshness kept a script for years: the **change** link of the unlock strip did nothing (it only added "#" to the address) until the cache cleared by itself. Every script and style of a page is now asked for as `?v=<hash of its content>` (the address changes whenever the file does, with nothing to raise by hand), the server lets a `?v=` file be kept for a year and makes the browser ask again for a script or style asked for without it (a 304 when it did not change), and a test fails for a page tag without the version.

- **"N changes not delivered" and a half-full delivery bar for a node that wrote those changes itself.** A push never sends a node its own events back, so a node whose events were the last in the log stayed at the same position for ever: the sync status counted them as undelivered ("151 changes not delivered", a bar that never filled, "last delivery" weeks ago) although nothing was left to send, and Check now could not change it. The count now covers what a push to the node would really carry, and the delivery position moves past the node's own events once nothing else is owed ("last delivery" still tells when something was last sent). The same stuck position made compaction think the node was far behind.

- **Saving a password-protected article after its password was forgotten no longer ends in a red message.** The node remembers the password of an article you unlocked for a quarter of an hour. Editing for longer and pressing Save used to fail with "400 ... a passphrase is required" at the top of the page. The page now asks for the password in a dialog over the editor, which keeps everything you wrote. **Cancel** closes the dialog and gives the editor back (it can still be edited and copied from); the right password saves what is in the editor; a wrong one says so in the dialog.

#### 2.6.3 (2026-10-18)

A node is now encrypted from its first day, long operations say what they are doing, and the database schema is folded into one baseline. **Updating from a version older than 2.6: update to 2.6.1 first, then to 2.6.3** (see "Notes for upgrading").

### Changed

- **A new node is encrypted from its first day.** A node you set up (Web Setup, `bmb init`, Docker, a join, the restore wizard) is created encrypted, under the master password you gave, with metadata encryption on. A new node joins only a node that already has metadata encryption on, and a restore never reads an unencrypted copy and never produces an unencrypted node. The "way back" snapshot that "Encrypt this node" made in 2.6.0 (and its block in Admin) is gone: going back to an unencrypted node is not possible from the node, and the warning before encryption says so.

- **One database baseline.** The database schema is now one baseline migration instead of a chain of 39. A 2.6.0 or 2.6.1 database is adopted automatically at its first start: its structure is checked in full, missing indexes and triggers are repaired, nothing is re-run. A database (or a backup) older than 2.6 is refused untouched with a message that says to update through 2.6.1 first. Restoring a backup refuses one whose structure this version cannot open, before anything is replaced. A program of 2.6.1 or earlier no longer opens a database that 2.6.3 has taken over: it stops with a message instead of replaying its migrations. To go back, restore the snapshot made before the update.

- **Admin shows what is left to do.** Once metadata encryption is on, its card is one line ("On since ..."); on an encrypted node the encryption page is "Storage protection" and does not offer "Encrypt this node". The result of an action shows in the card of its button, and the page opens at that card. Master password fields are password fields. "Activate the new sync format first" now names the real place (Admin > Metadata encryption) and links to it.

- **Last contact says what really happened.** Trusted nodes and the sync list show when each device was last in contact, in which direction, and since when and why one does not answer; a device that only calls this node is said to call it. A node no longer shows another as online on a fixed five-minute rule and says "not checked since this node started" after a restart. Double-click a device in the sync list to ask it now.

- **Long operations show a "please wait" pop-up.** Every button that starts something long (adding a blind node, encrypting a node or a phone, snapshots and backups, restores, key changes, updates, imports and exports, wiping) shows the pop-up, cannot be pressed twice, and shows the result, or the whole error, where the button was. Short actions disable their button and spin. The same holds in the desktop app, the Android and iPhone apps and the blind node's console page. A second identical long request is refused with HTTP 409 `already_running` instead of starting a second copy.

- **Snapshots need less memory.** Making or restoring a snapshot of a large database needs about one copy of it in memory instead of two or three. A node that does not have the memory says so before it starts, names the memory limit that would be enough (for a container: its memory limit) and changes nothing, instead of failing with an out-of-memory error.

- **Failure messages no longer carry other people's words.** Screens, answers, statuses and logs of join, restore, update, node check and the phone apps tell a failed call as a kind of failure ("no answer", "refused", "not a sync server"), never with the text of an exception or of the other computer. A peer's address has one rule and one writer, and every call to a peer connects only to addresses that were checked after name resolution (link-local, unspecified and cloud-metadata addresses are refused; loopback and the local network stay allowed).

### Added

- **"No sync server at this address."** The Android app and every node now say so when a peer's address answers 404 or with something that is not a sync server, instead of "ok (applied 0)". "Sync now", the background status, "Edit URL" and "Change node address" on the Trusted nodes page say it. A wrong address no longer fills the log with a warning every minute: one warning per streak, then growing pauses. A node's address is changed through one checked path on the server and on the phone (a sync server with the right node id and key must answer); the old route that changed an address without a password is closed.

- A node that refuses a blind-node seed now says why in its own log.

### Fixed

- A vault made before the master-key check existed gets that check at the next password unlock, and "Encrypt this node" no longer blames the key slots when the check was what was missing. An empty check value counts as missing at every door a vault is opened through.
- A setup, join or restore that is cut short is wiped at the next start instead of being left open; a leftover or foreign setup marker can no longer erase an existing node; a restore refuses a data folder that holds an open copy of the vault. A data folder, database, boot store or working folder that is a symbolic link or junction is refused by name, and nothing is read, moved or wiped through a link.
- Windows: encrypting a node, and importing a sealed snapshot, no longer fail at random with "Could not move chat.db".
- A snapshot that authenticates but holds no readable database is refused with a clear answer; the check of an encrypted copy fails when a table or the search index cannot be read; staged archives are written whole and honour cancellation. Restore and sealing measure memory against the real limit of the process (container, job object) and refuse with a clear 507 instead of crashing.
- A long operation keeps its button locked until the work has really ended. The server refuses a second identical request for folder exports, folder copies, hard deletes, password changes and the metadata activation; a network restore keeps its guard while it runs in the background, and a folder copy, a restore from the trash or a password change stops, and undoes what it made, when the client goes away.
- "Cancel" of a proposed key rotation no longer submits the confirmation; "Encrypt this node" asks for confirmation again. A refused metadata-encryption switch keeps its form, and its answer expires by itself after ten minutes.
- A blind node no longer logs a warning a minute for a peer that answers 401 or 403: one line when the peer closes its door, a retry once an hour, and one line when it answers again. A closed-door episode ends when the peer answers 404 or with a page that is not a sync server.
- The re-key names a table it does not know and says what to do about it; a sentinel that is empty is treated alike by the re-key and the sync check.

### Notes for upgrading

- **From a version older than 2.6, update to 2.6.1 first, then to 2.6.3.** 2.6.3 refuses an older database or backup, leaves it unchanged and says so.
- After the first start of 2.6.3, a database cannot be opened by 2.6.1 or earlier. To go back, restore the snapshot or backup you made before the update.
- Nodes that already run 2.6.0 or 2.6.1 keep their data, their keys and their settings; the schema is adopted at the first start, which takes a moment on a large database.
- Before activating metadata encryption, check the readiness report in Admin > Metadata encryption: a device that does not declare `metadata-envelope-v1` must be updated, or revoked in Trusted nodes and joined again.

#### 2.6.1 (2026-10-12)

### Fixed

- **"Encrypt this node" now starts on Windows and macOS desktop nodes.** In 2.6.0 the check for unencrypted copies of the vault, which runs before encryption, reported the node's own lock file (`node.lock`) as an unreadable copy and refused to start. It now passes over a lock file only when it is provably the lock the running node holds (the same file on disk, empty, a regular file); any other file of that name, and any other unreadable file, still blocks encryption.

- Tests that failed by chance on CI runners or only on Windows were fixed; the CI job runs every test project, reports all failures at the end and stops after 150 minutes.

### Added

#### 2.6.0 (2026-10-11)

Two new switches, both **off until you turn them on**: metadata encryption (everything that leaves a full node sealed, blind copies kept sealed) and encryption at rest (a node's own database files page-encrypted). Both are one-way: there is no off-switch. Old 2.5.x peers of an activated network are refused with HTTP 426 until they update. Update every device to 2.6.0 before flipping either switch — see "Notes for upgrading" below.

- **Metadata encryption (sync protocol 4).** After activation, sync events travel between devices as encrypted envelopes; a blind copy holds no titles, no folders, no tags and no count of notes — only encrypted changes and an encrypted snapshot of the whole bank that the blind copy cannot open. A new key, the **metadata key (MEK)**, is the fourth key in the chain (after the master DEK that opens the vault, the password-derived unwrap key, and the row keys of note content): it wraps the per-event envelopes and rotates inside every key rotation, so a node a rotation left out can never open anything written afterwards. The page header, the Admin page and `bmb status` show how many sealed changes wait when a node is locked. Each full node also seals its own outgoing events; a relay (a node that only forwards) checks signatures but never reads the metadata. Each new device declares the capability it supports (`metadata-envelope-v1`) and its app version; the Admin card shows what every device runs and what is ready. A 2.5.x peer is refused with HTTP 426 and never asked again until it is updated.

- **A locked node keeps working.** A node that is locked (or sealed and unlocked) still receives sealed changes from its peers and relays them on. They are held in a small inbox next to the database, applied exactly once and in order when the node is unlocked, and refused to be compacted, rotated or network-restored while they wait (the Admin card and the page header say so). The "N changes wait for unlock" line keeps showing until the inbox is empty.

- **Encryption at rest ("Encrypt this node").** A per-node switch that page-encrypts the node's two database files (the main vault and the chat database) so a stolen disk or laptop shows nothing readable — no titles, no folder names, no tags, no note count. The key chain grows by one link: the door unwraps the master DEK, the master DEK unwraps the **database key (DBK)** (AES-256-GCM, the node id and the generation number bound in as AAD), and the DBK derives page keys with HKDF-SHA256. A small plaintext file, **`beememorybank.boot.db`**, sits next to the encrypted databases and holds the doors — the keys wrapped under each password, recovery key, agent key or auto-unlock option. The encryption itself is SQLCipher 4 page encryption; the key is never in a .NET connection string, only in the keyed factory that applies it to each physical connection and clears it at once when the connection is closed.

- **Pre-encryption snapshot.** "Encrypt this node" first copies the unencrypted files to `data/pre-encryption/` (with the snapshot envelope that opens with the master password of that day), then makes the encrypted copy, self-checks it, and only then replaces the originals. Until you delete it, that snapshot is the only way back to an unencrypted node. A restore of it (CLI: `bmb snapshot restore-standalone`) opens with the old password or the recovery key you had that day, and replaces the encrypted node with the unencrypted one. After a restore the node is plain again until you re-encrypt it.

- **Server: "Start unlocked after a restart"** (off by default). A new checkbox in Admin > Security lets an unattended server open its vault by itself after a restart. It is off unless you turn it on, enabling it asks for the master password and the typed phrase **`START UNLOCKED`**, and shows a warning twice (anyone with the disk can then open this node without a password). A key file on disk (`BMB_AUTO_UNLOCK_KEY_FILE`) holds the wrapped unlock material; the node never writes, moves or deletes a file the operator mounted itself, and an operator-provided file must be readable only by the node's account (`chmod 0400` on Linux, an explicit DACL on Windows). The key file is **not** restored from a snapshot or a backup of the data folder; the checkbox switches off at every key rotation, by design. Desktop apps keep the existing opt-in OS auto-unlock, now with a warning of its own, and that warning also applies to the new database file: anyone with the computer can open the bank without a password.

- **Locked sealed node keeps syncing.** A sealed node that is locked (no auto-unlock option, no administrator signed in) answers only as a relay: it accepts sealed events from its peers, stores them in a small inbox, refuses plain-text content (the other side will send it again after this side is open), and applies everything once and in order at the next unlock. While closed every other tool answers `vault_sealed` (HTTP 503, same code) and the node reveals nothing. The agent keys of superadmins can open a sealed node, including right after a restart with nobody typing a password; an ordinary user's agent cannot; a `bmbrt_…` remote-account token is not a key to the disk.

- **Phone and desktop copies.** A blind phone or a desktop copy shows how old its restore point is, how many sealed changes arrived since and how much space they take; never a count of notes. A phone's sealed copy is the same file the full node keeps, and the phone receives it by itself after the full node has it. Face ID / Touch ID still opens the sealed vault as before; the closed phone's container holds ciphertext plus the small plaintext boot store, and it is still excluded from iCloud and computer backups. On a Mac or Windows desktop copy, OS auto-unlock now opens the encrypted database too.

- **Backups.** On an encrypted node, snapshots work as before and restore as before. File copies of the data folder change in two ways: copy them only while the node is stopped, and leave out the auto-unlock secrets (`server-auto-unlock.*`, `os-auto-unlock.dat`) — they are doors, not data. Restoring a file copy means restoring **all** of its files at once (database, WAL, the boot store); a node restored from files runs `bmb vault verify` before it rejoins the others. Backups made before you encrypted the node stay unencrypted — that is the old trade-off in a file. Delete them yourself when you no longer need them.

- **Compatibility with 2.5.x peers.** Until the metadata-encryption switch is on, every node speaks sync protocol 3 exactly as in 2.5.2, and 2.5.x and 2.6.0 nodes sync and join in both directions. After the switch, 2.5.x peers are refused with HTTP 426 — update them, or revoke and pair/join them again with 2.6.0. The two migration scripts (Step A 037/038/039, Step B 040) are silent upgrades; a 2.5.2 node opens a 2.6.0 database and the reverse. The old unencrypted `blind-node` package format 1 is still accepted by an unactivated node; an activated node refuses it (use the new format 2).

- **One-time encryption of existing databases.** "Encrypt this node" runs a one-time migration: it copies the unencrypted database files into a sealed copy, self-checks the copy (every page opens, the anchor matches, the size is right), and only then replaces the originals. **Do not interrupt the migration.** The node pauses its writes for the duration (a few minutes for a desktop database, longer for a big one) and refuses to start if anything is missing. The pre-encryption snapshot it leaves next to the data is your way back until you delete it. Free disk space about the size of the database is needed; a shortage fails the migration cleanly and tells you so. **TODO(step-b-code):** the exact progress text, the preflight wording and the timings of the migration depend on the Step B integration track (startup, unlock, migration wiring) and may be tightened before 2.6.0 ships.

- **Updated commands.** `bmb sync readiness` lists each peer's state (`ready`, `not_capable`, `stale`, `unknown`); `bmb sync activate [--password-stdin] --yes` flips the metadata switch after the readiness check. `bmb vault seal` runs the encryption-at-rest migration from the command line (same preflight as the Admin page); `bmb vault verify` checks a sealed node's files before it rejoins the network; `bmb rekey` also works on a sealed vault (it re-wraps one key and touches no page, so it is as quick as on a plain node). `bmb snapshot restore-standalone` opens the pre-encryption snapshot onto an empty or reset node. **TODO(step-b-code):** the exact CLI flag names of the server auto-unlock (`bmb vault auto-unlock status|enable|disable`) come from the integration track.

- **New routes.** `GET /api/sync/readiness`, `POST /api/sync/activation` (both superadmin, never an agent), and the server auto-unlock routes `status|enable|disable` (a 409 `role_desktop` if asked on a desktop host, a 409 `role_server` if the desktop route is asked on a server). The default agent keys and existing routes are unchanged. **TODO(step-b-code):** the admin card page paths (`/VaultEncryption`, `/SyncActivation`) come from the integration track and may be adjusted before release.

- **What is sealed, what is not (honest limits).** Step A seals titles, folder paths, tag names, file names and the shape of the tree on the wire and on every blind copy; the control events of the mesh (who is trusted, key rotations, recovery material) stay readable by design and carry no note content. Sizes, counts and timing of sealed messages are visible to a man in the middle; what they say is not. A 2.5.x peer that is still on the network when you switch is refused with HTTP 426 — there is no quiet downgrade. Step B seals the page content of the database on the node's own disk; the boot store stays plaintext (it carries no titles, no folder names, no note content; only wrapped keys and a node id). A running, unlocked node decrypts for whoever is reading it (the encryption is at rest, not in memory); a node that auto-unlocks itself has traded that protection back for convenience — the warnings say so before you turn those options on.

### Changed

- **Working copies are never left readable.** Snapshot, restore, join, recovery and activation make their working copies in an owner-only folder inside the data folder and remove them together with SQLite's side files (`-journal`, `-wal`, `-shm`); if a copy cannot be removed, the operation fails and asks for repair instead of reporting success. "Encrypt this node" does not finish, and the node does not open, while any readable file of the old database is left.

- **Lock on an encrypted node closes the database itself**, not just the session. The same things as before can still open it (a superadmin's agent key, an enabled auto-unlock option); on an encrypted node the key that decrypts the files leaves memory with the vault key, and the node answers only as a relay until it is opened again.
- **Key rotation on an encrypted node is lighter** — it re-wraps one wrapper key, touches no page, and switches the server's "start unlocked" option off (rotation is the moment to re-decide who can open this node unattended).
- **Blind copies show a restore-point age and a number of stored sealed changes**, never a count of notes; they raise a "stale baseline" alarm after 7 days without a fresh sealed snapshot.
- **A held (waiting) row carries the originator's superadmin flag at receipt**, so a revocation that arrives while a node is locked cannot make it diverge from an unlocked one.

### Fixed

- **Two starts of a blind node at the same moment on an empty or cut-short node-key file** no longer both write a new identity (BMB-190): the replacement now runs under a lock on `node-identity.key.lock`, and the start that waited takes the key the first one wrote. After such a repair an empty `node-identity.key.lock` file stays in the data folder.
- **`scripts/smoke-docker.sh` removes its container when the run ends** — on success and on failure (the failure's last log lines are printed first) — instead of leaving a stopped `bmb-smoke-*` container behind on every run (BMB-227). Set `BMB_SMOKE_KEEP=1` to keep it for `docker logs`. Smoke containers carry the label `bmb.smoke=1` (`docker ps -aq --filter label=bmb.smoke=1` finds any still around).
- **Sync test parallelism is bounded to the KDF gate on small runners** (test-host scheduling only; no product KDF code was changed).
- The "two starts at once on a torn node-key file" race is closed on Windows and Linux; on file systems that do not honour `flock` (NFS, SMB, `DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1`) the old race remains. A guard with `.instance.lock` (already required by `InstanceGuard.AcquireOrExit` in every host's `Program.cs`) is still needed. On Windows, a start that finds another process briefly reading the file retries the replacement instead of failing; if the file stays busy, the start fails and the key file is left as it was.

### Notes for upgrading

- **The order matters.** Step A and Step B are two switches; both must be turned on in this order: (1) update every device to 2.6.0 — servers, desktops, phones and iPhones in particular (an iPhone that cannot be updated must be revoked and paired again); (2) let every device sync once; (3) take a fresh offline backup of every full computer (it is the only way back); (4) flip the metadata switch from the Admin page once; (5) node by node, with a fresh backup of each one, run **"Encrypt this node"**. Do not interrupt the encryption migration; the node copies its own files first and refuses to start if anything is missing. **Never delete or restore a database without its `beememorybank.boot.db`** — without it the encrypted databases cannot be opened.
- **A 2.5.x peer that reconnects after the metadata switch is on is refused with HTTP 426**. Update it first, or revoke it and pair/join it again with 2.6.0.
- **Old backups made before the switch still contain readable titles wherever they are** — delete them yourself when you no longer need them.
- **The migration needs free disk space about the size of the database**; a shortage fails the migration cleanly and tells you so.
- **`BMB_AUTO_UNLOCK_KEY_FILE`** (server auto-unlock) is operator-only — the node never writes, moves or deletes a file you mounted. Make it readable only by the node's account: `chmod 0400` on Linux, an explicit DACL on Windows. On Docker, mount it as a secret.
- **The pre-encryption snapshot** is kept in `data/pre-encryption/` next to the encrypted databases. It is your way back to an unencrypted node until you delete it. It opens with the master password or the recovery key you had that day, even if either has changed since.
- **`bmb vault verify`** after a restore of file copies; a node that fails it refuses to rejoin the network until the issue is fixed.

### Fixed

#### 2.5.1 (2026-10-09)

No sync protocol change (still 3) and no database migration. The join response gains two optional fields per peer (below) and the host a new route,
`POST /api/join/abort` (below); nodes of 2.0.x to 2.5.0 ignore the fields, never call the route and keep syncing and joining with 2.5.1 in both directions. Each fix has a test that fails without it.

- **Two first starts at once on Linux or macOS no longer both create the node identity.** The second start used to replace the first one's key file, which
  left the node refusing to start later ("does not belong to node"). The same step now protects a blind node's TLS certificate.
- **A second node on the same data folder is refused at start** with a clear message and exit code 1 (a second container on one volume, an old container left
  running). A node holds `.instance.lock` in its data folder while it runs; a folder shared over NFS or SMB by two computers is still not protected.
- **`docker run` of the full image without a compose file** keeps the Api and the Web front on one data folder (`BMB_DATA_PATH=/app/data` is set in the image).
- **Sync: "cannot catch up" is kept per peer.** A node stuck behind a peer that has compacted past it was forgotten as soon as another peer synced fine; the
  notice now stays until that peer answers again or is removed.
- **Adding a blind node again after removing it** takes in what it holds first, so it syncs again, Reseed works and nothing it received meanwhile is lost; its
  silent and protocol alarms count from the new pairing.
- **A blind copy is no longer reported silent in the first hour after this PC starts or wakes.**
- **A phone that joined from a snapshot no longer restarts its clock at 0** (Android app and iPhone full app). After the app was killed its edits lost to every
  peer; it now keeps the clock of the imported rows, like `bmb join` and the Setup page.
- **A joining node pins the TLS keys of the peers it learns from the host.** The phone, `bmb join` and the Setup page used to check those peers through the
  public certificate authorities, so a self-signed peer was unreachable and any valid certificate at its address was accepted. The join response now carries
  `tlsTrust` and `tlsSpki` for each peer, and a value that is not a valid pin is never stored.
- **A join no longer fails because the host needs minutes to build the snapshot of a big vault** (`bmb join` and the Setup page gave up after 30 seconds, the
  phone after 100).
- **After a failed join** the messages say which member row stays on the other computer and that it must be revoked on its Admin page (it holds back
  compaction); they no longer claim that nothing was kept.
- **The archive and the extracted database of a join or restore are made in the data folder, readable by the owner only** (not in the shared temp folder), and
  what a killed join left behind is removed at the next start.
- **iPhone full app:** the password and passphrase fields are emptied after every outcome of join, create, unlock and opening a protected note, not only after
  success.
- **Android:** sync and first-load or backup failure messages include the inner cause instead of the HTTP stack's generic "An error occurred while sending the
  request."
- **Windows blind app:** a state file that another program holds at start-up is no longer replaced by an empty one (it is read again, or the call fails and is
  retried); a damaged state file is kept as `state.json.damaged-*` before it is replaced; a failed write no longer leaves the app ahead of the disk; a key
  file that cannot be read for now is reported as "the key store did not answer", not as a lost key.
- **macOS blind app:** the login item refuses to point at a temporary, build or quarantined (App Translocation) copy, and shows as off when the app it starts is
  gone or was moved.
- **Blind apps:** after a refused "Disconnect and wipe" the sync and backup schedule is given back (it stayed stopped until the app was restarted); starting the
  app again while it is quitting brings it up instead of ending with nothing running.
- **Full app:** a blind-node alarm notice that could not be shown is tried again after five minutes instead of being counted as shown for a day. Starting the
  app a second time shows the running copy's window (Windows), and a click on the Dock icon shows the window (macOS). When "Restart to update" cannot apply the
  update, the node is started again and the update window says why (it used to shut the app down with the vault not served); the reason is in
  `logs/velopack.log`.
- **Security:** `bmb init` and `bmb join` take the master password on stdin (`--password-stdin`) or by a prompt without echo; `--password` still works but
  warns that it is visible in the process list and shell history.
- **Security:** the release workflows download the search model from a pinned commit and verify its SHA-256 on every run.
- **A failed join no longer leaves a phantom member on the other computer.** New route `POST /api/join/abort`: a joiner whose join fails after the other node
  answered it (`bmb join`, the Setup page, the Android and iPhone apps) asks that node to take its row back, so a failed join no longer leaves an active
  never-synced member that holds back compaction. It needs the master password, removes only the never-synced row of that attempt, and is rate-limited and
  audited (`join_aborted`). A node or proxy that does not know the route (2.5.0 or older) keeps the old message that names the row. Reverse-proxy recipes
  forward `/api/join/abort` together with `/api/join`; a proxy that whitelists paths exactly must add it.
- **Two joins of the same new node id at the same moment** no longer fail with a database error on the host.
- **The "Connect a device" door stays up two more minutes after a finished join**, answering only that abort, so a failure after the snapshot download
  through a join code can still be taken back.
- **Images: SixLabors.ImageSharp is gone; the image library is now SkiaSharp (MIT).** Its fixed versions need a paid licence and the free versions carry open
  advisories (NU1902/NU1903); no advisory remains against the image library. Phone photos with an EXIF rotation are now stored upright, and transparent
  pictures become white, not black, when converted to JPEG. A damaged, cut-short or oversized picture (over 100 megapixels or 64 MB) is refused with a clear
  message (HTTP 400) before it is decoded, instead of an error. Only JPEG, PNG, GIF, WebP and BMP (and SVG and animated GIF untouched) reach the decoder.
  The full-node server grows by 9 to 14 MB (the native library). `THIRD-PARTY-NOTICES.txt` is added (and copied into the Docker image).

### Changed

#### 2.5.2 (2026-10-09)

No sync protocol change (still 3) and no database format change: every database stays a plain SQLite file — 2.5.1 opens what 2.5.2 writes and the reverse.
Two things land together: the database engine is now SQLite3 Multiple Ciphers (MIT), in preparation for encryption at rest, and four places where private
metadata could leave a node are closed. Peers of 2.0.x to 2.5.1 keep syncing and joining with 2.5.2 in both directions.

- **The database engine is now SQLite3 Multiple Ciphers** (SQLite 3.53.4, MIT licence) on every platform: Windows, Linux (x64 and arm64 images), macOS,
  Android and iOS. No key is set: the files stay plain SQLite. The Android apps no longer exclude native SQLite assets; they carry the engine's own
  library for each ABI. SQLite temp data stays in files, as before, so large `VACUUM`s do not use more memory. The notices of the new engine are in
  `THIRD-PARTY-NOTICES.txt`, which both Docker images now ship.
- **Actor metadata no longer leaves a node.** The actor type and name, the initiating agent's name and an event's entity id are local-only: sync events
  served, pushed and handed to a blind restore client carry none of these fields, and neither do the standing events of a blind replica package.
- **The join-snapshot endpoint (`GET /api/sync/snapshot/for-join`) answers 403 to blind nodes**; a blind node never had a legitimate use for it. Full
  joiners are unaffected.
- **Blind packages strip concept-tag embeddings and their model version too** (article embedding projections were already stripped).
- **Sync applier log lines identify events by id only** — no article titles, folder paths, tag names or actor names reach plaintext logs, including through
  the text of the errors the applier logs.

### Removed

#### 2.2.0: the Internet Access wizard, Let's Encrypt and DDNS are gone (2026-10-05)

- **The "Access from the Internet" wizard, the built-in Let's Encrypt (ACME) client and the dynamic DNS (DDNS) updater.** They were a half-finished
  spike: an issued certificate was never served by any listener and never renewed, the Docker image has no HTTPS listener at all, the wizard showed port
  5311 but told the user to forward 5443, and on Linux the account key and PFX password were written with ordinary file modes. This removes the Internet
  page and its Admin shortcut, the `/api/internet-access/*` endpoints, the DuckDNS, deSEC and Cloudflare providers, the UPnP and static external-IP
  helpers, the TLS-ALPN-01 hook of the node front and the `Certes` package. The node's local CA (the home-network, phone and blind-node-pairing
  certificate), the LAN join listener, the LAN firewall helper and `POST /api/sync/probe` stay. To open a node to the internet use your own router
  forwarding and a reverse proxy, a tunnel or a VPN: see [docs/internet-access.md](docs/internet-access.md). Nothing is deleted from an existing
  installation: leftover `internet-access/`, `ddns-state.json` and `certs/acme/` entries are no longer read, and a re-key still carries them over.

### Added

#### 2.5.0: the iPhone apps (2026-10-08)

No sync protocol change (still 3) and no database migration. The macOS, Windows, and Android blind apps are unaffected except for the shared blind-copy status types; the Android ordinary app also receives the join-by-code fix below.

- **The iPhone blind copy.** `Bee Memory Bank Blind` is an iOS app that keeps an encrypted replica, pairs through a computer, syncs,
  makes backups and never holds or asks for the master password. Its Keychain secrets are readable only after the first device unlock,
  never synchronized or restored onto another device; its data folder is excluded from device backups; and it uses pinned TLS like the
  other blind copies. The app has a local three-day no-contact notification and accepts the pairing call code by paste or QR link. It
  runs while open and requests iOS background time, but iOS decides when to grant it; background sync and long work are not guaranteed
  like Android's WorkManager jobs. It is source-only for now: build it on a Mac with Xcode and the .NET iOS workload. A manual macOS
  workflow builds the unsigned simulator app and scans the built output to keep Vault code out.
- **More useful blind-copy status.** The shared `Blind.AppCore` reports the number of replicated notes and retains the text of a failed
  sync's exception (`BlindReplicaStats` and its failed-sync reason) so the iPhone screen can explain a failure in words instead of an opaque runtime resource key.
- **The iPhone regular app.** `Bee Memory Bank` is a full-node app with the vault on the phone: create a vault with a recovery key shown once or join a computer with its pinned join code, unlock with the master password or Face ID / Touch ID, and read and edit Markdown rendered locally with no network loads. It syncs as a client while open and once when leaving; iOS makes it a node that calls and cannot be called. It is source-only for now; see `docs/full-node/IOS.md`.

### Fixed

- **Android ordinary app: join by code.** A node that joined through a computer's pinned join code did not retain that pin on the computer's whitelist row, so later sync with a self-signed computer certificate failed. The joined row now retains the code's pin, as the desktop and CLI join flows do.

#### 2.4.0: ready-made Docker images, alarms for a silent blind node, `bmb join` takes the snapshot (2026-10-07)

No sync protocol change (still 3) and no database migration: nodes of 2.0.x to 2.3.0 keep syncing with 2.4.0 in both directions.

- **Ready-made Docker images** on GitHub's container registry: `ghcr.io/ultrathinker/beememorybank` (full node) and `ghcr.io/ultrathinker/beememorybank-blind`
  (blind node), for `linux/amd64` and `linux/arm64`. A node starts from one compose file (`docker-compose.image.yml`, `docker/blind/compose.image.yaml`): no clone,
  no build, no model download. Tags `X.Y.Z`, `X.Y` and `latest` (a pre-release gets its own tag only); each image carries a GitHub build provenance
  attestation, and each architecture is started and checked before any tag is written. The full-node image is built per architecture (about 140 MB of
  programs instead of 640), carries the search model verified by its SHA-256 at build time, declares only the Web port (5301) as exposed, and `docker stop`
  now stops the Api cleanly as well. Coming from the source `docker-compose.yml`: put `BMB_DATA_PATH=./data` into `.env` first, see the README.
- **The desktop app tells you when a blind node needs attention.** A Windows notification (a banner on macOS), once per problem and a reminder at most once a
  day: a server blind node this PC could not reach for 6 hours while awake, a blind copy that has not called for 3 days, or a sync protocol mismatch; and
  "N blind nodes need attention" in the tray tooltip. A sleep of the computer pauses an outage, it does not end it. Nothing is judged in invisible mode or
  before a sync cycle has completed. The Blind nodes page also warns when a blind node runs a newer sync protocol than this PC ("update this PC"), shows the
  protocol of server blind nodes, and its "last contact" includes this PC's own call each cycle. `GET /api/blind-nodes/alarms` (superadmin, internal key).
- **`bmb join` takes the signed snapshot** like the Setup page and the phone, so a node joined from the command line gets every note even when the source
  compacted or re-keyed its log. A failed join says why, exits non-zero and rolls the new node back, so it can be run again; joining a blind node says to join
  a node that holds the data. A node that joined from a snapshot or was restored no longer restarts with its clock at zero (its first edits could lose to older
  ones on other devices).
- **A node that cannot catch up says so.** When a peer compacted past this node, every page of the web UI and `bmb status` show it, instead of only the log.

Fixed in this release:

- The Windows notification is actually shown. The balloon the app raises (the new blind-node alarms, and the "about to sleep" notice, which has been
  silent since it was added) had no icon, and Windows 10 and 11 show nothing for a balloon without one; checked by hand on Windows 11. Notices that are due
  at the same moment are now sent six seconds apart, because a second balloon replaces the first on screen.
- A phone can join a new or small vault again: the join no longer stops with "Tar archive exceeds maximum extracted size (0MB)".
- Search no longer fails now and then with "An item with the same key has already been added" while the index is being written to.
- On Windows, a blind node's identity key, TLS key and backup credentials are readable only by the account that runs it (and SYSTEM); files an older version
  left readable by every account are repaired at start. On a FAT32 or exFAT disk, which has no ACLs, the file is created as before.
- A snapshot's working copy of the database is made, owner-only, inside the node's data folder instead of the system's temp folder, and removed at the next
  start if a crash left it behind. It needs free space on the data drive about the size of the database.
- A blind node whose first start was cut off while it wrote its TLS certificate starts again; two first starts on the same data volume no longer leave an
  identity key that does not match its record; the seed cutover also refuses a link planted at the staged database or a sidecar.
- Restoring from a backup or a blind node is safe against a cut halfway: devices the backup's anchor does not vouch for are never trusted, not even for a
  moment, a restore run again replaces everything the cut run wrote, and the restored node never restarts with its clock below the restored data. After a
  restore cut between the database and the media swap, the next start moves the restored images into place.
- Claiming a blind node with a restore code survives a failure halfway: the device's retry completes the claim instead of finding the code used up (a used
  code still cannot be used by another device or to change a trusted key). Two syncs with the same blind node at the same moment no longer both adopt its
  checkpoint.

#### 2.3.0: the product does what the website promised (2026-10-07)

No sync protocol change (still 3). One database migration (036) adds the trust mode of a trusted node; nodes of 2.0.x to 2.2.0 keep syncing with 2.3.0
in both directions and ignore the new optional field. A blind copy that is paired through a hub with a normal certificate needs the 2.3.0 apps; update the
blind copies and the pairing computer first.

- **Forgot your password? Use a recovery key.** The Sign In page has a new link. An administrator enters the user name, a recovery key and a new password; the
  password is replaced without signing in, the vault is not unlocked by it, the sessions of that user end, attempts are limited and audited, and the other
  nodes are told. The command line does the same: `bmb user reset-password --user NAME --recovery-key-stdin`. The Admin > Security text no longer promises
  that a recovery key "unlocks the vault" without saying how.
- **Restore: a restored node signs again, and you choose what to restore.** A restore of this node alone stored the new node key unwrapped and the node could
  not authenticate or sign afterwards; it is now stored wrapped under the master key like at the first start. The restore dialog asks **What should be
  restored?** (this node only, or the whole network) and says what each does; after a node-only restore the sign-in page says the node is new. A snapshot of
  another vault (another master key) is refused with a plain message and nothing is changed. The Auto-restore texts now say what the code does.
- **Devices on my network.** A per-profile setting (Admin > Nodes, off by default) serves the node over HTTPS on port 5311 and announces it on the local
  network, without a restart; it works in the Windows app, the Mac app and the Windows service. The Windows firewall rule can be added and removed from the
  same card. The announcement follows the real state of the listener (nothing is announced when the port is taken), and switching on and off at the same
  time can no longer leave the file and the listener disagreeing. `BMB_HTTPS_ENABLED=1` still works as an override.
- **Join by code, and Connect a device on macOS.** The code of **Connect a device** can be pasted on another computer's first-run page ("Connect to another
  device") or given to `bmb join --code`: the joiner sends the master password only to the computer whose key the code pins. The page works on macOS too.
  **Find devices on my network** finds the computers that have the setting on and says so when it finds nothing.
- **A full node can be called by blind copies (a hub).** Admin > Trusted Nodes > **Let blind copies call this node** takes an https address and a trust mode:
  a pinned certificate, or a normal certificate such as Let's Encrypt behind a reverse proxy. The server checks the identity and the certificate before it
  saves. The Android, Windows and macOS blind copies follow the mode in the connection code (old codes keep working). The trust mode is authoritative: an
  unknown mode is refused, and a stale pin cannot override a normal-certificate row. The probe refuses loopback, link-local, unspecified and multicast
  targets. The blind-copy download route is limited to 20 starts per 10 minutes per blind copy, and revoking a lost blind copy ends its access at once.
  The reverse-proxy recipes (Caddy, nginx, Apache, Cloudflare Tunnel, compose files) carry the routes blind copies need.
- **Docker is closed by default.** The compose file publishes the web port on `127.0.0.1` only (`BMB_WEB_BIND=0.0.0.0` opens it on purpose); the Profile page
  prints an MCP address that works in Docker (`BMB_MCP_URL`, `BMB_API_PUBLISHED_PORT`) or says that assistants cannot reach the node yet.
- **Setup:** "Open an existing profile" appears only in the desktop apps (Windows and Mac); browsers and Docker no longer get a form that always refused.
- **Guest accounts for other people (remote accounts):** an optional block in every reverse-proxy recipe forwards the three routes; the failure messages say
  what is wrong (the proxy does not forward the routes, an `http://` or private-IP address).
- **Texts made true:** the macOS local-network prompt, the MSI firewall feature, the LAN listener paragraph, the Windows NSSM restart commands.

#### 2.1.0: blind copies for Windows and macOS, the macOS app, lock on sleep (2026-10-05)

No sync protocol (still 3), database format or ciphertext change; nodes of 2.0.x and 2.1.0 keep syncing with each other in both directions.

- **A blind copy for Windows and macOS.** A quiet tray (Windows) or menu-bar (macOS) app that keeps an encrypted copy of a memory bank and never holds the
  master password: pairing with a node, sync, backups and "Disconnect and wipe", with the same rules as the Android blind app. All three are built on
  one shared core library (`BeeMemoryBank.Blind.AppCore`); only the thin platform parts differ (DPAPI or the Keychain for secrets, a registry entry or
  a login item for autostart). None of them contains the vault code: the assembly scan of the boundary tests covers the publish folders of all three.
- **The full app for macOS** (Apple silicon): the desktop shell, the node and the web UI as `Bee Memory Bank.app`, signed with a Developer ID and
  notarized by Apple, with its secrets in the Keychain (`IUserSecretStore`: DPAPI on Windows, Keychain on macOS), a login-item autostart, keep-awake,
  Cmd+Q and lock on sleep.
- **Lock on sleep works.** The desktop app asked its node to lock the vault when the computer went to sleep, but `POST /node/lock` was a stub, so the vault
  was never locked. It now locks: loopback only and only with the node's own internal key. Like the Lock button it is advisory (see SECURITY.md).
- **Stopping the node is safe right after it starts.** A stop that arrived while the front was still starting could abort `bmbd` (exit code 134, about one
  fast stop in four); a stop now waits for the start in flight.
- **The blind apps never wait for the charger, the battery, Wi-Fi or a metered network, and nothing interrupts a running job** (Android, Windows, macOS).
  The first load starts right after pairing. A real failure is shown with its real reason and retried. On Android only WorkManager's "network connected"
  hint is kept; a phone that already has the app picks the new rule up when it is updated.
- **Android blind app:** moved onto the shared core, with the pairing, keys and schedule kept across an update. APKs are signed with the project's release key
  from this release on; earlier builds were never published.
- **A new app icon** (the bee on a stack of database discs) in every app: the Windows apps and installer, the macOS apps, the Android apps, the web site (favicon and touch icons) and the README. The macOS menu-bar image stays a plain black shape, as the menu bar needs.
- **Downloads:** the release carries the macOS app, the macOS and Windows blind copies and the Android blind app (see the README). The Windows and
  Android downloads are not signed by a vendor certificate of the platform; the macOS apps are signed and notarized.

#### 2.0.2: Sync now in the Android blind app (2026-10-04)

Android blind app only; nothing on a server, the sync protocol (still 3) or the database changes.

- **"Sync now" button.** The phone synced by itself every 15 minutes, the shortest period Android's WorkManager accepts, and a periodic job cannot be
  started early. The button queues one sync round with the same worker and the same network rule and the "Last sync" line changes within seconds. A second
  press while a round is queued or running is dropped.
- The full-node route list golden no longer contains the debugger attribute that only a Debug build adds, so it is the same in Debug and Release.

#### 2.0.1: the code that can read is not in a blind node (2026-10-04)

No sync protocol (still 3), database format, migration or ciphertext change; nothing on the wire depends on the application version, so
1.0.17 and 2.0.1 nodes keep syncing with each other in both directions. A blind node of 1.0.17 is replaced by the new image on its own volume.

- **The libraries are split by what a node needs.** `Core`, `Crypto`, `Search`, `Storage` and `Sync` keep what every node uses (store, relay and
  apply ciphertext, the sync protocol, the external-key identity, pairing and TLS pinning, backups, recovery sets a node receives and serves). The code that
  holds the master key, opens a session, encrypts or decrypts content, creates or opens recovery boxes, restores snapshots with the master key or
  manages the node is the new `BeeMemoryBank.Vault` (120 files, moved with their history, namespaces unchanged). The shared libraries cannot reference it.
- **A blind node does not contain the vault.** The Linux blind host, its console and CLI and the Android blind app reference the shared libraries only.
  Release 1.0.17 had the same code compiled in behind closed doors (no route reached it); an assembly-level scan of that host finds 41 vault types in it, the
  same scan of 2.0.1 finds none. The scan reads the metadata of every application assembly (types defined, types and members referenced, assemblies referenced) and runs
  in the tests, over publish folders and over the image file system.
- **Mixed code was split by operation, not by passing the key around**: snapshot key operations, the master-key sentinel check, the current-key
  fingerprint, who may sign a replica, the external-key node-auth signer and an event logger that refuses to author. The library DI is split the same way
  (`AddNodeStorage` / `AddNodeCore` / `AddNodeSync` are shared; `AddStorage` / `AddCore` / `AddSync` are in the Vault and call them), so no full-node host changed.
  The route list and the service container of the full node and the container of the blind node are pinned by golden files taken before the split; the only differences from them are the intended ones (the vault services are gone from the blind container, the full node gained three seam registrations).
- The old `BeeMemoryBank.Blind` link assembly is gone; `BeeMemoryBank.Blind.PhoneClient` is a real library (14 files: the phone's own client; the Linux node, the Vault and the ordinary Android app do not contain it).
  Notes for contributors: `docs/blind-node/ADR-0002-vault-split.md`, `docs/vault-split/SEAM.md`.

#### 1.0.17: blind nodes are programs of their own (2026-10-03)

No sync protocol (still 3) or database change. A blind node of 1.0.12-1.0.16 is replaced by the new image on its own volume:
identity, TLS pin, data, backup settings and paired peers stay as they were.

- **The Linux/Docker blind node is no longer the full server in a "blind" role.** It is `BeeMemoryBank.BlindNode`, a program
  with its own startup, container registrations and 47 routes (pinned in `docs/blind-node/ROUTES.golden.txt`). The MCP server,
  agent login, caller scopes, AI chat, the embedding model, image and Markdown libraries, update and key-rotation machinery and
  the "restore into this machine" side of recovery are not part of it, so no request and no startup step can reach them. The
  image is 402 MB instead of 1.35 GB (the programs 12 MB instead of ~870 MB, because they no longer carry every platform's
  native files); `bmb` inside it has only `bmb blind ...`.
- **A shared core of all blind nodes: `BeeMemoryBank.Blind`.** The library code a blind node uses (234 of 347 files of Core,
  Storage, Sync, Crypto and Search; 60 % of their lines) is linked into one assembly under its original names and namespaces,
  so it cannot drift from the full application's. The Linux node and the Android blind app both use it; the Android app adds
  only `BeeMemoryBank.Blind.PhoneClient` (16 files: the phone's own client of a blind node) and its APK holds no Core, Storage,
  Sync, Crypto or Search assembly. `tools/blind-link` computes what is linked by compiling and tests hold it to an exclusion list.
- **Every database migration is embedded, byte for byte, under its original name** (the migration runner deletes ledger rows it
  cannot match, so a smaller set would be unsafe); tests pin the names and the bytes.
- **Fixed: an arm64 image used the amd64 programs and the amd64 restic.** A default on `ARG TARGETARCH` overrode the platform
  Docker passes in. Found by running the arm64 image; a test now refuses the default.
- Checked end to end: the image against a real full node (pair, seed, sync, restore package, restart); the old image swapped for
  the new one on the same volumes; both images on a copy of a live node's data volume (same identity, ledger, counts and key
  files); the Android app on a real phone. Notes for contributors: `docs/blind-node/`.

#### 1.0.16: agents can delete a file of an article (2026-10-03)

Server only; no sync protocol or database change.

- **New MCP tool `bee_delete_file`.** An agent could read (`bee_get_file`, `bee_get_image`) and add (`bee_save_media`) the
  files of an article but not remove them. The new tool deletes an inline image or a file attachment, by the media `id` or by
  `articleId` + `fileName`, and an unlinked upload by `id`. Two steps like `bee_delete_article`: without `confirm=true` it only
  describes the file and warns. It is the web UI's soft delete, so the deletion reaches the other devices by sync. It refuses the
  files of a password-protected article and a folder that is read-only for the agent, and it tells the agent when the article
  text still embeds the deleted image, so the reference can be removed.

#### 1.0.15: a separate Android blind-node app and the server part for it (2026-10-02)

No sync protocol (still 3) or database change; devices on 1.0.12-1.0.14 keep syncing.

- **A separate Android app for a blind node: `com.beememorybank.blind`.** It holds only encrypted data, never the data
  key or the master password, and never authors events. It pairs with the full node by two codes and pins the
  server's certificate, downloads the blind package from `GET /api/blind/replica` (signature checked before install,
  resumable, atomic), syncs by protocol 3 with a key kept in the Android Keystore, writes an encrypted backup file
  through the system file picker, and runs on a schedule (Wi-Fi, charger, battery at least 20%) in a foreground
  service. "Disconnect and wipe" removes everything. The app is about 44 MB and carries no search model, media or
  Markdown code. A backup needs the superadmin's integrity anchor; each backup downloads the package again and needs
  about four times its size free; the phone's event log is not trimmed yet.
- **The server prepares the blind package for the phone once and caches it** in its own directory (not the snapshots
  one) in `BlindReplicaPackageCache`; the package now includes the recovery box, so a cached package never lacks it.
- **The ordinary Android app no longer contains any blind-node code** (the unfinished blind mode, its pages and
  key store are removed; the 242 MB app is otherwise unchanged). A test guards that it stays so.
- **The sync library no longer depends on the embeddings library** (`PendingEmbeddingProcessor` moved to
  `BeeMemoryBank.Embeddings`), which is what lets the blind app stay small.

#### 1.0.14: agents can get the files of an article; the upload script stops creating clients (2026-09-30)

No sync protocol or database change; devices on 1.0.12 and 1.0.13 keep syncing. Server and desktop only; the Android app is unchanged.

- **Agents can get the files of an article.** `bee_get_article` lists the article's files (`files`: id, name, type,
  size, image or attachment, date; metadata only, also with `content=false`), and the new `bee_get_file` returns one
  of them as a file: an image as an image, any other file as an embedded resource with its name and type. Pass the
  media `id`, or `articleId` with `fileName`. Same access rules as `bee_get_image`; files above 10 MB are refused with
  a plain error. Files of a password-protected article are refused by `bee_get_file` and `bee_get_image` and are not
  listed. Whether a client shows a non-image file to its model depends on the client.
- **The upload script no longer creates a new client in the gateway on every use.** It needs `--client-name` (the
  agent's main client, never a new name) and reports it as the client name; `--bearer` is optional, and `--tool-prefix`
  (for example `bee-memory-bank__`) lets it call the tools through an MCP gateway. It never follows a redirect, so a
  bearer cannot be sent on to another host. The usage comment and the descriptions of `bee_get_upload_script` and
  `bee_save_media` say so.

#### 1.0.13: blind node console password, a link in Admin, clear errors (2026-09-30)

No sync protocol or database change; devices on 1.0.12 keep syncing. Only the full node (server, desktop)
and the blind node image change; the Android app is unchanged.

- **The blind node console can change its password.** A new "Console password" section asks for the
  current password (through the same sign-in, with the same attempt limit and lockout), refuses a short new
  one first, and ends every other browser session. A change made with `bmb blind init` ends them too.
- **The console password is the default backup (restic) password.** It is taken as the restic password
  only while no repository exists; after that the restic password is fixed by the repository. The settings
  refuse another restic password wherever a repository exists (a folder repository is recognised by its
  `config` file, a bucket once saved counts as existing, a folder that cannot be inspected counts as
  existing) and say what to do: point the backup at a new repository and enter its password in the same
  save. Creating the repository and pinning its password are one step, so a backup that starts while the
  password changes can no longer orphan it. If the backup settings cannot be read, the console password is
  not changed.
- **Admin has a "Blind nodes" card** that opens the Blind nodes page (superadmin only).
- **The Blind nodes page explains what is wrong.** A failed add no longer clears the pair code and says,
  per device, what to do: for example that a device has not been seen on the current version for over 7
  days, and that it must be updated and opened once, or removed under Admin, Nodes. Unreachable address,
  expired code, busy node, no disk space and a locked vault have their own plain sentences.

#### 1.0.12: blind nodes, recovery by master password, offline re-key (2026-09-29)

**Update every device together: 1.0.12 speaks sync protocol 3 and does not sync with 1.0.11 or older.**
An older device is refused cleanly (nothing is half-applied and nothing is quarantined) and shows
"not synced" without a reason until it is updated; sync resumes by itself once both sides are on 1.0.12.

- **Blind nodes: a backup relay that holds no keys.** A blind node (`BMB_ROLE=blind`) keeps a copy of the
  mesh as ciphertext only. It never unlocks, never derives a key and cannot author events: events
  authored by a blind node, and key rotations that seal a key for one, are refused and never relayed. A
  blind row can never be a superadmin or auto-accept peers. It ships as a Docker image with a local
  console (loopback only, its own password), a `bmb blind` command line (secrets never on the command
  line), pairing by one-time pair codes, a seed and a lossless reseed from a signed package, and backups
  with restic to a folder or S3 (schedule, retention, a weekly check, "Save a copy to…"). "Disconnect and
  wipe" removes everything the node holds. On Android a phone can hold a blind copy too: background sync
  and resumable encrypted backup files on Wi-Fi and charger.
- **Sync protocol 3 as an enforcement boundary, and pinned TLS.** Authentication answers 426 below
  protocol 3, tokens are bound to the declared protocol, and the client neither pulls nor pushes below it.
  Every sync client, mobile included, can pin the peer's TLS key; join codes carry the pin (QR and text),
  and a pinned request never follows a redirect or falls back to plain HTTP.
- **Recovery by the master password when every full device is gone.** Recovery boxes seal the master key
  under the password (device boxes while passwords differ, one strong box otherwise), and a state anchor
  published by a superadmin lets a restore say what it can vouch for. A new node can be restored from a
  blind node, a backup folder or restic, through a Setup wizard; the restore code carries the pin, and
  superadmin flags and peers survive only where the anchor vouches for them.
- **Join a device on the fly.** A running node opens a LAN join listener without a restart and without
  losing the unlock: one-time token, only the join calls exposed, off after 15 minutes or once the
  joined device has its snapshot. Any device that knows the master password may join.
- **Offline re-key (`bmb rekey`).** For after a key has leaked: with the node stopped, `bmb rekey`
  re-seals the vault under fresh keys on a copy, proves that no old master, entity or data key opens
  anything in the copy and that no old ciphertext survives in its files, scrubs it, and swaps it in with
  a journaled, crash-safe swap. Desktop has it as "Re-key…" under Manage profiles, with progress and a
  report page. Every agent is revoked, every other user is locked until the owner resets the password,
  and only the owner's slot survives. Every other device, blind nodes included, is revoked and has to
  join or pair again, and a peer that asks for the old log is sent to a snapshot. A comment sealed under a
  key that only the event log kept is dropped from the copy and listed in the report.
- **Chat titles and provider key prefixes are sealed** under the chat key, like the messages.
- **A master key has a fingerprint.** Recovery boxes, key links and anchors name their key by it,
  because rotation numbers are not unique across devices.

#### Confidential per-peer DEK rotation — X25519 envelopes (ADR 0006, 2026-09-04/05)

A DEK rotation used to wrap the new Master DEK under the *old* one and ship it inside the signed
`dek_rotation_commit` event — which every current **and revoked** peer already had a copy of via
`tbl_event` (plaintext JSON, replicated everywhere). Revoking a peer therefore didn't stop it from
reading every future rotation's key: an attacker holding one old DEK plus any later database copy
could derive every DEK that came after it. Rotation defended only against an attacker who held the
old key and never saw the event log again — close to the attacker the old key already defended
against.

The initiator now seals the new DEK **once per currently-trusted peer** with a per-peer X25519
envelope, derived from the Ed25519 identity keys every node and whitelist row already carries — no
new key material, and a revoked node simply receives no envelope it can open:

```
peer Ed25519 pubkey → X25519 (birational map, validated as a canonical prime-order-subgroup point)
HKDF-SHA256(shared_secret, salt=rotation_id, info=recipient_node_id) → envelope_key
AES-256-GCM(newDek, envelope_key) → this peer's envelope
```

The node's own Ed25519 identity seed (wrapped under the Master DEK) is re-wrapped inside the same
rotation transaction — a rotation that forgot to do this worked once and then permanently locked
out every node on the second rotation. The legacy `encrypted_new_dek` (wrapped under the *old* DEK)
survives only as a fallback for peers that haven't upgraded past this change; it is now nullable
and omitted on the confidential path. This does not retroactively protect a rotation that already
shipped the old-style commit — see [SECURITY.md](SECURITY.md#online-dek-rotation) for what is and
isn't fixed.

Robustness fixes shipped alongside, each closing a way a single bad row could brick an entire
rotation for the whole mesh: a row already re-wrapped by a peer that raced ahead is now left alone
instead of aborting the transaction; legacy pre-versioning ("v0") rows keep their original framing
instead of being silently corrupted; `tbl_article_version`/`tbl_conflict_version` rows use the
correct AAD (their own row id was being used instead of the owning article's); the projection
(embedding) matrix is re-wrapped in the same transaction and repaired rather than discarded if
found unreadable; and the rotation chain material needed by `LazySlotRewrapService` is now
persisted locally (migration 020) so event-log compaction can no longer strand a user who hasn't
logged back in since a rotation. The rewrap core (`DekRewrapper`) moved to `BeeMemoryBank.Sync` so
mobile and CLI nodes — which previously registered a no-op applier and silently fell behind the
cluster on every rotation — now apply it too.

#### Content-addressed blob store & sync protocol v2 (2026-09-04)

Every article write logged an event whose JSON payload embedded the entire encrypted body as
base64 — the same bytes already living in `tbl_article_body`/`tbl_article_version` — tripling
storage for no benefit (measured on the production node: `tbl_event.payload` alone was 42 MB from
this duplication). `tbl_blob(hash, data)` now stores ciphertext addressed by the lowercase-hex
SHA-256 of its own bytes; `article_create`/`article_update`/`media_create` events carry
`ciphertext_sha256` instead of the ciphertext itself, with the hash living **inside** the
Ed25519-signed payload so the signature still transitively covers the body even though the body
itself never crosses the wire in the event. New blob-transport endpoints
(`/api/sync/blobs/check|get`, `POST /api/sync/blobs`) let the side that opened the connection push
or pull the bytes it's missing; an hourly GC sweeps blobs unreferenced by any row or event after a
2-hour grace window. Media migrated the same way in two more phases (16a/16b): a media row now
carries its own ciphertext hash, `.enc` files are backfilled into the blob store on startup
(desktop and mobile), and new media creation stopped writing `.enc` files entirely once every row
had a confirmed blob to fall back to.

Older (protocol-1, inline-ciphertext) events keep applying unchanged. A peer still on protocol 1
is pulled from normally but not pushed to (it can't apply blob-referencing events) and still has
its position reported so its compaction accounting doesn't stall.

### Changed

#### Core is the kernel again; infrastructure moved to its own projects (2026-09-24)

`BeeMemoryBank.Core` no longer references Certes, Makaretu.Dns.Multicast, ONNX Runtime/Tokenizers,
ImageSharp or ProtectedData. Everything that consumes Core, including the MAUI app and the CLI, used to
inherit all of them.

- **`BeeMemoryBank.Infrastructure`**: ACME, Cloudflare/DDNS, UPnP/static external IP, firewall, local CA,
  mDNS announcer/browser and OS auto-unlock (DPAPI).
- **`BeeMemoryBank.Embeddings`**: the ONNX generator, tokenizer, model manager, projection and hybrid search.
- **`BeeMemoryBank.Media`**: the ImageSharp transcoder behind the new Core interface `IImageTranscoder`.
  `MediaService` now requires the transcoder, so a host that forgets to register it fails at startup
  instead of silently skipping conversion.
- **`model.onnx` path**: now `libs/BeeMemoryBank.Embeddings/Models/model.onnx`. CI's cache key was bumped
  to match.

#### Web proxy: one table-driven forwarder instead of hand-written passthroughs (2026-09-24)

Pure `/api-proxy/*` passthrough endpoints are now entries in `ProxyRouteTable`: a prefix plus per-method
role gates, deny-by-default, served by one catch-all forwarder. Endpoints with real Web-side logic stay
explicit.

- **Path handling**: the forwarder refuses any non-canonical path before table matching. That covers dot
  segments, empty segments, backslashes, encoded `/`, `\` and `.`, and control characters. It also
  checks that the resolved upstream URI still sits under the matched prefix, so a path can't be used to
  reach an API route the table doesn't list.
- **Chat SSE**: the streaming routes forward `Content-Type` with its parameters again. Previously every
  `application/json; charset=utf-8` POST got a 500.

#### Web UI: no inline JavaScript; CSP `script-src 'self'` (2026-09-24)

- **Page code**: every page script now lives in `wwwroot/js/`, and server data reaches it through
  non-executable JSON blocks.
- **Handlers**: every inline `on*=` handler is a delegated listener. Page modules bind per page instance
  and clean up on SPA navigation.
- **Big files split**: `Admin.cshtml` is split into partials and `site.js` into cohesive modules.
- **CSP**: `script-src` dropped `'unsafe-inline'`. `WebCspComplianceGuardTests` fails the build if any
  inline script, inline handler or `javascript:` URL comes back, including markup generated from JS.

#### Comments state invariants, not history (2026-09-24)

Production-code comments no longer carry reviewer names, wave/finding IDs or war stories. Each one
keeps only the invariant or reason a maintainer needs. The change is comment-only: a Roslyn token
diff over every touched file reports no code change.

#### Node trust model: a fresh join gets content authority only, not cluster-state authority (2026-09-04/05)

`POST /api/join` used to write every new peer into `tbl_whitelist` with `is_superadmin = 1` — a
phone that joined only to read notes had exactly the same power as the operator's own server: it
could revoke any other peer, hard-delete content network-wide, or trigger a destructive restore.
That default is now `0`. Membership (proven by the master password) and cluster-state *authority*
are separate grants; `PUT /api/whitelist/{nodeId}/superadmin` is the only way to promote a peer
afterwards, and it is an explicit act by a node that already holds the authority itself. Existing
whitelist rows are untouched — this changes only the default for new joins. See
[SECURITY.md](SECURITY.md#trust-model) and [docs/sync.md](docs/sync.md#trust-model) for the full
picture, including the one node that still has to be trusted on faith (the bootstrap node a joiner
first dials into).

`tbl_whitelist` is now version-stamped (`lamport_ts`/`source_node_id`, migration 021) like every
other replicated table — a node that was offline while an admin revoked a compromised peer could
otherwise put that peer right back into the mesh with its own stale `whitelist_add` the moment it
came back online, with nothing distinguishing "my revoke was undone" from "my revoke never
applied". Rows revoked *before* this migration default to version 0 and get a narrower, permanent
carve-out: an incoming remote `whitelist_add` can never outrank one of those (a local, deliberate
re-add by an admin still can).

Changing the master password now says what it actually did instead of nothing: it rewraps the key
slot on the one node you changed it on (slots are node-local, never synced), and every other node
silently keeps accepting the old password at its own `/api/join` until changed there too — the
endpoint now reports how many peers are still on the old password.

#### One idiom for "superadmin only", and the node decides its own public surface (2026-09-04)

The superadmin role gate had been written four different ways across `Endpoints/` — a raw
`X-User-Role` header compare, `CallerIdentity.Extract(ctx).IsSuperadmin`, per-file
`IsSuperadmin`/`Forbidden` helpers, and the actual filter — and five endpoints turned out to have
no gate at all (`POST /api/admin/compact`, whitelist revoke/update/address, quarantine clear). A
single `RequireSuperadmin()` endpoint filter now replaces every unconditional check, with a
guardrail test asserting both the metadata *and* a live 403 for every mutating route. Separately,
`PublicSurface` gives the node its own authoritative list of what a caller **without**
`BMB_INTERNAL_KEY` may reach — previously this was decided entirely by whatever reverse proxy sat
in front (Apache on a server, a YARP table on the desktop node), two hand-maintained lists that had
already drifted out of sync with each other and with the actual endpoint set. Anything not on the
list now answers `404`, not `403` — "this exists but you may not use it" is itself information
worth withholding from an anonymous prober. See [docs/deployment.md](docs/deployment.md#reverse-proxy--what-is-exposed).

#### Bounded, explicit system-scope elevation via `RunAsSystemAsync` (Fable #6, completed 2026-09-05)

The `CallerScopeHolder.ElevateToSystem()` disposable introduced earlier in this file's own
"Changed" section above closed the *forgotten-restore* failure mode; it didn't remove the
ambient-mutation shape itself — eight call sites still assigned the system scope by hand and relied
on `using`/`try`-`finally` discipline to put the caller's real scope back. All eight (remote-event
apply, folder ACL bootstrap, folder auto-vivification, the MCP write-tool ACL bootstrap, copy
rollback, remote-account sync, and the sync push endpoint that had no restore at all) now go
through `RunAsSystemAsync(delegate)` — the elevation is scoped to exactly one delegate's execution,
not to "until something remembers to undo it". The existing guardrail test now fails on *any*
hand-written `.ElevateToSystem(...)` call outside `CallerScopeHolder.cs` itself, not just a
reappearing field assignment.

#### Sync quarantine: persistent, and Permanent vs. Deferred (2026-09-05)

A single 5-consecutive-failure budget quarantined a forged signature and "the sender's
`whitelist_add` hasn't arrived here yet" identically — the latter resolves itself given time, but
was being discarded exactly as fast as the former. `SyncFailureClassifier` now sorts an apply
failure into `Permanent` (bad signature, malformed payload — quarantined immediately) or
`Deferred` (a missing precondition — whitelist/blob/rotation-ordering — retried for hours on a
wall-clock budget immune to attempt-count inflation from push-triggered retries) via an
`IDeferrableSyncFailure` marker. `tbl_sync_quarantine` (migrations 013, then 022 for the
Permanent/Deferred split) tracks both counters on one row and survives a restart, and quarantine
state is now cleared by a standalone restore and stripped from join snapshots (it was leaking this
node's raw exception text to a joining peer). A revoked event originator is now always treated as
a permanent rejection rather than a retriable one, closing a window where re-adding a revoked peer
inside the retry budget would have quietly replayed its entire pre-revocation backlog.
`POST /api/whitelist/backfill-revokes` (superadmin, requires an unlocked session) heals legacy
revoke rows that predate the row-versioning migration above and never got their own
`whitelist_revoke` event to anchor a version against.

### Fixed

#### 2.2.0: fixes after the 2.1.0 review, the macOS sign-in loop (2026-10-05)

No sync protocol change (still 3) and no ciphertext change; nodes of 2.0.x, 2.1.0 and 2.2.0 keep syncing with each other in both directions. One schema
migration (035, a unique index for the OS auto-unlock slot, created by a repair on first use). Each fix below has a test that fails without it.

- **macOS app: signing in no longer loops back to the login page.** The app shows the web UI in WebKit at `http://127.0.0.1`, and WebKit drops a `Secure`
  cookie from there, so a correct sign-in redirected to `/Tree` and straight back to `/Login`. The session cookie now loses `Secure` only for plain HTTP
  from this machine itself (a loopback peer, a loopback host name, no forwarding header left over from another proxy); HTTPS, any other address and
  anything that came through a proxy keep `Secure`.
- **The setup page no longer reloads itself every two seconds** (the restore panel's refresh tag was emitted on every visit).
- **Lock on sleep is now an option, off by default.** The 2.1.0 desktop apps locked the vault every time the computer slept, which sent you back to the
  login page after every wake-up. The setting "Lock the vault when this computer sleeps" (Settings, Power; Windows and macOS) is off unless you turn it
  on, is read at the moment of each sleep, and sends nothing while it is off. `SECURITY.md` describes it.
- **Sync keeps the events it has not delivered.** The push cursor moves only over the contiguous run of applied or deliberately dropped events, so an event
  that failed to apply is retried instead of being stepped over when a later event of the same chunk went through (both sides: upgrade the receiving
  node too, an older receiver still reports the old, non-contiguous value). A chunk of events the peer drops no longer stalls the cursor. A single event
  that is too large for the peer is skipped for good and now shows as quarantined. Deleting a media file is one transaction and wakes the sync loop.
- **OS auto-unlock:** two concurrent enables can no longer leave two slots. Duplicates that 2.1.0 may have left are repaired on first use: the slot that
  opens with the OS-stored secret is kept, the others are removed, then the unique index is added.
- **Re-key:** the internal key file is flushed to disk and, on Linux and macOS, owner-only.
- **Android blind app:** one run at a time (no repeated "Back up now"), the activity and the status screen go through the safe controller, "Disconnect and
  wipe" waits for running work and an aborted wipe re-enqueues the jobs, sync is visible in the status, the blind role's services are registered in one
  place, and the AndroidX package warnings are gone.
- **Node and web:** the `/node/*` endpoints refuse any request that carries a forwarding header; the local CA keys on Windows are bound to a purpose
  entropy (with a fallback for keys written by older versions); the node's lifecycle resources are released reliably; the strong-box check no longer throws
  when the session locks during its identity lookup; a damaged secret scope explains what deleting it means.
- **Updates, snapshots and tools:** a cancelled update check is no longer reported as failed, a missing Velopack locator is a clear error instead of a
  null reference; an encrypted snapshot database over the 2 GiB in-memory limit is refused with a clear message instead of failing later; the DEK-rotation
  proposal wipes the key-encryption key on every failure path; the macOS command runners kill the whole process tree on a timeout and wait for it; the
  blind timer scheduler no longer disposes its cancellation source under a running loop; `scripts/pack-android-blind.ps1` always checks the signing identity.

#### 1.0.12: fixes from the review rounds (2026-09-29)

The blind-node, recovery and re-key work went through several review rounds; each fix has a test that
fails without it.

- **A blind node paired again after a re-key never synced.** The full node asked it for events from
  nothing and was refused on every cycle, so nothing was pushed either. A full node that holds no pull
  position for the blind node now takes the blind node's own checkpoint as its pull position, once, and
  only when the answer is the "position too old" refusal and its checkpoint does not contradict the head it
  reports; the Blind nodes page shows it as "Adopted checkpoint N". A position the node already holds is
  never moved by a refusal, and any refusal that is not taken stays a refusal, worded as the blind node's
  (a refusal from any other kind of peer still asks for a wipe and rejoin).
- **Sync:**
  - the log is cut by what a peer acknowledged, not by what it was served, so a slow peer no longer
    loses events it never received;
  - an event of our own pulled back from a peer is skipped only when it is proven ours, and a forged
    event that names us is not skipped;
  - a pull that brings nothing still counts as a pull;
  - a position a peer reports must fit the log it claims to have read;
  - the owner-flow mark lasts one apply, not the whole flow.
- **Blind nodes:**
  - a completed reseed wipes the database it replaced, and a reseed waits for a running backup;
  - the cutover writes and deletes only its own plain paths, and never through a link;
  - the wipe takes a barrier for every writer before it deletes anything, and a wiped node gets its
    identity back before the wipe answers;
  - a cutover marker nobody can read is unresolved, not absent, and a key file a crash left empty or torn
    no longer stops the node;
  - "already set" and "wrong password" are different refusals, and the key file's temporary path is
    unpredictable and never a link;
  - the status counts what the node holds, and a network of one PC and a blind node bootstraps its
    authority from the pair code.
- **Recovery:**
  - an inactive recovery box keeps no key material;
  - a box change is one transaction, and trimmed boxes take their events with them;
  - a half-written restore is not an initialized node, and a restore that stopped resumes instead of
    repeating itself.
- **A database that already holds a node is never initialized over,** and a vault from before the
  password unification counts as an initialized vault.
- **Re-key:**
  - every Api process holds the vault lease, and the verb refuses while one runs;
  - every `bmb` command passes the vault gate before it opens the data, and a swap is resolved only with
    the lease held exclusively;
  - the swap journal and its renames are durable, a lost journal is recognised, and the journal is never
    trusted: only the swap's own siblings are moved;
  - a vault reached through a junction or symbolic link is refused, and the re-key never follows one out
    of the data folder;
  - the node completes its first start after a swap, and the old internal key is not carried over;
  - the pre-flight is read-only, fails closed, and refuses what the re-seal would fail on.
- **Search:** a full rebuild drops the in-memory index too.

#### 1.0.11: key rotation, protected comments (2026-09-27)

Second pass on the three-node test stand, this time through master-key rotations and a revoked
phone. Each fix has a test that fails without it and was re-run on the stand.

- **Articles written before a key rotation could become unreadable after a restart.** A node
  keeps the previous master keys so that bodies still arriving under an old key open, but it kept
  them in memory only: after a restart, a late body from a slow device failed to open for good.
  Retired keys are now stored in the profile, each wrapped under the key that replaced it, so the
  chain survives restarts and later rotations.
- **Android did not follow key rotations started on another device** and stopped syncing after
  one: the automatic accept was off for the other superadmin devices. It is now on for them, like
  on the other clients.
- **Comments on a protected article are sealed under its passphrase,** like the body. Before,
  anyone who could open the vault read them, and the comments API listed any article's comments
  to any caller, including a user denied the folder; it now applies the folder rules too. Without
  the unlock a comment shows as locked and a new one cannot be added; protecting, unprotecting or
  changing the passphrase re-seals the existing comments and keeps their dates. Comments written
  before this version are sealed the next time the article is unlocked.
- **Plaintext comments from older Android builds** are encrypted the next time the node is
  unlocked, and their plaintext events are removed from the event log.

#### 1.0.10: sync fixes from a three-node test (2026-09-27)

A web node, the Windows app and the Android app were run against each other on a test stand
(a hub in Docker, the app in a Windows VM, a phone) through creates, edits, moves, deletes,
tags, comments, media, protected articles, offline conflicts, long offline periods, load,
lock/restart and password changes. Six real faults turned up; each is fixed with a test that
fails without the fix, and the scenario was re-run on all three nodes.

- **Folder renamed or deleted on another node:** a folder that one node had created as a mere
  parent (for a synced subfolder or article) has a different id on every node, and a rename or
  delete of it arriving over sync looked the folder up by id only. The rename moved the
  subfolders and article paths but left the folder behind; the delete did nothing. Both now fall
  back to the path.
- **Android comments were stored and sent unencrypted,** and every other node then refused them,
  so they showed up nowhere but on the phone while their text sat in every node's event log.
  Comments written elsewhere showed up on the phone as empty lines. The phone now encrypts,
  decrypts and deletes comments like every other client; a source check keeps comment creation
  in one place.
- **A folder deleted on one node while an article in it was edited on another** split the nodes
  for good: one revived the folder, the other moved the article to the root. The article now
  stays in its folder (which stays or comes back) when the edit is newer than the delete, and
  goes to the root when the delete is newer, whatever order the two arrive in.
- **Saving about once a second** started a sync cycle per save, each with a fresh handshake, and
  the hub answered 429 for minutes. Cycles now start at most every 3 seconds.
- **Signing in right after a password change** bounced back to the sign-in page for up to five
  minutes (a cached security stamp); the sign-in now refreshes it.
- **Admin → Security → Change master password** changed only the key slot, not the sign-in: the
  new password was refused at sign-in and the old one signed in without unlocking, so after the
  next lock or restart the node could not be opened from the web. It now changes both, as
  Profile → Change password does.

#### 1.0.9: every page works in a narrow or short window (2026-09-26)

The web UI (the same pages the Windows app shows) had almost no rules for small windows. At
about 800 px the logo broke onto three lines, header links ran off the edge, and toolbar
buttons were cut off or out of reach — body hides overflow, so they could not even be scrolled
to. Checked page by page at 360–1366 px wide with a new audit script
(`tools/layout-audit/shot.py`: screenshots plus automatic detection of page-wide sideways
scroll, controls past the edge and clipped boxes) and three independent reviews of the shots.

- **Header:** the logo never wraps; below 1280 px the links become icons with tooltips, below
  720 px they move into one menu button (with Logout), below 480 px the logo shows the bee only.
- **Folder tree:** below 720 px it is a drawer over the page, closed by default, opened from a
  header button, closing itself after a pick or a tap beside it, with the page dimmed behind it;
  between 720 and 1024 px it is capped at 260 px. The wide-window collapsed/expanded preference
  is not touched by the narrow-window drawer.
- **Content column** can shrink (`min-width: 0`), so one wide table or long title no longer
  widens the whole page; tables inside cards scroll sideways instead of cutting off their action
  columns (Admin nodes/agents/models/snapshots, Users, Roles, Profile, Folder, Tags).
- Rows of buttons and title bars wrap: the home page actions, the article toolbar, Graph, Tags,
  Users, Roles; the Internet Access address grid folds into one column.
- The AI page hides the tree button (it has no tree) and keeps its heading clear of the chat
  history tab.
- The Windows app window can now be made as small as 420×480 (it was 800×600).

#### Desktop 1.0.8: an update window, no second sign-in, a shorter first run (2026-09-26)

- **"Check for updates" shows what it is doing.** The tray item (and Settings → Check now) used to
  close the menu and do the check invisibly; the result only appeared as a new tray item later. It
  now opens a window: checking, then downloading with a percent, then "You're up to date" or
  "Version X is ready" with **Update now**. Closing the window cancels the check or download.
  "Restart to update" in the tray opens the same window, which says the app is about to restart.
- **An update no longer asks for the password again.** Right before the node stops for an update
  the app hands the open vault to the next start: the master DEK is written DPAPI-protected (current
  Windows user) with a 10-minute expiry to `update-unlock.dat`, and the restarted API deletes the
  file first and uses it only if the delete worked, the expiry holds and the key matches the
  sentinel. See [SECURITY.md](SECURITY.md).
- **A locked vault goes straight to the login page.** After a restart the web cookie was still
  valid while the vault was locked, so the tree rendered, its folders even loaded, and the first
  click on an article bounced to /Login. Page navigations now check the lock first.
- **First run:** the username comes pre-filled as `admin` and the computer's name as the name
  others see; finishing setup signs you in and opens the app instead of a "done" page and the same
  login again; "network" and "node" are gone from the texts; no step indicator; and the join form
  no longer scans the local network by itself (the "Find devices on my network" button still
  does, on request).

#### Desktop 1.0.7: sign-in in the app window, profiles, settings, a simpler first run (2026-09-26)

From a hands-on test of the Windows app as an ordinary user would meet it.

- **After a restart the app window stayed black and the sign-in page opened in the external
  browser.** The node's front proxies to the Web process with that process's own host:port, and
  cookie authentication built an absolute `/Login` redirect from it. The window treated the
  internal port as a foreign site and handed it to the default browser; every start that needed a
  sign-in did this. The login and access-denied redirects are now relative.
- **Profiles instead of "storages", in English like the rest of the app.** The tray menu was half
  English, half Russian; it now reads Open / Profiles / Settings… / version / Exit. The Profiles
  submenu and window can create a profile, **add an existing profile folder** (checked to really
  hold a vault) and **move a profile's data folder**: the node is stopped, the vault is copied and
  verified, the profile is repointed and started again, and on any failure it keeps (or gets back)
  its old folder. The old copy is kept unless the user chooses to delete it.
- **One Settings window, opened from the tray:** start with Windows, which profile opens at
  startup, keep the computer awake, check for updates automatically / now, restart to update.
  Settings owned by different parts of the app no longer overwrite each other's keys in
  `desktop-settings.json`.
- **Dialogs grow with their content and scroll.** They had a fixed size, so opening "Advanced"
  pushed the buttons out of the window. Buttons now sit at the bottom and always stay visible.
- **First-run setup has one way forward per step.** The "Connect an AI agent now" button and the
  AI search note are gone from the last step (agents are added later from the running app). Step
  one offers three paths: create a network, join one by URL, or **open an existing profile** — in
  the app a folder picker, in a browser the existing copy-from-path form.
- **Desktop updates download a delta.** CI seeds the release folder with the latest published
  release before packing, so an update fetches a few MB instead of the full ~190 MB package.
- Android: the app version comes from `VERSION` (it was always 1.0), the server address on the
  setup screen starts empty instead of a half-typed domain, the app leaves the content pages
  the moment the vault locks rather than only on the next resume, and a folder page shows an
  article created in it right away instead of after the folder is opened again. The Maestro flows
  run against a local test stand again (`mobile/maestro-tests/stand`).
- A failure early in node initialization is no longer hidden behind an `ArgumentNullException`
  from the key cleanup in `finally`.

#### No firewall prompt on a fresh desktop install; `/api/dek-rotation/accept` answers 409 when busy (2026-09-25)

- **Windows Firewall no longer asks about BeeMemoryBank.Api after installation.** The prompt came
  from the mDNS announcer's multicast socket, the only one not on 127.0.0.1. On the desktop it was
  also useless: the announced port (the front's HTTP listener) is loopback-only, so no peer could
  connect. bmbd now passes `BMB_MDNS_ENABLED=false` to Api unless the front's opt-in HTTPS listener
  is on, and Api skips the announcer. Standalone and Docker deployments announce as before.
- **A DEK rotation accept sent while another one holds the lock is refused with 409.** The endpoint
  claimed the lock inside its background task, answered 202 first, and a refusal was only logged:
  the accept silently never happened and `/progress` kept showing the previous result. A failed
  accept publishes Failed just before it releases the lock, so a retry clicked the moment Failed
  appears hit exactly this. The web UI and CLI already show a 409 as an error. This was also the
  cause of the intermittent CI failures of `DekRotationHookMandatoryTests`, together with the
  unlock's background sweep of pending auto-accepts running late on a slow runner.
- Releases built by CI up to 1.0.6 carry no delta package, so updating to them downloads the full
  package (~190 MB); 1.0.7 fixed that.

#### Windows desktop install: self-updating, half the size, starts with Windows (2026-09-25, 1.0.5-1.0.6)

- **The desktop app updates itself from GitHub releases.** The tray "Check for updates" item used
  to POST a placeholder manifest to the node's operator update pipeline and could only ever report
  a signature failure. It now asks Velopack for the newest published release, checks quietly two
  minutes after start and then daily, downloads only the delta (a few MB), and shows
  "Restart to update to X"; the update also applies on the next start if the user never clicks it.
  `BMB_DESKTOP_UPDATE_SOURCE` points it at a local folder or http feed instead, for testing.
- **One .NET runtime instead of five.** Every exe was self-contained and carried its own ~70 MB
  runtime. They are now framework-dependent with `AppHostRelativeDotNet`, and the installer ships
  one runtime in `dotnet\` inside the app folder: nothing is installed machine-wide and no admin
  rights are needed. The Web process no longer loads the ONNX runtime (`Int8Quantizer` moved to
  Core, so Storage no longer references Embeddings), and the CLI uses the API's `model.onnx`
  instead of its own 113 MB copy. Setup.exe 441 MB -> 193 MB, installed payload 874 MB -> 488 MB.
- The WebView2 profile moved out of the app folder (it sat in Velopack's `current\`, which every
  update replaces) to `%LOCALAPPDATA%\BeeMemoryBankData\webview2`.
- A fresh install now registers autostart (the tray toggle can still turn it off); updates keep the
  user's choice, and uninstall removes the Run entry instead of leaving it pointing at a deleted exe.
- Logs from `bmbd`, Api and Web are UTF-8: a redirected console used the OEM codepage and turned
  non-Latin text (a Cyrillic display name, for one) into `????`.
- mDNS announce no longer fails for a display name with non-ASCII characters (the TXT `name` is sent
  percent-encoded behind a `u8:` prefix; ASCII names are unchanged).
- The server MSI embeds its cabinet (it used to need external `cab1.cab`/`cab2.cab` next to it and
  installed nothing on its own) and ships the same shared runtime.
- New `release-windows.yml` workflow builds the installer on a `v*` tag and attaches it to a draft
  GitHub release. `pack-windows.ps1 -OutputDir` builds into a separate folder.

#### Review fixes: auth, KDF, ACL, crypto framing (2026-09-24)

- **Non-ASCII display names broke every Web→API call.** HttpClient rejects non-ASCII header values, and
  the Web layer forwarded the display name in a header. It no longer does, and a real-socket test
  covers it, because TestServer hid the bug.
- **Anonymous MCP callers.**
  - An unauthenticated request could reach tools such as `bee_save_media`; the MCP identity gate now
    returns 401 before any tool runs.
  - Uploads not linked to an article fail closed.
  - A new "anonymous internet caller" test persona sends no internal key and no role.
- **`/api/join` was unthrottled from loopback.** Only a valid internal key exempts a caller from rate
  limiting now, not a loopback address.
- **An article update accepted a blank title.** It no longer does.
- **Revoked peers kept a valid sync token for up to an hour.** Every token-authenticated sync endpoint,
  including the snapshot file download, now re-checks that the peer is still whitelisted.
- **Argon2id had no concurrency cap and trusted peer-supplied parameters.**
  - Derivations now go through a memory-budgeted FIFO gate. A full queue returns
    503 (`KdfBusyException`) instead of piling up threads.
  - KDF parameters from a join peer or a stored blob must lie within 32–256 MiB, 2–10 iterations and
    1–8 lanes.
  - Protected-article passphrase attempts are throttled per caller and article.
- **Folder ACL matching and subtree operations are case-exact.**
  - SQLite `LIKE` folds ASCII case, so subtree queries, including hard delete and its preview, reached
    case-variant sibling folders.
  - Subtrees are now BINARY ranges (`[p + "/", p + "0")`), and every ACL comparison is ordinal.
- **Article, media and comment crypto framing is defined once.**
  - `EnvelopeFraming` is the single definition of the framing.
  - Existing rows decrypt with the full candidate-DEK set, which includes the retired DEK during a
    rotation.
  - A comment's framing is detected by trial rather than inferred from its parent article.
- **Exception handler order and hard-coded versions.** Specific exception mappings now run before the
  generic handler. The node status reports the real assembly version.

#### DEK rotation no longer destroys agent keys and chat.db it has no reason to touch (2026-09-24)

- **Every agent key was deleted on every rotation.** The rewrap ran `DELETE FROM tbl_agent`, on the
  initiator and on every auto-accepting peer — so on a shared node every teammate's MCP client was
  silently disconnected. Only agents that carry a wrapped Master DEK (superadmin-owned auto-unlock
  agents) are now removed and must be re-issued; every other agent holds no key material and keeps
  working. The progress/audit texts, the MCP "agent key not recognized" message and the Admin
  rotation dialog say so.
- **Chat history, attachments and stored LLM provider keys became undecryptable after a rotation.**
  chat.db is a separate file the rotation transaction cannot reach, and its rows were sealed
  directly under the Master DEK. They are now sealed under a node chat key whose wrapped form lives
  in the new node-local `tbl_node_data_key` table (migration 026) and is re-wrapped inside the
  rotation transaction on the initiator and on peers. Existing rows are migrated in the background
  (and forcibly right before every rotation); until then they still open with the current or a
  retired Master DEK. A restored database whose chat key no longer matches chat.db degrades to
  placeholders rather than errors. The pre-rotation move is mandatory: if any chat record is still
  under the Master DEK (or the move fails) the rotation refuses to start — a 400 at propose, or on a
  peer the rotation stays pending and is retried on the next unlock.
- **Remote-account tokens were broken by every rotation.** `tbl_remote_account` bearer tokens are
  sealed directly under the Master DEK and were never re-encrypted; they are now re-encrypted inside
  the rotation transaction, and a token write that races a rotation re-checks the sentinel inside
  its own transaction and re-seals under the new DEK.
- **A deferred rotation no longer strands a node.** If the mandatory pre-rotation step fails at the
  initiator's accept (after the COMMIT is public), the commit stays pending and can be accepted
  again; a deferred peer retries on its own with bounded backoff while it stays unlocked.
- **"Completed" is reported only after maintenance mode ends**, so acting on it never gets a 503.

#### Security review findings, 2026-09-03/04: key material, node reset, chat history, write races

- **Critical: a recovery key's own bytes were its key slot's salt.** `AddRecoveryKeyAsync` derived
  the recovery slot's KEK using the recovery key itself as the Argon2id salt, so
  `tbl_key_slot.salt` literally *contained* the recovery key in plaintext — anyone with read access
  to the database file could reconstruct it and unwrap the vault with no password at all. Fixed to
  use an independent random salt, matching every other slot type. Migration 012 deletes existing
  recovery slots (the exposure already happened and cannot be un-done retroactively); generate a
  new one from the Admin page.
- **Unbounded Argon2 parameters in a protected article's passphrase blob.** `ProtectedContentCodec`
  read memory/iterations/parallelism straight out of the (attacker-controlled) blob with no bounds
  — a crafted article could demand a multi-terabyte allocation on unlock attempt. Now bounded the
  same way key-slot KDF parameters already are.
- **A protected article's cached passphrase survived Lock().** The 20-minute unlock cache was only
  cleared by the `/lock` endpoint, not by every path that calls `Lock()` — so a previously-verified
  passphrase kept working through a lock/unlock cycle within its TTL.
- **Changing a password was two unguarded statements, not one transaction.** A crash between
  creating the new key slot and deleting the old one left both passwords permanently valid.
- **DPAPI-protected auto-unlock secret had no entropy parameter** — any process running as the same
  Windows user could unwrap it with a bare `ProtectedData.Unprotect` call.
- **Re-checking the master password before a dangerous operation used to unlock the whole vault as
  a side effect**, even when the operation then failed — because it called the same `UnlockAsync`
  used for a real login. `SessionService.VerifyMasterPasswordAsync` checks the password without
  that side effect; `/api/init/reset`, snapshot restore, the whitelist endpoints, and the network
  restore initiator all use it now. `UnlockAsync`/`UnlockWithDek` are pinned by an architectural
  test to the small set of files that are genuine entry points (session endpoints, CLI, mobile, OS
  auto-unlock).
- **`/api/session/unlock` and `/api/join` accepted a password from *any* key slot**, not just a
  superadmin's — a "user"-type slot is now honored only if its owner is currently an active
  superadmin (`recovery` and legacy pre-user-table `password` slots are exempt by design).
- **Two anonymous, unauthenticated node-wipe vectors closed:** a *"Reset & rejoin"* form on the
  locked Login page, and an unauthenticated `POST /api-proxy/init/reset` — both let anyone who could
  load the login screen grind the master password and destroy the node on a correct guess. Reset
  now lives behind the superadmin-only Admin page (and rejects an agent's bearer token even from a
  superadmin's own agent) plus a host-only `bmb init reset` CLI command for when nobody can sign in.
  Both share one `NodeResetService` in Core. See [docs/deployment.md](docs/deployment.md#resetting-a-node).
- **Every per-IP rate limit was one shared bucket behind Docker** — published-port traffic all
  arrives from the bridge gateway, so an anonymous caller could exhaust the login or sync-challenge
  budget for the whole mesh with one stream of requests. `BMB_TRUSTED_PROXIES` declares which
  hop's `X-Forwarded-For` to believe; see [docs/deployment.md](docs/deployment.md#environment-variables).
- **Anonymous browser traffic through the Web layer was never rate-limited at all** — the API's own
  limiter deliberately skips loopback callers (which is what every browser request looks like once
  it reaches the API through Web), so `/Login`, the master-password reset form, and the proxy reset
  route had no limit on either side. A new `PublicRateLimitMiddleware` in the Web pipeline closes
  the gap with the same sliding-window semantics as the API's own limiter.
- **`probe-relay` followed HTTP redirects** — a validated public hostname could still 302 the probe
  onto a loopback or metadata address, leaking a status code for an internal target. Fixed with a
  dedicated `HttpClient` that disables `AllowAutoRedirect`.
- **Two stored-XSS vectors.** An uploaded SVG is an active document, and media was served inline
  from the same origin holding the session cookie under a CSP permitting `unsafe-inline` — opening
  one by URL ran its script in an authenticated session. Media responses (API, Web proxy, and chat
  attachments) now send `Content-Security-Policy: sandbox` + `X-Content-Type-Options: nosniff`.
  Separately, ten front-end escaping helpers used the `textContent`→`innerHTML` idiom, which does
  not escape `"`/`'`, and their output was interpolated into HTML attributes (folder/tag picker
  `data-path`/`data-name`) — a folder or tag name containing a quote and an event handler executed
  in every user's picker. All ten now escape the full five characters; three places that were
  interpolating into an inline `onclick` (where HTML-entity decoding happens before JS parsing, so
  escaping alone can't fix it) moved to `data-*` attributes with delegated listeners.
- **Chat tool-call arguments were stored in plaintext.** An earlier fix encrypted
  `chat_message.content_text` under the master DEK but missed `tool_calls_json` — and a write
  tool's arguments *are* the article body or patch being saved. Now encrypted uniformly for every
  tool call (including reads, to avoid a per-tool classification that's one more place to get
  wrong), with a background processor backfilling rows written before the fix.
- **Concurrent writes to the same article could silently lose one side's change.** MCP/chat
  append/prepend/replace were plain read-modify-write with nothing serializing the pair — two
  agents appending at once, or an agent appending while a browser edit is in flight, could each read
  the same body and the second save silently overwrote the first. A new per-article-id
  `ArticleWriteLock` puts the read and the write of `AppendAsync`/`PrependAsync`/`ReplaceInAsync`
  (and `UpdateAsync`) inside one critical section.
- **A relaying peer could rewrite an event's `EntityId`** without invalidating its signature (the
  signed payload never covered that field) — and the hard-delete gate keys on exactly that field, so
  a forged value could either silently censor a legitimate event or resurrect one that had been
  hard-deleted. `EntityId` is now derived from already-signed fields on both the write and the read
  side instead of trusted as transported data.
- **The replicated-table list was a deny-list, not an allow-list** — a table added since the deny
  list was last updated shipped to every peer, contents included, until someone remembered to add
  it (this is how `tbl_remote_api_token` — bearer tokens this node issued to remote accounts — was
  leaking into snapshots). Every table not on an explicit allow-list is now stripped, and a test
  requires every table in the schema to be classified one way or the other.

#### Custom roles with role-level folder permissions (2026-08-27)

Folder permissions were per-user only. On a vault with ~20 users, hiding one folder from everyone
meant opening all 20 users and adding the same deny rule to each — and remembering to repeat it for
every new account, or the folder leaked to the next hire.

Roles now carry folder rules of their own.

- **`tbl_role`** — seeded with the two system roles (`superadmin`, `user`, `is_system = 1`) plus any
  number of custom roles a superadmin creates. Custom roles sit at exactly the same privilege tier
  as `user`: every authorization check in the codebase tests for the literal `"superadmin"`, so a
  new role can never grant an administrative capability — it only changes which folders its holders
  see.
- **`tbl_role_folder_acl_entry`** — the same rule shape as the per-user ACL table, keyed by role.
- **Effective rules = union** of the holder's role rules and their own per-user rules; the existing
  deny-wins prefix matcher then runs unchanged over the merged sets. A per-user allow cannot reopen
  a role deny, and a read-only marking sticks whichever side sets it.
- **The headline case needs no custom role at all:** rules can be attached to the built-in `user`
  role, so "hide `/HR` from every regular user" is one rule and zero per-user edits.
- **`base_policy` (`open` | `closed`)** makes "this role has no allow rows" explicit instead of
  implicit. New custom roles default to `closed`, so a role assigned before its rules are finished
  cannot expose the vault; the built-ins stay `open`, which is exactly their historical behaviour.
- **Roles do not inherit from one another** and **`Role.Name` is immutable** — a rename would need
  one transaction spanning two repositories that each open their own connection, and a half-applied
  rename leaves users pointing at a role that no longer exists, which resolves fail-closed.
- Agents inherit their owner's role rules for free: `AgentAuthMiddleware` already resolves the
  owner's user id, and every ACL consumer funnels through the same resolver.
- New superadmin UI: a **Roles** page (create/edit/delete, folder rules per role) and role-aware
  dropdowns on **Users**. The folder-access dialog moved to a shared partial so the two pages
  cannot drift.

Guards, each with a regression test: reserved and malformed role names refused (a role differing
from `superadmin` only in case would be unprivileged to `CallerIdentity`'s ordinal check and
privileged to the Web layer's case-insensitive one); system roles cannot be deleted; a role users
still hold cannot be deleted; folder rules are refused on the `superadmin` role and on superadmin
users, because superadmins bypass them and the rule would be silently inert; role names are stored
canonically, since a mis-cased `tbl_user.role` would pass a NOCASE lookup and then fail every
ordinal comparison downstream.

### Changed

- **Scope elevation is a `using`, not a convention.** Running as `SystemCallerScope` — no folder
  ACL, no read-only guard — was written out by hand at eight sites: assign the scope, then remember
  a `try/finally` to put the caller's real one back. Forgetting the restore fails silently, and
  under the HttpContext-backed scope store it leaks full-vault access to the rest of the HTTP
  request. `CallerScopeHolder.ElevateToSystem()` now returns a disposable that restores on exit;
  a test scans `libs/`, `server/`, `desktop/` and `mobile/` and fails if a hand-written
  `Scope = SystemCallerScope.Instance` reappears. One of the converted sites (the sync push
  endpoint) had no restore at all.
- **Every MCP tool must be classified as content-touching or not.** An MCP tool that reads or
  writes encrypted content without `[RequiresUnlockedSession]` does not error while the vault is
  locked — it returns *empty results*, which an agent reads as "the vault is empty". The existing
  tests asserted the classification of tools someone remembered to list, so a new tool was in
  neither list and passed both. The registry test now requires every registered tool name to
  appear in one of two curated sets, so adding a tool fails until its side is chosen deliberately.

### Fixed

- **BREAKING (sync): the unbound V1 challenge signature is gone from both ends.** Peer
  authentication used to fall back to the pre-audience-binding `BMB-CHALLENGE-V1` payload whenever
  a peer's challenge response carried no `ServerNodeId`, and servers still verified that payload.
  Whether the field is sent is the *responding* peer's choice, so "old peer" and "attacker" were
  indistinguishable: omit one JSON property, hand the victim a challenge fetched live from node C,
  and redeem the resulting unbound signature at C as the victim — the exact relay attack the
  binding exists to prevent. The client now signs only `BMB-CHALLENGE-V2` (audience-bound) and
  refuses a challenge that declares no `ServerNodeId`; the server verifies only V2.
  **Every node in the mesh must run this build or newer to sync — including the Android app.**
- **The sync audience anchor is now a required parameter, not a lookup.** `SyncClient.SyncWithAsync`
  takes the peer's NodeId from the caller (which is already iterating `tbl_whitelist`) instead of
  reverse-matching `remoteApiBase` against `api_address`. The lookup silently resolved to "nothing
  pinned" whenever the address was passed in a different shape, and the only safe response to
  "nothing pinned" is refusal — so a string mismatch would have become a sync outage.
- **Migration 015 disarms agent keys revoked before that fix.** 014 cleared key material by the
  owner's *role*, which is the right rule for the hybrid model but says nothing about revocation —
  so a superadmin's already-revoked agents passed the role test and kept their wrapped DEK.
  Confirmed on a live node right after 014: four revoked keys still armed.
- **A revoked agent key stayed a vault key.** Deleting an agent only flipped its status, and
  clearing a demoted superadmin's agents skipped already-revoked rows, and deleting a superadmin
  account cleared nothing at all. In each case the row kept its wrapped master DEK, so the old
  `bee_…` string plus a copy of the database file still decrypted the whole vault. All three paths
  now wipe `encrypted_dek`/`dek_iv`/`salt`.
- **`tbl_sync_quarantine` leaked between nodes.** It was neither cleared by a standalone restore
  (pre-poisoning the restored node against events it had never tried) nor stripped from join
  snapshots (shipping this node's raw exception text to a joining peer).
- **Finding H6: every agent key was, cryptographically, a key to the entire vault.** Creating an
  agent wrapped the master DEK with that agent's API key regardless of who owned it, so a
  self-service agent minted by an ordinary, folder-restricted user (limit 20 per user) unwrapped
  the SAME master DEK as a superadmin's — the folder ACL and read-only flag are enforced only in
  software, over already-decrypted content, never by the key material itself. Anyone holding such
  a key, plus any copy of the database file (a backup, a decommissioned disk), could decrypt every
  article in the vault, including folders that key's own owner has no web access to.
  Now only an agent owned by a superadmin gets a wrapped master DEK and can auto-unlock a locked
  node (`Agent.CanAutoUnlock`) — a superadmin can already unlock the vault through the web UI, so
  their agent doing it too adds no capability. Every other agent authenticates and works exactly
  as before whenever the vault is already unlocked by someone else; it just can't unlock it itself,
  and a stolen database file yields nothing usable from its row alone.
  Demoting a superadmin now strips the wrapped DEK from every agent they own — a demoted user must
  not keep a key that can still unlock the vault. Promoting a user does NOT retroactively wrap
  their pre-existing agents' keys: that would need the plaintext API key, which is shown only once
  at creation and is not recoverable from `key_hash`; only a newly created agent gets wrapped.
  Migration `014_agent_dek_optional.sql` clears the wrapped DEK from every existing agent row
  whose owner isn't a superadmin (irreversible, for the same reason) and relaxes
  `encrypted_dek`/`dek_iv` to nullable. The Admin and Profile agent tables now show whether each
  key can wake a locked node.
- **A role name typed with capitals was refused instead of accepted.** The restricted alphabet
  exists so that no role can differ from a privileged one only by case — storing the name
  lower-cased delivers exactly that, while rejecting `OneFolder` outright turned an ordinary typo
  into a dead end. Names are now folded to lower case before validation (the reserved-name list
  still stops `SuperAdmin`, and characters outside the alphabet are still refused with a message),
  and the Add Role field normalizes as you type so the stored name is never a surprise.
- **A failed request in the Users and Roles dialogs could show a bare "Request failed".** When a
  response body was not JSON with an `error` field — an empty 500 from a deserialization failure,
  for instance — the message carried no status and no server text. Both dialogs now show the HTTP
  status and whatever the body contained, and the role proxy routes translate a malformed body
  into a real message instead of an empty 400.
- **Folder ACLs resolved for an unidentified caller granted full access.** `GetFullAccessInfoAsync`
  returned empty sets for a null user id, which `IsAccessDenied` reads as "no restrictions". The
  endpoints that consume those sets directly (`ArticleEndpoints`, `TreeEndpoints`, `CopyEndpoints`,
  …) bypass `CallerScopeMiddleware`'s own fail-closed default, so an agent whose owner could not be
  resolved saw the whole vault. Now deny-all, matching the middleware. A user id that no longer
  resolves (deactivated, deleted) fails closed the same way.
- **A folder rename arriving over sync left stale ACL paths.** The cache stores resolved paths, not
  folder ids; `FolderService` invalidated on local renames but `EventApplier` did not, so a rule on
  the old path kept being enforced against a path that no longer existed while the folder became
  reachable under its new name.
- **The folder-ACL cache was keyed by user id alone.** User ids restart at 1 in every database, so
  two vaults open in one process answered for each other's users. Keys are now namespaced by
  database (`IDbConnectionFactory.DatabaseId`).
- **A user's cached folder rules survived their own role change** for up to the 60-second TTL —
  permissive-stale whenever the new role was the more restricted one.
- **`RemoveSlotAsync`-style dangling reference, for roles:** deleting a role out from under its
  holders is refused rather than silently stranding them on a role that resolves to no access.
- **The folder-ACL cache survived a node reset and a snapshot restore.** Both replace the database
  under an unchanged path while user ids restart at 1, so the per-database key namespacing does not
  help — a node re-initialized inside the 60-second TTL handed the new account the wiped account's
  permissions. Cleared explicitly in the reset handler and in both restore paths.
- Role and role-rule rows are stripped from filtered snapshot variants and from a node wipe, like
  users and per-user ACLs. They are cleared rather than dropped: `SecretTables` DROPs its tables and
  `tbl_migration` is not stripped, so a restored node would believe migration 009 had run while the
  tables were gone.

### Security Hardening (Pre-GitHub Audit, 6 waves + mobile)

#### Crypto / Key Management
- **Encrypted node identity (v=1):** `tbl_node_identity.ed25519_private_key` now stored AES-256-GCM-wrapped under master DEK. Fresh nodes start at v=1; legacy v=0 nodes auto-upgrade on next unlock via `UpgradePrivateKeyToV1Async`. Mobile `NodeSetupService.JoinAsync` also creates v=1 identities.
- **HKDF-derived agent keys (v=1):** New agents use HKDF-SHA256 with per-agent random salt instead of plain SHA256. `kdf_version` column in `tbl_agent` dispatches; legacy v=0 agents still authenticate. Stops cross-agent precomputation if database is exfiltrated.
- **Argon2 defaults:** 64 MiB / parallelism=4 / iterations=3 (revert from earlier reduced settings — original passwords keep verifying).

#### Sync
- **Lamport clock saturation:** `Update(remoteTs)` clamps forward jumps to `MaxJump = 10_000_000` and uses saturating add so a peer can't lock the local clock at `long.MaxValue`.
- **Tombstone LWW:** `INSERT … ON CONFLICT … WHERE excluded.lamport_ts > existing` instead of naive `INSERT OR REPLACE`. Stops out-of-order delete events from overwriting strictly-newer tombstones.
- **`EventApplyResult` enum:** `Applied` / `SilentlyDropped` / `Skipped`. Pull/push loops advance their cursor past `SilentlyDropped` events so the same poison event isn't re-fetched forever.
- **Hard-delete audit table:** `tbl_hard_delete_audit` with `lamport_ts` per entity. Survives event-log compaction; gates against late updates from peers that didn't see the hard-delete.
- **`WhitelistAddPayload.IsSuperadmin`:** Closes 3+ node cluster split-brain — without it the bit was lost in transit, receivers stored the new peer as non-superadmin, then rejected its `hard_delete` / `restore_network` events forever.
- **`WhitelistRepository.UpdateAsync`** now writes `is_superadmin` (was missing — UI updates silently reset the bit).
- **Authorization gate:** `whitelist_*`, `hard_delete`, `restore_network` events from non-superadmin peers raise `UnauthorizedAccessException` on the receiver.
- **TreePath canonicalisation:** New `TreePathCanonicalizer` rejects `..` / `.` / control chars at write paths (FolderService, ArticleService, ObsidianImportService) and at sync apply (`EventApplier.IsTreePathPayloadValid`). Cosmetic non-canonical input (`//`, trailing `/`) passes through for compat with pre-canonicalisation peers.

#### Auth / Multi-tenancy
- **Legacy agent ACL fail-closed:** `CallerScopeMiddleware` now returns deny-all when an agent has `OwnerUserId == 0` (pre-migration-004). Previously empty ACL meant "see everything".
- **`/api/articles/{id}/content` ACL gate:** Endpoint pre-fetches metadata via scope-aware `GetMetadataAsync` and runs an explicit `IsAccessDenied` check. Previously a User-role caller knowing a GUID could pull plaintext for any article.
- **Snapshot endpoint role gates:** LIST / CREATE / DOWNLOAD now require `X-User-Role==Superadmin` (restore/upload/delete already did). Stops User-role disk DoS via repeated `VACUUM INTO` and exfil of encrypted DB blobs.
- **Init password complexity:** Both standalone init and JOIN paths now run `UserService.ValidatePassword` (8+ chars with upper/lower/digit) — previously the JOIN path accepted 6-char passwords with no complexity.

#### Web UI
- **Content-Security-Policy + security headers:** Added middleware emitting CSP (`default-src 'self'`, `frame-ancestors 'none'`), `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy`, `Permissions-Policy`. CSP `connect-src` allows `data:` for Shoelace icon URIs; `style-src` allows `https://maxcdn.bootstrapcdn.com` for EasyMDE FontAwesome.
- **Auth cookie `SecurePolicy = Always`** (was `SameAsRequest`).
- **Folder picker autocomplete:** New reusable `bmbFolderPicker` (search input + Ajax `/api-proxy/folders/search` + dropdown with universal "/ (root)" option). Replaces the old radio-group "selected vs root" pattern across all create-folder / create-article dialogs and the move-folder dialog.
- **Compact path-selector breadcrumb on Article/Edit:** Path under the title is now clickable; opens a mini-dialog with the same picker. Hidden `treePath` input updates on Apply.
- **No-popup-for-new-article:** Sidebar and Folder-page "New Article" buttons jump straight to `/Article/Edit?treePath=…` — no popup. The user picks a different folder later via the breadcrumb.
- **Cancel button on every modal dialog** (17 dialogs across the UI).
- **`Folder.cshtml` onclick JS-string injection fix:** path interpolated directly into an `onclick` attribute could break out of the JS string via single quote in the folder name; switched to a JS variable reference.

#### Mobile (MAUI Android)
- **`FLAG_SECURE`** on `MainActivity`: blocks screenshots, screen recording, and recent-apps task-switcher previews of decrypted content.
- **Auto-lock on minimize:** `App.OnSleep` calls `_session.Lock()`; `OnResume` re-routes to `//unlock`. No more "snatch-and-run" full-vault access.
- **Intent-extra unlock bypass closed:** `bmb_init_password` / `bmb_unlock_password` extras in `MainActivity` are now wrapped in `#if DEBUG`. Release builds ignore them. (Maestro tests use the normal UI flow.)
- **Markdig `DisableHtml()`:** Article rendering pipeline now strips raw HTML at the markdown layer in addition to the WebView CSP `script-src 'none'`.
- **Mobile WebView CSP `img-src`** tightened from `*` to `data: blob:` (all images are inlined as data URIs).
- **Node identity v=1 on join:** Mobile `NodeSetupService.JoinAsync` wraps the Ed25519 private key with the master DEK before persist (server-side init was already correct).

#### MCP
- **`bee_get_log includeAdminEvents`:** Optional parameter that, when `true` AND the caller is superadmin, includes events without an `articleId` (whitelist_*, hard_delete, dek_rotation_*, restore_network, snapshot_checkpoint). Non-superadmin callers always get the article-only view.

#### Observability
- **Audit-log coverage:** Snapshot create/restore/upload/delete + user create/update/delete/admin-password-reset + agent create/delete now write to `tbl_audit_log`. Restore logs intent BEFORE applying so the row survives in the pre-restore state's last backup. DEK rotation already had coverage.

#### Tests
- **349/349 green** across Cli (16) + Core (106) + Crypto (22) + Storage (26) + Sync (44) + Integration (127) + Migrator (8). Added `TreePathCanonicalizerTests`, EventApplier admin-gate + payload-validation tests, MCP includeAdminEvents 3-state matrix.

### MCP Tool Fixes (External Client Compatibility & Access Control)

Filed by an external MCP client agent after real-world use against a populated vault; fixed as one batch.

- **`bee_get_log` folder-event scope leak:** `folder_create`/`folder_rename`/`folder_delete` events (which carry no `articleId`) now appear in the default (non-admin) log view like `article_*` events — closing a gap where the tool reported soft-deleted articles as indistinguishable "not found" while giving agents no way to audit folder deletions at all. The new visibility is gated by the same `IsAccessDenied(EntityId)` folder-scope ACL check every other read path enforces (`EntityId` is the folder path for these event types) — a scope-restricted agent still cannot see folder events outside its access grant.
- **`bee_list_tags` semantic-search pagination:** New `offset` parameter, threaded through both the plain substring filter and the semantic (`~query`) embedding-similarity search path. The semantic path now scope-filters candidate tags *before* ranking/windowing instead of after, so `offset` advances relative to what the caller can actually see — previously a scope-restricted caller's page could permanently skip tags it was allowed to see but that ranked just below the global top-N.
- **`bee_save_article`/`bee_update_article` tags schema:** `tags` changed from a nullable array (`List<string>? tags = null`) to a non-nullable optional array (`List<string> tags = null!`). Some MCP clients (Gemini's function-calling translation) reject the *entire* tool list when a parameter's generated JSON Schema is a `["array","null"]` union with no `items` constraint — this brings `tags` in line with the already-correct `bee_add_tags` schema. Runtime null-handling (omit vs. clear-with-`[]`) is unchanged.
- **`bee_get_article` soft-delete distinction:** Soft-deleted articles now return `"Error: article {id} was deleted"` instead of the same generic "not found" a nonexistent id gets, so an agent mirroring a tree can tell the two cases apart. Still folder-scope gated — a caller denied access to the article's folder sees "not found" either way.
- **Timestamp UTC normalization (`DapperConfig`):** The Dapper `DateTime`/`DateTime?` type handlers now coerce `DateTimeKind.Unspecified` (legacy/imported rows stored without a zone marker) to `Utc` on read, so JSON responses always carry a `Z`/offset suffix. Previously an agent computing "now minus 24h" against an offset-less `updatedAt` from older data could silently get zero results instead of an error.
- **`bee_replace_in_article` parameter-alias tolerance:** Coding-agent MCP clients (Claude Code, opencode, etc.) reflexively reach for their own file-edit tool's parameter names — `old_string`/`new_string`, `oldString`/`newString`, plus an incidental `filePath` — instead of this tool's `search`/`replace`. `McpParameterValidationMiddleware` now transparently rewrites those aliases (and drops the incidental extra parameter) before the unknown-parameter check runs, in addition to a description hint naming the real parameters.
- **Tests:** Added `Acl_BeeGetLog_FolderDeleteEvents_DeniesSecretFolder` (the folder-event scope leak above had zero prior coverage — the ACL test fixture wires `FolderService` with a `NullEventLogger`, so no existing test ever persisted a real folder event; verified this test fails against the pre-fix behavior and passes against the fix). Full-suite run: all previously-green tests remain green; pre-existing failures in `SnapshotService`/CLI snapshot tests (`IOException: file in use`, Windows temp-file locking) reproduce identically on the pre-fix code and are unrelated to this change.

#### MCP follow-up (2026-08-19): missing-required-parameter validation

The alias tolerance above had a blind spot: a call that supplies *only* aliased/dropped names and never
supplies a genuinely required parameter (e.g. `bee_replace_in_article` called with `filePath`/`oldString`/
`newString` and no `id` at all — an agent that fully confused this tool with its own local file-edit tool)
passed the "no unknown parameter names" check cleanly after alias rewriting, then failed deep inside the
MCP SDK's own parameter binder with an opaque `"An error occurred invoking {tool}"` instead of a message
naming the missing parameter. Before the alias feature existed this was caught by accident (the extra wrong
names always tripped the old "Unknown parameter(s)" error, which happened to list `id` as a side effect).

- **`McpParameterValidationMiddleware`** now also checks for missing *required* parameters — symmetric to
  the existing unknown-parameter check, using the same `McpToolRegistry.ParamInfo.Required` flag — for
  every registered tool, not just `bee_replace_in_article`. The error now names the missing parameter
  directly: `"Error: Missing required parameter(s): 'id' for bee_replace_in_article. ..."` (unknown and
  missing problems are combined into one message when both occur).
- **Omitted/null `arguments`:** a `tools/call` request with no `arguments` property at all, or an explicit
  `"arguments": null`, used to skip parameter validation entirely (early-return). It's now treated as "no
  parameters supplied" for the missing-required check instead of silently reaching the SDK's own opaque
  failure. A malformed `arguments` (present but not an object, e.g. an array or string) still falls through
  to the SDK untouched.
- **Audited every `[McpServerTool]` method** across all 7 `server/BeeMemoryBank.Api/McpTools/*.cs` files
  (32 tools, 69 parameters) for a required parameter missing `[Description]` (which would make it invisible
  to `McpToolRegistry` and thus to this check too) — none found; independently re-verified.
- **Tests:** New `McpParameterValidationMiddlewareTests` (7 cases) — constructs the middleware directly
  (its dependencies are plain constructor parameters, no DI host needed) and covers: missing required
  parameter, the exact aliased-call-without-`id` regression, omitted/null `arguments`, unknown-parameter-only,
  a fully valid call, and a zero-parameter tool. No prior test coverage existed for this middleware at all.

#### MCP follow-up (2026-08-19): invalid GUID parameter values

A second, distinct blind spot reported against the same tool surface: every article/media `id` parameter
across ~15 MCP tool methods is typed as `Guid`/`Guid?` directly in the C# method signature. A value that
isn't a valid GUID — most commonly a tree path (e.g. `/Projects/_Sync/_README`) passed by an agent that only
knows the article by the path named in its own instructions, not its GUID — was never caught by the
name-based checks above: the SDK's own JSON argument binder throws while coercing the value into
`System.Guid`, *after* the middleware's `next(context)` call, producing the same opaque
`"An error occurred invoking {tool}"` failure the missing-parameter fix closed for names but not values.

- **`McpToolRegistry.ParamInfo`** gained an `IsGuid` flag (`Nullable.GetUnderlyingType(type) ?? type ==
  typeof(Guid)`), so the middleware can identify which parameters need value-shape validation without
  re-deriving CLR type info itself.
- **`McpParameterValidationMiddleware`** now validates every `IsGuid` parameter's provided value with
  `Guid.TryParse` before invocation. A non-GUID string, or any non-string JSON value (number/array/object),
  is reported as `"Invalid parameter value(s): 'id' must be a GUID, got \"/Projects/_Sync/_README\""`, with a
  trailing hint: *"GUID parameters take article/media IDs only, never tree paths. Resolve a path to its GUID
  first via bee_get_tree or bee_search, then pass that GUID here."* JSON `null` is accepted as "no value" for
  optional `Guid?` parameters (their default), but reported as invalid for a *required* Guid parameter — a
  non-nullable `Guid` can never legitimately bind from `null`, so explicit `null` there is unparseable like
  any other bad value, not silently equivalent to omitting the field entirely (missed by the first draft;
  caught in an Antigravity review pass and fixed before shipping). Oversized values (e.g. an entire article
  body mistakenly passed as `id`) are truncated to 80 chars in the error text and the log line.
- **Tests:** Two new cases (non-GUID string value, explicit `null` on a required GUID) plus one confirming
  `null` still passes through cleanly for an optional `Guid?` parameter; test tool registry extended to cover
  `BeeReadTools`/`BeeSearchTools`/`BeeSessionTools`/`BeeAuditTools`/`BeeConceptTools` in addition to the two
  already covered, for full parity with the tools actually registered in `Program.cs`.
- **Known follow-up, not fixed here:** the same class of bug likely affects other non-string MCP parameter
  types bound directly by the SDK (`List<string> tags`, `bool`, `int`/`int?`) if a client sends a
  differently-shaped JSON value (e.g. a comma-separated string instead of an array). Flagged by the
  Antigravity review pass; deliberately out of scope for this change pending a separate decision on whether
  to generalize `McpParameterValidationMiddleware` to arbitrary JSON-Schema-shape checks.

#### MCP follow-up (2026-08-19): per-agent response limits, `ignoreLimit`, and a JSON truncation data-loss bug

A fourth report against the same tool surface, this time about `bee_set_max_tokens`: values above 20,000
were silently clamped to 20,000 (`Math.Clamp`, no error), while the tool's own description read as advice
("may cause issues with smaller models") rather than a hard wall. Separately, `McpResponseManager` — which
backs both `bee_set_max_tokens` and the truncation/`bee_continue` machinery — is a **process-wide
singleton**, so the old single `_maxTokens` field was shared by every connected agent: one agent raising
its limit silently changed what every other concurrently connected agent's calls returned too. Confirmed
via `Program.cs` DI registration; not just a surprising default, a real correctness bug.

- **`bee_set_max_tokens` range is now 1,000–100,000, enforced with an explicit error, never a silent
  clamp.** `McpResponseManager.TrySetMaxTokens(int, out string? error)` replaces the old always-succeeding
  `SetMaxTokens` — an out-of-range value leaves the caller's limit untouched and returns
  `"Error: maxTokens must be between 1000 and 100000 (got ...)."`.
- **The limit is now per-agent, not global.** `McpResponseManager` keys it off
  `HttpContext.Items["AuthAgent"]` (the same per-request agent identity `AgentAuthMiddleware` already sets)
  via a `ConcurrentDictionary<string, int>`, instead of one shared `int` field. Requests that never resolve
  to an agent share one fallback bucket. The service stays a singleton — it still owns the on-disk
  continuation store `bee_continue` reads across separate requests — only the limit itself became
  per-caller state inside it.
- **`bee_continue` gained `ignoreLimit: bool = false`.** Set it to fetch all remaining content in a single
  call instead of the next chunk, bypassing the caller's own limit for that one call — capped at the same
  100,000-token hard ceiling as `bee_set_max_tokens`, never higher. Exists so an agent never has to touch
  its own persistent limit (which then stays raised for all its future calls too) just to read one large
  document once. The truncation hint on every truncated response now states the caller's exact remaining
  token count and, when it would fit, the literal `bee_continue(...)` call to make — a number-driven
  suggestion instead of a menu of APIs with no criterion for picking between them.
- **Fixed a real, pre-existing data-loss bug found by an Antigravity review pass, not introduced by the
  above:** for JSON tool responses, the truncation envelope reported `offset: charPos` while only ever
  delivering a 500-char `preview` — never the actual `charPos`-length prefix. An agent that followed the
  hint (`bee_continue(guid, offset: charPos)`) silently lost everything between character 500 and `charPos`
  forever; the gap was neither in the initial response nor in the continuation. JSON responses now always
  report `offset: 0`, so continuation reads from the true start of the saved document with no gap.
  `bee_continue`'s negative-offset case was also hardened: it used to throw an unhandled
  `ArgumentOutOfRangeException` instead of returning a structured error.
- **Tests:** `McpResponseManagerTests` rewritten (18 cases), including
  `MaxTokens_IsIsolatedPerAgent_RaisingOneAgentsLimitDoesNotAffectAnother` (the core regression this whole
  fix targets) and two cases proving the JSON offset-zero fix delivers gapless content end-to-end. Three
  other test files that constructed `McpResponseManager` directly were updated for the new constructor
  parameter. Full-suite run: same pre-existing `SnapshotService`/CLI snapshot family of failures (Windows
  temp-file locking) reproduce identically in isolation, confirmed unrelated to this change.
- **Docs:** `docs/mcp.md`'s `bee_set_max_tokens`/`bee_continue`/truncation sections were out of date (still
  said "max 20000", didn't mention `ignoreLimit` or the JSON preview behavior) — updated to match.

#### User management (2026-08-27): promoting a user to superadmin was impossible, plus four lockout bugs found reviewing the fix

Changing an existing user's role to superadmin always failed with *"Password is required when promoting a user
to superadmin. Use the change-password endpoint after role change, or provide password."* The advice in that
message could not be followed: the role change was rejected **before** anything was saved, so there was no
"after role change" to reach, and `AdminChangePasswordAsync` only ever re-wrapped an *existing* key slot —
it never created a missing one. The web UI could not satisfy the requirement either: the Edit-User dialog has
no password field and the Web proxy DTO (`UpdateUserProxyRequest`) has no `Password` member at all, so the
password never left the browser. Promotion was unreachable through every path a person actually uses.

The underlying constraint is real: a key slot wraps the master DEK with an Argon2id KEK derived from the
user's **plaintext** password, which the promoting admin does not have and must not be made to invent.

- **Promotion is now deferred, not refused.** `UpdateUserAsync` changes the role and leaves `key_slot_id`
  NULL. The slot is provisioned from the plaintext password at the first moment it legitimately exists — the
  promoted user's next successful login (`UserService.ProvisionMissingKeySlotAsync`, called from
  `/api/session/login`) — or earlier if an admin resets their password, since `ChangePasswordAsync` /
  `AdminChangePasswordAsync` now provision a missing superadmin slot instead of skipping it. The user keeps
  their own password; nothing is silently reset. In the gap they hold the role but cannot unlock a *locked*
  vault, which the Edit-User dialog now states in a hint shown only when the pending change is a promotion.
- **Promote/demote now key off the slot the user actually holds** (`user.KeySlotId`) rather than their
  previous role, so re-promoting someone whose demote half-failed reuses their existing slot instead of
  orphaning it behind a second one.
- **Passing a password to `PUT /api/users/{id}` still works** and provisions the slot immediately — but it
  now also updates `password_hash` and revokes the user's remote API tokens. Previously the slot's password
  and the login password silently diverged. `UpdateUserAsync` returns whether the password was actually
  applied, so the audit log stops claiming `password changed=True` for a password it ignored.

Four further bugs were found by an Antigravity review pass over the fix (four models, all four flagged the
first one independently) — each is a lockout or credential-lifetime bug, not a cosmetic issue:

- **The "last key slot" guard counted slots that cannot unlock with a password.** `DeleteUserAsync` already
  had an `allSlots.Count <= 1` check and the new demote path copied it. But `tbl_key_slot` also holds
  `recovery` slots (openable only with the recovery key) and `os_auto_unlock` slots (no KDF parameters at
  all — `UnlockAsync` filters them out entirely). Either one pads the count past the guard. With deferred
  provisioning making slot-less superadmins reachable, demoting or deleting the last password-bearing
  superadmin while a recovery key existed would drop the only slot anyone could unlock with. Replaced by
  `EnsureAnotherSuperadminHoldsAKeySlotAsync`, which asserts the actual invariant: some *other* active
  superadmin still holds a slot.
- **Concurrent logins could leave an orphaned slot that outlives every password change.** Two parallel
  logins by a freshly promoted user each saw `KeySlotId == null`, each created a slot, and the whole-row
  `UpdateAsync` left the loser's slot unreferenced in `tbl_key_slot` — where `UnlockAsync` still honours it,
  so that password would open the vault forever, surviving later rotations. Provisioning now commits via
  `IUserRepository.TryAssignKeySlotAsync`, a conditional `UPDATE … WHERE key_slot_id IS NULL AND role =
  'superadmin' AND is_active = 1`, and deletes its own slot when it loses the race. Writing only that one
  column also closes a lost-update window: the whole-row write could revert a concurrent admin password
  reset, demotion, or deactivation using data read before the ~100 ms Argon2id derivation began.
- **`KeyManagementService.RemoveSlotAsync` left `tbl_user.key_slot_id` dangling** (there is no FK on that
  column). The user then looks provisioned, which silently suppresses re-provisioning at their next login and
  leaves them unable to unlock. It now clears the reference via `IUserRepository.ClearKeySlotAsync`.
- **DEK rotation could be blocked outright by a slot-less superadmin.** Both `DekRotationService.Propose`
  and `.Accept` fall back to `users.FirstOrDefault(u => u.Role == Superadmin)` when no initiator is given
  (CLI/system calls); `ListActiveAsync` orders by `created_at`, so a promoted-but-not-yet-logged-in user
  could be picked and the following `KeySlotId == null` check would fail the whole rotation even with
  another eligible superadmin present. Both fallbacks now require a slot and say so if none exists.
- **Endpoint status codes:** `PUT /api/users/{id}` returned 500 for every rejected role change; it now maps
  `ArgumentException` → 400 and `InvalidOperationException` → 409. `DELETE /api/users/{id}` had the same
  gap ("Cannot delete the last superadmin" surfaced as a 500) and now returns 409 too.
- **Tests:** new `UserServiceTests` (22 cases) and `SuperadminPromotionTests` (4 HTTP end-to-end cases),
  including regression cases for each review finding — demote/delete refused with a recovery slot padding
  the count, the losing side of a provisioning race discarding its own slot, a stale in-flight login not
  resurrecting a concurrently demoted user, `RemoveSlotAsync` clearing the dangling reference, and both
  re-wrap paths retiring the old slot. Full-suite run: the same pre-existing `SnapshotService`/`JoinWithSnapshot`
  family of Windows temp-file-locking failures reproduces identically on a clean `master`, confirmed unrelated.
- **Docs:** `docs/encryption.md`'s key-slot section now explains why promotion is deferred and what the
  demote/delete guard actually asserts.

### Added

- **Multiple Storages & Data Isolation (Windows Desktop):**
  - **Stable Data Root:** Relocated all user database files, media, logs, and settings to a persistent per-user directory (`%LOCALAPPDATA%\BeeMemoryBankData`) outside the versioned Velopack installation path, ensuring data survives program updates, repairs, and uninstallation.
  - **Multiple Vaults:** Added desktop support for running multiple isolated vaults. Users can create, rename, and switch between vaults via the system tray menu. "Forgetting" a vault removes it from the UI profile list while safely preserving files on disk.
  - **Automatic Legacy Data Rescue:** Integrated a startup rescue utility (`LegacyDataRescue`) that detects and migrates data from the old `current\data` directory to the stable data root. Conflicting data is automatically quarantined into a `recovered-<date>` vault, and the app fails-closed with a diagnostic screen if a migration error occurs.
  - **E2E Update Verification:** Added `smoke-update.ps1` to test the full Velopack update and repair lifecycle on throwaway packages, ensuring data preservation across updates.
  - **Pre-Apply Safety Guards:** Added checks in the update service to block application updates if legacy database files are found in mutable folders.
  - **Process Log Redirection:** Redirected backend `bmbd` process logs (stdout/stderr) to `<DataRoot>\logs` with automatic rotation for easier troubleshooting.
- **Obsidian vault import:** Upload an Obsidian vault as a ZIP archive — Markdown files become articles, folder hierarchy is preserved, Obsidian `![[image.png]]` embeds are rewritten to encrypted media links. Per-article error isolation (one bad file does not abort the whole import), Windows-ZIP path normalization, oversized images auto-downscaled to 4096px before reject. Endpoint: `POST /api/import/obsidian`.
- **Hard delete (Superadmin only):** Permanent purge of an article or folder subtree and all attached media. Propagates to every synced node via the new `hard_delete` sync event. Subsequent `article_update` events for a purged entity are suppressed via the `tbl_event(event_type, entity_id)` gate index. New admin page `/HardDelete` with preview, filter, status chips, and paginated audit log; entry point lives in the Admin page (removed from the header to avoid accidental clicks).
- **Hard-delete audit log:** New `tbl_hard_delete_audit` records every hard delete (actor, source node, entity type/id/title, counts of rows removed) with pagination UI.
- **Migration `DROP COLUMN` idempotency:** `MigrationRunner` now treats `ALTER TABLE DROP COLUMN … no such column` as already-applied, making repeated runs on partially-migrated replicas safe.
- **Near-realtime sync (push-on-save):** SyncTrigger signals immediate sync after every save, reducing sync delay from 60 seconds to near-instant between public nodes
- **Push position tracking:** New `tbl_sync_push_position` table tracks what was sent to each remote node (vs pull position which tracks what was received)
- **Sync delivery status endpoint:** `GET /api/sync/delivery-status` returns per-node push progress (lastPushedSeq, totalLocalEvents, isSynced, lastContactAt)
- **Lightweight ping endpoint:** `GET /api/sync/ping?afterSequence=N` — returns 204 (no new events) or 200 with count; no auth required
- **Sync status UI in Web header:** Badge with pending node count, click to expand per-node delivery details
- **Post-save sync toast:** After saving an article, a toast shows per-node sync delivery circles (green=synced, yellow=pending); click to open detailed modal
- Encrypted image storage with per-image DEK (AES-256-GCM), same envelope encryption as articles
- Image upload in Web UI editor: drag & drop, clipboard paste, and toolbar button via EasyMDE
- Image display in article view with URL rewriting through Web proxy
- API endpoints: `POST /api/media` (upload), `GET /api/media/{id}` (download with on-the-fly decryption)
- Media files included in snapshots (TAR archive with SHA256 hashes, manifest v2)
- Snapshot restore now recovers media files alongside the database
- Sync events for media: `media_create` and `media_delete` with Base64-encoded ciphertext
- Automatic cleanup: soft-deleted media purged after 30 days, orphaned uploads after 24 hours
- Browser caching for media: `Cache-Control: private, max-age=31536000, immutable`
- Full-text search across encrypted article bodies with batched decryption (50 articles per batch)
- Separate MCP tool `bee_search_content` for body content search (slow, opt-in)
- Content search checkbox on Web search page (unchecked by default)
- Content search toggle on Mobile articles page with 1-second debounce
- Docker Compose deployment tested and verified
- Screenshots section in README (Web themes, Mobile app)
- **Concept tag sync events:** `concept_tag_rename`, `concept_tag_merge`, `concept_tag_delete` for syncing concept tag operations across nodes
- **MCP concept tag tools:** `bee_search_by_concept`, `bee_list_concept_tags`, `bee_add_concept_tags`, `bee_remove_concept_tag`, `bee_rename_concept_tag`, `bee_merge_concept_tags`, `bee_delete_concept_tag`

### Changed

- SyncScheduler now uses `SemaphoreSlim(1,1)` concurrent guard to prevent overlapping sync cycles
- SyncScheduler resilient to exceptions: catches and logs errors instead of crashing the background service
- SyncClient push now filters by local `node_id` — no longer sends remote-origin events back to their source (reduces wasted traffic)
- `DeliveryNodeStatus.TotalLocalEvents` changed from `int` to `long` to support large event logs
- `bee_search` MCP tool now performs fast metadata-only search (title/tags)
- Web search defaults to metadata-only; content search is opt-in via checkbox
- SearchService uses batched processing instead of loading all articles at once
- EventApplier now supports file system access for media sync (writes .enc files to disk)
- Snapshot manifest bumped to v2 when media files are present
- **BREAKING:** Removed keyword tags — `Article.Tags` property removed, articles now only have concept tags via `ConceptTagService`
- **BREAKING:** Migration `004_unify_tags.sql` copies existing keyword tags into concept tags (case-insensitive merge), renames `tbl_tag` → `tbl_tag_deprecated`, `tbl_article_tag` → `tbl_article_tag_deprecated`
- API: `Article.Tags` field in responses kept as empty `[]` for 1 release (mobile compat), will be removed next release; use `ConceptTags` instead
- API: `CreateArticleRequest` and `UpdateArticleRequest` now use `ConceptTags` parameter instead of `Tags`
- MCP: `bee_save_article(tags=...)` parameter deprecated but still works (merged into concept_tags with audit warning)
- ConceptTagService now emits sync events for rename/merge/delete operations

### Security

- **Delivery-status endpoint** protected by `InternalKeyValidator` (prevents node topology exposure)
- **Ping endpoint integer overflow fix:** removed `(int)` cast on `afterSequence` parameter (long values >2B were truncated)
- **XSS fix in sync toast:** node `displayName` now HTML-escaped in title attributes
- Admin endpoints (user management, lock) now require `BMB_INTERNAL_KEY` shared secret between Web and API, preventing role spoofing via HTTP headers
- Admin page and proxy routes restricted to `superadmin` role (cookie-based auth)
- `ApiClient` reads role from cookie claims per-request instead of a mutable singleton field
- Revoked sync nodes now correctly rejected by `GetByNodeIdAsync` (status filter added)

## [1.0.0] - 2026-04-08

### Added

- Initial release of BeeMemoryBank monorepo ([5258599])
- Multi-user authentication with role-based access: superadmin, unlocker, user ([0afc1d9])
- Bee delete folder MCP tool for removing empty folders ([737eee4])
- Dark Classic and Dark Bee themes ([4afd77b])
- Cache busting for site.css and site.js via `asp-append-version` ([43023a1])
- Folder creation: API endpoint, UI buttons on Home, Sidebar, and Folder pages ([8c32031])
- Mobile app: markdown rendering, security page, UI icons, app icon fix, peer management ([31908dd])
- Deploy button on Admin page with remote server setup instructions ([5c4b500], [5d7b6d7])
- Deploy mechanism via systemd oneshot service to survive API restart ([0da53ab])
- Maintenance page redirect during deployment ([9c160a7])
- Admin deploy section with description and disabled state ([fb061bd])
- Whitelist update sync event and Change Node URL feature ([ddc8f52])
- Security hardening, comment encryption, and sync status UI ([9685236])

### Changed

- Translated all Russian/Ukrainian text to English across the entire codebase ([ca690a2])

### Fixed

- Empty folders not showing in tree: TreeService now uses `tbl_folder` ([b0f99e9])
- Sidebar splitter lag; increased max width to 50% ([67ce3af])
- Sidebar UX: removed `+` from add folder, hidden refresh on hover, scrollbar padding ([30e8a5c], [70f90ca])
- URL validation: auto-prepend `https://` if scheme is missing ([b107eff])
- Deploy button: run via systemd oneshot service to survive API restart ([0da53ab])

[1.0.0]: https://github.com/ultrathinker/BeeMemoryBank/releases/tag/v1.0.0
