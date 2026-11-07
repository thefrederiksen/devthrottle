# Review: the end-to-end proof report of step F (pull request 4699)

Reviewer: a separate review session (Factory Inbox - Reviewer - end-to-end proof report (4699)), in the detached
worktree `/home/soren/ReposFred/devthrottle-fi-review-4699` at commit 938c74cd1, on devlinux, 6 and 7 November 2026.
Nothing was changed in the report, the screenshots, the session lists or the fixture. The only file this review adds
is this one. Nothing was pushed.

**Verdict: APPROVE.** No finding proves harm. Every claim I could check against a screenshot, a session list, the
token table or the live service holds. The observations below are for the author's and the owner's judgement; none
of them makes a sentence of the report false.

## Scope

What I read:

- `docs/qa/factory-inbox/phase-6-end-to-end/README.md` (the report), every one of its 53 screenshots (opened and
  read, each against the caption and the step text that cites it), all 16 session list files, `token-use.md`,
  `fixture/launch-in-container.sh` and `fixture/manifest.json`.
- `docs/factory-inbox/devlinux-readiness.md`.
- `docs/factory-inbox/MISSION-FINISH.md` section 3 (the goal) and section 7 step F (the eight steps), to check the
  report against what it had to prove.
- The pull request's diff against origin/main (72 files, documentation only; no product code).

What I ran (read-only, on the live service and this machine):

- Timed the live thread route the report says pull request 4690 fixed: `cc-devthrottle factory inbox thread
  factory-inbox-test` three times with the installed tool, and `factory inbox list` once.
- Checked that nothing from the run is still running: `docker ps -a` for the container, the process list for the
  slot-5 Director binary, `cc-devthrottle director list` (online and offline), the lifecycle-signals folder of the
  test root, and `readlink ~/.local/bin/cc-devthrottle`.
- Read the test Director's stored identity files, to settle why the readiness document and the report name two
  different Director identifiers.
- Searched the pull request's text files for tokens, keys and e-mail addresses; scanned every screenshot for embedded
  text chunks; checked which names shown on the screens were already in the repository on origin/main; checked the
  repository's visibility on GitHub.
- Re-added the token table's sums, medians and ranges, and the step 1 timings.

