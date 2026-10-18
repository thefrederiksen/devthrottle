# Mission document - Mail Desk: one front door that screens every incoming email and routes it

Date: 18 October 2026. Architect: session 116 ("Mail Desk - Architect - screening front door for every channel"),
on devlinux. Owner: soren@centerconsulting.com. Written from origin/main at 880a68323 (devthrottle) and origin/main
of devthrottle_internal. The owner's design conversation is summarised in the dev report
`https://gateway.devthrottle.com/r/ec5f7c7e-f37e-4899-8b3f-d0c4176a4464` (versions 1 to 3); everything decided there
is written into section 4 below, because the conversation itself will be thrown away.

---

## 1. The mission

Build the **Mail Desk**: a check with no agent that looks at the email@devthrottle.com inbox every 5 minutes, screens
each new email through four layers (fixed rules, an allow-list, a block list with an unknown-sender screen, and a
small model on DeepInfra), records what it decided and why, and - once the owner approves - files the email and starts
the right factory's seat for it. Deliver it to the hosted Gateway running in **observe mode** (decides and records,
touches nothing), with a QA report that shows it working on the real mailbox, including its decisions on emails
already in the inbox, for the owner to review before any email is sorted.

## 2. The why

- **An agent session is the expensive way to look at an empty inbox.** Today a Mail Desk seat is started on a timer
  (08:00, 12:30, 16:30) to read the mailbox with a full model, whether or not anything came in. The owner: "running
  Python code is free and it shouldn't be that hard to check and see if there's any new emails ... And if there is a
  new email, then we start the agent session."
- **Timers are slow.** "We could check for emails often ... and then we could run whenever the email comes in instead
  of only doing it at a certain time in the morning." Five minutes instead of up to eight hours.
- **Most email does not need an agent at all.** Notifications should be archived, spam should go to Junk, and mail
  for a known purpose (the citizenship application) should go straight to the factory that handles it. Code and a
  small model can make those calls for a fraction of a cent.
- **It is the start of every channel.** The owner: "eventually we will have multiple channels in and out to the
  agents. We'll put this screening in front. And for now we'll call it Mail Desk." Email is first; Teams and Slack
  follow on the same front door. This mission builds the email half and must not paint the design into an
  email-only corner.

## 3. The goal

All of these are true at the end, and the Delivery Lead's final QA report shows each one:

1. The Mail Desk is merged to main and **deployed to the hosted Gateway**, switched on for the owner's account only.
   Every other account sees no change: the switch is off by default.
2. The screening check runs **every 5 minutes** on a Director (devlinux, see question 2) against
   **email@devthrottle.com** and **the owner's second Outlook address** (D19), each signed in through cc-outlook's sign-in,
   and the run history shows it.
3. In **observe mode** it records, for every new email, the decision it would take (which layer decided, the route or
   the filing, and the reason) - and **changes nothing in the mailbox and starts no session**.
4. The **existing inbox** has been screened: the QA report lists the decision for each of the most recent emails
   already in the inbox (question 5: 200 or the last 30 days, whichever is fewer), grouped by layer and decision, with
   the reasons, for the owner to read and judge.
5. **Live mode** - filing to Archive or Junk, starting seats in any factory, the record of each routed email, and
   "not for me" sending it back - is built, merged and deployed, **proven only on a throwaway Gateway with a test
   Director and a fake mailbox**, and **switched off in production** until the owner approves.
6. The **allow-list and block list** exist on the Gateway, can be changed by the owner and by agent sessions, and
   every change is recorded with who made it and why.
7. The QA report covers the **failure cases** as well as the flow (section 7).
8. The **Mail Desk page** in the Cockpit and the mobile app lists every supported address with its provider, mode,
   machine, signed-in state, last check and today's counts (D21).

## 4. Decisions

Every answer the owner gave, in his words where he gave words. Dictated text is quoted as spoken.

### What the feature is

- **D1. A Python check decides; an agent starts only when there is work.** "What we really need is the factory have
  the ability to check the email without using a large language model session ... running Python code is free ... keep
  the memory of when we ran last and just keep checking. And if there is a new email, then we start the agent
  session."
- **D2. It runs on a Director, which may have a backup.** "I guess running from a director is probably the better
  approach. Might have the ability to run from different directors. If one is down, we could have a backup. Because I
  guess it shouldn't really matter where we check the email. on which machine."
- **D3. It is built into DevThrottle, for every factory, and is the front door for every channel.** "I think mail desk
  is actually should be a built-in thing for the factories. because we can have multiple emails ... We'll have
  multiple input channels ... We can look at Open Claw because I should also be able to connect teams ... We'll put
  this Screening in front. And for now we'll call it Mail Desk. We'll do one for... email and then we'll do one for
  other communications like Teams and Slack."
- **D4. Simple code rules first, a small model after.** "If I've never emailed this particular email or it's not on
  my white list of emails as notifications, something I get often. then we just add it to the spam folder. So I think
  we could do some very, very simple things with code, and then use a small model to do classification after that."
