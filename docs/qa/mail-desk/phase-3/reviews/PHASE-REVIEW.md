# Review of phases 3 and 5 as a whole: pull request 4270

Reviewed commit `87d723b74` on branch `mail-desk-phase-3`, against `docs/missions/mail-desk-2026-10-18/MISSION.md`
(sections 4, 5.5 to 5.10, 6 and 7) and `HANDOVER.md` (the eleven rulings in its section 4), both read from
origin/main `e00092bd2`. Date: 19 October 2026, on devlinux. The reviewer is a separate session of another agent family,
under ruling 9. It read and ran; it built nothing and fixed nothing.

## Verdict

**No findings.** Within the scope below, nothing in this phase changes behaviour for an account whose Mail Desk switch
is off, nothing in it starts a session or files a message from any path, and the model call does what the mission asks.
Merging it and deploying it harms no one I could find. Three notes follow that are not findings: none proves a harm, but
the first one decides whether phase 6's model layers will work, and it can be settled with one call.

## Scope

**Read, whole:** `TriggerService.cs`, `TriggerStore.cs`, `TriggerMachines.cs`, `TriggerEndpoints.cs` (the report and
assignment routes), `DueCheckService.cs`, `MailDeskService.cs`, `MailDeskScreening.cs`, `MailDeskModel.cs`,
`MailDeskModelFactory.cs`, `MailDeskMemory.cs`, `MailDeskTemplateOffer.cs`, `MailDeskDecisionStore.cs` (its diff), the
six migrations, the phase 3 and phase 5 READMEs and the phase 3 contract's sections 1, 2, 3, 7, 8 and 9.

**Read, diff only:** `GatewayHost.cs`, `SessionKeyGuard.cs`, `FactoryEmailSweep.cs`, `FactoryEmailComposer.cs`,
`CronRunResultService.cs`, `CronRunResultFold.cs`, `FactoryCheckEndpoints.cs`, `FactoryPublishApply.cs`,
`FactoryPublishPlan.cs`, `FactoryDefinitionChanges.cs`, `AdminMailDeskEndpoint.cs`, `MailDeskEndpoints.cs`,
`TenantSettingKeys.cs`, the contracts in `CcDirector.Gateway.Contracts`, and the `FileLog`, `AiCallTag` and
`IncludedModelPrice` changes in Core.

**Read outside this repository:** the website proxy's chat route and upstream builder in `devthrottle_internal`, at its
origin/main `391e7362` (`website/api/v1/chat.js`, `website/api/_lib/inference-providers.js`), to answer the log
probability question.

**Ran, on this commit:**

| Run | Result |
|---|---|
| `Gateway.UnitTests`, filter `Category=MailDesk`, `~MailDesk`, `~Trigger`, `~DueCheck`, `~BackupMachines` | 908 passed, 0 failed |
| `Gateway.UnitTests`, filter `~FactoryEmail`, `~CronRunResult`, `~Factory.Definition`, `~FactoryActivity`, `~FactoriesScreen`, `~SessionKeyGuard`, `~FactoryCheck`, `~Migration` | 2,036 passed, 0 failed |
| `screen.py`'s tests (`docs/qa/mail-desk/phase-2/tests`) | 292 passed |
| `cc-devthrottle` tests for `maildesk`, `trigger` and the help-and-errors step | 658 passed, 1 skipped |
| A search of every added line of the diff for email addresses, domains, keys and tokens | see point 4 |

