# Mail Desk, phase 4: live mode - the contract every Developer of this phase builds to

Mission: `docs/missions/mail-desk-2026-10-18/MISSION.md` (issue #4213), section 6 row 4, sections 5.5 step 2, 5.6
(`mail_desk_routed`), 5.7 "Live mode", 5.8 (`done`, `return`) and the live-mode cases of 7.2. Written by the phase 4
Tech Lead on 19 October 2026, before any code, on the phase 3 branch (`mail-desk-phase-3`, pull request 4270). It
builds on:

- phase 1's `docs/qa/mail-desk/phase-1/CONTRACT.md` (the tables, `MailDeskWords`, who may do what, the routes);
- phase 2's `docs/qa/mail-desk/phase-2/CONTRACT.md` (what `checks/screen.py` reads and prints - **the check already
  carries out the filing queue in live mode and reports each result; this phase does not change the check**);
- phase 3's `docs/qa/mail-desk/phase-3/CONTRACT.md` (the hand-off, the four layers, the memory the Gateway hands and
  merges, the decision rows, the problems and the 08:00 section) and phase 5's (backup machines).

Where this file and the mission document disagree, the mission document wins and this file is corrected. Where this
file and the code disagree, the tests decide, and one of the two is a defect.

**LIVE MODE IS PROVEN ONLY ON THE RIG** (a throwaway Gateway, a slot 5 or higher test Director, a fake Graph, a fake
model). It stays OFF in production: a channel starts in `observe` (phase 1) and nothing in this phase may change that
default, switch any channel to `live`, or make `live` reachable except by `maildesk mode <address> live`, which phase 1
already limits to a person or a session in no factory. Everything stays behind the Mail Desk switch.

## 1. Decisions this phase settles

| Point | Decision | Why |
|---|---|---|
| What acts | Only a decision **recorded in live mode** (`mode` = `live` on its row) is ever acted on. A channel switched to live later does not file or route what was decided in observe mode; one switched back to observe stops queuing new filing and starts no new seat for its emails. | The decision row records the mode it was made in (phase 3); acting on an observe decision would be acting without the owner's switch. |
| Filing | A live `archive` or `junk` decision puts `{messageKey, decision}` in that channel's `filingQueue` in the memory the Gateway keeps (section 2). The next run of the check carries it out and reports it; the Gateway writes the result on the decision row (section 2.3). | Mission 5.7; the check side is phase 2's and is unchanged. |
| Routing | A live `route` decision is routed to its factory and seat; a live `mail-desk-agent` decision is routed to the channel's own factory and seat (phase 1's `factory`/`seat` columns, `mail-desk`/`mail-desk` by default). Each gets one `mail_desk_routed` row (section 3). | "Unsure ... the Mail Desk agent" (5.7) is a seat like any other. |
| Who starts seats | A new class, `MailDeskRouter` (in `src/CcDirector.Gateway/MailDesk/`), run by `TriggerService` right after a Mail Desk run is recorded, off the request, on the Gateway's lifetime, through the same `TriggerSessionStarter` delegate the trigger uses (production: `MachineSessionSpawner.SpawnOnMachineWithOutcomeAsync`). | Mission 5.7: "through the same MachineSessionSpawner path the trigger uses". |
| Where | On the machine of the Director that ran the check (`PendingScreening.Where`), naming that Director (`request.Director`). | Mission 5.7; phase 5 row "Where a session starts while a backup holds the claim". |
| One session per seat per run | All emails waiting for one (factory, seat) are given to ONE new session, in its prompt and in its kit's `check.json`. Two seats waiting: two sessions. | Mission 5.7. |
| A busy seat | While the newest session the Mail Desk started for a (factory, seat) has not ENDED (3.2 step 3: a row with `FleetManagerSessions.IsGone` false, or no row yet inside 24 hours of its start), that seat's new emails wait: their routed rows stay `open` with no session (queued). The next run gives every queued email of that seat to one new session once the seat is free. | Mission 5.7, "as a trigger's skipped-running does". |
| No body text | The prompt and `check.json` carry, per email: the routed id, the mailbox address, the sender, the subject, the receive time, the decision's layer and reason, and any return note. **Never the preview.** The seat reads the email itself (it has the mailbox, sender, subject and time to find it with cc-outlook). | "No body text is stored" (5.6); a prompt is kept with the session. A queued or re-routed email no longer has its preview anyway. |
| Work is never lost | A routed row stays `open` until its session runs `maildesk done` or `maildesk return`. An `open` row whose session ended without either is re-routed once (to the same seat), then handed to the owner. | Mission 5.7, "A routed email's work is never lost". |
| "With the owner" | The state `returned` means **handed back to the owner**: after a second return, after a session ended twice without done or return, after three failed starts, or after a return by the Mail Desk agent itself. A new column says why (section 3.1). It is an Open problem on the `mail-desk` factory and is listed in the 08:00 section. No new state word. | Mission 5.6 names the states `open`, `done`, `returned`. |
| Keep for the owner | Unchanged from phase 3: nothing is moved, nothing started; it is listed in the 08:00 section (phase 3, section 9.3). The decision's outcome stays null. | Mission 5.7. |

