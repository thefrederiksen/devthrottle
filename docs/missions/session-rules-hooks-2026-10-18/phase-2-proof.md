# Session Rules part 2, phase 2 - proof: agents manage rules

Developer seat, 2026-10-18. Branch `mission/session-rules-hooks-p2`, commit `60c5464f8`, cut from `origin/main` at
`7a365544b`. Machine: devlinux (Linux), .NET SDK 10.0.301. Every number below carries its exit code and the commit it
ran on.

## What was built

- **Session keys reach every `/gateway/rules` route** (owner decision D7). `SessionKeyGuard.IsRuleRoute` lists the
  eight shapes `SessionRuleEndpoints` maps, each as a literal at its exact length; anything else under
  `/gateway/rules` stays refused.
- **A change route, `PATCH /gateway/rules/{id}`, carrying the rule body generically.** The Gateway takes the rule's
  current body in the shape a create accepts (`SessionRuleBody.Of`), lays the caller's fields over it, and reads the
  result through the same reader a create uses (`SessionRuleWire.ReadDraft`). The fields a change may carry are
  derived from that body, never listed. A field the body does not have is refused, naming the ones it does have.
- **The change history, `session_rule_changes`.** Every create, change, promote and delete writes one row in the
  same save as the change: who (`session:<id>`, `device:<id>` or a signed-in person), the session id, the agent the
  live roster says that session runs, the time, and the rule before and after (body, state and who promoted it).
  It is append only: the write gate refuses an edit or a removal, and the bulk SQL interceptor now closes this table
  too. `GET /gateway/rules/history` reads it, with `rule`, `session`, `action` and `limit` filters; an unreadable
  filter is refused, not ignored.
- **`cc-devthrottle rules list | show | create | change | promote | delete | history`**, to the AXI standard:
  `--json` keeps the Gateway's shape and every filter applies to it, an unknown flag exits 2 with the valid options,
  an empty answer says `count: 0`, ids are never cut, and each command ends with `help[]` next steps. Documented in
  `docs/cli-reference.md` under "Rules".
- Two migrations, SQLite and PostgreSQL (`AddSessionRuleChanges`); both model snapshot diffs are pure additions.

## Decisions taken inside the task - for the Delivery Lead to confirm or reverse

1. **Any change puts the rule back in dry run**, and clears who promoted it. Two reasons. Dry run is what puts a
   person between an instruction and its first real use, and a changed instruction is one nobody has watched behave.
   And the existing write gate refuses any save that leaves a rule live without a promotion grant, so keeping a
   changed rule live would have meant weakening that gate. The answer to a change carries `wasLive`, and the command
   says "It was live and is back in dry run: promote it again". Reversing this means adding a change marker to the
   gate the way promotion has one.
2. **A change that changes nothing is refused** (400, no history row), so the history never says a rule changed when
   it did not, and a live rule is never dropped to dry run for nothing.
3. **The promote route's caller was read from a request item nothing wrote.** `RulePromotionGrant.CallerOf` read
   `Items["DeviceKeyId"]`; the middleware stamps the device under `cc.auth.DeviceIdentity`. Only the unit tests set
   the old item, so over real HTTP a device-key promotion named nobody and was refused, while the tests passed. It
   now reads what the middleware records and names a session as well. `PromotedBy` is now written `device:<id>` or
   `session:<id>` (a signed-in person is still their bare name). The test helper builds its request through the real
   item, so this cannot drift again unnoticed.
4. **Deleting a rule that does not exist** still answers 200 `{"deleted": false}` (unchanged, for existing
   callers), and the command turns that into exit 1, "there is no rule with the id ...".
5. **A session not on the live roster** is recorded with agent `unknown (the session was not on the live roster)`,
   said rather than guessed. Its session id is still recorded, because that comes from the verified key.

### For whoever rebases on phase 1

Phase 1 adds `kind` and scope fields. For them to flow through a change, add each field in four places, and a test
fails if the body is forgotten: `SessionRuleBody.Of` (the body), `SessionRuleWire.ReadDraft` and `SessionRuleDraft`
(the one reader create and change share), and the store's `Create` and `Change`.
`SessionRuleBodyTests.Of_EveryFieldTheCreateReaderReads_IsInTheBody` reads the draft's properties and fails on any
field the body lacks. The CLI needs nothing: `--body` sends any field as given, and `--scope key=value` passes keys
through. Both phases add a migration; whichever lands second regenerates the two model snapshots.

