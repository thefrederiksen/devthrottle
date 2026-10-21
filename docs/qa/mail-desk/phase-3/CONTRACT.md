# Mail Desk, phase 3: the contract every Developer of this phase builds to

Mission: `docs/missions/mail-desk-2026-10-18/MISSION.md` (issue #4213), section 6 row 3, sections 5.5 to 5.7 and 5.10.
Written by the phase 3 Tech Lead on 18 October 2026, before any code. It builds on two contracts and changes one of
them on purpose:

- phase 1's `docs/qa/mail-desk/phase-1/CONTRACT.md` (the tables, the vocabulary in `MailDeskWords`, the rules
  document, `MailDeskStart.Is`, the switch, who may do what, the decision row);
- phase 2's `docs/qa/mail-desk/phase-2/CONTRACT.md` (what `checks/screen.py` reads, writes and prints). **Section 6 of
  this file changes phase 2's rule "when any one channel fails, the whole run fails"**, by the Delivery Lead's ruling
  of 18 October 2026 (one mailbox failing must not stop the others).

Where this file and the mission document disagree, the mission document wins and this file is corrected. Where this
file and the code disagree, the tests decide, and one of the two is a defect.

Everything here is behind the Mail Desk switch, off for every account by default. **Observe mode only: this phase
moves no message and starts no session, in any mode.** Live mode is phase 4's.

## 1. Decisions this phase settles

| Point | Decision | Why |
|---|---|---|
| How a Mail Desk report is handled | `TriggerService.ReportCheckAsync` sends every report of a trigger for which `MailDeskStart.Is(trigger)` holds to `MailDeskService`, **before** the count is read, whatever the count. It never reaches `Decide` or a session start. | The hard order: a published `mail-desk` factory must never start a seat on a count. |
| A Mail Desk report with no Mail Desk service wired | Refused, **409** `{"error":"mail_desk_not_ready","detail":"<sentence>"}`, nothing recorded: no run row, no kept memory, no claim change, no start, no decision. | The guard holds even in a Gateway built without the service (a test host, a later refactor). The status follows phase 1's `mail_desk_off`. |
| A Mail Desk check on a TIMED start | Refused: `DueCheckService` answers a Mail Desk timed start's report (`MailDeskStart.Is` on its schedule) with **409** `mail_desk_not_ready`, "the Mail Desk screening runs only on an interval start", nothing recorded, no seat started. `CronRunResultFold.IsProblem` is unchanged, because no schedule run is ever `screened`. (Found by the task F Developer, 19 October 2026, accepted by the Tech Lead.) | An owner editing the draft from `every:` to `at:` would otherwise reach "count above zero starts the seat" through the timed path: a hole in the hard order. |
| When the screening runs | **Off the request**, on the Gateway's lifetime, like a session start (`PendingStart`). The report answers at once. While one screening of a trigger runs, a second report of it is recorded `skipped-running` with the reason "the previous screening is still running", memory not kept. | Up to 20 model calls must not hold the Director's request open. |
| The new run outcome | `screened` (`TriggerRunOutcome.Screened`). It **keeps the check's memory**, like `nothing-to-do`, and clears a check problem like any outcome that is not `failed`. Every place that names every outcome (the activity record, `CronRunResultFold.IsProblem`, the status and factory folds, `trigger runs`) names it. | Mission 5.7. |
| A screening that could not be finished for some email | The run is still `screened` and the memory is kept, **except for that email's mailbox**: the Gateway writes back that mailbox's check fields (`cursor`, `sentCache`, `keys`) exactly as the memory held them before this run, so the check reads that mailbox's same mail again next run. Every other mailbox moves on. That mailbox gets a `mail_desk_channel_checks` failure row, "the model could not be asked about <n> of <m> emails on <address> (<error kind>); they are read again next run", so it is a mailbox problem (6.2). | "Retried next run, never guessed", without storing any body text, and scoped to the one mailbox (the Delivery Lead's condition, 18 October 2026, consistent with the per-mailbox ruling). |
| A reread is idempotent | One decision row per (channel, message key). An email already holding a decision that is not `failed` is skipped on a reread: no model call, no charge, no new row. | The Delivery Lead's condition 2. |
| The same email failing again and again | On its **3rd** failed attempt (counted on its decision row) the email is decided `keep-for-owner`, reason "screening failed 3 times (<error kind>); kept for the owner", it is listed in the 08:00 section, and its mailbox's memory moves on as usual. | One email the model always refuses can never block its mailbox (the Delivery Lead's condition 3). |
| The model | `IncludedModelId.WingmanFast` (`devthrottle/wingman-fast`), through the Gateway's hosted path (`TranscriptionEndpointResolver.ResolveWingman`, the key vault), the account named on the call: `GatewayAiCallTags.For(tenant, AiFeature.MailDesk)`, a new feature name `mail-desk`. | Mission 5.7, D14, question 4. |
| The thresholds and the spend limit | Account settings in `tenant_settings`, settable, every change recorded in `mail_desk_list_changes` with kind `setting`. Defaults: **route 0.90, spam 0.95, spend limit 5.00 US dollars.** | The brief: "make it settable and record it". Phase 0 (c) proposed the two thresholds. |
| What the spend limit counts | Every model call the Mail Desk has made for the account, ever: the sum of `costUsd` on its decision rows plus the cost of calls whose screening failed (kept on the row too). The limit is checked **before each call**; at or over it, no call is made. | "A spend limit of 5 dollars for the mission, after which it stops calling the model and says so." A call costs about $0.0001, so at most one call can cross it. |
| What the Gateway hands the check | The kept memory, with its `channels` list made from the account's active channel rows **that name the claiming Director's machine** (section 4). | Phase 2's check needs the Gateway to write the channel list (its 3.2), and nothing does yet. |
| Where a failed mailbox shows | A Mail Desk problem source: `mail_desk_channel_checks` rows, folded into the Open problems card of the `mail-desk` factory and the 08:00 email, one problem per mailbox per run of failures. | The ruling: each mailbox fails on its own, as its own named problem. |