## 2. Filing (task A)

### 2.1 Queuing

`MailDeskService.ScreenAsync` adds to the memory it keeps:

- for every channel **handed this run whose check fields move on** (not written back after a model failure, phase 3
  section 1), every decision of that channel that is `archive` or `junk`, made in `live` mode, with outcome null, and
  whose key is not already in the channel's `filingQueue`, is appended to that channel's `filingQueue`, oldest receive
  time first. This covers the emails decided this run AND an email decided on an earlier run whose mailbox was written
  back then (its key was not in the kept table, so it could not be queued; it is reread and queued now).
- A mailbox written back this run queues nothing this run: its key table is reverted, so a queued key would come back
  `refused` ("no such key in this channel's key table").
- The queue is appended to what the check returned (the check has removed what it attempted). Nothing is ever removed
  from the queue by the Gateway except by the check's own removal.
- The size cap of phase 3 section 4 holds; a queue that would push the memory over it is a refusal like any other, so
  the whole run's memory is not kept and the same mail is read again (no partial queue is ever stored).

### 2.2 Reading the filing results

`MailDeskReport.Read` reads each section's `filing` list (phase 2, section 4.1): `messageKey`, `decision`, `result`
(`moved` | `already-gone` | `refused`) and `reason` for a refusal. An item whose `result` is none of these, or a key
that has no decision row in that channel, is a broken contract: logged, the run is `failed`, memory not kept.

### 2.3 What the decision row says

| Result | `outcome` | `outcomeDetail` |
|---|---|---|
| `moved`, archive | `filed` | `moved to Archive` |
| `moved`, junk | `filed` | `moved to Junk Email` |
| `already-gone` | `already-gone` (a new word in `MailDeskWords.Outcomes`) | `not in the Inbox any more when the move was tried (moved or deleted before)` |
| `refused` | `failed` | `the screening check refused the move: <reason>` |

- `already-gone` is never written as `filed` (phase 2 section 3.4: "read already-gone as not in the Inbox any more, not
  as moved by us").
- A result for a decision whose outcome is already set is logged and ignored (a retried move after a channel failure
  comes back `already-gone` for what was moved before; the first `filed` stands).
- The 08:00 section (phase 3 section 9) gains, for a channel in live mode, the count of emails moved to Archive and to
  Junk Email in its window, and every junked email keeps its line as before with "moved" beside it once filed.

### 2.4 Tests (all required)

1. A live channel's `archive` and `junk` decisions appear in the kept memory's `filingQueue`, oldest first; a
   `route`, `keep-for-owner`, `mail-desk-agent` and `failed` decision never do.
2. An observe channel's decisions never queue (phase 3's test stays) - and a channel switched to live does not queue
   its earlier observe decisions.