- **D5. Fixed rules that always run, and the receiving factory may send an email back.** "We could also create simple
  rules that's always run. for instance in the citizenship email, if it says certain things in the subject like
  citizen and the from email, the two emails we're currently sending to, now automatically just pass it on. to the
  factory that should handle it. And it's then up to the factory if it is the wrong email, it will respond. to the
  factory mail desk basically and saying this was not for me ... it should Respond back to the mail desk with
  information about the email so it could be rerouted."
- **D6. The allow-list says what each sender is and what to do with it.** "We also need a whitelist because I do get
  emails in from things I've never emailed to because of notification emails. But if we put it on a whitelist, we
  should say what they are because I get a lot of notification emails where some of them I should just archive and
  some of them I want to read."
- **D7. Agent sessions may change the lists.** "the white list and the black list shoot me able to be manipulated by
  the agent session as well." Answered in the report: **"Freely, every change recorded and shown to you"**
  (`free-recorded`).

### Answers in the dev report (tapped)

- **D8. Where the check lives:** "The intended way: a check in the DevThrottle factory's definition"
  (`definition-check`). Refined by D3: the Mail Desk is its own built-in factory, not a seat inside the DevThrottle
  factory - see design 5.2.
- **D9. What counts:** "Any new email in the Inbox" (`any-new-inbox`).
- **D10. How often:** "Every 5 minutes" (`5m`).
- **D11. Unknown senders:** "The small model screens it first" (`model-screens`) - a real person goes on to routing,
  spam goes to Junk. This softens D4 so a new customer's first email is not junked unseen.
- **D12. Who files:** "The screening check files it, with no agent" (`check-files`) - Archive or Junk only, never
  delete, enforced in code and tests, every move recorded.
- **D13. Sign-in:** "A one-time sign-in through one DevThrottle app" (`one-time-sign-in`), with the owner's comment:
  **"Can't we use cc-outlook tool for this or at least use same Azure app"**. Settled: the mailbox is signed in with
  cc-outlook, and the check reuses cc-outlook's saved sign-in and Azure app (design 5.4).

### Models

- **D14. No new accounts; DeepInfra.** On Jev (TypeSafe's classifier): "I don't have a type safe account and I don't
  want to start another account right now. How would we do this on Deep Infra?" Jev is not on DeepInfra and TypeSafe
  paused new sign-ups, so Jev is out of this mission.

### This mission's scope and grants (law 18)

- **D15. The ask.** "Can you implement the full mission report for this new feature so we can give it to an
  implementation lead? that can implement all of it. and come out at the end with a full QA report after it's been
  deployed to... the gateway ... to a full QA report showing that this email will work. and also do some simple
  classifications on some of the existing emails that are already in the inbox."
- **D16. GRANT - deploy to production, the hosted Gateway only.** "which you're allowed to do because this feature
  shouldn't affect anybody. So you can deploy it to the gateway". The deploy is by the `deploy-hosted-gateway` skill
  and no other way, as often as the mission needs. It holds only while the feature is off for every other account
  (goal 1) - a change that affects any other account is outside this grant.
- **D17. NOT GRANTED - sorting email.** "but I would like to review it and approve it before we actually start
  sorting any email." Until the owner approves in his own words, in production: no email is moved, flagged,
  categorised, marked read or deleted; no session is started by an email; live mode stays off. Reading the mailbox and
  classifying is allowed (D15).
- **Also not granted, so the design must not need them:** a desktop (Director) release; a deploy of the website proxy
  in devthrottle_internal; sending any email from email@devthrottle.com; any change in Azure or Microsoft 365.

### Approval

- **D18. Approved, 18 October 2026.** The owner, in the dev report: "Approve, with the five recommendations, and
  start" (`approve-and-start`). So every question in section 10 is answered with its recommendation: a $5 spend limit
  for model calls; devlinux runs the check, SOREN_NORTH is the backup; the citizenship rule is data the owner supplies
  when he likes; `devthrottle/wingman-fast` is the model; the most recent 200 emails or the last 30 days, whichever is
  fewer, are screened.

### Added after approval (18 October 2026, the owner, in the Delivery Lead's session)

- **D19. A second mailbox: the owner's second Outlook address.** On being told that several addresses are supported, each
  signed in with cc-outlook: "I would like you to also add that into the mail desk." So the
  owner's **second Outlook address** is a second channel, in observe mode, checked on devlinux (its cc-outlook sign-in
  already exists there). The address is named in the Gateway's channel row and the owner's own QA report, never here:
  this repository is public. The owner was told first that screening sends each email's sender, subject and first 200
  characters to the model on DeepInfra, and that this mailbox holds another organisation's mail. No sender, subject or
  body text from it is ever committed to a repository.
- **D20. Gmail: the next mission, not this one.** The owner: "we'll have to support Gmail as well", then chose
  "Next mission" (recommended). So every channel carries a **provider** (`outlook` now, `gmail` next) and nothing in
  this mission may assume Outlook beyond the reader in `checks/screen.py`; Gmail is a new reader on the existing
  cc-gmail sign-in, not a redesign.