## Tests

| Run | Result | Exit | Commit |
|---|---|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Rules"` | 508 passed, 0 failed | 0 | `60c5464f8` |
| Same project, the structural guards: `RulesTypeNothingGuardTests`, `RulesPromotionBoundaryGuardTests`, `RulesBulkSqlGuardTests`, and every `SessionKeyGuard` test | 518 passed, 0 failed | 0 | `60c5464f8` |
| `python -m pytest tools/cc-devthrottle/tests` (the whole `cc-devthrottle` suite) | 4,904 passed, 2 skipped, 0 failed | 0 | `60c5464f8` |
| `dotnet test src/CcDirector.Gateway.Tests` (parked), `ContextLessRouteCensusTests` and `CensusRouteTenancyProbeTests` | 9 passed, 1 failed - see below | 1 | `60c5464f8` |

`RulesTypeNothingGuardTests` is green: nothing in this change can type into a session.

The one parked failure is `The_selfhost_context_less_route_set_adds_only_the_hosted_denied_families`. It expects the
`/exes/slots` routes, which `GatewayEndpoints` maps only on Windows, so it fails on Linux with or without this
change. The hosted census, the list this change edits (the rule `DELETE` now reads the request, so it left the
context-less list), passes.

The new tests, all `Method_Scenario_Result` in Arrange-Act-Assert:

- `SessionRuleChangeHistoryTests` (store, real migrated SQLite, 18 tests): each action recorded with the session and
  agent; a refused create, change or delete records nothing; a change to a live rule returns it to dry run with before
  and after; a no-op change, an unknown check, a missing scope and a missing rule are refused; the history outlives a
  deleted rule; the filters narrow; and a history row cannot be edited, removed, deleted by bulk SQL, or written
  naming nobody.
- `SessionRuleBodyTests` (11): the body round-trips through the create reader, including an input argument and a
  literal that looks like one; every field the reader reads is in the body; the change reader's refusals.
- `SessionRuleEndpointsAgentTests` (6, a TestServer with the session identity stamped as the middleware stamps it):
  create, promote, change, delete as a session, each in the history with that session and agent; a 404 for a missing
  rule; a 400 for an unknown field, with nothing recorded; an unreadable history filter refused; an empty history says
  `count: 0`.
- `SessionKeyGuardRuleRoutesTests` (18): the eight shapes allowed, nine neighbouring shapes refused.
- `tools/cc-devthrottle/tests/test_rule_ops.py` (29): what each command sends and prints, the filters under
  `--json`, `count: 0`, ids read back in full from the default output, every usage error and every Gateway refusal.

### The tests fail without the code they guard

With the `IsRuleRoute` line in `SessionKeyGuard.IsAllowed` disabled and the append-only check in
`GatewayDbContext.GuardRuleWrites` disabled, the guard and history tests were run: **11 failed, 25 passed** - the nine
allowed shapes in `Check_EveryRouteTheRulesSurfaceMaps_IsAllowedToASessionKey`, and
`HistoryRow_EditedThroughTheContext_IsRefusedByTheWriteGate` and
`HistoryRow_RemovedThroughTheContext_IsRefusedByTheWriteGate`. Both lines were restored before the commit. The rest
of the new tests exercise code that did not exist before this change (no change route, no history), so without it
they do not compile.

## The live run, from a real session, using only `cc-devthrottle rules`

**The rig.** A Gateway built from this tree on port 7996 and a slot-5 test Director built from this tree, both on a
scratch storage root, the Director with its own HOME so it could not touch the user's tools, and connected to that
Gateway with its machine token. On that Director, a real session was opened: agent `RawCli` running `/bin/bash`,
named "Session Rules - Proof - agent manages rules", id `fdaca9fd-6e57-4183-92b4-7874722e1b4e`. The Director gave it
its own session key, as it gives every session. One line was typed into that session's terminal through the
Gateway's prompt route: a script that runs `cc-devthrottle rules` commands (this tree's copy, which has the `rules`
group) and nothing else. So every call below was made by that session, with that session's own key. The session
id, the agent and `changedBy` in the history are what the Gateway recorded from the verified key and the live roster;
the commands sent no identity of their own.

Torn down cleanly afterwards: the session stopped with a reason, the Director on its named shutdown signal, the
Gateway on the terminate signal. None was force-killed.

