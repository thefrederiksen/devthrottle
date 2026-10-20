# Answers: review of the Mail Desk rig

The review is `rig-review.md` beside this file. Answered by the rig Developer, 20 October 2026, with the Tech Lead's
rulings. Every finding is accepted. For each check changed under findings 1, 3, 4 and 6, the rig was run once with
its case broken on purpose (a temporary edit, reverted at once) to show the check fails. Those runs are quoted
here, not in the committed transcript.

## Finding 1. The switch-off case proved only absences - ACCEPTED, FIXED

- The Director now starts with `TZ=UTC`, so its log and the rig's clock agree.
- The case now waits, with a 5-minute deadline instead of a fixed sleep, for a **presence**: a line in the
  Director's own log, after the switch went off, saying `report NOT recorded: trigger=<the Mail Desk start>, HTTP 409:
  {"error":"mail_desk_off",...}`. It is a CHECK, and the line is quoted in the transcript. The two absences ("no
  run", "no decision") stay, after the presence. When the switch is on again, the next run's own reason must say
  something was decided.
- **Broken case:** `TZ=UTC` removed from the Director's environment (the reviewer's blind spot). The run failed:
  `the Director's log shows no refused Mail Desk report while the switch is off within 300s` (exit 1).

## Finding 2. The process scan could not see the Gateway or the Director - ACCEPTED, FIXED

- `rig_processes()` now reads `/proc`. A process owns the rig when its working folder is under the rig, or when its
  environment points `CC_DIRECTOR_ROOT` or `HOME` into it. That covers the Gateway, the Director, the seats and
  anything they start. Command lines are no longer read. The rig's own process and its parents are left out.
- **At start-up** the rig refuses to run, and never removes the folder, while any process owns it.
- **After the Director starts**, a CHECK shows the scan sees both the Gateway's and the Director's process ids. That
  proves the end check and the start-up refusal can see them.
- **At the end**, "processes that still own the rig folder" is a CHECK that none remain, naming the Gateway's pid.
- The reviewer's two small points were taken as well. The rig kills nothing whose session folder is outside the rig
  (a guard before the signal). The README now says the `cc-outlook` guarantee also rests on `CC_DIRECTOR_ROOT`.

## Finding 3. "No request to devthrottle.com" could not fail - ACCEPTED, FIXED

- That check is gone. At the end of every run, the Gateway's own `[MailDeskModel] AskAsync: question=..., labels=..`
  lines are counted; each is written as a real call starts. The count must equal the number of calls the fake model
  received. The fake accepts only the rig-only key, so every question the Gateway asked reached the fake and nothing
  else.
- **Broken case:** the fake model leaves one route question out of its count. The run failed: `6 questions asked
  by the Gateway, 5 received by the fake model` and `CHECK FAIL: every model question the Gateway asked reached the
  rig's fake model (6 asked, 5 received)`.

## Finding 4. Checks the unique index made true by itself - ACCEPTED, FIXED

Each now reads what the index cannot make true: the runs' own counts and reasons ("<n> decided, <m> already
decided"), and the decision row's attempt count with no default.

- **Batches:** the runs together reported exactly 23 messages, decided 23, and every run says `0 already decided`.
  No run reports more than 10.
- **Graph error:** the recovering run says `1 decided, 0 already decided`.
- **Model:** `AttemptCount` of the failed email is 2, read from its row.
- **Backup:** the stand-in's run reported 1 message: `1 decided, 0 already decided`.
- **Broken case:** the fake Graph's receive-time filter starts 2 milliseconds before the cursor (a cursor that slips
  back). All 23 decision rows stay unique, exactly as the reviewer said, yet the run failed: `CHECK FAIL: none twice:
  the runs reported 27 messages in all, decided 23 ...`. The runs' reasons said `8 decided, 2 already decided` and
  `5 decided, 2 already decided`.

## Finding 5. The seats write into the owner's real `~/.claude` - ACCEPTED: STATED PLAINLY, NOT MOVED

The ruling asks for a config folder of the rig's own, with the sign-in linked and never copied, or a plain statement
in the README if that is impossible. I did not move the seats, because linking only the sign-in is not safe for the
owner:

- The agent rewrites its files by **replacing** them (a rename), not by writing through a link. This was seen on
  this machine: the linked `~/.claude.json` in the rig's scratch home became a file of its own after the first seat
  started. The owner's `~/.claude.json` was not written.
- If the sign-in file (`.credentials.json`) is written the same way when its token is refreshed during a run, a link
  to that file alone would be replaced by the rig's own copy. The owner's file would keep a refresh token the
  service has just spent, and the owner would be signed out. I could not confirm from the agent how it writes that
  file, so I did not take the risk.
- Linking the **whole folder**, as the rig does, keeps every refresh in the owner's own file.

So the README now says plainly what the rig writes there. Every seat session's agent transcript goes into
`~/.claude/projects` (folders named `-tmp-mail-desk-rig-...`), and its prompts into `~/.claude/history.jsonl`. They
stay; the rig removes nothing outside its own folder. Every run records how many such folders it added. The README
also states the shared uv cache. Nothing already in `~/.claude` was deleted.

If the Tech Lead prefers the rig's own folder anyway, the safe way is a product change: the Director could start a
rig's agents with its own `CLAUDE_CONFIG_DIR`, signed in on its own. That is not in this phase.

## Finding 6. The preview search was a record, not a check - ACCEPTED, FIXED

- `marker_anywhere` is now `check(not marker_anywhere(...))` in the observe and routing cases.
- **Broken case:** one email's subject carries the marker, so it is stored. The run failed: `mail_desk_decisions.Subject:
  1` and `CHECK FAIL: nothing of an email's body preview is stored or logged`.

## Runs

- The changed sections on correct code (observe, graph-error, model, batches, switch-off, backup): **51 checks
  passed, 0 failed, exit 0**.
- The final full run on the phase branch is the committed `docs/qa/mail-desk/rig/transcript.txt`.
