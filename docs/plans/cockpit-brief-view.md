# Cockpit Brief View - the session page that replaces the terminal

**Status:** BUILT + live-verified (all 4 phases, 2026-06-04)
**Owner:** Cockpit workstream
**Mockup history:** `docs/features/cockpit-session-briefing/MOCKUPS.html` (3 earlier options, all rejected
because they kept the terminal visible)

---

## 1. The problem (the owner's words, condensed)

The owner operates sessions at the **decision level**, not the terminal level. Coming back to a session,
the only things that matter are:

1. **What I asked** (the goal / the last ask)
2. **What Claude did** about it
3. **What is needed of me** right now

Today the terminal forces him to do the summarizing himself: scroll, re-read two pages of Claude's
prose, find the question buried at the bottom. He keeps telling Claude Code to "be more succinct"
because the UI makes verbosity expensive. Wrong fix - the UI should absorb the verbosity.

**Decision made:** the session detail page becomes a full-page **Brief** (no terminal visible).
The terminal is demoted to a tab for the rare cases that need it (slash-command UI, permission
prompts, watching raw output). Content comes from the **Claude Code transcript (JSONL)** - real
prompt text, real reply markdown - NOT from screen-scraping the TUI.

## 2. The page

### Needs-you state

```
+------------------------------------------------------------------------------------+
| (*) webapp - cards redesign        WAITING FOR YOU - 14m      [Brief|Terminal]  < 3/6 > |
|     GOAL: Redesign the Create-New launcher - bigger cards, images,    2h - 9 turns  |
+------------------------------------------------------------------------------------+
|  YOU ASKED                                                                  14:49   |
|  "Can you give me some alternative layouts - I think the cards should be bigger    |
|   so we can have an image on each that describes what it does."                    |
|  --------------------------------------------------------------------------------  |
|  CLAUDE DID                                                  14:51   [full reply v] |
|   * Compared the shipped launcher against your wireframe                           |
|   * Proposed 3 changes: describe-in-chat first, larger 3-col cards, inline page    |
|   * Changed NOTHING yet - explicitly waiting for your approval                     |
|  --------------------------------------------------------------------------------  |
|  !! NEEDS YOU - Claude's words:                                                     |
|  |  "Approve 1+2 (and your verdict on 3) and I'll make the changes while the       |
|  |   server's up so you can refresh and re-judge."                                 |
|  [ Approve 1+2 ]   [ Approve all 3 ]   [ (mic) Speak ]                              |
+------------------------------------------------------------------------------------+
| > Reply...                                                          [Send]  [Queue] |
+------------------------------------------------------------------------------------+
```

### Working state

Same header; body shows YOU ASKED + "CLAUDE IS DOING (live)" (latest condensed bullets + the live
spinner line + elapsed) + "Nothing needed from you. Next that does: [ Go to ... > ]".

### Rules (locked)

- Three blocks, fixed order, fixed positions: ASK / DID / NEEDS YOU.
- **DID is condensed; NEEDS YOU is verbatim.** A paraphrased question is the one thing that cannot
  be trusted (wingman fidelity rule). The verbatim text must be an exact substring of the reply.
- `[full reply v]` expands Claude's complete answer as **rendered markdown** (readable typography,
  not a terminal grid).
- `< 3/6 >` flips through the needs-you sessions in triage order (the conveyor belt).
- Terminal stays one tab away, unchanged.

## 3. What already exists (verified 2026-06-04)

| Need | Source | Status |
|---|---|---|
| Last user prompt + last assistant reply (exact text) | `GET /sessions/{sid}/summary` -> `LastUserPrompt`, `LastAssistantText`, `TurnCount` (parses the Claude JSONL via `ClaudeSessionReader` + `StreamMessageParser` + `SummaryBuilder`) | EXISTS |
| Full turn list / widgets | `GET /sessions/{sid}/turns` | EXISTS |
| Open todos, files touched, recent commands | `/summary` (`OpenTodos`, `FilesTouched`, `RecentCommands`) | EXISTS |
| Session state, needs-you ordering | Gateway envelope + Cockpit triage view | EXISTS |
| Reply / queue / interrupt / speak | composer paths | EXISTS |
| Recap (long-form), turn summaries | `/recap` (fixed in 209a509), `/turn-summaries` | EXISTS |
| **Goal = FIRST user prompt** | not exposed (SummaryBuilder only keeps the last) | NEW (small) |
| **Condensed "Claude did" bullets** | nothing suitable (wingman briefing is opt-in + continuous) | NEW |
| **"Needs you" verbatim extraction** | wingman briefing has it but only when wingman is on | NEW |

