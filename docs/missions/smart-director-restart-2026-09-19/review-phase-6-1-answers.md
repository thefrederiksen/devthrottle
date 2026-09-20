# Answers - Smart Director Restart, phase 6, review 1

Written on 20 September 2026 by the Developer seat standing in for the one that built phase 6, which is
gone. A Reviewer advises; it does not command. Every finding below was checked against the code before it
was accepted, on `origin/main` with `git show` and `git grep`, never from the working tree and never from
the Reviewer's word alone.

The review itself is committed beside this file, unchanged, as `review-phase-6-1.md`.

---

## Finding 1 - the page describes a feature no released build has: ACCEPTED

**Checked first.** The Reviewer is right, and I confirmed the facts myself rather than taking them:

- `git tag --sort=-creatordate` newest is `v2.8.1`.
- `git tag --contains 217b79f63` (the way up engine) and `git tag --contains 1ed421f7d` (the phase 2
  swap): both empty. Neither merge is in any tag.
- `cc-devthrottle director list --fields id,name,machine,version,state`: every live Director on 2.8.1 or
  2.7.0.
- `docs/public/index.json` says it itself: "Served as raw markdown from GitHub - no build step." The page
  is public the moment this merges.

So a reader on today's release opens the File menu and the item is not there, and nothing on the page
lets them reconcile it. That is the exact harm the mandate named.

**What I changed.** A note at the top of `docs/public/features/10-smart-restart.md`, above the first
paragraph, in a blockquote so it cannot be skimmed past:

> **Not in a released version yet.** Smart Restart is finished and merged, and it arrives in the next
> release of DevThrottle. The newest release as this page was written is 2.8.1, and it does not have it.
> If there is no **Smart Restart** on your File menu, that is why: your build predates the feature, and
> updating to the next release is what gets it. Everything below describes the feature as it is built.
> This note comes off the day the release ships.

It names the version a reader can compare against their own build, it explains the missing menu item
before they go looking for it, and it says when it is to be removed, so it cannot quietly rot into a lie
after the release.

**What I did NOT change, and why.** The rows in `docs/public/features/01-overview.md` still read
`implemented`. That page defines its own word three lines above the table: "Status reflects the code, not
intent: **implemented** = the screen exists and its actions are wired". The screens exist and are wired,
so `implemented` is true under that definition, and inventing a new status word for one feature would
change the meaning of the column for every other row. Every one of those rows links to the page, so a
reader who follows any of them meets the note. The Reviewer asked for one sentence on the page; the page
is where it went.

## Finding 2 - both documents promise the record unconditionally: ACCEPTED

**Checked first, and the Reviewer is right on both halves.**

- `src/CcDirector.ControlApi/SmartRestart/ISmartShutdown.cs:33` - "It works with the Gateway unreachable
  too - then `IgnoreAllResult.RecordWritten` is false ... and the sessions are still ended, because the
  owner chose to discard them."
- `src/CcDirector.Avalonia/SmartRestart/SmartShutdownCoordinator.cs`, the `IgnoreAllSessions` case:
  `if (!result.RecordWritten) FileLog.Write(... "the record was NOT written: ...")` - a log line and
  nothing else. The sentence the owner is shown, four lines further down, is
  `$"{result.SessionsEnded} session(s) were ended. The Director was not restarted: ..."`. It says nothing
  about the record. So the absence is silent, exactly as the finding says.

The Reviewer offered two remedies: carry the condition in the documentation, or make the product tell the
owner. **I took the documentation one.** This phase changes no code - the proof says so and the mandate
asked for a skill and a page - and a Developer quietly adding a message to a screen nobody has ever run
would be a change no one reviewed and no test covers. The product change is worth making and is not mine
to make here; it is written down at the end of this file so it is not lost.

**What I changed on the page** (`10-smart-restart.md`, the "Shut down and ignore all sessions"
paragraph). It now reads:

> **Shut down and ignore all sessions** ends everything at once and writes no handovers. If DevThrottle
> can reach the Gateway, what was running is written down first, so you can see afterwards what was
> closed. If it cannot reach the Gateway, the sessions are ended anyway - you asked for them to be
> discarded - but nothing is written down, so there is nothing to look at afterwards, and the message you
> get says only how many sessions were ended. Chosen from the File menu, this one does not restart the
> Director - it ends the sessions and leaves DevThrottle open and empty, and says so.

That last clause matters as much as the condition: a user who is told the record may be missing AND that
the product will not say so knows to go and look rather than to trust the silence.

**What I changed in the skill** (the "Shut down and ignore all sessions" bullet). It now reads:

> - **Shut down and ignore all sessions** - the record is written first, then everything is ended at
>   once, with no handovers. **With the Gateway unreachable the record is NOT written and the sessions
>   are ended anyway**, by design - the owner asked to discard them - and nothing on screen says the
>   record is missing; the refusal reaches the log alone. From the File menu this does NOT restart the
>   Director, and it says so.

## Finding 3 - the handover folder path omits the run mark: ACCEPTED

**Checked first.** `src/CcDirector.ControlApi/Drain/DrainPaths.cs`, `DirectoryFor`:

