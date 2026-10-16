# Factory Definition, phase 7: Eric's factory, the first real factory

Date: 2026-10-16. Mission: `docs/missions/factory-definition-2026-10-14/MISSION.md`, goal 13 and the phase 7 row, as
amended in this pull request. Main at `49f3fed5c`.

The owner's decision of 16 October 2026: the switch-over goes one factory at a time, starting from nothing, and the
first real factory is a new, simple one for Eric. The same day he changed it: "Lets just start without the Python
check, but let the agent deal with the email check every morning at 8am". So the factory has no check: one seat starts
every morning at 08:00 and checks the mailbox itself. The DevThrottle factory (its versions 1 and 2, already built)
waits for his word.

The folder is NOT in this repository: it is kept with the mission's private briefs. This note carries its shape, the
open questions and the proof.

## What was written

| Part | What |
|---|---|
| `factory.yaml` | factory `eric`, title `Eric`, machine SOREN_NORTH, agent ClaudeCode, no model (the machine's default), no repositories |
| `seats/mail-desk.yaml` | one seat, Mail Desk, no repository (it works in its kit's `work/`), one start: `cron "0 8 * * *"`, `timeZone America/Toronto`, `preventOverlap: true`, no check, no secrets |
| `files/instructions.md` | where the owner's instructions go; every part still `TO FILL:` |

3 files, 2,291 bytes. A zip of the folder for the Definition tab's Import is beside it (3 entries, each compared with
the folder, identical).

The seat's brief says plainly: the owner's instructions are in `files/instructions.md` in the kit, and nothing in the
brief adds to them. Each morning the seat reads them; if they still hold `TO FILL:` it opens no mailbox, writes one
journal line ("Not set up yet: files/instructions.md still has TO FILL:") and stops. Otherwise it checks the mailbox
the way the instructions say, follows them when the email is there, and writes one journal line saying what it found
and did - which, when the email is not there, is all it does.

## What the owner must fill in

The owner called the mailbox "other" (not Microsoft 365, not Gmail), so no tool and no credential is guessed. Every
open question is marked `TO FILL:` in `files/instructions.md`:

1. **The mailbox**: its address, how the seat reads it (the tool or command), and the name of the cc-secrets entry
   that holds what it signs in with. The value itself goes into cc-secrets, never into the file.
2. **The email to act on**: which email counts (for example its sender and subject) and how far back the seat looks
   each morning.
3. **What to do when it is there**: the steps, as plain words.

Until all three are filled, every morning's run opens no mailbox and writes "Not set up yet" in the seat's journal.

## Proof, on a throwaway Gateway and a real Director

`eric-rig/run_qa.py` beside this note, run in the foreground from the repository root after building the Gateway
(Debug), the slot 7 Director and the Cockpit:

```
dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
powershell -NoProfile -File scripts\local-build-avalonia.ps1 -Slot 7 -OutputDir "<repo>\scripts\local-build"
(cd apps/cockpit && npm run build)
uv run --no-project --with "typer>=0.16.1" --with "click>=8.2.1" --with "rich>=13.0.0" --with "requests>=2.31.0" --with tzdata --with playwright python docs/qa/factory-definition/phase-7/eric-rig/run_qa.py <eric folder> docs/qa/factory-definition/phase-7/eric
```

It reuses the phase 5b rig: a Gateway of this tree on port 7992 with its own empty storage root, Tailscale off,
authentication on, serving this tree's Cockpit; a person (an enrolled browser device); and the slot 7 Director,
started only by a scheduled task of the rig's own (`cc-director-launch-p7`, set up as CLAUDE.md rule 0b sets up
`cc-director-launch`) and stopped only by its named shutdown signal. The shared `cc-director-launch` task was read,
never changed (it pointed at slot 5 before and after). The transcript is `eric/transcript.txt`; the folder's text is in
it only as the Gateway's "what changes" and the agent quote it.