### Negative control: the same session against a Gateway with the allow-list line disabled

The first run met, by accident, a Gateway binary built while the `IsRuleRoute` line was disabled for the red run
above. Every call was refused with the guard's sentence, which is today's behaviour on main:

```
=== who is calling ===
CC_SESSION_ID=fdaca9fd-6e57-4183-92b4-7874722e1b4e
CC_GATEWAY_URL=http://127.0.0.1:7996
CC_GATEWAY_SESSION_KEY is set (a session key, value not shown)

=== the flow: create, promote, change, delete ===

$ cc-devthrottle rules list
Error: a session key may not call GET /gateway/rules; it may run the fleet's agent routes and configure the product - settings and handovers - but never the admission surface: device enrollment, account identity, Director registration, or force-killing a Director (HTTP 403)
help[1]:
  cc-devthrottle rules list
[exit 1]

$ cc-devthrottle rules create --instruction "When a session runs echo RULE-PROBE, tell me what it was doing." --screen-description "A shell command that echoes RULE-PROBE." --trigger-word RULE-PROBE --scope all-sessions --cooldown 5m --daily-cap 5
Error: a session key may not call POST /gateway/rules; it may run the fleet's agent routes and configure the product - settings and handovers - but never the admission surface: device enrollment, account identity, Director registration, or force-killing a Director (HTTP 403)
help[1]:
  cc-devthrottle rules list
[exit 1]

$ cc-devthrottle rules list
Error: a session key may not call GET /gateway/rules; it may run the fleet's agent routes and configure the product - settings and handovers - but never the admission surface: device enrollment, account identity, Director registration, or force-killing a Director (HTTP 403)
help[1]:
  cc-devthrottle rules list
[exit 1]
```

### The flow and the failure cases, against the Gateway built from `60c5464f8`

What it shows:

- **create, promote, change, delete**, each in `rules history` with session `fdaca9fd-6e57-4183-92b4-7874722e1b4e`,
  agent `RawCli`, and `changedBy` `session:fdaca9fd-...`; `--json` carries the rule before and after each change.
- The rule was created in dry run; promote made it live and named the session; the change (daily cap 5 to 2) put it
  back in dry run and said so; the delete kept the history.
- **A session key changing a rule that does not exist:** exit 1, "there is no rule with the id ... (HTTP 404)".
- **Invalid bodies:** an unknown field (`dailycap`) refused naming the fields a change carries; a check the product
  does not ship (`run_this_python`) refused naming the ones it does; the state refused, pointing at promote; a change
  that changes nothing refused; JSON that does not parse refused before anything was sent (exit 2).
- **An unknown flag:** exit 2 with the valid options. A promote with no acknowledgement: exit 2.
- **A refusal records nothing:** the session's history at the end has six rows - two creates, one promote, one
  change, two deletes - and none for the five refused changes.