## 4. Design decisions

- **D1. Condensation engine: direct OpenAI nano-tier HTTP call (like `CleanupOrchestrator` /
  voice cleanup), NOT a `claude --print` side-spawn.** Issue #142 already proved the cold-spawn
  costs seconds and times out; the Brief renders on every session flip, so ~1s latency is the
  budget. One call returns JSON: `{ didBullets: [..], needsYouVerbatim: "..." | null }`.
  `needsYouVerbatim` is validated server-side as an exact substring of `LastAssistantText`; if the
  model fails the substring check, fall through to showing the reply's last paragraph verbatim.
- **D2. Cache by turn count** (same pattern as `RecapCache`): condense once per turn, every flip
  hits the cache. Generate eagerly when a turn completes (TurnSummaries hook) so the page is
  instant; lazily on first view otherwise.
- **D3. Brief works WITHOUT the wingman.** No opt-in, no opus, no continuous loop.
- **D4. Old-Director degrade:** `/brief` 404 -> Cockpit builds the page client-side from
  `/summary` (raw `LastUserPrompt` + full `LastAssistantText`, no bullets, last paragraph as
  the needs-you block). `/summary` also missing -> Terminal tab is the view. No blank pages.
- **D5. Brief is the DEFAULT tab** when a session is selected; the chosen tab persists per session
  within the circuit.

## 5. Phases

### Phase 1 - Director: `GET /sessions/{sid}/brief`
- Extend `SummaryBuilder` with `FirstUserPrompt` (+ timestamp, and timestamp on the last exchange).
- New `BriefBuilder` (Core/Claude): takes the summary, calls the nano condenser (D1), validates the
  verbatim substring, caches by turn count (D2), eager-refresh on turn completion.
- Endpoint returns: goal, lastAsk{text,at}, did{bullets[],at}, needsYou{verbatim}|null,
  fullReplyMarkdown, turnCount, activityState, status/error (explicit, no silent degrade).
- Tests: builder unit tests (fixture JSONLs), substring-validation test, cache-staleness test,
  endpoint 404/no-session-id/no-jsonl paths.

### Phase 2 - Cockpit: the Brief page
- Center pane becomes `[Brief | Terminal]` tabs, Brief default (D5); TerminalPane unchanged behind
  the tab (connect lazily on first open to skip the stream cost for never-opened terminals).
- `BriefPane.razor`: the three blocks, full-reply markdown expander (use a Razor markdown renderer,
  e.g. Markdig -> sanitized HTML), working-state variant fed by the existing 2s poll + turn
  summaries, needs-you `< n/m >` navigation in triage order, composer unchanged below.
- Quick-action buttons v1: [Speak] + [Reply]; option-detection buttons ("Approve 1+2") deferred to
  Phase 4 (needs option parsing).
- Degrade ladder per D4.

### Phase 3 - Verify + roll
- Live E2E on a slot-5 Director: fresh session, multi-turn, confirm Brief correctness vs the
  terminal truth at every state (working / waiting / idle / no-jsonl / unlinked).
- The endpoint rides the existing "new Director build" train (with recap fix, attach nudge, replay
  cap, compression). Note in HANDOVER that Directors must relaunch to serve /brief.

### Phase 4 - Power polish (separate go-ahead)
- Option detection -> one-click answer buttons (numbered choices like AskUserQuestion footers).
- Auto-advance after replying (FIFO behavior from the Android conveyor belt).
- Phone: the Brief IS the phone session page eventually (Android track owns that port).

## 6. Risks / edges

- **Unlinked sessions** (no ClaudeSessionId yet): degrade per D4; relink endpoint exists.
- **Compacted/forked JSONL**: SummaryBuilder already parses what Claude writes; first-prompt
  extraction must tolerate a transcript that starts mid-conversation (label "earliest available").
- **Multi-question replies**: v1 shows the single verbatim block (model picks the operative ask,
  substring-validated); the full reply is one click away.
- **OPENAI_API_KEY absent** on a Director: brief returns did=raw + needsYou=last paragraph and an
  explicit `condenser: "unavailable"` field - visible degrade, not silent.