**Not reached:** the parked suites as a whole and `-Parked` (the Tech Lead's gate table covers them; I did not
re-run it), `scripts/test-database.ps1` (no PostgreSQL run of my own - for PostgreSQL I read the migrations and rely on
the Tech Lead's 81 of 82, whose one failure also fails on main), Windows and macOS, a real Director, a real mailbox, the
real model, and the hosted Gateway. I made no call to the model, as the brief says.

## 1. With the switch off, nothing changes for another account (D16)

Each change to shared code, and why it is the same as before for a start that is not the Mail Desk check, or an account
that is off:

- **`TriggerService.ReportCheckAsync`.** The new branches run only when `MailDeskStart.Is(found)` is true (the
  `mail-desk` factory and `checks/screen.py`); the switch-off refusal from phase 1 comes before them. Two changes do
  reach every trigger, and both keep today's behaviour:
  - The route now passes the reporting Director's machine, and a report from a machine not on the trigger's list is
    refused (`machine_not_on_the_list`). The machine comes from the same `directorMachine` lookup the assignment route
    already used to decide which triggers a Director is handed, compared ignoring case as `ClaimForDirector` always did.
    So a Director is only ever refused a report for a trigger it would not have been handed - for example after the
    trigger moved machine, which used to be recorded and is now refused. That is not a harm.
  - The session starts on the reporting Director's machine (`PendingStart.StartMachine`) instead of the trigger's.
    With no backups these are the same machine (the claim proves it), and the request already names the one Director.
- **`TriggerStore.ClaimForDirector`.** With no backups every machine stands at place 0, so `HeldFromLaterMachine` is
  never true and a live claim is skipped exactly as before. A claim's machine that is empty or not on the list is never
  "later". The migration fills `ClaimedByMachine` from `Machine` for every held claim, so the first poll after the
  deploy sees what the code expects.
- **`TriggerStore.RenewClaim`.** Recording a result no longer takes a live claim back from another Director. In the
  report path the report is refused before this when another Director holds it, so nothing changes there. The only
  new case is a session start that outlasts the lapse while a second Director on the same machine took the claim;
  leaving it with the second Director is the better answer, not a harm.
- **`DueCheckService`.** The new refusal is under `MailDeskStart.Is(job)` only.
- **Publish.** `WriteDefinitionTrigger` released a claim when the machine changed; it now releases it when the holder's
  machine is off the list. With no backups and the holder's machine filled in by the migration, those are the same
  test. `AsStart`, `TriggerRow` and `SameStart` compare an empty backup list for every existing trigger, so no existing
  start is seen as changed by a republish.
- **`FactoryCheckEndpoints`.** The new condition adds seats only for a start that names backup machines.
- **The 08:00 email.** `MailDeskEmailFold.Section` answers null when the switch is off or there is no channel, and the
  composer is then unchanged byte for byte (the "nothing to send" rule included).
- **The problems list.** `MailDeskProblemSource.Facts` answers `MailDeskProblemFacts.None` when the switch is off, before
  any read.
- **The template offer at Gateway start.** Off the start path (`Task.Run`), wrapped, and only for accounts whose
  switch is on. The administrator route offers it only when it turns a switch on.
- **`SessionKeyGuard`.** Adds `settings` under the Mail Desk routes only; every Mail Desk route, `settings` included, is
  inside the gate that answers 404 for an account that is off.
- **New outcome words** (`screened` for a trigger run and for factory activity). Only the Mail Desk hand-off writes
  them. I found no switch over trigger run outcomes that throws on an unknown word and lacks `screened`.

## 2. Observe mode starts nothing and changes nothing

- `HandToMailDesk` is the only thing a Mail Desk report reaches once it is past the refusals, and it never calls the
  session starter or `BeginStart`; the count is recorded but never decides. The only other way into a start is
  `ReportCheckAsync`'s ordinary path, which the Mail Desk report never reaches, and the timed path, which
  `DueCheckService` now refuses. `ResumeAsync` releases a lock and starts nothing.
- No code in this phase writes a filing queue. `MailDeskMemory.Hand` copies `filingQueue` from the kept memory and
  `Merge` keeps what the check returns, but nothing on the Gateway ever adds an item, so the queue is always empty.
  The channel's `mode` in the memory is always the channel row's, never the check's.
- The decision rows record the mode and leave `Outcome` null.
- The check itself moves nothing outside live mode (`read_channel` files only when `mode == "live"`), and its 292
  tests hold that.

## 3. The model call

- **Included model only.** `MailDeskModelFactory.Create` builds the call on `IncludedModelId.WingmanFast`
  (`devthrottle/wingman-fast`) at the Wingman endpoint, after the account's plan gate for the Wingman scope. The price
  in `IncludedModelPrice` (0.36 and 0.40 dollars per million tokens) is the same as the proxy's catalog entry for the
  upstream model, `Qwen/Qwen2.5-72B-Instruct`.
- **Tag.** Every request carries `GatewayAiCallTags.For(tenant, AiFeature.MailDesk)`, the feature `mail-desk`.
- **The spend limit stops calls.** `MailDeskSpendLedger` starts from the sum of every decision row's cost and is charged
  after each answered question; `IsReached` is checked before each of the two questions, so at most one call crosses
  the limit. A row decided again after a failure keeps the cost of both attempts, so the sum stays true.
- **A failure is never a guess.** A failed call is `failed` and retried; an answer that is not exactly one listed letter
  goes to the Mail Desk agent; a spam or route answer with no confidence goes to the Mail Desk agent; nothing defaults
  to a route or to Junk.
- **Confidence comes only from log probabilities.** It is `exp(logprob)` of the reply's first token, set only when that
  token is the answered letter; otherwise null, and a null confidence never routes and never junks.

**Are log probabilities asked for and passed through on the hosted path? Yes, as far as our code goes.** The Gateway
sends `logprobs: true, top_logprobs: 5`, not streamed, to `https://devthrottle.com/api/v1/chat/completions`. The proxy's
`buildUpstreamRequest` copies the client's body unchanged except for the model name (`{ ...clientBody, model:
upstreamModel }`), and the non-streamed path answers `res.status(200).json(responseBody)` - the provider's reply,
unchanged. Nothing in `chat.js` or `inference-providers.js` strips a field; there is no mention of `logprobs` in the
website at all, which here means nothing touches it. So the "every model decision becomes the Mail Desk agent" outcome
can only come from DeepInfra itself, not from our path. That is **not a finding**: our code does what it should.

## Notes that are not findings

1. **What DeepInfra does with `logprobs` for this model is still unproven, and it matters more than the README says.**
   I am fairly sure, not certain, that DeepInfra returns log probabilities on its chat completions for
   `Qwen/Qwen2.5-72B-Instruct`. There are two ways it could go wrong, and they look different in phase 6:
   - It ignores the field: every answer has no confidence, so every spam or route answer goes to the Mail Desk agent
     (the README's case). Safe in observe mode; the model layers are then useless until fixed.
   - It rejects `top_logprobs` with a 400: every question fails `http-400`, each email is retried twice and kept for the
     owner on the third run, and **no model decision is made at all**. The proxy writes no usage row for an upstream
     4xx, so this costs nothing and shows only as `failed` and `keep-for-owner` rows with that error kind.

   **The one cheap check that settles both:** a single non-streamed call through the proxy with the included model and
   the same two parameters, on a made-up prompt (no email text), for example
   `{"model":"devthrottle/wingman-fast","messages":[{"role":"user","content":"Answer with one letter: A or B."}],"max_tokens":2,"temperature":0,"logprobs":true,"top_logprobs":5}`,
   then look for `choices[0].logprobs.content[0].logprob`. It costs about a hundredth of a cent. It is a call to the
   model, so it is the Implementation Lead's to make, not mine. Otherwise the first decision row in phase 6 shows the
   same: a `confidence` column that is never empty, or a run of `http-400` reasons.
2. **The spend limit can be raised without the owner.** `maildesk set spend-limit` is allowed to a person and to a
   session in no factory (the same rule as `channel add`), up to 100 dollars, and every change is recorded with who and
   why. The owner approved 5 dollars (D18, question 1); a top-level session such as the Implementation Lead can raise it.
   The contract chose this ("make it settable and record it"), and it is recorded, so it is not a harm - but it is the
   one place where the 5-dollar promise rests on a seat's restraint rather than on code.
3. **An email stopped by the spend limit is never screened again.** It is decided `failed`, its mailbox moves on, and
   raising the limit does not bring it back; it stays in "Could not be screened" in the 08:00 email. This is what the
   contract says (section 3), and in observe mode the email is untouched in the Inbox, so it is not a harm. It means
   the 08:00 list is the only record of those emails.

## 4. The rulings

- **Ruling 1** (one mailbox failing never stops the others): a failing section is recorded as its own check, the other
  sections are screened, and a model failure writes back only that mailbox's check fields (`writtenBack` holds only
  channels with a model failure). Held.
- **Ruling 2** (a model failure rereads only its mailbox; a reread is idempotent; the third failure is kept for the
  owner): `Find` skips any row that is not `failed`, so a reread makes no second call and no charge; one row per
  (account, channel, message key) is a unique index; `attempt >= 3` turns the decision into `keep-for-owner` and that
  mailbox is not written back. Held.
- **Ruling 5** (at most 7 KB and 10 messages per run): in `screen.py`, held by its tests; I did not re-derive the numbers.
- **Ruling 10** (the factory published only after this hand-off is deployed; the Gateway refuses the ordinary path):
  the interval path hands off and never starts; with no service wired it refuses `mail_desk_not_ready`; the timed path
  refuses `mail_desk_not_ready`. Held. Publishing still waits for phase 6, as the ruling says.
- **Ruling 7** (the public repository): the only real address in any added line is `email@devthrottle.com`. Every other
  address and pattern is under `example.com`, `example.org`, `example.net` or a `.example` domain, or is a placeholder
  (`*@domain`). The two key-shaped strings (`dt_live_mail_desk_factory_key`, `dt_live_mail_desk_secret_key`) are test
  constants, not keys. No second mailbox address, no real sender or domain from phase 0, no token, and no email text.
  Held.

## 5. Migrations

The three pairs agree column for column. SQLite and PostgreSQL types are the usual pair (`TEXT` against
`character varying(n)`, `uuid`, `timestamp with time zone`, `boolean`, `integer`); both trigger migrations default
`BackupMachinesJson` to `[]` and `ClaimedByMachine` to empty, and both fill `ClaimedByMachine` from `Machine` for every
held claim (the PostgreSQL one with the `gateway` schema and quoted names). The decision-attempts pair defaults
`AttemptCount` to 1 and `RulesVersion` to 0, which is true for every row written before it. Every `Down` drops exactly
what its `Up` added. As the brief says, the order against main's newer `AddSessionRuleChanges` is being regenerated by
the Tech Lead and is not reviewed here.
