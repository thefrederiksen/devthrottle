# Brief - Developer, the Cockpit screens (phase D)

You are a Developer on the Factories screen mission. Read `MISSION.md`, `PLAN.md`, the design report
(`DESIGN-report-dcdcdec9-v4.html` - **build what its four mockups show**), and the two other briefs in this folder
(what the Gateway now serves) first. The Implementation Lead (session b496c54d) opened you and is who you report to.
Work only in your own worktree, cut from origin/main after phase B merged. Follow CLAUDE.md (rule 7: the client is
dumb - render the Gateway's words, tones and order verbatim; never count, sort or decide a status in a view),
docs/VisualStyle.md and docs/CodingStyle.md.

## Your task: one pull request - the Cockpit

1. **Sidebar**: "Factory Agents" becomes **Factories**. New routes under `/factories` (list, `/factories/:factory`,
   `/factories/:factory/seats`, and the page's other tabs); every old `/factory-agents...` address redirects to its
   new equivalent. Still behind FactoryAreaGate and the switch.
2. **The list** (mockup 1): tabs Factories, Activity, Reports; one row per factory - name, waiting on you, status
   chip, and the "Talk to <CEO>" button or the "No CEO" text. The "All factory agents" tab is gone. Row click opens
   the factory's page.
3. **A factory's page** (mockup 2): breadcrumb, header (name, status, CEO, seat count, computer with the
   "change - coming" label that is visibly NOT a control, "Talk to <CEO>"), tabs Overview, Seats (n), Activity,
   Reports, Memory (the existing FactoryMemoryTab), Documents (the Gateway's placeholder text). Overview: goal,
   goal number, waiting on you, latest from the CEO, last talk with you.
4. **The Seats tab** (mockup 3): seat, when it runs, last run, computer (+ the coming label), Talk.
5. **Talk** (both buttons): calls the Gateway's talk endpoint; the button shows a busy state at once, then
   navigates to the new session; a refusal shows the Gateway's sentence next to the button. Never a button that
   does nothing silently.
6. **Phone width** (mockup 4): the list collapses to cards (name + status, waiting text, Talk button) with no
   horizontal scroll at 390px; the factory page and Seats tab stay usable at that width.
7. Remove what the new screens no longer use (the old list view, its agent table) together with their tests;
   keep the Activity, Reports, Waiting and Memory pieces that are reused. Remove the Gateway's old `/factories`
   view fields only if nothing else reads them.
8. Tests (vitest, the existing factory test style): list rendering verbatim, the redirects, Talk busy/success/
   refusal, the Seats tab, the phone layout class. Run the web test suites for cockpit and client-core.

## How you work

Commit `type(scope): description` with NO attribution, push, `gh pr create` with before/after screenshots from a
local Cockpit build against fixtures (put them in the mission folder under `qa/`), then
`cc-devthrottle message send b496c54d "<one line>"`. The Lead arranges the review, merges and deploys. Never deploy
yourself.