- **D21. A Mail Desk page showing the supported addresses.** The owner: "We really need a place also on the mail desk
  where we show the supported email addresses", then chose "Cockpit and phone page" (recommended). A Mail Desk page in
  the Cockpit and the mobile app lists each channel - address, provider, mode, machine, signed-in state, last check,
  today's counts by decision. Every value on it is folded on the Gateway and rendered verbatim (repository rule 7); the
  page is shared client code mounted by both shells, like Settings (rule 8). It ships in the Gateway image, so it is
  inside D16's deploy grant, and it is hidden when the account's switch is off.

- **D22. GRANT - the Delivery Lead publishes and adds the mailboxes.** Asked whether the Delivery Lead may publish
  the `mail-desk` factory and add both mailboxes in observe mode, the owner: "You can publish when you have something
  ready that we can test out." So in phase 6 the Delivery Lead - a session in no factory, the 6c rule - publishes the
  account's `mail-desk` factory and adds both channels (D19), each in **observe** mode on devlinux, once phases 1 to 5
  and 7 are merged and deployed. This grant does NOT cover `maildesk mode ... live`: live mode stays the owner's own
  decision (D17).

### Overrides of the method

None. The order of reviewers is the repository rules' section 5a (the owner's ruling of 28 September 2026), which is
newer than the method's version 1 text.

---

## 5. Design

### 5.1 The picture

```
 email@devthrottle.com                 Director (devlinux; backup later)               Gateway (owner's account only)
 ---------------------                 ------------------------------                  -------------------------------
                         every 5 min   checks/screen.py  (no model)                    MailDeskService
   Inbox  <--- read ---------------   - reads its memory (cursor, filing queue)  ---> - layer 1: fixed rules
   Sent   <--- read (who we wrote to)  - lists new Inbox mail since the cursor   JSON  - layer 2: allow-list (what + do)
   Archive/Junk <-- move (LIVE only)   - flags "we have written to this sender"        - layer 3: block list; unknown
                                       - carries out the filing queue (LIVE only)        sender -> small model: person or spam?
                                       - prints {count, context}; rewrites memory      - layer 4: small model: which route?
                                                                                       - records every decision (observe)
                                                                                       - LIVE: queues filing into the check's
                                                                                         memory, starts seats in any factory,
                                                                                         keeps a record per routed email
```

The check is **thin**: it reads mail and carries out filing it is told to do. **Every decision is the Gateway's**
(repository rule 7: the Gateway owns all ruling): the rules and lists live there, the model is called from there, and the decision is recorded
there. That keeps one ruler, lets agent sessions edit the lists through the Gateway, and keeps the whole feature a
**Gateway change** - no Director release (D17).

### 5.2 What exists, and what this mission builds on (all on origin/main)