3. A live channel with a model failure on one email queues nothing that run; the next run, which rereads it, queues the
   emails decided on the first run and the one decided now.
4. A key already in the queue is not queued twice; a reread of an already-decided live `archive` email does not queue
   it twice.
5. The next report's `filing` results write `filed` / `already-gone` / `failed` with the details above; a result for
   an unknown key fails the run with memory not kept.
6. Two channels, one live and one observe, in one report: only the live one's queue grows.
7. The memory cap reached by a queue: the run is `failed`, memory not kept.

## 3. Routing (task B)

### 3.1 The table

`mail_desk_routed` (phase 1) gains, in one migration pair (SQLite and PostgreSQL, after main's newest migration at
the time of merging, proved with `scripts/test-database.ps1`):

| Column | Meaning |
|---|---|
| `ChannelId` | The channel the email came in on (for the prompt's mailbox address and the busy rule's seat). |
| `RerouteCount` | How many times a session ended without done or return and the email was given to a new session. |
| `StartFailures` | How many starts of a session for it failed outright. |
| `StartName` | The exact name of the newest session start made for it (for a start whose outcome was unknown). |
| `StartedUtc` | When that start was made. |
| `OwnerReason` | Why it was handed to the owner (state `returned`); null otherwise. |
| `DoneNote` | The note given with `maildesk done`, if any. |

One routed row per decision (unique per tenant and decision id). `SessionId` null with state `open` means **queued**.

A store, `MailDeskRoutedStore`, owns every write: create for a decision, take for a session (queued -> started), the
re-route, the start failure, `done`, `return`, and the hand to the owner. Each write records `ChangedUtc`. Every state
change is logged (ids and counts only; never a sender or subject).

### 3.2 When the router runs

After `FinishScreeningAsync` has recorded a `screened` run for a Mail Desk trigger - never after a `failed`, `paused`
or `skipped-running` one - `TriggerService` runs `MailDeskRouter.RouteAsync(tenant, trigger, directorId, machine)`.
It runs outside the per-trigger lock, on the Gateway's lifetime, and is awaited by the screening task (so a test can
await `LastScreeningForTest`). A router failure is logged and never changes the run row. One router run per account at
a time: a second is skipped and logged (the next screening runs it again).

In order:

1. **Create.** Every live `route` or `mail-desk-agent` decision of the account with no routed row gets one: `open`,
   queued, its factory and seat (the decision's route, or the channel's own factory and seat for `mail-desk-agent`).
2. **Settle unknown starts.** A queued row with a `StartName` whose start's outcome was unknown: a session with that
   exact name found (`findSessionByName`) is adopted (the row takes its id); not found and `TriggerService.StartGrace`
   is over, the start counts as failed (3.4).
3. **Orphans.** An `open` row whose session has ENDED without `done` or `return`. Ended means positive evidence: a
   session row the Gateway holds with `FleetManagerSessions.IsGone` true. A session the Gateway cannot see at all is
   NOT ended by its absence - after a Gateway restart, or while its Director is away (a backup takeover), a live
   session is invisible for a while, and re-routing it would put two sessions on one email. Absence alone counts as
   ended only **24 hours** after the start (`MailDeskRouter.UnseenSessionEndsAfter`), so an email whose Director died
   with it is still never lost. Until then the seat counts as busy too. (Review of 4275, finding 1, ruled by the Tech
   Lead, 19 October 2026: lean to keep.) Ended: `RerouteCount` 0 -> it is queued again for the same
   seat, `RerouteCount` 1; `RerouteCount` already 1 -> handed to the owner, reason "its session ended twice without
   saying done or returning it".
4. **Start.** Group the queued rows by (factory, seat). For each group whose seat is not busy (section 1), oldest
   email first, at most **10 emails** to one session (the rest stay queued for the next run): build the session (3.3)
   and start it through the starter. Groups are started one after another.
