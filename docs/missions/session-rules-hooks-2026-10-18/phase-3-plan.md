# Session Rules part 2, phase 3 - the Rules page: plan, rulings and wire contract

Written 2026-10-20 by the phase 3 Tech Lead ("Session Rules - Tech Lead - phase 3 Rules page"). Branch
`mission/session-rules-hooks-p3`, cut from `origin/main` at `0eb3a64bc` (phases 1 and 2 merged: #4268, #4267).
Scope is `mission.md` section 3 goal item 4 (and the page half of item 2), section 5 item 8, section 6 row 3.

## What phase 3 is

A Rules page in the Cockpit, in the left rail after Workflows (ruling A7), and a read-only Rules list and
firing record on the phone. Tabs: All, Screen, Hook, Session start, Fired today, Changed by agents. A rule's
page shows its sentence, scope, mode, handler and firing record. "Preview a machine + repo" shows the
effective rules for that machine and repository and which rule overrides which (most specific wins, D2).

## Rulings (phase 3 only)

**T1. The client is dumb (CLAUDE.md rule 7).** Every label, tone, tab membership, count, empty-state text
and every "decided / overrode" verdict is computed on the Gateway and arrives as a finished string or
boolean. The client lays out what it is given. A conditional in a `.tsx` that decides what a rule's state
MEANS is a defect; move it to the Gateway fold.

**T2. Three new READ-ONLY Gateway routes**, each a fold over the existing store, beside the existing rule
routes in `SessionRuleEndpoints` (the fold itself in its own class under `src/CcDirector.Gateway/Rules/`,
unit tested without HTTP):
- `GET /gateway/rules/page?tz=<IANA zone>` - wire P1.
- `GET /gateway/rules/{id}/page?tz=<IANA zone>` - wire P2.
- `GET /gateway/rules/preview?machine=<name>&repo=<remote, may be empty>&command=<text, may be empty>` - wire P3.
They are NOT opened to session keys: the page is for people; agents already read everything through
`cc-devthrottle rules list | show | history`. `SessionKeyGuard.IsRuleRoute` stays as it is.

**T3. "Today" is the viewer's day.** The client sends its IANA time zone
(`Intl.DateTimeFormat().resolvedOptions().timeZone`) as `tz`. That is an input, not a verdict. The Gateway
works out the day and every time label in that zone. A missing or unknown zone is refused with 400 and a
stated reason - never silently read as UTC.

**T4. "Fired today"** = the rule has at least one firing (screen or hook) whose time falls in the viewer's
day. **"Changed by agents"** = the rule's change history has at least one row made by a session
(`sessionId` set). A deleted rule is on no tab (it is gone); its history is still readable through
`rules history`.

**T5. Session start rules do not exist until phase 5.** The tab is always present, and when empty shows
the Gateway's empty text, which says honestly that rules acting when a session starts are not built yet.

**T6. One resolver, not two.** The preview's decision for a sample command MUST be the hook's decision.
The Gateway compiles the hook's own `src/CcDirector.RuleHook/RuleResolver.cs` and `RulesFile.cs` as linked
source (`<Compile Include="..\CcDirector.RuleHook\RuleResolver.cs" Link="..." />`), so there is one
implementation of P3/P13. A test drives the preview with a command and asserts the deciding rule,
overridden and matched ids are those `RuleResolver.Decide` returns. If linking cannot be made to compile
cleanly, STOP and tell the Tech Lead; do not write a second resolver.

**T7. "Overrides" without a sample command** (the preview's standing view). Hook rule A overrides hook
rule B when they have the same event and tool, their results differ, A wins the P3 rank contest (higher
rank, or the same rank with A deny and B allow), and some match text of A CONTAINS some match text of B -
so every command A matches on that text, B matches too, and A decides it. This is a statement that is
always true between those two rules, never a guess: it is pairwise, so a third, still more specific rule
that also matches a command takes that decision instead (amended after the Gateway review, observation 3).
With a sample command, the decision is T6's, exactly. Screen rules appear in
the preview (in phase 1 they can only be `everywhere`) but never override or are overridden by hook rules.

**T8. Shared code lives in `packages/client-core/src/rules/`** - the client, the list, the rule page, the
firing record, the preview - mounted by both shells, the Settings shape (CLAUDE.md rule 8): one component,
two shells. Each shell supplies only its frame. The Cockpit mounts list, rule page and preview; the phone
mounts list and rule page (read-only; there are no write controls on either surface in phase 3).

**T9. Errors are shown, never hidden.** A Gateway error shows the error state with the Gateway's `error`
text when it sent one, and the HTTP status otherwise, using client-core's existing error display. A
loading state shows at once (CLAUDE.md rule 1).

**T10. Style.** `docs/VisualStyle.md`, `docs/CockpitVisualStyle.md`, `docs/CodingStyle.md`, and the
surrounding Workflows and Skills pages for shape.

## The wire contract

Build to these exactly. A change to one is a change to this file first, agreed with the Tech Lead. Every
`*Label`/`*Text` is a finished English string. `tone` is one of `neutral`, `live`, `warn`, `deny`,
`allow` - the client maps a tone to a style token, nothing more.

### P1. The Rules page - `GET /gateway/rules/page?tz=Europe/Copenhagen`

```json
{
  "today": "Monday 20 October",
  "tabs": [
    { "id": "all",               "label": "All",               "count": 3, "emptyText": "No rules yet. ..." },
    { "id": "screen",            "label": "Screen",            "count": 1, "emptyText": "..." },
    { "id": "hook",              "label": "Hook",              "count": 2, "emptyText": "..." },
    { "id": "session_start",     "label": "Session start",     "count": 0, "emptyText": "No session start rules. Rules that act when a session starts are not built yet." },
    { "id": "fired_today",       "label": "Fired today",       "count": 1, "emptyText": "No rule has fired today." },
    { "id": "changed_by_agents", "label": "Changed by agents", "count": 1, "emptyText": "No agent session has changed a rule." }
  ],
  "rules": [
    {
      "id": "3f2c...",
      "sentence": "Never run rm -rf in this repository.",
      "kindLabel": "Hook",
      "triggerLabel": "Before a shell command",
      "scopeLabel": "In github.com/acme/web on devlinux",
      "modeLabel": "Dry run",
      "modeTone": "neutral",
      "handlerLabel": "Match \"rm -rf\" - deny",
      "firedLabel": "Fired 3 times today, last at 10:42",
      "changedLabel": "Changed by session 8a1e... (claude_code) at 09:15",
      "tabs": ["all", "hook", "fired_today", "changed_by_agents"]
    }
  ]
}
```

**Amendment (after the first reviews, 2026-10-20):** P1 also carries `"previewSuggestions": { "machines": [...],
"repos": [...] }`, the same lists as P3's `suggestions`, so the preview form can offer them before the first
preview runs. `changedLabel` names the action the agent took ("Created by", "Changed by", "Promoted by") and
writes "session" in lower case, as above.

Rules newest first. `firedLabel` is never blank ("Never fired" when there is no firing).
`changedLabel` is blank when no session changed the rule. Tab order and labels are exactly as above.

### P2. One rule's page - `GET /gateway/rules/{id}/page?tz=...`

404 `{ "error": "..." }` for a rule that does not exist (as the other rule routes answer).

```json
{
  "rule": { "...every P1 rule field..." },
  "fields": [
    { "label": "Sentence", "value": "Never run rm -rf in this repository." },
    { "label": "Kind",     "value": "Hook - before a shell command" },
    { "label": "Scope",    "value": "In github.com/acme/web on devlinux (most specific: repo on machine)" },
    { "label": "Mode",     "value": "Dry run - records what it would have done; nothing is blocked" },
    { "label": "Handler",  "value": "Match: the command contains \"rm -rf\". Result: deny" },
    { "label": "Promoted by", "value": "Not promoted" },
    { "label": "Created",  "value": "18 October at 14:02" }
  ],
  "firings": [
    {
      "id": "...",
      "whenLabel": "Today at 10:42",
      "decisionLabel": "Would have denied",
      "tone": "deny",
      "sessionLabel": "Session 8a1e... (claude_code)",
      "whereLabel": "devlinux - github.com/acme/web",
      "subject": "rm -rf build",
      "overrodeLabel": "Overrode 1 rule",
      "overrode": [ { "id": "...", "sentence": "Allow anything in this repository." } ],
      "outcomeLabel": "Dry run: recorded what this rule would have decided; the command was not blocked."
    }
  ],
  "firingsEmptyText": "This rule has not fired yet.",
  "changes": [
    { "whenLabel": "Today at 09:15", "actionLabel": "Changed", "byLabel": "Session 8a1e... (claude_code)" }
  ],
  "changesEmptyText": "No changes recorded."
}
```

`fields` is ordered and complete: the client renders it as a list and adds nothing. A screen firing fills
`subject` with the screen text it read (cut as the Gateway sees fit) and `overrode` empty. An overridden
rule that has since been deleted is named "(deleted rule <id>)".

### P3. Preview a machine + repo - `GET /gateway/rules/preview?machine=devlinux&repo=git@github.com:acme/web.git&command=rm%20-rf%20build`

`machine` is required (400 with a reason otherwise); `repo` is normalised with `GitRemoteIdentity.Normalise`
and may be empty; `command` may be empty.

```json
{
  "machine": "devlinux",
  "repo": "github.com/acme/web",
  "headline": "3 rules reach devlinux in github.com/acme/web.",
  "rules": [
    {
      "id": "...",
      "sentence": "...",
      "kindLabel": "Hook",
      "scopeLabel": "In github.com/acme/web",
      "rankLabel": "Repo (rank 3)",
      "resultLabel": "Allow",
      "modeLabel": "Dry run",
      "overrides":    [ { "id": "...", "sentence": "Never run rm -rf." } ],
      "overriddenBy": [],
      "verdictLabel": "Overrides 1 rule: \"Never run rm -rf.\""
    }
  ],
  "emptyText": "No rule reaches devlinux in github.com/acme/web.",
  "decision": {
    "command": "rm -rf build",
    "verdictLabel": "Would allow",
    "tone": "allow",
    "summary": "\"Allow rm -rf in this repo.\" (repo) decides. It overrode \"Never run rm -rf.\" (everywhere).",
    "decidingRuleId": "...",
    "overriddenRuleIds": ["..."],
    "matchedRuleIds": ["...", "..."]
  },
  "suggestions": { "machines": ["devlinux", "SOREN_NORTH"], "repos": ["github.com/acme/web"] }
}
```

`rules` most specific first. `decision` is null when no command was given; when a command matches nothing,
`decision.verdictLabel` says no rule matched and the ids are empty. `suggestions` are the machines and repos
named by the account's rules and recorded firings, so the form can offer them; free text is still allowed.

## The work, one Developer each

| Seat | Task | Worktree / branch |
|---|---|---|
| A | Gateway: the fold, the three routes, T6 linked resolver, unit tests (fold without HTTP, routes through the existing endpoint test harness), the 400/404 cases. | `../devthrottle-session-rules-p3-gateway`, `mission/session-rules-hooks-p3-gateway` |
| B | client-core `rules/` (client, list, rule page, firing record, preview), Cockpit rail + routes + icon, phone routes + its entry point. Built against P1-P3 with test fakes, in parallel with A. Vitest for client-core, cockpit, mobile. | `../devthrottle-session-rules-p3-pages`, `mission/session-rules-hooks-p3-pages` |
| C | After A and B are merged into the phase branch: the QA run against a real Gateway with real rules, screenshots at desktop and phone width, the flow and the failure cases. Writes the screenshot half of `phase-3-proof.md`. | `../devthrottle-session-rules-p3-qa`, `mission/session-rules-hooks-p3-qa` |

## The check (phase 3)

- Gateway: `~/.dotnet/dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Rules"`
  green, including `RulesTypeNothingGuardTests` and the phase 2 guards. `src/CcDirector.RuleHook.Tests` green.
- Web: vitest for `packages/client-core`, `apps/cockpit`, `apps/mobile` (`--minWorkers=1 --maxWorkers=2`),
  and the type check of each.
- QA (seat C): screenshots, desktop (1440 wide) and phone (390 wide), of: the list on every tab; a rule
  with firings, including one that overrode another; a rule with no firings; the Session start tab's empty
  state; the preview with an override and with a sample command; a Gateway with no rules; a Gateway error.
  Against a real Gateway (the phase 1 rig, `scripts/session-rules-rig.sh`, or a self-host Gateway built
  with the Cockpit) holding real rules created with `cc-devthrottle rules`.