What I did not do: I did not re-run any step of the proof; I did not read the test Director's log or the agents'
transcripts (neither is committed, and the report's seat start and end times and token counts rest on them); I did
not check the dev report to the owner (not in this pull request); I did not review the product code the report
names as the likely cause of defect 1.

## What was checked, and holds

| Step | What the mission asks | Evidence checked | Result |
|---|---|---|---|
| 1 | Typed message from the Cockpit; thread shows Sent, Received (one Secretary), With the Boss, decision | `01d`, `01e` show the full trail with the session identifier; `01-before` and `01-after-decision` are both empty | Holds. "One Secretary" for this step rests on the Director's log, which is not committed; the step 2 poll proves the same mechanism from committed files. |
| 2 | Spoken Bad note from the phone; one Secretary, one Boss, delegated; trail with session identifier | `02a` to `02e`; `02-poll.txt` shows exactly one Secretary (d1a347d7), one Boss (3dde71e8), one Writer (8dc0c6c4), all on Opus 5.5, all gone by 21:37:23 | Holds. The trail says "Sent from the Cockpit"; the report explains why (defect 3) and lists it as not proven. |
| 3 | A second message while the Boss is alive; no second Boss; decision arrives | `03c-poll.txt`: Boss 38bac783 is the only Boss from 21:55:31 to 21:58:06; B's Secretary 947e3bc3 hands over at 21:56:39 to 21:56:53 while that Boss is alive; B is Delegated to f7955ba8 while the first Writer 32de16f7 is still working. `03e`, `03f` show both second-try trails. | Holds. The report is right to keep the first try as "not a proof". |
| 4 | Declined on the thread; one new Your inbox message; unread count on both shells; reply from Your inbox lands in the factory inbox | `04-poll.txt` (one Secretary, one Boss, Declined 22:05:44); `04b`, `04c`, `04d`, `04e` (rail "Inbox 1", tab "1 unread", phone menu "Inbox 1 unread", list "All 2" so exactly one new message); `04f`, `04g` (the Boss's reason in its words); `04h`, `04i`, `04j`, `04k` and `04-reply-poll.txt` (the reply Delegated to 1de3c5de at 22:13:33) | Holds. The "1 unread" seen before step 1 (`00`, `01a`) is the Delivery Lead's "Your inbox is live" message, read by step 2, so the count did rise from 0 to 1 at step 4. |
| 5 | A session's `inbox send` appears in Your inbox on both shells; Open opens the session | `05a` to `05f`; `05-sent.txt` names session 146 (cffb02a7) on fi-inbox-test; `05-after-stop.txt` empty | Holds. "No email is sent" is listed as not proven, which is right. |
| 6 | Summary button; morning message | `08b`, `08d`: "12 messages came in; 12 decisions made (Delegated 7, Noted 4, Declined 1)". I counted the run's messages: 1 + 1 + 4 + 2 + 4 = 12; Delegated 7 (steps 1, 2, four in step 3, the step 4 reply), Noted 4 (three mid-claim notes, the Director-stopped message), Declined 1 | Holds. The morning message is deliberately open until the 13:05 run on 7 November. |
| 7 | Secretary stopped mid-claim | `07b-poll.txt`: only 4b0de1f1 alive from 22:25:55; the row goes Received to With the Boss to Noted; `07b`, `07c` show the three trails | Holds. The report says plainly that which Secretary handed the bundle is read from the times. |
| 7 | Director stopped: thread says why; stuck alert; resumes on reconnect | `07d-poll.txt` carries the Gateway's waiting reason; `07e` shows the row "1 waiting for a decision"; `07i`, `07j` show the alert in the Gateway's words; `07k-poll.txt` shows the Secretary at 22:50:50, Noted at 22:51:13; `07l` "Nothing needs you"; `08a` row "Noted, All dealt with" | Holds, except that the thread never showed the waiting reason on screen (defect 1 at the time), and the report says so. See observation 1. |
| 7 | Silence | `07g`, `07h` | Holds. Phone only; see observation 5. |
| 7 | Archived factory refused in words | `07m` to `07q`; `07o` says "Archived 6 Nov 17:57 by the owner" (22:57 Coordinated Universal Time, as the report says) | Holds. |
| 8 | Cost of one Secretary and one Boss for one bundle | `token-use.md`: the step 1 rows, their sums (37,080 / 581,211 / 1,984), the medians and ranges across 11 Secretaries and 9 Bosses, the Writer's cost, and the alive times (72 seconds, 106 seconds, 1 minute 51 seconds from Send to decision) | All re-added; every figure matches. |

The goal's sentences (section 3): leaving a message in seconds from both surfaces is shown by the "Your message was
left" screens at once after Send; the Secretary and Boss hand-over is on every trail; delegation to a Writer is on
every Delegated trail; every message shows received and decided; the refusal reached Your inbox and not email
(step 4). "Factories stop emailing me" and the "no email" checks are not claimed proven, and the report says so.

**Defect 1 re-measured by me.** The thread of the test factory (thirteen messages) came back through the installed
command-line tool in 0.50, 0.53 and 0.63 seconds end to end, and the factory list in 0.53 seconds. That confirms the
report's 7 November figures and that the thread screens of step 6 needed no test script.

**Nothing left running.** No container named fi-inbox-test-director exists; no slot-5 Director process is running; no
Director is listed on computer fi-inbox-test, online or offline; the lifecycle-signals folder is empty (the stop
request was consumed); `~/.local/bin/cc-devthrottle` still points at the installed tool. The test factory is still
listed (active), as the report says it must be for the morning run.

**The two Director identifiers.** The readiness document says the test Director registered as 81766668; the report's
stop request names abe5577d. Both are right: a Director's identity is keyed by the executable it runs from and the
instance (`DirectorIdentitySlot.cs`), the readiness check used the installed binary and step F used the slot-5 build,
and the test root holds one identity file for each. The check list's stop-request path is the right one for the
slot-5 build the fixture script launches.

## Observations (no harm proven; for the author's and the owner's judgement)

1. **"The thread says why" was never shown on a screen, and could be now.** The mission's step 7 asks for a
   screenshot of the thread saying why a message waits while the Director is stopped. On 6 November defect 1 made
   that impossible, and the report lists it as not proven. Pull request 4690 is live and the thread now answers in
   under a second, so this one sub-step can be proven in a few minutes: stop the test Director with its stop request,
   leave a message, wait past the 60-second window, screenshot the thread on both shells, start the Director again
   with the fixture script. If the dev report to the owner is to claim every failure case with screenshots, this is
   the one still missing a screen.

2. **Two captions claim more than their picture shows.** `03d` is captioned "Second try, B left while the Boss
   works" but is cut off above the composer's confirmation line and shows only the first try's trails. `07a` is
   captioned "The three Bad notes left" but shows one "Your message was left" line and a thread still loading. In
   both cases the proof is in the session list the step cites, not in the screenshot, so nothing false is claimed;
   the captions could say "the composer's confirmation" rather than imply the picture shows the messages.

3. **"Every 10 seconds" is 11 to 14 seconds.** The poll files' stamps are 11 to 14 seconds apart (the list command's
   own run time, presumably). Harmless; "about every 10 to 15 seconds" would be exact.

4. **"The Boss never does it itself" is shown by attribution, not by inspection.** Every note is attributed to a
   Writer session and the Boss's own decisions are Delegated, Noted or Declined. The report does not say it checked
   the Boss transcripts for file writes. I think the attribution is enough for this proof, but the evidence section
   could say that this sentence of the goal rests on it.

5. **The silence case has no Cockpit screenshot.** The mission asks for a phone and a Cockpit screenshot at every
   step. The Cockpit composer also has a Mic button (`01d`), so a Cockpit silence case was possible. The report does
   not claim one.

6. **Step 1's "one Secretary, one Boss" rests on an uncommitted log.** The committed session lists for step 1 are
   only the empty before and after lists; the report says the starts and ends come from the Director's log. Step 2's
   poll proves the same thing from committed files, so I do not ask for more, but a reader of the repository cannot
   check step 1's seat count without the log.

7. **The readiness document cites a different commit as "the same code".** It says the 11 Linux-only failures pass on
   GitHub's Windows run of commit 0641d53b4 while the baseline was run on cbce2401f. The one commit between them adds a
   single markdown file, so the claim is true in substance; naming the difference would make it exact.

## Private data

The repository is **public** on GitHub, so everything committed here is world-readable. I found:

- No token, key, bearer string or credential in any text file; no embedded text chunk in any screenshot.
- One e-mail address, `soren@centerconsulting.com`, in the readiness document and on the phone menu screenshot
  (`04d`) and truncated in the Cockpit rail of most screenshots. That is the owner's account name already shown by the
  product, which the brief allows.
- The owner's factory names on every inbox list (Center Consulting, Citizenship, CPMC, DevThrottle Marketing,
  DevThrottle Sales, Erik Laursen, Factory Course, Machine Care, Mail Desk, Managed Marketing, mindzie, Private,
  Website Business New). All but "Website Business New" already appear in files on origin/main, "Erik Laursen" in
  five; a factory title is not customer data.