| Step | Result |
|---|---|
| Import the folder as written, as a person | exit 0: "version 1 ... draft", 3 files, `"created": true`, no findings at all (no secret, no repository to ask about). |
| What changes | "First publish: 5 factory settings set, 1 seat added, 1 start created, 1 file added"; would do: "eric registered from its definition, in no folder" and "A new schedule (Eric - Mail Desk) for seat mail-desk: Daily 08:00 America/Toronto". |
| Version 2, the rig's copy | the one line changed: `cron: "0 8 * * *"` to a time five minutes away (same zone), because the rig cannot wait for 08:00. "what changes: 1 start moved." Every other byte is the folder's (hashes in the transcript). |
| Publish version 2, as a person | exit 0: "Schedule cj_1a0182 (Eric - Mail Desk) created for seat mail-desk: Daily 09:09 America/Toronto." |
| The start fires | one run of the schedule, one session (one session history row of the factory, and one session listed), a minute after it finished still one of each. |
| The session | seat `mail-desk`, definition version 2, started on the rig's Director, working folder the kit's `work/` (`<rig>\director-root\instances\default\factory-run\<session>\work`), its kit holding `brief.md`, `files/instructions.md`, `grants.md`, `kit.json`, `rules.json`, `records/` and the first prompt in `prompts/`. |
| Its first prompt | the brief in full ("--- your brief, definition version 2 --- ... --- end of your brief ---") and then the start's prompt ("Good morning. It is time for the 08:00 mail check. Do what your brief says."), read from the file the Director saved in the kit and sent. |
| What the agent did | read `files/instructions.md`, found `TO FILL:`, opened no mailbox, wrote the journal line ("Added: entry ... to the journal of mail-desk (57 bytes)"), and stopped. `factory journal list --factory eric --seat mail-desk` shows the one entry, written by that session. |

Pictures, from the run, in headless Chrome signed in as the rig's person:

- `eric/02-definition-tab-published.png` - the Definition tab: version 2 published, by a person.
- `eric/03-schedule-runs.png` - the Schedule screen, the factory's schedule open with its recent runs.
- `eric/04-seat-page.png` - the seat's page: its brief from the definition, its daily start, its last run.

There is no picture of the draft before the first publish: the factory has no page until then (finding 2).

## Findings

1. **A start the Director answers after 30 seconds is recorded as "did not start a session", although the session
   starts.** In every rig run (three of three) the Gateway sent the start, gave up after 30 seconds ("The Director did
   not answer within 30 seconds. It is not known whether the command was carried out."), and recorded the run as
   `not-started`, result `problem`. The Director then started the session anyway: in the final run it logged the
   command as received three minutes after the Gateway sent it, and answered "status=Ok" eight minutes after. So:
   the schedule's runs say "did not start a session" (picture 3), the seat's page says "did not start" and the
   factory reads FAILING (pictures 2 and 4), the run has no session, and the agent's `cc-devthrottle run result ok`
   was refused: "Session ... was not started by a schedule, so it has no run to report on." One session ran and did
   its work; the record says none did. An outcome the Gateway itself calls unknown is shown as a known failure, and
   the session it does not know about can never report. Whether the owner's own Director answers within 30 seconds
   at 08:00 is not known; on this machine today the test Director did not.
2. **A new factory cannot be started from the Cockpit.** Before its first publish, `GET /gateway/factories/eric`
   answers 404 ("There is no registered factory 'eric'"), so there is no factory page and no Definition tab to Import
   from, and the Factories screen has no import of its own. The command line takes it: `cc-devthrottle factory import
   <folder>` stored the first draft of a factory that did not exist (`"created": true`), and `factory definition
   publish eric --version 1 --replaces 0` published it and registered the factory. Run here with the rig's person key;
   whether the owner's own terminal counts as a person is not shown here.
3. **The slot 7 Director started very slowly on this machine today.** Its instance registration took about three
   minutes after it was started in the first attempt and over twenty in the second, with almost no processor use; the
   rig now waits up to thirty minutes. The cause was not established.
4. **The phase 5b rig's PowerShell helper reads its answers in the machine's code page**, and one process's command
   line on this machine was not in it, which stopped the run while listing processes. This rig replaces that helper
   with one that decodes as UTF-8 and replaces what it cannot read.

## What this does NOT prove

- The real mailbox: the instructions are not written, so no mailbox was opened; the run proves the seat stops when
  they are not.
- 08:00 itself: the rig moved the time; the folder keeps 08:00 America/Toronto.
- The hosted Gateway, and the owner's own Director: everything ran on the throwaway Gateway and the slot 7 Director.
- An import in the Cockpit (finding 2).