## 2. The hand-off, and the hard order

1. `ReportCheckAsync` finds the trigger. If `MailDeskStart.Is(trigger)`:
   - the switch off: phase 1's 409 `mail_desk_off`, nothing recorded;
   - no `MailDeskService` wired: 409 `mail_desk_not_ready`, nothing recorded;
   - a screening of this trigger already running: `skipped-running`, memory not kept;
   - otherwise the report goes to `MailDeskService.ScreenAsync` off the request, and the route answers as it does for a
     start that is under way.
2. The claim checks (`HeldByAnotherDirector`, definition-check support) run first, exactly as for any trigger.
3. **A failed check report** (non-zero exit) of a Mail Desk start is recorded `failed` by the ordinary rule, memory not
   kept. That is the whole-run failure of section 6.
4. A successful report is read by `MailDeskService`, never by `Decide`, whatever its `count` (a run where every
   mailbox failed may report 0).

**Tests (all required):** a Mail Desk report with a count of 5 starts no session (the starter fake is never called) and
writes a `screened` run; the same with no service wired is refused 409 with no run row, no kept memory and the starter
never called; the same with the switch off is phase 1's 409; a Mail Desk report with count 0 is still screened; a
trigger of the `mail-desk` factory whose check is NOT `checks/screen.py` takes the ordinary path (the predicate is
phase 1's, unchanged); a second report while a screening runs is `skipped-running` and keeps no memory.

## 3. The four layers, in order

For each message in the report, oldest first, the first layer that decides wins. Matching is plain code (phase 1
section 3): an exact address or `*@domain`, case-insensitive; "subject contains" is case-insensitive. The rules
document is the account's newest version, read **once per run**, and its version is written on every decision.

| Layer | `layer` word | Decides when | Decision | `reason` (one line, no body text) |
|---|---|---|---|---|
| 1. Fixed rules | `rule` | the first rule, in document order, whose every given condition holds (`subjectContains`: any of the texts; `fromAny`: any of the patterns) | `route` to its factory and seat | `rule <name>` |
| 2. Allow-list | `allow` | the first entry whose pattern matches the sender | its `do`: `archive`, `keep-for-owner`, or `route` | `allow <pattern> (<what>)` |
| 3a. Block list | `block` | the first entry whose pattern matches | `junk` | `block <pattern> (<why>)` |
| 3b. Unknown sender | `sender-screen` | the sender is not known (`knownSender` false; lists 1 to 3a have not matched by now) | `spam` at or above the spam threshold, or `spam` with no confidence returned: `junk`. `person`, any confidence: on to layer 4. Anything else (an answer off the labels, `spam` below the threshold): `mail-desk-agent` | `sender-screen: spam at 0.97 (threshold 0.95)`, `... spam, no confidence returned`, `... spam at 0.80, under the threshold 0.95`, `... answered outside the labels` |
| 4. Route | `route-model` | always, for what is left | a route at or above the route threshold, or a route with no confidence returned: `route` to it. `unsure`, an answer off the labels, or below the threshold: `mail-desk-agent` | `route-model: devthrottle/mail-desk at 0.93 (threshold 0.90)`, `... devthrottle/mail-desk, no confidence returned`, `... unsure`, and so on |

- **No confidence returned (amended 21 October 2026, the owner's ruling):** DeepInfra returns `logprobs: null` for
  `Qwen/Qwen2.5-72B-Instruct`, the included fast model, even when the request asks for them - proven with one real
  reply. So every label comes back with no confidence, and a label with none is acted on as answered; the reason says
  no confidence came with it. When a confidence does come back, the thresholds decide as before.
- **A known sender skips 3b** and goes straight to layer 4.
- **No routes in the document:** layer 4 asks nothing and decides `mail-desk-agent`, reason "no routes are defined".
  At most 25 routes are offered (letters A to Y, `unsure` last); phase 3 adds that cap to `MailDeskRulesValidator`, so
  a 26th route is refused when it is added, never cut at screening time.
- **An empty sender address** (Graph gave none) matches no pattern and is unknown.
- **The model only picks a label.** It never writes text that is stored, and nothing it answers can delete a message.
- **The spend limit reached:** a message that needs a model question is decided `failed`, reason "the Mail Desk spend
  limit of $5.00 is reached; the model was not asked". It is NOT retried (the limit does not lift by itself), the run
  keeps its memory, and the spend problem (section 7) says so.
- **The model failing** (no key, an HTTP error, a timeout, an answer that is not a chat completion, an answer with no
  token count so its cost is unknown): that message's decision is `failed` with the error kind, and the run is
  run handles it as section 1 says: only that email's mailbox reads it again.
- An email already holding a decision that is not `failed` is never decided again and costs nothing; a `failed` one
  is decided again and its row updated in place, its attempt count raised; the 3rd failure makes it `keep-for-owner`.

**Tests (all required):** each layer deciding alone; the order (an email matching a rule AND the allow-list AND the
block list is routed by the rule; one matching allow AND block cannot exist, phase 1 refuses it; an allow match is
never asked the model); a known sender skipping 3b; `unsure`; below each threshold; exactly at each threshold
(decides); an answer off the labels; no confidence; no routes; the 26th route refused; a model failure on one email
of three on mailbox A while mailbox B is screened (two decided, one `failed`; the run `screened`; A's cursor, keys and
Sent cache written back as they were, B's moved on; A's failure row raised; the next run rereads A only, makes no model
call and no charge for A's two decided emails, decides the third and updates its row; B is not reread); the third
failed attempt deciding `keep-for-owner` and letting A's memory move on; the spend limit (the call that would start at the limit is never made, the
fake model's call log proves it); observe mode starting nothing and queuing no filing (the memory's `filingQueue`
unchanged, the starter fake never called) for every decision kind, and the same for a channel whose mode is `live`
(this phase acts on no mode; the decision row records the mode it was made in).

## 4. The memory the Gateway hands the check

The Mail Desk start's assignment carries the kept memory (`DefinitionAssignment.State`) **made up by the Gateway**:

- `channels` is the account's active channel rows whose `machines` list holds the machine of the Director being handed
  the assignment, in channel creation order. Each entry carries the Gateway's fields `id`, `address`, `provider`,
  `mode`, and the check's own fields (`cursor`, `sentCache`, `keys`) and `filingQueue` exactly as the kept memory last
  held them for that channel id.
- A channel the kept memory has never seen gets only the Gateway's fields, so the check takes it as a first run.
- A channel handed to this machine and dropped from the report keeps its kept fields (the check returns every channel
  it was given, phase 2 3.2; a missing one is a defect, logged and refused).
- **A channel NOT handed** to this machine (it names other machines only) keeps its kept fields untouched in the
  stored memory, so a backup machine (phase 5) never erases another machine's cursor. The Gateway stores the memory by
  channel id and merges what came back into it.
- A removed channel is handed to nobody; its kept fields stay in the stored memory for 30 days and are then dropped.
- No active channel names this machine: the memory handed holds an empty `channels` list, so the check fails with
  phase 2's "no channels" sentence, and the problem source raises "the Mail Desk start runs on <machine>, but no
  channel names it: add it with maildesk channel add ... --machine <machine>".
- The memory stays under 65,536 bytes; above that the Gateway refuses to write it and raises a problem naming the
  sizes (phase 2 measured about three channels at their caps).

**Tests:** a first channel; a channel added through the real route (`POST /gateway/mail-desk/channels`, what
`maildesk channel add` calls) appearing in the very next assignment's memory and being read as a first run while the
first keeps its cursor; a channel removed through `POST /gateway/mail-desk/channels/remove` missing from the next
assignment's memory, so it stops being read, its decisions kept; two machines each handed only their own channels, and each one's report leaving the other's cursor
byte for byte; no channel for this machine; the size refusal.

## 5. The model call

- One class, `MailDeskModel` (in `src/CcDirector.Gateway/MailDesk/`), shaped like `HostedCandidateJudge`: one stateless
  `POST <base>/chat/completions`, never a retry inside a run, the credential never logged, no sender, subject or
  preview ever in a log line (counts, timings, status codes and error kinds only).
- The two questions are phase 0's prompts, held as embedded resources (`prompts/system.txt`,
  `prompts/person_or_spam.txt`, `prompts/route.txt` from `docs/qa/mail-desk/phase-0/c-model/prompts/`): labels shown as
  letters, the sender name and address, the subject, and the 200-character preview.
- The request: `model` = `WingmanFast`, `temperature` 0, `max_tokens` 2, `logprobs` true, `top_logprobs` 5,
  `stream` false, the `mail-desk` tag with the account.
- **The answer** is the reply's text, trimmed. Exactly one listed letter: that label. Anything else: `off-labels`.
- **The confidence** is `exp(logprob)` of the first token of the reply when that token is the answered letter; no log
  probabilities in the reply: `null` (never guessed, never 1.0).
- **The cost** is the reply's `usage.prompt_tokens` and `usage.completion_tokens` at **$0.36 and $0.40 per million**
  (one constant pair, named for the model, beside `IncludedModelId`). A reply with no usage is a failure: its cost is
  unknown, so it cannot be held to the limit.
- A per-call deadline of **15 seconds**, and a deadline of **4 minutes** for one whole screening; a question not asked
  by then is a failure of that email (retried next run).
- Result: `{ label | off-labels | failed, confidence, costUsd, inputTokens, outputTokens, errorKind }`.
- **The plan's gate.** The factory asks the tenant AI gate with `EntitlementScopes.Wingman`, as every other internal
  feature on the Wingman endpoint does (the dictionary suggestions, the Mentor). A tenant the gate refuses gets a model
  whose every question fails with the gate's own sentence. A self-hosted Gateway is not gated. (Decided by the Tech
  Lead on the model-call Developer's evidence, 18 October 2026.)
- **Strict answers.** `a` and `A.` are off the labels, unlike phase 0's script. Phase 6 measures how often the real
  model answers that way; loosening it is a change to this contract, not a quiet fix.
- **A failed call's cost is not known**, so it is recorded as none. A call that timed out may still have been billed,
  so the spend counted can be lower than the spend made by those calls (about $0.0001 each).
- No key in the vault, or no hosted endpoint (a self-hosted Gateway): every model question fails with "the Gateway
  holds no key for the hosted model", so rules and lists still decide and model questions are `failed`.

**Tests**, over a fake HTTP handler: the request body exactly (model, temperature, max_tokens, logprobs, the tag
headers, letters in the prompt); each answer kind; confidence from log probabilities and `null` without them; cost from
usage and a failure without it; HTTP 500, 402, 429, a timeout, a body that is not a chat completion; nothing of the
email in any log line (a test reads the log sink).

## 6. One mailbox failing does not stop the others (the ruling)

### 6.1 `checks/screen.py`

- **A failure that belongs to one channel no longer fails the run.** That channel's section in the context carries
  `"failure": {"sentence": "<phase 2's sentence for it>", "detail": "<phase 2's line 2>"}`, `messages` empty, `filing`
  empty, `more` false. Its own fields in the memory (`cursor`, `sentCache`, `keys`, `filingQueue`) come back **byte for
  byte as they were**. Every other channel is read as if the failing one were not there.
- **Per channel:** every sign-in failure (none, more than one, no account, the account list unreadable, the sign-in
  service unreachable or failing), every Graph failure for that channel (HTTP, unreachable, not JSON, no `value`, a
  bad message, a move failing), an unknown provider, a bad `mode`, and a `cursor`, `keys` or `sentCache` this check did
  not write.
- **Still the whole run (exit 1, memory untouched):** a memory missing, empty, not UTF-8 or not JSON; no `channels`
  list or an empty one; two channels with one address; the memory over its cap; anything the check did not foresee.
- The context `version` becomes **2**. `count` stays messages reported plus filing items attempted; a run where every
  channel failed prints count 0 and exits 0, with every failure in the context.
- Phase 2's tests stay, changed where the rule changed, and new ones prove: one channel's sign-in missing while the
  other is read and its cursor moves; the failing channel's fields unchanged byte for byte; each per-channel failure
  kind in its own section; the whole-run failures still exit 1; two channels both failing.

### 6.2 The Gateway

- `MailDeskService` writes one `mail_desk_channel_checks` row (new table, both databases, a migration pair) for **every
  failed mailbox check, and for the first good check after a failure** (that row clears the problem); a good check
  after a good check writes nothing. Columns: id, tenant, channel id, trigger run id, checked utc, machine, ok, sentence,
  detail, resolved utc, resolved by, resolved reason.
- **The problem**: failures in a row of one channel are one problem, kind `mailbox-failed`, sentence
  "<address> could not be checked on <machine>: <the check's sentence>" (the check's sentence names the fix), counted
  like a failed check, cleared by that channel's next good check, resolvable by hand with a one-line reason like every
  problem. It is folded into `CronRunResultFold.Problems` as a third source, under the `mail-desk` factory, so the Open
  problems card and the 08:00 email show it with no change of their own.
- Phase 1's channel row `signedIn` is folded from these rows, per channel: `failing` with the check's own sentence
  while the channel's newest row is a failure; `yes` once a run has read it; `unknown` before any run. One fold,
  `MailDeskChannelFold`.

**Tests:** the problem raised by the first failure, counted over three, cleared by the next good check, resolved by
hand and a new failure starting a new problem; two channels, one failing, the other with no problem; the problem on
the factory card and in the 08:00 email; the `signedIn` fold for each state.

## 7. The settings, the spend problem, and the commands

- `tenant_settings` keys `mail_desk_route_threshold`, `mail_desk_spam_threshold` (numbers from 0.5 to 1.0) and
  `mail_desk_spend_limit_usd` (from 0 to 100), defaults as in section 1. No row means the default.
- `GET /gateway/mail-desk/settings` answers `{ routeThreshold, spamThreshold, spendLimitUsd, spentUsd, stopped, label }`,
  every value folded on the Gateway; `POST /gateway/mail-desk/settings` with `{ name, value, why }`. Who may change
  them: **a person, or a session in no factory** (the 6c rule, as for `mode`); a factory's session is refused 403. Every
  change is a `mail_desk_list_changes` row, kind `setting`, `detail` `{"from":..,"to":..}`.
- `maildesk status` gains a `spend` block (the same values) and `today` stays as phase 1 built it.
- Commands (AXI): `maildesk settings [--json]`, `maildesk set route-threshold|spam-threshold|spend-limit <value> --why
  "<text>"`. An unknown name, a value out of range or no `--why` fails before any request.
- **The spend problem:** once the limit stops a call, a Mail Desk problem "Mail Desk stopped asking the model: the
  spend limit of $5.00 is reached ($5.0003 spent). Raise it with maildesk set spend-limit <dollars> --why ..." is open
  on the `mail-desk` factory until the limit is raised above what was spent.

## 8. The decision rows

- Written with phase 1's insert, one per (channel, message key), and updated in place only from `failed`. Every value:
  channel, message key, received time, sender address, subject, layer, decision, route, reason, confidence, cost,
  mode, decided utc, and the rules version and attempt count (two columns this phase adds in its migration). Outcome
  stays null: nothing was done.
- **No body text, ever.** The preview is used for the model questions and dropped. A test screens an email whose
  preview holds a marker text and then searches every table and the log sink for the marker: none.
- The sender address and subject are stored, as the mission says, and are never logged.

## 9. The 08:00 email's Mail Desk section

Added to the morning email (`FactoryEmailComposer.ComposeMorning`) when the account's switch is on and the account has
a channel. It covers the same 24 hours as the email, folded on the Gateway (a `MailDeskEmailSection` input; the
composer only lays it out):

1. **Counts by decision**, every decision listed, zeros included, per mailbox and in total.
2. **Every junked email**, by mailbox, sender and subject, and the layer and reason - every one, never a sample, so a
   wrongly junked email is seen.
3. **Kept for the owner**: every one, by sender and subject.
4. **Could not be screened**: every email whose decision is `failed` (the spend limit, or a failure still being
   retried), and every email kept for the owner after 3 failed screenings, with the reason.
5. **List changes**: every `mail_desk_list_changes` row in the window - what, who (`whoInWords`) and why.
6. **The spend**: spent and the limit, and the spend sentence when it stopped.
7. A line saying the channels are in observe mode and nothing was moved, when every channel is in observe mode.

Returned emails are phase 4's and get their place then. Mailbox problems are shown by the existing problem cards. Every
sender and subject is escaped (`E()`). With the switch off, or no channel, the section is absent and the email reads
exactly as today (a test compares it).

## 10. The template's offer as a draft

Phase 2's `MailDeskTemplateOffer.Offer(tenant)` gets its two callers: when `POST /gateway/admin/mail-desk` turns the
switch **on**, and at Gateway start for **every account whose switch is on**, so a newer template after a deploy is
offered. Never when it is turned off. **Tests:** switch on offers a draft; on again offers nothing new; a Gateway start
offers for an on account and not for an off one; a newer template version is offered at start.

## 11. Who builds what

| Task | Developer session | Starts | Depends on |
|---|---|---|---|
| A. `screen.py`: one mailbox failing does not stop the others (6.1) | Mail Desk - Developer - screen.py per-mailbox failure | now, from origin/main | phase 2 (merged) |
| B. `MailDeskModel`, the model call (5) | Mail Desk - Developer - model call | now, from origin/main | nothing |
| C. Phase 5, backup machines (`docs/qa/mail-desk/phase-5/CONTRACT.md`) | Mail Desk - Developer - backup machines | now, from origin/main | nothing |
| D. The layers and the settings (3, 7) | Mail Desk - Developer - screening layers and settings | after phase 1 merges | phase 1 |
| E. Mailbox problems and the 08:00 section (6.2, 9) | Mail Desk - Developer - mailbox problems and 08:00 section | after phase 1 merges | phase 1 |
| F. The hand-off, the run, the memory, the offer (1, 2, 4, 8, 10) | Mail Desk - Developer - screening hand-off | after B, D and E are in the phase branch | B, D, E |

Each Developer works in its own worktree cut from origin/main (rule 0), on its own branch, and opens its pull request
**into the phase branch `mail-desk-phase-3`**. The Tech Lead rebases the phase branch onto origin/main when phase 1
merges. A Developer's tests are the proof it owes; each Developer's code is reviewed by a separate Reviewer session of
another agent family, in the order of repository rule 5a, opened by the Tech Lead, and every finding is answered by
the Developer in `docs/qa/mail-desk/phase-3/reviews/`.

## 12. Example data

The repository is public. Tests and examples use only `email@devthrottle.com` and the reserved example domains
(`example.com`, `example.org`, `example.net`, `*.example`). Never the owner's second mailbox address, never a real
sender's address or domain. No real email's sender, subject or text is committed.