```
Path.Combine(RestartsRoot, $"{startedLocal:yyyy-MM-ddTHHmmss}-{runMark}-{Sanitize(directorName ?? "Director")}")
```

and `HandoverFileName` returns `$"{shortId} - {name}.md"`. So the page was wrong twice: it dropped the
run mark from the folder, and it wrote the file as `<session>.md` when the real name carries the short id
and a space-hyphen-space before the session name. The mark is deliberate - the same file's comment says
it exists so a cancelled run and a re-started one can never share a directory.

**What I changed.** The pattern on the page is now

```
<data folder>/vault/handovers/director-restart/<when>-<tag>-<director name>/<short id> - <session name>.md
```

with two sentences under it saying what `<tag>` and `<short id>` are and showing a real folder name,
`2026-09-20T101030-a1b2c3-DevThrottle 1`, so a user standing in the folder recognises what they are
looking at. The skill already stated the path in full and correctly and was not changed.

## Finding 4 - the page names a step it never explains, and the app offers no way to take it

**ACCEPTED, and it is no longer answered against a version with no way up.** The mandate for this
answering seat told me to wait in the foreground for pull request 3208, the phase 3 way up, and then to
make the page true about what is actually on main. I waited from 14:38 to 15:14 UTC on 20 September 2026,
polling once a minute in the foreground; it merged as `99cd1c034` at 15:14. I then `git fetch origin` and
`git merge origin/main` into this branch (merge `61ec88f0c`) and read the merged code before writing a
word of the page.

**What the way up actually does, read on `origin/main` after the merge - not from the pull request
description and not from the phase 3 proof:**

- `src/CcDirector.Avalonia/MainWindow.axaml.cs:757` -
  `SmartRestart.WayUpStartUpAsk.WatchForRestartOffer(host, this)`. The engine now HAS a caller, which is
  exactly the fact finding 4 and my own proof (section 8) said was missing.
- `src/CcDirector.Avalonia/SmartRestart/WayUpStartUpAsk.cs` - asks the engine once, on the first
  `GatewayConnectionStatus.Connected` and never again, off the interface thread, and shows a window ONLY
  when the answer is `WayUpOfferState.Offered`. A Gateway that cannot be reached shows nothing at all at
  start-up; the reason goes to the log.
- `WayUpWords.Headline` = `"A restart is available"`; `WhenLabel`, `ReasonLabel` and `SeatsOwedLabel`
  supply the three lines under it. The buttons are `WayUpOfferViewModel.BringBackButtonText` =
  `"Bring back"` and `NotNowButtonText` = `"Not now"`.