```
=== who is calling ===
CC_SESSION_ID=fdaca9fd-6e57-4183-92b4-7874722e1b4e
CC_GATEWAY_URL=http://127.0.0.1:7996
CC_GATEWAY_SESSION_KEY is set (a session key, value not shown)

=== the flow: create, promote, change, delete ===

$ cc-devthrottle rules list
count: 0
rules[0]{id,state,scope,instruction}:
No rules in this account.
help[1]:
  cc-devthrottle rules create --instruction "<sentence>" --screen-description "<what it watches for>" --trigger-word <word> --scope all-sessions --cooldown 5m --daily-cap 5
[exit 0]

$ cc-devthrottle rules create --instruction "When a session runs echo RULE-PROBE, tell me what it was doing." --screen-description "A shell command that echoes RULE-PROBE." --trigger-word RULE-PROBE --scope all-sessions --cooldown 5m --daily-cap 5
Created rule 2782ff44-2ceb-4f6e-b837-b4c9cb268623 in dry run: it records what it would do and types nothing.
help[3]:
  cc-devthrottle rules show 2782ff44-2ceb-4f6e-b837-b4c9cb268623
  cc-devthrottle rules promote 2782ff44-2ceb-4f6e-b837-b4c9cb268623 --acknowledgement "<what you are agreeing to>"
  cc-devthrottle rules history 2782ff44-2ceb-4f6e-b837-b4c9cb268623
[exit 0]

$ cc-devthrottle rules list
count: 1 (dry_run 1)
rules[1]{id,state,scope,instruction}:
  2782ff44-2ceb-4f6e-b837-b4c9cb268623,dry_run,all-sessions,"When a session runs echo RULE-PROBE, tell me what it was doing."
help[2]:
  cc-devthrottle rules show <rule-id>
  cc-devthrottle rules history
[exit 0]

$ cc-devthrottle rules show 2782ff44-2ceb-4f6e-b837-b4c9cb268623
id: 2782ff44-2ceb-4f6e-b837-b4c9cb268623
state: dry_run
instruction: When a session runs echo RULE-PROBE, tell me what it was doing.
screen-description: A shell command that echoes RULE-PROBE.
trigger-words: RULE-PROBE
checks: none
scope: all-sessions
cooldown: 300s
daily-cap: 5
promoted-by: -
created: 2026-10-18T16:43:56.7362904Z
updated: 2026-10-18T16:43:56.7362904Z
firings: 0
help[3]:
  cc-devthrottle rules history 2782ff44-2ceb-4f6e-b837-b4c9cb268623
  cc-devthrottle rules change 2782ff44-2ceb-4f6e-b837-b4c9cb268623 --daily-cap <n>
  cc-devthrottle rules promote 2782ff44-2ceb-4f6e-b837-b4c9cb268623 --acknowledgement "<what you are agreeing to>"
[exit 0]

$ cc-devthrottle rules promote 2782ff44-2ceb-4f6e-b837-b4c9cb268623 --acknowledgement Proof run: I have read the dry run and am making this rule live.
Rule 2782ff44-2ceb-4f6e-b837-b4c9cb268623 is live, promoted by session:fdaca9fd-6e57-4183-92b4-7874722e1b4e.
help[2]:
  cc-devthrottle rules show 2782ff44-2ceb-4f6e-b837-b4c9cb268623
  cc-devthrottle rules history 2782ff44-2ceb-4f6e-b837-b4c9cb268623
[exit 0]

$ cc-devthrottle rules change 2782ff44-2ceb-4f6e-b837-b4c9cb268623 --daily-cap 2
Changed rule 2782ff44-2ceb-4f6e-b837-b4c9cb268623 (dailyCap). It was live and is back in dry run: promote it again to make it live.
help[2]:
  cc-devthrottle rules show 2782ff44-2ceb-4f6e-b837-b4c9cb268623
  cc-devthrottle rules history 2782ff44-2ceb-4f6e-b837-b4c9cb268623
[exit 0]

$ cc-devthrottle rules delete 2782ff44-2ceb-4f6e-b837-b4c9cb268623 --yes
Deleted rule 2782ff44-2ceb-4f6e-b837-b4c9cb268623. Its firing record and its history are kept.
help[1]:
  cc-devthrottle rules history 2782ff44-2ceb-4f6e-b837-b4c9cb268623
[exit 0]

$ cc-devthrottle rules history 2782ff44-2ceb-4f6e-b837-b4c9cb268623 --fields when,action,rule,session,agent,changed-by,changed
count: 4 (create 1, change 1, promote 1, delete 1)
changes[4]{when,action,rule,session,agent,changed-by,changed}:
  2026-10-18T16:43:59.0219093Z,delete,2782ff44-2ceb-4f6e-b837-b4c9cb268623,fdaca9fd-6e57-4183-92b4-7874722e1b4e,RawCli,session:fdaca9fd-6e57-4183-92b4-7874722e1b4e,-
  2026-10-18T16:43:58.596558Z,change,2782ff44-2ceb-4f6e-b837-b4c9cb268623,fdaca9fd-6e57-4183-92b4-7874722e1b4e,RawCli,session:fdaca9fd-6e57-4183-92b4-7874722e1b4e,dailyCap;state;promotedBy
  2026-10-18T16:43:58.1729904Z,promote,2782ff44-2ceb-4f6e-b837-b4c9cb268623,fdaca9fd-6e57-4183-92b4-7874722e1b4e,RawCli,session:fdaca9fd-6e57-4183-92b4-7874722e1b4e,state;promotedBy
  2026-10-18T16:43:56.7362904Z,create,2782ff44-2ceb-4f6e-b837-b4c9cb268623,fdaca9fd-6e57-4183-92b4-7874722e1b4e,RawCli,session:fdaca9fd-6e57-4183-92b4-7874722e1b4e,-
help[2]:
  cc-devthrottle rules history --json
  cc-devthrottle rules show <rule-id>
[exit 0]

$ cc-devthrottle rules history --session me --json
{
  "count": 4,
  "changes": [
    {
      "id": "faddd276-ef91-4774-8abd-3df82ae9bca8",
      "ruleId": "2782ff44-2ceb-4f6e-b837-b4c9cb268623",
      "action": "delete",
      "changedBy": "session:fdaca9fd-6e57-4183-92b4-7874722e1b4e",
      "sessionId": "fdaca9fd-6e57-4183-92b4-7874722e1b4e",
      "agent": "RawCli",
      "occurredUtc": "2026-10-18T16:43:59.0219093Z",
      "before": {
        "instruction": "When a session runs echo RULE-PROBE, tell me what it was doing.",
        "screenDescription": "A shell command that echoes RULE-PROBE.",
        "triggerWords": [
          "RULE-PROBE"
        ],
        "checks": [],
        "scope": "all-sessions",
        "cooldownSeconds": 300,
        "dailyCap": 2,
        "state": "dry_run",
        "promotedBy": ""
      },
      "after": null
    },
    {
      "id": "69e7b71e-94bf-497a-bd15-59a4febac061",
      "ruleId": "2782ff44-2ceb-4f6e-b837-b4c9cb268623",
      "action": "change",
      "changedBy": "session:fdaca9fd-6e57-4183-92b4-7874722e1b4e",
      "sessionId": "fdaca9fd-6e57-4183-92b4-7874722e1b4e",
      "agent": "RawCli",
      "occurredUtc": "2026-10-18T16:43:58.596558Z",
      "before": {
        "instruction": "When a session runs echo RULE-PROBE, tell me what it was doing.",
        "screenDescription": "A shell command that echoes RULE-PROBE.",
        "triggerWords": [
          "RULE-PROBE"
        ],
        "checks": [],
        "scope": "all-sessions",
        "cooldownSeconds": 300,
        "dailyCap": 5,
        "state": "live",
        "promotedBy": "session:fdaca9fd-6e57-4183-92b4-7874722e1b4e"
      },
      "after": {
        "instruction": "When a session runs echo RULE-PROBE, tell me what it was doing.",
        "screenDescription": "A shell command that echoes RULE-PROBE.",
        "triggerWords": [
          "RULE-PROBE"
        ],
        "checks": [],
        "scope": "all-sessions",
        "cooldownSeconds": 300,
        "dailyCap": 2,
        "state": "dry_run",
        "promotedBy": ""
      }
    },
    {
      "id": "94886909-b47a-4038-8fdd-78f7c375ba27",
      "ruleId": "2782ff44-2ceb-4f6e-b837-b4c9cb268623",
      "action": "promote",
      "changedBy": "session:fdaca9fd-6e57-4183-92b4-7874722e1b4e",
      "sessionId": "fdaca9fd-6e57-4183-92b4-7874722e1b4e",
      "agent": "RawCli",
      "occurredUtc": "2026-10-18T16:43:58.1729904Z",
      "before": {
        "instruction": "When a session runs echo RULE-PROBE, tell me what it was doing.",
        "screenDescription": "A shell command that echoes RULE-PROBE.",
        "triggerWords": [
          "RULE-PROBE"
        ],
        "checks": [],
        "scope": "all-sessions",
        "cooldownSeconds": 300,
        "dailyCap": 5,
        "state": "dry_run",
        "promotedBy": ""
      },
      "after": {
        "instruction": "When a session runs echo RULE-PROBE, tell me what it was doing.",
        "screenDescription": "A shell command that echoes RULE-PROBE.",
        "triggerWords": [
          "RULE-PROBE"
        ],
        "checks": [],
        "scope": "all-sessions",
        "cooldownSeconds": 300,
        "dailyCap": 5,
        "state": "live",
        "promotedBy": "session:fdaca9fd-6e57-4183-92b4-7874722e1b4e"
      }
    },
    {
      "id": "b9834cf2-bd8f-461e-bef5-7254f9bc07c7",
      "ruleId": "2782ff44-2ceb-4f6e-b837-b4c9cb268623",
      "action": "create",
      "changedBy": "session:fdaca9fd-6e57-4183-92b4-7874722e1b4e",
      "sessionId": "fdaca9fd-6e57-4183-92b4-7874722e1b4e",
      "agent": "RawCli",
      "occurredUtc": "2026-10-18T16:43:56.7362904Z",
      "before": null,
      "after": {
        "instruction": "When a session runs echo RULE-PROBE, tell me what it was doing.",
        "screenDescription": "A shell command that echoes RULE-PROBE.",
        "triggerWords": [
          "RULE-PROBE"
        ],
        "checks": [],
        "scope": "all-sessions",
        "cooldownSeconds": 300,
        "dailyCap": 5,
        "state": "dry_run",
        "promotedBy": ""
      }
    }
  ]
}
[exit 0]

=== failure cases ===

$ cc-devthrottle rules change 00000000-0000-4000-8000-000000000000 --daily-cap 2
Error: there is no rule with the id 00000000-0000-4000-8000-000000000000. (HTTP 404)
help[2]:
  cc-devthrottle rules list
  cc-devthrottle rules show 00000000-0000-4000-8000-000000000000
[exit 1]

(second rule for the failure cases: ac6f1cac-85fa-4b74-99d7-708ba11a48d1)

$ cc-devthrottle rules change ac6f1cac-85fa-4b74-99d7-708ba11a48d1 --body {"dailycap": 2}
Error: 'dailycap' is not a field a change can carry. A change carries: instruction, screenDescription, triggerWords, checks, scope, cooldownSeconds, dailyCap. (HTTP 400)
help[2]:
  cc-devthrottle rules list
  cc-devthrottle rules show ac6f1cac-85fa-4b74-99d7-708ba11a48d1
[exit 1]

$ cc-devthrottle rules change ac6f1cac-85fa-4b74-99d7-708ba11a48d1 --checks [{"name": "run_this_python", "arguments": {}}]
Error: there is no check called 'run_this_python'. The checks we ship are: elapsed_since, extract_first, is_path_inside, matches_any, retry_delay_from. (HTTP 400)
help[2]:
  cc-devthrottle rules list
  cc-devthrottle rules show ac6f1cac-85fa-4b74-99d7-708ba11a48d1
[exit 1]

$ cc-devthrottle rules change ac6f1cac-85fa-4b74-99d7-708ba11a48d1 --body {"state": "live"}
Error: 'state' is not changed by a change. A rule is moved out of dry run by promote, and any change puts it back in dry run. (HTTP 400)
help[2]:
  cc-devthrottle rules list
  cc-devthrottle rules show ac6f1cac-85fa-4b74-99d7-708ba11a48d1
[exit 1]

$ cc-devthrottle rules change ac6f1cac-85fa-4b74-99d7-708ba11a48d1 --daily-cap 1
Error: this change leaves the rule ac6f1cac-85fa-4b74-99d7-708ba11a48d1 exactly as it was, so nothing was changed. Name at least one field with a new value. (HTTP 400)
help[2]:
  cc-devthrottle rules list
  cc-devthrottle rules show ac6f1cac-85fa-4b74-99d7-708ba11a48d1
[exit 1]

$ cc-devthrottle rules change ac6f1cac-85fa-4b74-99d7-708ba11a48d1 --body {not json
Error: --body is not valid JSON: Expecting property name enclosed in double quotes at line 1 column 2.
Usage: cc-devthrottle rules change [OPTIONS] {rule}
Valid options: --body, --file, --instruction, --screen-description, --trigger-word, --checks, --scope, --cooldown, --daily-cap, --json, -j, --help
help[1]:
  cc-devthrottle rules change --help
[exit 2]

$ cc-devthrottle rules list --everything
Error: No such option: --everything
Usage: cc-devthrottle rules list [OPTIONS]
Valid options: --json, -j, --state, --fields, --help
help[1]:
  cc-devthrottle rules list --help
[exit 2]

$ cc-devthrottle rules promote ac6f1cac-85fa-4b74-99d7-708ba11a48d1
Error: Missing option --acknowledgement / -m.
Usage: cc-devthrottle rules promote [OPTIONS] {rule}
Valid options: --acknowledgement, -m, --json, -j, --help
help[1]:
  cc-devthrottle rules promote --help
[exit 2]

$ cc-devthrottle rules show ac6f1cac-85fa-4b74-99d7-708ba11a48d1
id: ac6f1cac-85fa-4b74-99d7-708ba11a48d1
state: dry_run
instruction: A second rule for the failure cases.
screen-description: Anything.
trigger-words: PROBE
checks: none
scope: all-sessions
cooldown: 60s
daily-cap: 1
promoted-by: -
created: 2026-10-18T16:44:00.8970202Z
updated: 2026-10-18T16:44:00.8970202Z
firings: 0
help[3]:
  cc-devthrottle rules history ac6f1cac-85fa-4b74-99d7-708ba11a48d1
  cc-devthrottle rules change ac6f1cac-85fa-4b74-99d7-708ba11a48d1 --daily-cap <n>
  cc-devthrottle rules promote ac6f1cac-85fa-4b74-99d7-708ba11a48d1 --acknowledgement "<what you are agreeing to>"
[exit 0]

$ cc-devthrottle rules delete ac6f1cac-85fa-4b74-99d7-708ba11a48d1 --yes
Deleted rule ac6f1cac-85fa-4b74-99d7-708ba11a48d1. Its firing record and its history are kept.
help[1]:
  cc-devthrottle rules history ac6f1cac-85fa-4b74-99d7-708ba11a48d1
[exit 0]

$ cc-devthrottle rules history --session me
count: 6 (create 2, change 1, promote 1, delete 2)
changes[6]{when,action,rule,session,agent}:
  2026-10-18T16:44:05.0183409Z,delete,ac6f1cac-85fa-4b74-99d7-708ba11a48d1,fdaca9fd-6e57-4183-92b4-7874722e1b4e,RawCli
  2026-10-18T16:44:00.8970202Z,create,ac6f1cac-85fa-4b74-99d7-708ba11a48d1,fdaca9fd-6e57-4183-92b4-7874722e1b4e,RawCli
  2026-10-18T16:43:59.0219093Z,delete,2782ff44-2ceb-4f6e-b837-b4c9cb268623,fdaca9fd-6e57-4183-92b4-7874722e1b4e,RawCli
  2026-10-18T16:43:58.596558Z,change,2782ff44-2ceb-4f6e-b837-b4c9cb268623,fdaca9fd-6e57-4183-92b4-7874722e1b4e,RawCli
  2026-10-18T16:43:58.1729904Z,promote,2782ff44-2ceb-4f6e-b837-b4c9cb268623,fdaca9fd-6e57-4183-92b4-7874722e1b4e,RawCli
  2026-10-18T16:43:56.7362904Z,create,2782ff44-2ceb-4f6e-b837-b4c9cb268623,fdaca9fd-6e57-4183-92b4-7874722e1b4e,RawCli
help[2]:
  cc-devthrottle rules history --json
  cc-devthrottle rules show <rule-id>
[exit 0]

=== proof finished ===
```