| Exists | Where | What it gives us |
|---|---|---|
| Triggers: a check with no model on an interval; a session only when it counts work; never two at once; claims; RED status | `src/CcDirector.Gateway/Factory/Triggers/TriggerService.cs`; proof in `docs/missions/website-factory-agents-2026-09-21/proof/trigger/README.md` | Live in production: the Website factory's `website-new-mail` trigger checks every 5 minutes. |
| Checks inside a factory definition, run by the Director from a published version, outside every checkout | `src/CcDirector.ControlApi/Triggers/DefinitionCheckRunner.cs`; mission `docs/missions/factory-definition-2026-10-14/MISSION.md` section 5.5a; QA `docs/qa/factory-definition/phase-5b/README.md` | The check's memory (`CC_CHECK_STATE_FILE`, kept on the Gateway in `factory_check_state`), secrets, `{context}` in the prompt and `check.json` in the kit. In Directors **2.22.0 and later** (devlinux runs 2.22.0). |
| Memory kept only when the work was taken | `TriggerService.KeepState` | Saved on nothing-to-do and on a session that started; NOT on skipped-running, paused, failed, or a start that failed - so counted work is seen again. |
| Failed checks as problems | pull requests 4048, 4062 | The Open problems card and the 08:00 factory email. |
| A mail check against Microsoft Graph for email@devthrottle.com | `docs/qa/factory-definition/phase-0/inventory/email-check/check_new_mail.py` (+ tests, fake Graph in the phase 5b rig) | A starting point: paging, the 500-message cap, state per folder, the fake Graph. Written for outreach replies with an app-only sign-in; this mission changes both. |
| cc-outlook's sign-in | `tools/cc-outlook/src/auth.py` | Device-code sign-in, authority `common`, the owner's Azure app (client id `f575e3f4...`), delegated scopes including `Mail.ReadWrite`; the saved sign-in (an MSAL token cache) at `CcStorage.tool_config('outlook')/tokens/<name>_msal.json`, where `<name>` is the address with `@` and `.` turned into `_` (`get_msal_token_path`). **Phase 0 finding (corrects the first version of this row, which said the path is resolved from the user's own folder, never from a `CC_` variable):** `tool_config` FOLLOWS `CC_DIRECTOR_ROOT`, and a Director gives its sessions `CC_DIRECTOR_ROOT=<Director base>/instances/<instance>`, so a sign-in made in a Director terminal lands in that instance's folder (on devlinux: `~/.local/share/cc-director/instances/default/config/outlook/tokens/email_devthrottle_com_msal.json`). See 5.4. |
| Hosted model calls from the Gateway, attributed and costed | `src/CcDirector.Gateway/Transcription/HostedCandidateJudge.cs`, `src/CcDirector.Gateway/Wingman/HostedInferenceBrain.cs`, `IncludedModelId` | A Gateway feature may call only an **included** model name, so an internal feature never bills a member's credits. Today: `devthrottle/wingman-fast` = `Qwen/Qwen2.5-72B-Instruct` on DeepInfra ($0.36 per million input tokens, $0.40 output; `devthrottle_internal/website/api/_lib/inference-providers.js`). |
| Factory definitions published by a person or a session in no factory | Factory Definition phase 6c (#4103) | How the built-in Mail Desk factory gets published for the account. |

### 5.3 The built-in Mail Desk factory

- The Mail Desk is a **factory DevThrottle ships**: its definition template lives in this repository
  (`src/CcDirector.Gateway/MailDesk/Template/`, compiled into the Gateway like the built-in skills). It holds:
  - `checks/screen.py` - the screening check (5.5).
  - `seats/mail-desk.yaml` - one seat, **Mail Desk**, no repository, the agent that gets what the screening could not
    place: emails the model was unsure about and emails sent back (5.8). One start: `every: 5m`,
    `check: checks/screen.py`, `preventOverlap: true`, on the machine chosen in question 2.
  - a brief for the Mail Desk agent: how to route by hand, how to answer "not for me", how to use the list commands.
- When the account's Mail Desk switch is turned on, the Gateway offers the template as a **draft** of the account's
  `mail-desk` factory; the owner publishes it (himself, or through a session in no factory that he tells to - the 6c
  rule). Nothing runs before that publish. A later template version is offered as a new draft, never applied silently.
- The channel (which mailbox, which machine, which mode) is Mail Desk data on the Gateway (5.6), not a definition
  file, so adding a mailbox is a command, not a publish. Design it as **one row per channel** with a `kind`
  (`email` now; `teams`, `slack` later) so the second channel kind is an addition, not a rewrite (D3).

### 5.4 Signing in to the mailbox (D13)

- The owner signs email@devthrottle.com in **once per machine** that runs the check:
  `cc-outlook -a devthrottle auth` (the account is already registered in cc-outlook as nickname `devthrottle`,
  `email@devthrottle.com`, "not authenticated" on devlinux today). The Delivery Lead opens a terminal for him with the
  `user-input-terminal` skill; never in a session's own terminal.
- `screen.py` reads that saved sign-in with MSAL (declared as a package in its PEP 723 header, run with
  `uv run --script`), using **the same client id and authority**, and asks only for
  `https://graph.microsoft.com/Mail.ReadWrite` - never `Mail.Send`, even though the cc-outlook sign-in carries it.
- MSAL renews the sign-in on use and the renewed cache must be written back to the same file, exactly as cc-outlook
  does (owner-only permissions on Linux and macOS). The sign-in **never** goes into the check's memory: that is kept
  on the Gateway.
- A missing or expired sign-in is a **failed check** whose reason names the fix ("email@devthrottle.com is not signed
  in on devlinux: run cc-outlook -a devthrottle auth"), so it reaches the Open problems card and the 08:00 email.
- Phase 0 proves the premise before anything is built on it (7.2, P0-a).

**Phase 0 findings (18 October 2026; evidence in `docs/qa/mail-desk/phase-0/README.md` and `a-sign-in/`). These
correct the bullets above, and where they disagree, these win:**

- **Where screen.py finds the sign-in.** A check runs with every `CC_` variable stripped, so it cannot use
  `CC_DIRECTOR_ROOT`, and cc-outlook's own fallback without that variable (on Linux `~/.cc-director`) holds no
  tokens. screen.py looks ONLY under the Director's per-user base folder - Linux `~/.local/share/cc-director`, Windows
  `%LOCALAPPDATA%\cc-director`, macOS `~/Library/Application Support/cc-director` - at
  `instances/*/config/outlook/tokens/<name>_msal.json`, with `<name>` made by cc-outlook's own rule
  (`get_msal_token_path` in `tools/cc-outlook/src/auth.py`: `@` and `.` become `_`). On Linux and macOS the home
  folder comes from the operating system's account record, not from `HOME`. **None found: a failed check naming the
  fix. More than one found: a failed check naming every path found; it never picks one.** No fallback to
  `~/.cc-director`. Proven by the test Director run and by tests (`a-sign-in/tests`).
- **The token CAN send mail.** Asking for `Mail.ReadWrite` alone, even with a forced refresh, returns a token whose
  scopes include `Mail.Send` (and Calendars.ReadWrite, MailboxSettings.Read, User.Read): Microsoft's version 2
  endpoint puts every scope consented for Graph into the token. A narrower token would need a second Azure app, which
  this mission is not granted. So "screen.py never sends mail" rests ONLY on screen.py's own code: one request guard
  through which every Graph call goes, allowing only GET and `POST /me/messages/{id}/move` with `destinationId`
  `archive` or `junkemail`, and phase 2's tests proving nothing else can leave it.
- **uv is not on the live Director's PATH on devlinux.** The owner's Director (2.22.0) was started with a PATH
  without `~/.local/bin`, the only place uv is, so a check that declares packages fails there with "the check
  declares packages and uv is not on this machine". This must be fixed on the machine before phase 6 (the Delivery
  Lead decides how); it needs no product change.
- Renewal works as designed: the renewed cache was written back to the same file, mode 0600, and read again silently
  by the next run, and cc-outlook still saw the account as signed in.

### 5.5 The screening check, `checks/screen.py`

It reads; in live mode it also files what the Gateway queued. It never decides.

1. **Memory** (`CC_CHECK_STATE_FILE`): the Inbox cursor (newest receive time read, plus ids read at exactly that
   time - the phase 0 pattern), a cache of addresses we have written to (from the Sent folder, refreshed once a day),
   a short-key table (below), and the **filing queue** the Gateway wrote (5.7). First run: look back per question 5.
2. **Filing first** (live mode only, and only when the queue is non-empty): for each queued item, move the message to
   `archive` or `junkemail` and nothing else. The only mail-changing call allowed is
   `POST /me/messages/{id}/move` with `destinationId` in {`archive`, `junkemail`}; the code refuses anything else and a
   test proves no other method or destination can be sent. Each result (moved, already gone, refused) goes into
   the report.
3. **New mail**: Inbox messages received after the cursor, oldest first: id, received time, sender address and name,
   subject, `hasAttachments`, the first **200 characters** of the body preview, and `knownSender` (we have written to
   them).
4. **Size budget.** The Director's output cap is 16 KB and `context` is at most 8 KB (mission 5.5a) - and the
   Director's cap cannot be raised without a desktop release. So messages are reported in **batches that fit**: a
   Graph message id is about 150 characters, so the check reports a short key (a hash) and keeps the key-to-id table
   in its memory. Expect roughly 20 messages per run; the check advances its cursor only past what it reported, and
   the next run continues. The Tech Lead of phase 2 measures the real number and records it.
5. **Output**: `{"count": <messages reported>, "context": "<JSON: the messages and the filing results>"}`. `count`
   is 0 only when there was nothing new and nothing filed.
6. A failure (sign-in, Graph, a page that will not parse) is a non-zero exit with the reason on standard error: a
   failed check, memory not kept, so the same messages are read again next time.

### 5.6 What the Gateway keeps

New tables (SQLite and PostgreSQL, the usual migration pair; tenant-scoped like every other table):

- **`mail_desk_channels`** - one row per channel: kind (`email`), provider (`outlook`; `gmail` next - D20), address,
  the machine or machines that check it, the account's `mail-desk` factory and its
  start, **mode** (`observe` | `live`, default `observe`), created and changed by whom.
- **`mail_desk_rules`** - the rules file, **versioned** (every save a new version, nothing overwritten): fixed rules,
  the allow-list, the block list and the routes, in one document per account (shape below).
- **`mail_desk_list_changes`** - every change to the rules: version before and after, what changed, **who** (person,
  or session id and name), and **why** (required text). This is D7's record.
- **`mail_desk_decisions`** - one row per screened email: channel, message key, received time, sender address,
  subject, the layer that decided, the decision (route to factory/seat, archive, junk, keep for the owner, Mail Desk
  agent), the reason (rule name, list entry, or the model's label and confidence), the model call's cost when there
  was one, the mode it was made in, and - in live mode - what was done (filed, seat started, failed). **No body text
  is stored.** The 200-character preview is used for the model call and then discarded.
- **`mail_desk_routed`** (live mode) - one row per email routed to a seat: decision id, factory, seat, session id,
  state (`open` -> `done` | `returned`), the return note, and how many times it has been returned.

The rules document (YAML or JSON; the Tech Lead decides and documents it):

```yaml
rules:                      # layer 1: always first, in order; first match wins
  - name: citizenship
    when: { subjectContains: ["citizen"], fromAny: ["<address 1>", "<address 2>"] }   # question 3
    route: { factory: citizenship, seat: <seat> }

allow:                      # layer 2: senders we never wrote to, but want (D6)
  - from: "*@github.com"
    what: "GitHub notifications"
    do: archive              # archive | keep-for-owner | { route: { factory, seat } }

block:                      # layer 3a
  - from: "*@spammy-domain.example"
    why: "cold sales, never answered"

routes:                     # layer 4: what the model may choose from; one line each
  - { factory: devthrottle, seat: mail-desk, handles: "customers and users of DevThrottle: support, billing, bugs" }
  - { factory: citizenship, seat: <seat>, handles: "the citizenship application" }
```

Matching is plain code: exact address or `*@domain`, case-insensitive; subject "contains" is case-insensitive.

### 5.7 The screening, on the Gateway (`MailDeskService`)

When the Mail Desk start's check reports, the Gateway hands the report to `MailDeskService` instead of the ordinary
"count above zero starts the seat" path, and records a new run outcome, **`screened`**, which keeps the check's
memory (like nothing-to-do). For each message, in order, the first layer that decides wins:

1. **Fixed rules** - first match: route.
2. **Allow-list** - first match: its `do`.
3. **Block list** - match: junk. **Otherwise, if the sender is unknown** (`knownSender` false and on no list): one
   model question, "a real person writing to us, or spam?" - spam: junk; a person: on to layer 4 (D11).
4. **Route** - one model question: which of `routes` (by its `handles` line), or `unsure`. Unsure, or a confidence
   under the threshold (the Tech Lead sets it from phase 0's sample and records it): the Mail Desk agent.

**The model** is called through the Gateway's hosted path with an **included** model name - `devthrottle/wingman-fast`
today, because a new cheaper name (for example `zai-org/GLM-4.7-Flash`, $0.06 per million input tokens) would need a
deploy of the website proxy, which is not granted (D17; question 4). Each call: the sender, subject and 200-character
preview, the list of labels, temperature 0, an answer constrained to one label from the list; anything else is
`unsure`. The model only picks a label; it never writes, and never decides to delete. Calls are tagged
`mail-desk` so their cost shows in the AI cost record, and the mission's spend stays under the limit (question 1).

**Observe mode** (the default, and the only mode in production in this mission): record the decision, do nothing
else. **Live mode** (built and proven on the throwaway Gateway only - D17):

- **Filing** decisions (archive, junk) are written into the start's stored memory as the filing queue, so the next
  run of the check carries them out (5.5 step 2) and reports the result; the decision row is updated with it.
- **Routes** start the seat in the target factory - any factory of the account, not only `mail-desk` - on the
  machine that ran the check, through the same `MachineSessionSpawner` path the trigger uses, one session per seat per
  run with all of that seat's emails in its prompt and in its kit's `check.json`, and a `mail_desk_routed` row per
  email. While that seat's previous Mail Desk session is alive, its emails wait (state stays `open`, queued), as a
  trigger's skipped-running does.
- **Keep for the owner** leaves the email in the Inbox and lists it in the 08:00 email.

**A routed email's work is never lost:** its `mail_desk_routed` row stays `open` until the seat marks it `done` or
returns it. An `open` row whose session has ended without either is re-routed once and then goes to the owner. This
closes the one gap left in the trigger path (a session that starts and then crashes).

### 5.8 Commands and "not for me" (D5, D7)

`cc-devthrottle maildesk` follows the AXI standard (`docs/axi-standard.md`): `--json` on every read, every filter
applies to `--json`, an unknown flag fails, no id cut short, an empty result says `count: 0`.

| Command | Who | What |
|---|---|---|
| `maildesk status` | anyone in the account | channels, mode, last check, today's counts by decision |
| `maildesk channel list`, `channel add <address> --provider outlook --machine <machine> --why <text>`, `channel remove <address> --why <text>` | list: anyone; add and remove: **person only**, or a session in no factory that the owner told to (the 6c rule) | the supported addresses (D19, D21). A new channel always starts in `observe`; removal keeps its decisions |
| `maildesk decisions [--since] [--layer] [--decision] [-n]` | anyone | the decision rows, newest first: what the owner reviews |
| `maildesk rules show [--version]`, `rules history` | anyone | the rules document and every change with who and why |
| `maildesk allow add <pattern> --what <text> --do <archive\|keep-for-owner\|route:factory/seat> --why <text>` | person or session | add to the allow-list (D6, D7) |
| `maildesk block add <pattern> --why <text>`, `allow remove`, `block remove` | person or session | the same for the block list; removal also needs `--why` |
| `maildesk rule add/remove ...`, `route add/remove ...` | person or session | fixed rules and routes |
| `maildesk done <routed id> [--note]` | the session the email was routed to | the work is done |
| `maildesk return <routed id> --note <what I know>` | the session the email was routed to | "not for me": back to the Mail Desk agent with the note; a second return of the same email goes to the owner (Open problems and the 08:00 email) |
| `maildesk mode <channel> observe\|live` | **person only**, or a session in no factory that the owner told to (the 6c rule); refused for a factory's own session | switch a channel's mode; recorded with who |

The 08:00 factory email gets a **Mail Desk section**: counts by decision, what went to Junk (every one, by sender and
subject, so a wrongly junked email is seen), what was kept for the owner, what was returned, and every list change
with who and why.

### 5.9 Backup machines (D2)

A start may name **several machines in order of preference**. The trigger claim already lapses after two intervals
plus a minute; today it hands over only between Directors on one machine. Extend the assignment so the next machine
on the list takes the claim when the first has lapsed, and hands it back when the first returns. The memory is on the
Gateway, so the check carries on where it was. Every machine on the list needs its own cc-outlook sign-in; a machine
without one fails its check with that reason. Gateway-only.

### 5.10 The switch

One account-level switch, **`mailDesk.enabled`**, off by default, read by every Mail Desk route and by the report
hand-off; with it off, the Mail Desk routes answer 404 and a Mail Desk start's report is refused, exactly like the
factory agents switch. It is turned on for the owner's account only (the Tech Lead of phase 1 finds where a
per-account switch belongs and records it). This is what keeps the deploy inside D16's grant.

### 5.11 In, and out

**In:** everything above, for email, for two Outlook mailboxes (email@devthrottle.com, and
the owner's second Outlook address - D19) end to end, with more mailboxes being another channel row and another sign-in;
the Mail Desk page in the Cockpit and the mobile app (D21).

**Out (later missions):** Teams, Slack and other channels, and the study of how OpenClaw and others connect channels
(the owner named OpenClaw; that study comes back to him as its own report before any channel is built); editing
the lists from the Mail Desk page (it lists; the commands change); a dedicated cheaper model name (needs a
website proxy deploy); Gmail (the next mission - D20); sending email; switching any channel to live mode in production (the owner's approval,
D17).

---

## 6. Phases

The Delivery Lead is opened top-level, answering to the owner. Each phase that writes code has a Tech Lead; each
Developer's code and each finished phase is reviewed by a different agent family (the order in repository rule 5a).

| Phase | What | Tech Lead | Runs alongside |
|---|---|---|---|
| **0. Proofs** (about a day, no product code) | **(a)** The owner signs email@devthrottle.com in with cc-outlook on devlinux. A throwaway definition check, run by devlinux's real Director (2.22.0) from a published version on a **throwaway** Gateway, finds that saved sign-in with every `CC_` variable stripped, gets a `Mail.ReadWrite`-only token silently, and the renewed cache is written back. If this fails, stop and report to the owner: 5.4 depends on it. **(b)** Read-only against the real mailbox, by hand: Inbox and Sent paging, message size, the real number of messages per 8 KB `context`. **(c)** The model on a sample: 50 recent Inbox emails (sender, subject, 200 characters) through `devthrottle/wingman-fast` with both questions, results and cost written up; the confidence threshold proposed from it. **(d)** Where an account-level switch belongs. Proofs are committed scripts and output under `docs/qa/mail-desk/phase-0/`, with no email address other than ours and no body text in the committed output. | Yes | - |
| **1. The Gateway store and commands** | 5.6 tables (both databases), the rules document and its validation, `maildesk` commands for status, channels (D19), rules, lists, decisions and mode, the change record, the switch (5.10). Unit tests for every command, including refusals: a factory session switching mode, a change without `--why`, an unknown flag. | Yes | 2 |
| **2. The screening check** | `checks/screen.py` and its tests against the fake Graph (extend the phase 5b rig's), the template factory (5.3) and its offer as a draft. Tests prove: no call other than GET and the two moves; no move in observe mode even with a filing queue present; batches fit the budget; the cursor never advances past what was reported; a failed sign-in is a failed check naming the fix. | Yes | 1 |
| **3. Screening on the Gateway, observe mode** | `MailDeskService`, the `screened` outcome, the four layers, the model call with its tag and the spend limit, decision rows, the 08:00 section. Tests for each layer, the order, `unsure`, a model failure (recorded as a failed screening for that email, retried next run, never guessed), and observe mode starting nothing. | Yes | 5 |
| **4. Live mode** | The filing queue, starting seats in any factory, `mail_desk_routed`, `done`, `return`, the second return to the owner, re-routing an orphaned `open` row. **Proven only on a throwaway Gateway with a slot 5+ test Director (repository rule 0b) and the fake Graph.** Production stays in observe. | Yes | 5 |
| **5. Backup machines** | 5.9, with tests for handover and hand-back. | Developer under the phase 3 Tech Lead | 3, 4 |
| **6. Deploy and production QA** | Merge; deploy with `deploy-hosted-gateway`; turn the switch on for the owner's account; the owner publishes the `mail-desk` factory; the channel in observe mode on devlinux; screen the existing Inbox (question 5) by letting the check catch up from its first-run look-back (or a one-off 1-minute interval until caught up, then back to 5 minutes); both channels (D19); the QA report (section 7). | No - the Delivery Lead | - |
| **7. The Mail Desk page** (D21) | One page in shared client code, mounted by the Cockpit and the mobile app; a Gateway route returning the folded channel rows; hidden with the switch off. Proof: the client tests, and a QA report of screenshots at desktop and phone width (the `web-qa` skill) with the switch on and off. Complies with `docs/VisualStyle.md`. | Yes | 3, 4 (after 1) |

Merges happen at the end of each phase, behind the switch.

## 7. The check

### 7.1 What counts as proven

- **Code decides first.** `.\scripts\test-local.ps1` green, and - because this touches the Gateway - the parked
  Gateway suites: `.\scripts\test-local.ps1 -Parked` (repository rule 5a; on devlinux mind the .NET 10
  SDK at `~/.dotnet` and `DOTNET_USE_POLLING_FILE_WATCHER=1`). New PostgreSQL migrations are also proved with
  `.\scripts\test-database.ps1`. The check's tests: `uv run --with pytest pytest docs/qa/mail-desk/` or wherever phase
  2 puts them, recorded in the phase's README. The `cc-devthrottle` tests for the new commands run on the platforms the
  tool's tests already run on.
- **The rig.** One script, `docs/qa/mail-desk/rig/run_qa.py`, in the shape of `docs/qa/factory-definition/phase-5b/rig/run_qa.py`:
  a throwaway Gateway, a test Director, the fake Graph, and every case below, writing `transcript.txt`. An agent runs
  it by itself; its transcript is committed.
- **Production, observe mode only**, after the deploy: `cc-devthrottle maildesk status --json`,
  `maildesk decisions --json`, `trigger runs` for the Mail Desk start, and the Gateway's run rows - read, saved and
  quoted in the QA report.

### 7.2 The cases the QA report must show

**The flow (production, observe mode, real mailbox):**
- the check running every 5 minutes, with its run rows;
- the existing Inbox screened: a table of every decision (time, sender, subject, layer, decision, reason, cost),
  grouped by decision, with the totals and the total model cost;
- a fresh email arriving and being decided within one interval (the owner sends one from another address when the
  Delivery Lead asks - **the mission sends no email itself**);
- **nothing changed in the mailbox**: the Inbox message count and ids before and after the whole QA, compared;
  no session started by the Mail Desk start (its run rows show `screened`, never `started`);
- a list change by an agent session, then seen in `rules history` with the session and the reason, and in the 08:00
  section.

**Failure cases (rig, unless marked):**
- not signed in / sign-in expired: a failed check naming the fix, on the Open problems card (rig; also once in
  production by pointing the channel at a machine with no sign-in, if the Delivery Lead can do it without touching
  the owner's other machines - otherwise rig only, said so);
- Graph unreachable or answering an error: failed check, memory not kept, the same messages read next run;
- the model failing or answering outside the labels: `unsure`/failed screening for that email, never a guess;
- more mail than one batch: several runs, nothing skipped, nothing reported twice;
- observe mode with a filing queue present: no move made (the fake Graph's call log);
- live mode (rig): archive and junk moves only; a route starts the right seat in another factory with the emails in
  its prompt and `check.json`; `done`; `return` re-routes to the Mail Desk agent; a second return reaches the owner;
  a session that ends without either is re-routed once; two routes in one run start two seats; a seat already busy
  queues its email;
- a factory session trying `maildesk mode ... live`: refused;
- the switch off: routes 404, a Mail Desk report refused, nothing recorded;
- backup machine: the first machine goes quiet, the second takes the claim, the cursor continues, the first takes
  it back.

The QA report is one dev report for the owner (`cc-dev-reports open`), with the production evidence and the rig
transcript linked, stating plainly what was not proven.

## 8. Merge plan

Merge at the end of each phase, squash, after the local gate and a review (repository rule 5a). This is safe because:
- the account switch is off by default, so merged code is dormant for every account until phase 6 turns it on for
  the owner's;
- the template factory does nothing until the owner publishes it;
- observe mode is the default for a channel, and live mode is a separate, recorded switch;
- no Director change is needed, so no desktop release is involved.

A deploy of the Gateway between phases is allowed (D16) but not needed before phase 6.

## 9. Where it ends

**In production, in observe mode.** Merged to main; deployed to the hosted Gateway (D16); the switch on for the
owner's account only; the `mail-desk` factory published by the owner; email@devthrottle.com and
the owner's second Outlook address screened every 5 minutes on devlinux in observe mode; each existing Inbox screened; the
Mail Desk page listing both; the QA report published to the owner. **Live mode is built,
proven on the rig and switched off.** Turning it on is the owner's decision after he has read the decisions - and it
is the next step, not part of this mission.

The Delivery Lead lands the record: this document with its answered questions and every review finding's answer,
the phase READMEs, the rig and its transcript, and the QA report.

## 10. Questions

All five are answered: the owner approved every recommendation (D18).

1. **Spend limit for model calls in this mission.** Recommendation: **$5**. At about $0.0003 per email on
   `wingman-fast`, that is over 15,000 emails, far more than the backfill and QA need; reaching it means something is
   wrong, and the screening stops calling the model and says so.
2. **Which machine runs the check.** Recommendation: **devlinux**, with SOREN_NORTH as the backup once phase 5 is in.
   devlinux stays on; SOREN_NORTH and the Mac mini were both offline this morning.
3. **The citizenship rule.** Which two sender addresses, and which factory and seat handle it? Recommendation: the
   rule is data, so the mission does not wait for it - the owner gives the addresses and seat whenever he likes
   (or a session adds them with `maildesk rule add`), and the QA report shows the rule matching on the rig with
   stand-in addresses.
4. **The model.** Recommendation: **`devthrottle/wingman-fast` for this mission** (no new grant needed). A dedicated,
   cheaper `devthrottle/mail-screen` name pointing at a small model such as `zai-org/GLM-4.7-Flash` (one sixth of the
   input price) is a follow-up needing a website proxy deploy, which the owner grants separately - phase 0's sample
   tells whether it is worth it.
5. **How much of the existing Inbox to screen.** Recommendation: **the most recent 200 emails, or the last 30 days,
   whichever is fewer.** Enough to see every kind of mail, cheap, and a table the owner can read.