- The rows: a mission head with its seats under it (`BringBackRowDetail` - "leads first, each reading its
  own handover") and a row of its own per seat that ended without a handover (`EndedRowTitle`,
  `EndedRowDetail` - "It is not brought back with the rest, because there is no handover for it to
  read"). `WayUpWords.ReopenOffer` decides what that row offers instead: `"Reopen its saved
  conversation"` for an agent whose plugin declares `CanResumeSavedConversation`, and `"Open a fresh
  session in its repository"` for one that does not.
- `src/CcDirector.Avalonia/MainWindow.axaml.cs:4615` - the File menu item `"Restart history..."`, and
  `RestartHistoryViewModel` carries the same offer for any record still owed. `WayUpWords.NoHistory`
  against `WayUpWords.GatewayRefusal` keeps "you have no records" and "the records could not be read"
  apart, and the window shows whichever it was handed.
- `WayUpWords.ReopenPrompt` - a session that comes back is told it was stopped and must check the state
  of its work before acting.

**What I changed on the page.** The dead-end paragraph is gone and a new section, **"Coming back up"**,
takes its place: nothing comes back on its own and you are asked; the start-up window and what it says;
the two kinds of line and the tick boxes; the two answers; **File, Restart history** as the way to do it
later, so "Not now" is not final; what happens when the Gateway cannot be reached; and the sentence a
returning session is given. The page's opening promise was reworded to match - the record is kept "so
when it comes back up it can offer you those sessions again" - so the promise and the door now agree.

**I did NOT name the command line on the page**, which was the Reviewer's own suggested fix. It is no
longer the user's answer: the user's answer is a window in the product, and sending a person to
`cc-devthrottle director restore` when the Director offers the same thing on screen would be worse
documentation, not better. The command line keeps its place in the skill, for an agent that is not
sitting at that Director's screen - which is the only case left for it.

**Two further changes the merge forced, which no finding asked for but which would otherwise have been
false the day this landed:**

- `docs/features/feature-inventory.yaml` gains `restart-offer-window` and `restart-history-window`, with
  their source files, and `docs/public/features/01-overview.md` gains the matching two rows. The page now
  documents those two windows, and the inventory is what records which source implements a documented
  feature. The drift check went from 33 source paths to 40 and stayed green, and both windows dropped out
  of its "not in the inventory" note - which is the evidence that it reads the new entries rather than
  passing over them.
- The skill's section 4 said the dialog's "Answer these first?" section "does not appear on a real
  Director" because nothing populated the property. Pull request 3215 merged into main in the same window
  and `PendingInteractionWatcher` now fills it from each session's own transcript at every turn end
  (`ControlApiHost.cs:1005` builds it). The skill would have been wrong the moment I republished it, so it
  now says the section is real but partial: Claude Code only, `AskUserQuestion` and `ExitPlanMode` only,
  and a permission ask still never appears because its source was a hook event and a hook event is not in
  the transcript.

**What I published, and when.** `cc-devthrottle skill push director-restart` at **15:20:39 UTC on
20 September 2026**, note: "The way up shipped (pull request 3208): the Director offers the sessions back
at start-up and in File, Restart history; the ignore-all record is not written with the Gateway
unreachable; the question-box section is filled now". The draft was pulled back into a separate directory
and diffed against the source before publishing - **identical**. Then `cc-devthrottle skill publish
director-restart` at **15:21:10 UTC**, which answered "Published 'director-restart' v6".

`cc-devthrottle skill get director-restart` was then fetched back and compared byte for byte against the
committed attachment `attachments/phase-6/director-restart-skill-v6.md`: what the fleet reads is the
committed file exactly, with one trailing newline and nothing else around it. The v5 body and metadata
are kept beside it, unchanged, because that is what the review read.

**The metadata changed too**, and it is in `director-restart-skill-v6.json`: the summary now says the
Director offers the sessions back, and the trigger "empty the director" was replaced by "restart
history", which is the name of the new door a person or an agent will actually say. Twelve triggers is
the limit, so one had to go.

**An outage on the way, reported rather than worked round.** The first push at 15:17 UTC failed: the
hosted Gateway answered `502 - Web server received an invalid response while acting as a gateway or proxy
server` to every command, `skill show` included. I did not retry in a loop and did not reach for
`--force`; I ran the remaining checks, and by 15:20 the Gateway was answering again and the push went
through first time. Nothing was published during the outage.

---

## The checks I ran myself

All in the foreground, in this worktree, on `smart-restart/p6-retire` **after** `origin/main` was merged
in, so every count below is of the branch as it will be merged - not of the branch as it was before the
way up landed. I read the count each time, never the colour.

| Check | My count |
|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain\|FullyQualifiedName~Restart"` - the mission's own check | **606 passed, 0 failed, 606 total.** Run twice: once before the merge and once after, the same 606 both times |
| `dotnet test src/CcDirector.Core.UnitTests --filter "FullyQualifiedName~RetiredMessagingWords"` - the sweep that reads every file under `docs/public/`, so it covers my edits to the page and to the overview | **5 passed, 0 failed.** Run twice, the second time after the last edit to `01-overview.md` |
| `.\scripts\check-inventory-drift.ps1` | **OK, exit 0.** 40 source paths and 7 pages - it was 33 source paths before I added the two way up windows to the inventory. `WayUpOfferWindow.axaml` and `RestartHistoryWindow.axaml` were listed in its "not in the inventory" note before the change and are absent from it after, which is how I know it read the new entries rather than passing over them |
| `dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~WayUp\|FullyQualifiedName~RestartHistory"` - not asked for, run because the page now describes those two windows and I wanted the behaviour I wrote about to be under test | **50 passed, 0 failed** |
| ASCII sweep of every file I wrote - a byte check for anything above 126, not `grep -P`, which refused on this machine's locale, and a check that cannot run is not a check | **0 non-ASCII bytes** in the page, the overview, the inventory, the skill body, the skill metadata and this file |
| Attribution sweep - "Claude", "Anthropic", "Co-authored-by", "Generated with", "Copilot", "Cursor", "Gemini", "Grok" | Only the legitimate product mentions: Codex, Claude Code, Copilot, Cursor, Gemini, Grok and Pi named as AGENTS the feature handles, in the skill and in this file. No signature, no trailer, no "Generated with" anywhere |

**What these checks do NOT cover, said plainly.** They are the three the proof ran plus one, and between
them they prove the documents do not use retired words, that the inventory matches the source tree, and
that the code the page describes still passes its own tests. **None of them reads a sentence of mine and
judges whether it is true.** Nothing here has been run against a real Director, a real Gateway or a real
launcher - not by the seat that built phase 6, not by the Reviewer, and not by me. I have never seen the
offer window on a screen; what I wrote about it comes from the code that draws it and from the words file
it renders. That is the same gap the proof and the review both declared, and the way up landing does not
close it.

---

## What I am handing on rather than doing here

**The product should tell the owner when the record was not written** (finding 2). Today it reaches the
log alone. The honest sentence already exists in the engine's answer - `IgnoreAllResult.RecordWritten`
and `RecordRefusal` - so the screen has everything it needs; only `SmartShutdownCoordinator.RunDoorAsync`
has to say it. That is a code change with a test, in a phase that owns code, not in this one.

**This page has a dated note on it** (finding 1). The day Smart Restart is in a release, the blockquote
at the top of `docs/public/features/10-smart-restart.md` comes off. It says so itself, so whoever cuts
that release finds the instruction on the page rather than in a mission record.

**Nobody has watched any of this run.** The quality assurance phase is still ahead of the mission, and
until it happens both the page and the skill describe what the code does rather than what anyone has
seen. Both documents say so themselves.