## What this does not prove

- **Windows and macOS.** This ran on Linux only. The command-line tests are platform-neutral and the Gateway change
  has no platform branch, but neither has been run on Windows here.
- **PostgreSQL.** The PostgreSQL migration was generated, not applied to a PostgreSQL database. The hosted Gateway
  uses PostgreSQL; `scripts/test-database.ps1` is where that is exercised.
- **A model agent.** The live session ran `RawCli`. The agent recorded is whatever the live roster reports, read the
  same way for every agent; it was not run with Claude Code or Codex.
- **The parked host suite as a whole.** Only the two census classes this change edits were run from it.
- **Nothing prunes the history.** It grows by one row per change, which matches the firing record beside it.

## Seen on the machine, outside this task

- `FactorySeatRoutesTests.Unlink_ByASessionOutsideTheFactory_IsRefusedInThePauseRulesOwnWords_AndNothingChanges`
  failed in 2 of 6 runs of the Rules filter (its name contains "Rules") and passed alone and in the other runs. It is
  in the factory code, which this change does not touch.
- `~/.local/bin/cc-pdf` on devlinux points into a `/tmp/cc-pyinuse-*` folder and was rewritten at 12:38 during this
  run. The test Director's own log shows it used only its scratch HOME. `cc-pyinuse` is the folder name
  `tools/cc-director-setup-engine.Tests/PythonToolsInUseTests.cs` uses, so a run of that suite elsewhere on the
  machine appears to relink the user's real tools folder.
- devlinux is at its per-user limit of 128 file-watch instances. The test Director had to be started with
  `DOTNET_USE_POLLING_FILE_WATCHER=1`, and the test Gateway failed to start once for want of a free instance.
- The self-host Gateway, started on a scratch root, tries to map Tailscale port 443 to itself at start-up; on
  devlinux it was denied for want of operator rights, so nothing changed.