- **One screenshot shows the owner's other sessions.** `05d-cockpit-open-the-session.png` carries the Cockpit's
  session rail with the names of seven of the owner's sessions on devlinux and the Mac mini, including "Factory
  Pricing - margins and partners", "Drawing User Pipelines", "Build on owned machines - Worker - register the Linux
  build runner" and "GitHub CI and CD - Architect - why continuous integration runs on every commit and merge". None
  of those names is anywhere else in the repository. They are session titles, not customer data, tokens or
  addresses, so this is not a finding under the brief's definition; but it is the one place where this pull request
  puts something about the owner's other work into a public repository that was not there before, and the owner may
  prefer the rail cropped. The same screenshot shows the agent badge the product prints for the session.
- `04d` shows fragments of session cards behind the phone menu ("Worker -", "Lead -", "waiting 18m"); nothing
  readable beyond that.

## The fixture scripts

`launch-in-container.sh` does what its header says: it starts the slot-5 Director binary from a second worktree in a
disposable Alpine container (`--rm`) with its own host name, the host's network and process list, the whole host file
system mounted and entered with chroot, dropped to the user soren with setpriv, with HOME and CC_DIRECTOR_ROOT both
inside `/home/soren/ReposFred/fi-test-director` and a minimal environment (`env -i`). Because HOME is redirected, the
Director's tool links go into the test home, and I confirmed the owner's `~/.local/bin/cc-devthrottle` link is
untouched. It stops nothing itself; the Director is stopped by its own stop request and the container ends with it,
which I confirmed has happened. The manifest registers only `factory-inbox-test` on computer `fi-inbox-test`.

Three things the header does not say, none of which I can show caused harm:

- `--ipc host` is passed as well, so the container shares the host's inter-process communication name space. The
  process runs as soren after setpriv, so it gains nothing a plain `setsid` launch as soren would not have.
- Between `chroot` and `setpriv` the process is root inside the container with the host's file system mounted; it
  runs the host's own `/usr/bin/setpriv` and `/usr/bin/env` binaries. Those are the host's trusted binaries, and the
  window is one exec, but a reader should know the script is root for that instant.
- The binary path is hard-coded to another worktree (`devthrottle-fi-qa/local_builds/linux/cc-director-linux5`),
  so the script only works on devlinux while that worktree and build exist. The report says what it was built from.

## The evidence section

It says plainly what was not proven, and the list is honest: the morning message and the "no email" checks; that
every 6 November thread screenshot needed the lengthened re-read timer; that no message carried the phone's surface
label; that the phone was the mobile web app in a desktop browser with a synthetic voice played into a replaced
microphone; that the thread never showed the waiting reason; that which Secretary handed the bundle is read from
times; that the second computer was a container on the same machine; and the cases not exercised. I found nothing
claimed as proven that the evidence does not carry. Observations 4 and 6 above are the two places I would add a line.

## Not covered by this review

The dev report to the owner (not in this pull request), the morning message check of 7 November (deliberately
open), the Director's log and the agents' transcripts behind the seat times and token counts, and the product code
named in defect 1 and defect 2.