5. **Record.** Started: each row of the group takes the session id; each decision's `outcome` becomes `seat-started`,
   `outcomeDetail` "session <id> on <machine> (<factory>/<seat>)". Failed: 3.4.

### 3.3 The session

- Built by `FactorySeatStart.Build` from the **target factory's** published definition and the target seat, exactly as
  `TriggerService.DefinedStart` does: the seat's agent, model, repository or home, and its kit.
- `Factory` = the target factory (the session is born into it). `Director` = the Director that ran the check.
  `AutoDismiss` = true. `Origin` = schedule, `OriginSurface` = `SessionOriginSurfaces.Trigger`, the trigger's own
  surface. **Not a new surface word:** the Director refuses a create whose surface is not in its own fixed list
  (`SessionCommandExecutor`, "unknown origin surface"), so a new word would make every Director released before this
  phase refuse every Mail Desk start - a desktop release this mission is not granted (found by the routing Developer,
  ruled by the Tech Lead, 19 October 2026). What tells a Mail Desk session from a trigger's is its routed rows and its
  name.
- **Name:** `Mail Desk - <factory>/<seat> - <n> emails - <yyyy-MM-dd HH:mm>` in the account's time zone, with "1 email"
  for one. The name is stored in each row's `StartName`.
- **Prompt:** the seat's own start prompt is not used (this is not one of the seat's starts). The prompt is the Mail
  Desk's, in plain words: "The Mail Desk routed <n> emails to you (<factory>/<seat>). They are listed in check.json in
  your kit and below. Read each one in its mailbox with cc-outlook. When you have dealt with one, run
  `cc-devthrottle maildesk done <routed id> --note "<what you did>"`. If it is not for you, run
  `cc-devthrottle maildesk return <routed id> --note "<what you know about it>"` and it goes back to the Mail Desk.",
  then one block per email (routed id, mailbox, sender, subject, received, why it was routed to you, and "Returned
  before: <note>" when it was). Every sender and subject in it is cleaned of control characters; nothing else of the
  email is in it.
- **`check.json`** (the kit's, written by the Director from `request.FactoryCheck`; no Director change): `CheckPath` =
  `checks/screen.py`, `Count` = the number of emails, `Context` = a JSON document
  `{"version":1,"kind":"mail-desk-routed","factory":..,"seat":..,"emails":[{"routedId":..,"mailbox":..,"sender":..,"subject":..,"receivedUtc":..,"layer":..,"reason":..,"returnNote":..|null}]}`
  at most 7,168 bytes (fewer emails are given to the session when it would be larger; the rest stay queued),
  `CheckedUtc` = the run's check time, `DefinitionVersion` = the `mail-desk` trigger's definition version, `Seat` = the
  target seat, `Start` = 0 ("not one of this seat's starts: routed by the Mail Desk").

### 3.4 A start that fails

- **The target cannot take it** - the factory has no published definition, the published version cannot be read, or it
  holds no such seat: no start is made. A row whose seat is not the channel's Mail Desk seat is re-routed to the Mail
  Desk seat with the reason as its return note (not counted as a return). A row already at the Mail Desk seat is
  handed to the owner with the reason.
- **The start fails** (the machine refused, an error): `StartFailures` + 1, the row stays queued; on the 3rd failure it
  is handed to the owner, reason "its session could not be started 3 times: <last error>". The decision's outcome stays
  null until then, and becomes `failed` with that reason when it goes to the owner.
- **The start's outcome is unknown**: the row stays queued with `StartName` and `StartedUtc` set and is settled by
  3.2 step 2; no second session is started for it meanwhile (the seat counts as busy while that start is inside
  `StartGrace`).

### 3.5 Tests (all required, with the starter and the session list as fakes and an injected clock)

1. A live route to another factory's seat starts that seat on the reporting machine, naming the reporting Director,
   with every email in its prompt and in `FactoryCheck.Context`, and nothing of a marker preview anywhere (the
   request, the rows, the log sink).
2. Two routes in one run to two seats start two sessions; three emails to one seat start one session with three.
3. An observe decision never gets a routed row; the starter is never called.
4. A seat whose Mail Desk session is alive: its new email stays queued, the starter is not called for it; once the
   session is gone the next run starts one session with every queued email of that seat.
5. `mail-desk-agent` routes to the channel's own factory and seat.
6. An orphan is re-routed once to the same seat, then handed to the owner with the reason.
7. A target with no published definition / no such seat: re-routed to the Mail Desk seat with the reason; the same
   for the Mail Desk seat itself: handed to the owner.
8. Three failed starts hand the row to the owner; a start with an unknown outcome is adopted by name, or counted
   failed after the grace, and no second start is made inside the grace.
9. Eleven emails for one seat: 10 in the session, 1 queued; a context that would pass 7,168 bytes gives fewer.
10. The router never runs after a `failed`, `paused` or `skipped-running` run; a router that throws leaves the run
    row as it was.
11. A backup machine's run (phase 5) starts the seat on the backup machine.

## 4. Done, return and the owner (task C)

### 4.1 The routes (mapped into `MailDeskGate.Group`, prefix `/gateway/mail-desk`; 404 with no body when the switch is off)

| Method and path | Body or query | Who | Answer |
|---|---|---|---|
| `GET /routed` | `?state=open\|done\|returned&queued=true&factory=&seat=&n=` (default 50) | anyone in the account | `{ count, total, routed: [<routed row>] }` newest first; every filter applies |
| `POST /routed/{id}/done` | `{ note? }` | **only the session the row is with now** (its `SessionId`) | 200 `<routed row>` (`done`) |
| `POST /routed/{id}/return` | `{ note }` (required, not blank) | **only the session the row is with now** | 200 `<routed row>`: re-queued to the Mail Desk seat, or handed to the owner |

- Any other caller - a person, another session, a session the row was with before a re-route, the machine token, a
  Director - is refused **403** `{"error":"not_your_email","detail":"<sentence naming who holds it>"}`. A row not
  `open` is **409** `{"error":"not_open","detail":"<sentence with its state>"}`. No such row: **404**
  `{"error":"no_such_routed_email"}`. A return without a note: **400** `{"error":"note_required"}`. Nothing is
  recorded on any refusal.
- **Return**, first time, from a seat that is not the Mail Desk seat: the row's factory and seat become the channel's
  Mail Desk seat, `SessionId` null (queued), `ReturnedCount` 1, `ReturnNote` the note; the next run starts the Mail
  Desk agent with it and the note in its prompt. **Return a second time, or a return from the Mail Desk seat itself:**
  state `returned`, `ReturnedCount` raised, handed to the owner, reason "returned twice: <note>" or "the Mail Desk
  agent returned it: <note>".
- **The routed row:** `{ id, decisionId, channel, messageKey, sender, subject, receivedUtc, factory, seat, sessionId,
  state, stateLabel, queued, returnNote, returnedCount, rerouteCount, startFailures, ownerReason, doneNote, createdUtc,
  changedUtc }`; `stateLabel` is folded on the Gateway ("Open - with session <id>", "Queued - waiting for
  <factory>/<seat>", "Done", "With you - <owner reason>").

### 4.2 The owner

- **The problem:** every row handed to the owner is one problem, kind `mail-handed-to-owner`, on the `mail-desk`
  factory, folded into `CronRunResultFold.Problems` through `MailDeskProblems` as phase 3 did for mailboxes: sentence
  "<sender> - <subject> on <mailbox> is with you: <owner reason>". Resolvable by hand with a one-line reason, like
  every problem. It shows on the Open problems card and in the 08:00 email with no change of their own.
  The routed row keeps what this needs in its own columns, added by task C's own migration pair after task B's:
  `ResolvedUtc`, `ResolvedBy`, `ResolvedReason` (as every other problem source keeps them), `OwnerUtc` (when it was
  handed to the owner), `ReturnedUtc` and `ReturnedFrom` (when and from which `factory/seat` it was last returned - the
  factory, seat and `ChangedUtc` are overwritten by the re-queue and the next start). The problem's id is the routed
  row's id. Resolving clears the problem only; the row stays `returned`. An email is returned at most twice, so three
  more columns, `EarlierReturnedUtc`, `EarlierReturnedFrom` and `EarlierReturnNote`, keep the first return when the
  second arrives, and the 08:00 list shows both. (Raised by the task C Developer, accepted by the Tech Lead,
  19 and 20 October 2026.)
- **The 08:00 section** (phase 3 section 9) gains **Returned and routed**: every email returned in the window (by
  mailbox, sender, subject, the seat that returned it and its note), every email handed to the owner (with the
  reason), and counts of emails routed, done and still open.

### 4.3 The commands (`cc-devthrottle maildesk`, AXI)

```
maildesk routed [--state open|done|returned] [--queued] [--factory F] [--seat S] [-n N] [--json]
maildesk done <routed id> [--note "<text>"]
maildesk return <routed id> --note "<text>"
```

- `return` without `--note` fails with exit 2 before any request. The command decides nothing and prints the
  Gateway's answer; a Gateway refusal prints its sentence, exit 1; the switch off says "Mail Desk is not switched on
  for this account", exit 1. `count: 0` on an empty list; ids never cut short; `help[]` lines.

### 4.4 Tests (all required)

1. `done` by the holding session; refused for another session, a person, a previous holder and the machine token;
   409 for a row not open; 404 for no row.
2. First `return` re-queues to the Mail Desk seat with the note, and the next router run starts the Mail Desk agent
   with the note in its prompt and `check.json`; a second `return` hands it to the owner; a return by the Mail Desk
   agent hands it to the owner.
3. The problem appears on the factory's Open problems and in the 08:00 email; resolving it by hand clears it.
4. Every filter of `GET /routed` applies; the switch off answers 404.
5. The command tests for each verb, the missing note, an unknown flag, `--json` and `count: 0`.

## 5. The rig (task D)

`docs/qa/mail-desk/rig/run_qa.py`, in the shape of `docs/qa/factory-definition/phase-5b/rig/run_qa.py` and of the
Linux rig `docs/qa/mail-desk/phase-0/a-sign-in/rig/run_proof.py`, run on devlinux, writing
`docs/qa/mail-desk/rig/transcript.txt`, which is committed. It runs in the foreground, owns everything it starts, and
stops everything it started, pass or fail.

- **A throwaway Gateway** built from this tree on a free 127.0.0.1 port, its own storage root under the rig folder,
  Tailscale off, authentication on, factory agents on in its own `config.json`, Mail Desk switched on for its one
  account through the administrator route.
- **A slot 5 or higher test Director built from this tree** (`scripts/local-build-linux.sh --slot <n>
  --framework-dependent`), with a scratch `CC_DIRECTOR_ROOT` AND a scratch `HOME`, every `CC_` variable of the rig's
  shell removed, auto-update off, stopped only by its own lifecycle request file. Afterwards the rig checks that
  `~/.local/bin` still points at the owner's tools.
- **The check is the real `checks/screen.py`, with its network faked.** A check gets only `CC_CHECK_` variables and its
  request guard allows only `https://graph.microsoft.com`, so the rig's published `mail-desk` definition holds, as
  `checks/screen.py`, a small rig wrapper that loads the template's real `screen.py` byte for byte (held beside it in
  the definition) and replaces only (a) the one network function the tests already replace, with a client of the rig's
  fake Graph on 127.0.0.1, and (b) the sign-in lookup and token acquisition, with the rig's own scratch sign-in folder
  and a fake token. **The rig must never let the check find the owner's real sign-in or reach Microsoft:** the wrapper
  refuses to run if the sign-in folder it is given is not under the rig folder. Everything else - the memory, the
  cursor, the batches, the request guard, the filing - is the real code. State this plainly in the README.
- **The fake Graph** (extend phase 2's and the phase 5b rig's): an Inbox and Sent per mailbox, the move endpoint, a log
  of every call (method, path with ids replaced, destination), switchable failures.
- **A fake model** on 127.0.0.1 answering chat completions with log probabilities, scripted per subject, able to fail
  or answer off the labels. **The rig never calls DeepInfra.** The model's address was a constant
  (`TranscriptionEndpoint.DevThrottleBaseUrl`) with no setting, so this phase adds one (decided by the Tech Lead on the
  rig Developer's evidence, 19 October 2026): an optional Gateway `config.json` key `mailDeskModelBaseUrl`, honoured
  ONLY when its host is a loopback address (`127.0.0.1`, `::1`, `localhost`); any other value refuses the Gateway's
  start with a sentence naming the key and the rule; absent, the constant is used, so production is unchanged. A proxy
  with a self-made certificate was rejected: if either setting were not honoured, the call would reach the real
  service with a real key.
- **Seats are real sessions** with tiny prompts on the cheapest agent the definitions allow, in two factories besides
  `mail-desk` (for example `qa-books` with seat `clerk`, `qa-support` with seat `desk`), whose briefs tell them to run
  `maildesk done` or `maildesk return` for the emails in their `check.json`. What a seat did is read from the Gateway
  (routed rows) and from its own transcript.
- Addresses: `qa-one@example.com`, `qa-two@example.com` and example senders only. No real address, no token, no real
  email text in the transcript (the 5b rig's `shown()` rule).

**Every case the transcript must show** (each a titled section with the commands run and what the Gateway answered):

Live mode: archive and junk moves only (the fake Graph's call log); a route starting the right seat in another factory
with the emails in its prompt and `check.json`; `done`; `return` re-routing to the Mail Desk agent; a second return
reaching the owner (Open problems and the 08:00 section); an orphaned session (the rig ends it) re-routed once; two
routes in one run starting two seats; a busy seat queueing its emails, then taking them; keep for the owner leaving
the email in the Inbox.

Failure cases: not signed in (a failed mailbox check naming the fix, on the Open problems card); Graph answering an
error (memory not kept, the same messages read next run); the model failing or answering outside the labels (`failed`
/ `mail-desk-agent`, never a guess); more mail than one batch (several runs, nothing skipped, nothing twice); observe
mode with a filing queue present making no move; a factory session refused `maildesk mode ... live`; the switch off
(routes 404, a Mail Desk report refused, nothing recorded); backup machine handover and hand-back (the second machine
may be the rig standing in as a second Director through the Director routes, with a different machine name - say so).

## 6. Who builds what

| Task | Developer session | Starts | Depends on |
|---|---|---|---|
| A. Filing (2) | Mail Desk - Developer - live filing | now, from `mail-desk-phase-4` | nothing |
| B. Routing (3) | Mail Desk - Developer - live routing to seats | now, from `mail-desk-phase-4` | nothing |
| C. Done, return and the owner (4) | Mail Desk - Developer - done, return and the owner | when B is in the phase branch | B |
| D. The rig (5) | Mail Desk - Developer - live mode rig | now (the Gateway, the Director, the fakes, the cases phase 3 already supports); each live case as A, B and C land | A, B, C |

Each Developer works in its own worktree cut from `mail-desk-phase-4` (`git worktree add ../devthrottle-md4-<task> -b
md4-<task> origin/mail-desk-phase-4`), opens its pull request **into `mail-desk-phase-4`**, and owes its tests run and
passing. Each Developer's code is reviewed by a separate Reviewer session (Claude Code on Opus, handover ruling 9),
opened by the Tech Lead; the review and the Developer's answer to every finding are committed in
`docs/qa/mail-desk/phase-4/reviews/`.

## 7. Example data

The repository is public. Tests and the rig use only `email@devthrottle.com` and the reserved example domains
(`example.com`, `example.org`, `example.net`, `*.example`). Never the owner's second mailbox address, never a real
sender's address or domain, no token, no real email text.
