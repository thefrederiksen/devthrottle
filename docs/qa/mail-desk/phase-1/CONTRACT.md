# Mail Desk, phase 1: the contract every Developer of this phase builds to

Mission: `docs/missions/mail-desk-2026-10-18/MISSION.md` (issue #4213), section 6 row 1, sections 5.6, 5.8 and 5.10.
Written by the phase 1 Tech Lead on 18 October 2026, before any code, so that the store, the routes and the command
can be built at the same time against one shape. Where this file and the mission document disagree, the mission
document wins and this file is corrected.

Everything here is behind the Mail Desk switch, which is off for every account by default.

## 1. Decisions this phase settles

| Point | Decision | Why |
|---|---|---|
| Where the switch lives (phase 0 (d)) | `tenant_settings` key `mail_desk_switch`, class `MailDeskSwitch`, route group `MailDeskGate` answering 404 with no body when off, administrator route `GET`/`POST /gateway/admin/mail-desk` behind `ADMIN_SERVICE_TOKEN`. **No machine-wide switch.** | Copies the factory agents switch exactly (phase 0 (d)); leaving out the machine switch means no `config.json` edit can turn a mailbox reader on for every account of the hosted Gateway. |
| How the Gateway knows a check report is a Mail Desk report | The reported trigger is a **definition trigger of the factory `mail-desk` whose check path is `checks/screen.py`** (`MailDeskStart.Is(trigger)`, reading phase 2's constants `MailDeskTemplate.Factory` and `MailDeskTemplate.CheckPath`, held once). | It holds whether or not a channel row exists, so a published Mail Desk factory with its channels removed still cannot fall back to "count above zero starts the seat". Phase 2's template defines the two names; phase 3 uses the same predicate for its hand-off. |
| The refusal when the switch is off | Inside the report handling of `POST /directors/{directorId}/triggers/{id}/checks`, before anything is recorded: **409**, body `{"error":"mail_desk_off","detail":"<sentence>"}`. No run row, no kept memory, no claim change, no start, no decision. | The factory gate in front of that route is ON for the owner's account, so it cannot be the refusal (phase 0 (d)). A 404 would read as "no such trigger". The Director treats every non-success the same way - it logs the HTTP status and body and runs nothing (`GatewayClient.ReportTriggerCheckAsync`) - so 409 needs no Director change. |
| The written command for phase 6 | `scripts/mail-desk-switch.ps1 -Account <email> -On\|-Off -Reason "<why>" [-DryRun]`, reading the Gateway address (`gateway-admin-url`) and the token (`admin-service-token`) from cc-secrets, looking the account up with `GET /gateway/admin/accounts?email=`, then `POST /gateway/admin/mail-desk`, then reading it back with `GET`. | No tool calls an administrator switch today (phase 0 (d)); phase 6 needs one exact command. |
| Rules document format | **JSON**, one document per account per version, shape in section 3. | The Gateway is C# and every edit is a command, never a hand-edited file; JSON has one reading. |
| Which providers a channel accepts | `outlook` only. Any other value is refused with the list of accepted providers; `gmail` is one added constant in the next mission (D20). | Accepting `gmail` now would make a channel whose every check fails. |
| A removed channel | Kept, marked removed (`RemovedUtc`, `RemovedBy`, `RemovedWhy`); its decisions stay. Adding the same address again makes a new row. | "removal keeps its decisions" (5.8). |
| Where channel and mode changes are recorded | In the same `mail_desk_list_changes` table as rule changes, with `kind` `channel` or `mode` and no rule versions. | One record of who changed what and why, for `rules history` and the 08:00 section. |

## 2. The tables (both databases, one migration pair, tenant-scoped like every other table)

- **`mail_desk_channels`**: id, tenant, kind (`email`), provider (`outlook`), address (lower case), machines (JSON array
  of machine names, in order of preference, at least one), factory (default `mail-desk`), seat (default `mail-desk`),
  mode (`observe` | `live`, default `observe`), created utc/by, changed utc/by, removed utc/by/why (null while active).
  At most one ACTIVE row per (tenant, kind, address).
- **`mail_desk_rules`**: id, tenant, version (1, 2, 3 ... per tenant, unique per tenant), document (JSON text), saved
  utc, saved by. Never updated or deleted: every save is a new row. No row means the empty document at version 0.
- **`mail_desk_list_changes`**: id, tenant, at utc, kind (`rule` | `allow` | `block` | `route` | `channel` | `mode`),
  action (`add` | `remove` | `set`), subject (the pattern, the rule name, `factory/seat`, or the address), detail
  (JSON of what was added or removed, or `{"from":"observe","to":"live"}`), version before and after (null for
  channel and mode), who kind (`person` | `session`), who id, who name (a session's name at that moment; null for a
  person), why (required, never blank).
- **`mail_desk_decisions`**: id, tenant, channel id, message key, received utc, sender address, subject, layer,
  decision, route factory, route seat, reason, confidence (nullable), cost in US dollars (nullable), mode, outcome
  (nullable; live mode: `filed`, `seat-started`, `failed`), outcome detail (nullable), decided utc. Unique per
  (tenant, channel, message key). **No body text, ever.** Phase 1 builds the table, an insert used by tests and by
  phase 3, and the reads.
- **`mail_desk_routed`**: id, tenant, decision id, factory, seat, session id (nullable), state (`open` | `done` |
  `returned`), return note (nullable), returned count, created utc, changed utc. Phase 1 builds the table and entity
  only; phase 4 uses it.

Vocabulary, held once as constants in `src/CcDirector.Gateway/MailDesk/MailDeskWords.cs` and used by every filter:

- layers: `rule`, `allow`, `block`, `sender-screen` (the "person or spam?" model question), `route-model` (the
  "which route?" model question);
- decisions: `route`, `archive`, `junk`, `keep-for-owner`, `mail-desk-agent`, `failed`;
- modes: `observe`, `live`.

## 3. The rules document

```json
{
  "rules":  [ { "name": "citizenship",
                "when": { "subjectContains": ["citizen"], "fromAny": ["a@example.com", "b@example.com"] },
                "route": { "factory": "citizenship", "seat": "intake" } } ],
  "allow":  [ { "from": "*@notifications.example.com", "what": "build notifications", "do": "archive" },
              { "from": "news@example.org", "what": "a newsletter I read", "do": "keep-for-owner" },
              { "from": "billing@example.net", "what": "invoices", "do": { "route": { "factory": "books", "seat": "clerk" } } } ],
  "block":  [ { "from": "*@spammy-domain.example", "why": "cold sales, never answered" } ],
  "routes": [ { "factory": "devthrottle", "seat": "mail-desk", "handles": "customers and users of DevThrottle: support, billing, bugs" } ]
}
```

Validation (one validator, `MailDeskRulesValidator`, every refusal a sentence naming the field and the fix):

- a pattern (`from`, each `fromAny`) is an exact address or `*@domain`, stored lower case; nothing else (no other
  wildcard);
- a pattern may not be on both `allow` and `block`, and may not appear twice in one list;
- rule names are unique (case-insensitive), not blank; a rule has at least one condition (`subjectContains` or
  `fromAny`) and a `route`;
- `do` is `archive`, `keep-for-owner`, or `{ "route": { factory, seat } }`;
- a factory name passes `FactoryNames.TryFactory`; a seat is not blank;
- a route (`factory/seat`) appears once in `routes`, and its `handles` line is not blank;
- `what` (allow) and `why` (block) are not blank;
- matching rules (first match wins) are phase 3's; phase 1 only stores, validates and orders (an add appends).

## 4. Who may do what

| Action | Person | Session in no factory | A factory's session | Neither (machine token, Director) |
|---|---|---|---|---|
| Every read (status, channels, rules, history, decisions) | yes | yes | yes | yes |
| allow / block / rule / route add and remove | yes | yes | yes (D7: "freely, every change recorded") | **403** - a change needs an author |
| channel add, channel remove, mode | yes | yes (the owner told it to; the 6c rule) | **403**, `outcome: "FactorySessionRefused"` | **403** |
| A session the Gateway does not know yet | | 409, as `FactoryDefinitionEndpoints` answers | | |

- Every change carries `why`, required and not blank: **400** `{"error":"why_required"}` otherwise. Nothing is
  recorded on any refusal.
- Who is recorded: a person by the identifier their device carries; a session by its id and its name at that moment
  (the `FactoryPublisher` shape). A session's name never goes into a log line.
- The caller kind is decided the way `FactoryDefinitionEndpoints.KindOf` and `TryCaller` decide it. Extract that into
  one shared helper rather than copying it.

## 5. The routes (all mapped into `MailDeskGate.Group(...)`; prefix `/gateway/mail-desk`)

Every route answers 404 with no body when the switch is off for the calling account. JSON is camelCase.

| Method and path | Body or query | Answer |
|---|---|---|
| `GET /status` | - | `{ enabled: true, rulesVersion, channels: [<channel row>], today: { date, timeZone, total, byDecision: { route: n, archive: n, ... every decision, zeros included } }, help: [...] }`. `channels` is the active list; `today` is the account's day over every channel, removed ones included (review of 4231, F1) |
| `GET /channels` | `?all=true` also lists removed ones | `{ count, channels: [<channel row>] }` - **this is the route phase 7's page reads** |
| `POST /channels` | `{ address, provider, machines: [..], why }` | 201 `<channel row>`; 409 if an active channel has that address; 400 for a bad address, provider, or empty machines |
| `POST /channels/remove` | `{ address, why }` | 200 `<channel row>` (removed); 404 `{"error":"no_such_channel"}` |
| `POST /channels/mode` | `{ address, mode, why }` | 200 `<channel row>`; 400 for a mode outside `observe`/`live` |
| `GET /rules` | `?version=N` (default newest) | `{ version, savedUtc, savedBy, document }`; version 0 is the empty document |
| `GET /rules/history` | `?n=` (default 50), `?kind=` | `{ count, total, changes: [<change row>] }` newest first |
| `POST /rules/{list}/add` (`list` = `allow`, `block`, `rule`, `route`) | the entry as in section 3, plus `why` | 200 `{ version, change: <change row> }`; 400 with the validator's sentence |
| `POST /rules/{list}/remove` | `{ key, why }` - key is the pattern, the rule name, or `factory/seat` | 200 `{ version, change }`; 404 `{"error":"no_such_entry"}` |
| `GET /decisions` | `?since=<ISO time>&layer=&decision=&channel=<address>&n=` (default 50) | `{ count, total, byDecision: {...}, decisions: [<decision row>] }` newest first; every filter applies; an unknown `layer` or `decision` is 400 naming the valid ones |

**The channel row, every value decided on the Gateway (repository rule 7):**

```json
{ "id": "...", "kind": "email", "provider": "outlook", "address": "email@devthrottle.com",
  "machines": ["devlinux"], "mode": "observe", "modeLabel": "Observe - decides and records, changes nothing",
  "factory": "mail-desk", "seat": "mail-desk",
  "signedIn": { "state": "unknown" | "yes" | "failing", "label": "..." },
  "lastCheck": { "utc": "..." | null, "outcome": "..." | null, "label": "..." },
  "today": { "total": 0, "byDecision": { "route": 0, "archive": 0, "junk": 0, "keep-for-owner": 0, "mail-desk-agent": 0, "failed": 0 } },
  "createdUtc": "...", "createdBy": "...", "changedUtc": "...", "changedBy": "...", "removedUtc": null }
```

- `lastCheck` comes from the Mail Desk start's trigger (`MailDeskStart.Is`) on the account: its `LastCheckUtc`,
  `LastOutcome`, `LastReason`. No such trigger yet: `utc` and `outcome` null, label "Not checked yet - the mail-desk
  factory is not published".
- `signedIn`: `unknown` when there is no check yet; `failing` when the last check failed (label quotes the check's
  own reason, which names the fix - phase 2 makes the sign-in failure say "is not signed in on <machine>: run
  cc-outlook -a <nickname> auth"); `yes` when the last check succeeded. Phase 2 may refine what the check reports;
  the fold stays in one place, `MailDeskChannelFold`.
- `today`: decisions of this channel since local midnight in the account's own time zone (the setting behind
  `/gateway/time-zone`), every decision listed with zeros.

**The change row:** `{ id, atUtc, kind, action, subject, detail, versionBefore, versionAfter, who: { kind, id, name },
whoInWords, why }`.

**The decision row:** `{ id, channel, messageKey, receivedUtc, sender, subject, layer, decision, route: { factory,
seat } | null, reason, confidence, costUsd, mode, outcome, outcomeDetail, decidedUtc }`.

## 6. The command, `cc-devthrottle maildesk` (tools/cc-devthrottle, AXI standard)

```
maildesk status [--json]
maildesk channel list [--all] [--json]
maildesk channel add <address> --provider outlook --machine <machine> [--machine <backup>] --why "<text>"
maildesk channel remove <address> --why "<text>"
maildesk mode <address> observe|live --why "<text>"
maildesk rules show [--version N] [--json]
maildesk rules history [-n N] [--kind rule|allow|block|route|channel|mode] [--json]
maildesk allow add <pattern> --what "<text>" --do archive|keep-for-owner|route:<factory>/<seat> --why "<text>"
maildesk allow remove <pattern> --why "<text>"
maildesk block add <pattern> --why "<text>"           (the block entry's own "why" is this --why)
maildesk block remove <pattern> --why "<text>"
maildesk rule add <name> [--subject-contains <text>]... [--from <pattern>]... --route <factory>/<seat> --why "<text>"
maildesk rule remove <name> --why "<text>"
maildesk route add <factory>/<seat> --handles "<text>" --why "<text>"
maildesk route remove <factory>/<seat> --why "<text>"
maildesk decisions [--since <time>] [--layer L] [--decision D] [--channel <address>] [-n N] [--json]
```

- The command decides nothing: it prints what the Gateway answered. Every filter goes to the Gateway and applies to
  `--json` too. An unknown flag fails with exit 2; a missing `--why` fails with exit 2 before any request is made.
- A 404 with no body from a Mail Desk route means the switch is off for this account: say so in one sentence
  ("Mail Desk is not switched on for this account"), exit 1.
- A Gateway refusal prints the Gateway's own sentence, exit 1.
- `count: 0` on an empty list; ids and addresses never cut short; `help[]` lines after output.
- `done` and `return` are phase 4's, not built here.

## 7. Example data

The repository is public. Tests and examples use only `email@devthrottle.com` and the reserved example domains
(`example.com`, `example.org`, `example.net`, `*.example`). Never the owner's second mailbox address, never a real
sender's address or domain (the phase 0 review's ruling, 18 October 2026) - not even a well-known one.
