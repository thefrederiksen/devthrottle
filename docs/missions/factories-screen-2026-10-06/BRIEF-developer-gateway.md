# Brief - Developer, Gateway data and views (phases A and B)

You are a Developer on the Factories screen mission. Read `MISSION.md`, `PLAN.md` and the design report
(`DESIGN-report-dcdcdec9-v4.html`) in this folder first. The Implementation Lead (session b496c54d) opened you and
is who you report to. Work only in this worktree: `D:/ReposFred/_wt/factories-screen-gw`, branch
`factories-screen/gateway-data`, cut from origin/main. Follow the repository's CLAUDE.md, docs/CodingStyle.md
and docs/axi-standard.md (the command-line standard). Rule 7: every word, tone and order on screen is folded on
the Gateway; the client renders it verbatim.

## Your task: two pull requests, in order

### Pull request A - the factory registry and the goal number

1. **Registry store** (Gateway, PostgreSQL via the existing DbContext, a migration): one row per (account,
   factory id) with title, folder (absolute path on its computer), computer (machine name), CEO seat id (nullable),
   goal text (nullable), goal approved date (nullable), registered by / when, and its seats: seat id, display name
   (e.g. "Nora Hale"), role (e.g. "CEO", "Savings Engineer"), brief file (relative to the folder), the schedule ids
   that run it, and computer. Re-registering a factory replaces its row (seats included).
2. **Goal number store**: posts per factory - value text, unit, as-of date, link to how it was measured, posted by
   (seat id), posted when. Keep every post; the newest is the one shown.
3. **Endpoints** under the existing factory area (`/gateway/factory-agents/...`, behind FactoryAgentsGate and the
   `factoryAgents.enabled` switch, tenant-scoped). Remember SessionKeyGuard: a new route that a session's key must
   reach (the commands below run inside sessions) has to be allowed there - see memory "adding a gateway route does
   not add it to SessionKeyGuard" - and prove it with a test that calls the route with a session key.
4. **Commands** in `cc-devthrottle factory`, AXI-compliant:
   - `factory register --manifest <file>` - a JSON or YAML manifest (your choice, document it) holding the fields
     above; when it names a goal file (relative to the folder) the command reads it and sends its text. Prints the
     registered factory; non-zero exit when refused.
   - `factory list` - the registered factories (AXI: count, no truncated ids, `--json`).
   - `factory goal-number post --factory <id> --value <text> --unit <text> --date <YYYY-MM-DD> --link <url>`
     (posted by: the calling session's factory agent when known, else a `--by` flag), and
     `factory goal-number show --factory <id>`.
5. Tests: store, endpoints (including the session-key route proof), and the Python command tests.

### Pull request B - the views the Cockpit will render

New DTOs in `CcDirector.Gateway.Contracts` and folds in `FactoryAgentsFold` (or a sibling fold), pure and unit
tested, served by new GET endpoints:

- **The list** (Factories tab): one row per registered factory - title, status word + tone, "waiting on you" text
  ("1 question", "2 decisions", "-" ...), the CEO's name for the button ("Talk to Nora Hale"; "Talk to the CEO" when
  two registered CEOs share a name; null and a "No CEO" text when it has none), and the factory page href. Status
  is exactly one of FAILING, NEEDS YOU, PAUSED, RUNNING with the rules in PLAN.md decision 5; sorted worst first,
  then by title. Tabs: Factories, Activity, Reports (the "All factory agents" tab is gone).
- **A factory's page**: header (title, status, CEO name, seat count text, computer, "Talk to <CEO>"); overview:
  goal text and "Only you change the goal. Approved <date>." (or "No goal set yet"), the newest goal number with
  "posted by <name>, <when>", value + unit, and the measured-how link (or "No number posted yet"), the waiting items
  for that factory, the CEO's latest activity lines (newest few, from the CEO seat's rows), and a "Last talk with
  you" line (leave a field for it; phase C fills it from `talked` rows - if you can, fold it already from rows whose
  outcome is `talked`). Tabs: Overview, "Seats (n)", Activity, Reports, Memory, Documents (Documents carries the
  text that definitions are not on the Gateway yet - no fake content).
- **The Seats tab**: one row per registry seat - name, role, "when it runs" (from its schedules' cron and time zone,
  in plain words: "Daily 06:15", "06:00 and 18:00", "Wednesday 05:30"), last run and how it ended ("Today 06:20 -
  succeeded", "Not run yet"), computer, a "change" label marked as coming (decision 7 - it must be visibly not a
  control: a field like `computerChangeText: "change - coming"`), and the Talk target (factory id + seat id).
  Activity-row names that are not registry seats never appear.
- The old `/factories` view and its routes keep working until the Cockpit moves (phase D removes what it no longer
  uses), so nothing on main breaks between pull requests.

## How you work

- Run `.\scripts\test-local.ps1` and, because you touch the Gateway, `.\scripts\test-local.ps1 -Parked` (needs
  Docker) or at least the Gateway unit and Gateway test projects for your area; run the Python command tests.
- Commit `type(scope): description`. NO attribution of any kind (no Co-Authored-By, no "Generated with", no
  assistant name). Push the branch and open the pull request with `gh pr create`; then report to the
  Implementation Lead with `cc-devthrottle message send b496c54d "<one line: PR number, what it holds, test
  results>"`. Do not arrange your own review and do not merge - the Implementation Lead does both.
- Answer every review finding the Lead passes you: fix it, or decline it with the reason.
- After pull request A is merged, rebase on origin/main and do B on a new branch from origin/main in this same
  worktree.
- Never guess on something undecidable: message the Lead and carry on with the rest. Foreground only, no hidden
  sub-agents. Never deploy, never touch a factory's schedules.
