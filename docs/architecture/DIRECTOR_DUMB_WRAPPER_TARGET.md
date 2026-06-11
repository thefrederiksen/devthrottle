# Hands, Brain, Face: Target Architecture (Director / Gateway / Cockpit)

**Status:** PLANNED (with a CURRENT violation inventory in section 4)
**Date:** 2026-06-11
**Audience:** Anyone deciding where a new feature lands (Director vs Gateway vs Cockpit), and anyone implementing the migration tracks in section 9. The Product Agent should use section 3 as the routing test when sharpening issues.

## Related documents

- [gateway/GATEWAY_DIRECTOR_ARCHITECTURE.md](gateway/GATEWAY_DIRECTOR_ARCHITECTURE.md) - CURRENT state of the Gateway/Director split
- [gateway/GATEWAY_DIRECTOR_TARGET.md](gateway/GATEWAY_DIRECTOR_TARGET.md) - Phase 1 thin-receptionist target (superseded in spirit by this doc: the Gateway has since grown the brain, the proxy legs, and work lists, and this doc embraces that)
- [gateway/GATEWAY_DIRECTOR_RESPONSIBILITIES.md](gateway/GATEWAY_DIRECTOR_RESPONSIBILITIES.md) - feature-by-feature decision matrix
- [cockpit/COCKPIT_DESIGN.md](cockpit/COCKPIT_DESIGN.md) - the Cockpit as the primary UI
- [../PHILOSOPHY.md](../PHILOSOPHY.md) - "anything deterministic is code; only parts requiring judgment are prompts"
- [../wingman](../wingman) - Wingman invariants (strong model, read-only side-calls, one write chokepoint)
- [../plans/plan-voice-mode.md](../plans/plan-voice-mode.md) - earlier voice mode plan (per-session phases; this doc adds the fleet-wide mode)

---

## 1. The vision

**One person, anywhere - even driving - runs a fleet of AI agents across multiple machines and multiple products, making only the decisions that need a human, with every tool the system can give them to make those decisions well.**

- **The Cockpit is where the human lives.** Log in from anywhere; see every session on every machine for every product. Its job is *cognitive load reduction*: it tells you which sessions need you, briefs you on what each one wants, recommends an answer, and lets you respond at whatever depth the moment allows - a tap, a sentence, a dictated reply, or full voice. Supervising 15 agents should feel like a briefing call, not 15 terminals.
- **The Wingman is the human's decision-support staff.** Not a status summarizer - it understands what each session is asking, gathers the context you would want, recommends an answer with reasons, and presents real options. The agents do the work; the Wingman's job is making your 10 seconds of judgment as well-informed as 10 minutes of reading would have been.
- **The Director is the hands - and the ultimate fallback.** A dumb, rock-solid, machine-local runtime that hosts sessions and tools. The desktop app stays **indefinitely**, deliberately small: the Terminal tab and the Source Control tab - raw metal you can always drop to when the smart layers misbehave, where *any* agent (not just Claude Code) can run in plain terminal mode. It never depends on the Gateway or Cockpit to function.
- **Voice closes the loop.** Per-session voice (talk to one agent) and fleet-wide Cockpit voice (one conversation that walks you through everything that needs you and reads you status) - the car mode.

### The trust ladder

```
raw terminal (always there)
  -> desktop Director fallback (Terminal + Source Control)
    -> Cockpit terminal (remote, typeable, reliable)
      -> Cockpit briefs and buttons
        -> voice (per-session, then fleet-wide)
```

Each rung must actually work before the rung above it is trustworthy. You can only manage from the car if you stopped needing the desk one verified rung at a time. This ordering is binding on the migration plan (section 9): **reliability before intelligence, intelligence before voice.**

---

## 2. The shape

> **Director = hands. Gateway = brain. Cockpit = face. Desktop = the escape hatch that never moves.**

The Director is a well-tooled but deliberately dumb machine-local runtime. The Gateway is the single place where interpretation, judgment, and orchestration live. The Cockpit is the primary UI a human works in. The desktop Director app is the permanent fallback surface - small, raw, and independent of everything above it.

This is the end-state the codebase has been drifting toward without naming it: the Gateway already hosts the warm brain (`GatewayTurnBriefAgent` + `BrainSupervisor`), already proxies the per-session WebSocket/HTTP legs (issue #268, #317), already owns work lists and the queue runner (issues #270/#273/#274), and already pushes `AssessedState` down to Directors (issue #186). This doc declares the rule so it can veto the remaining exceptions instead of letting them ride.

### Why dumbness is the goal

1. **Fungible workers.** A Director with no opinions is stamp-out installable: install, point at Gateway, pull keys from the vault, done. Adding a machine to the fleet adds capacity, not configuration.
2. **Independent release cadence.** The smart layer (Gateway/Cockpit) is the layer the agent pipeline modifies most. A dumb Director with a stable contract can lag the Gateway by months without breaking. The oversight layer iterates daily; the custody layer stays boring.
3. **One brain, one truth.** Two interpreters of session state (Director quiet-timer vs Gateway assessment, issue #186) means two answers to "does this session need me?". One brain means one answer.
4. **Cost.** One warm Opus process per fleet replaces N Directors' worth of cold `claude --print` side-call spawns.
5. **A trustworthy fallback.** The escape hatch only works if it is too simple to break. Keeping the desktop surface small and judgment-free is what makes it the place you go when nothing else works.

---

## 3. The boundary rule

Every feature, issue, and PR gets sorted by one sentence:

> **The Director may do anything deterministic that requires *this machine*. It may never make a judgment call, and its UI never grows beyond the fallback surface (Terminal + Source Control).**

The Director keeps exactly four jobs:

| Job | Contents | Why it cannot move |
|---|---|---|
| **Process custody** | ConPTY, session lifecycle, persistence/recovery (`SessionStateStore`, `DirectorCrashJournal`), resize, kill | The processes live here |
| **Raw I/O** | Terminal buffer serving, keystroke injection (`Session.SendInput`), screenshots, image upload, dictation audio capture | Bytes in, bytes out; latency-sensitive |
| **Machine facts** | Git plumbing, repo listing, filesystem, hardware info, exe/slot inventory, tool inventory | Facts about *this* machine |
| **Tool hosting** | The 30+ cc-* CLIs on PATH, executed locally | They need local browser profiles, Outlook, the desktop, the GPU |

**The custody layer is agent-agnostic.** ConPTY, buffers, keystrokes, git, and tools do not care what process runs in the terminal. Any CLI agent (Claude Code, Codex, Aider, a plain shell) must be runnable in a Director session in raw terminal mode. Claude Code-specific knowledge (turn detection heuristics, prompt formats, JSONL transcripts) belongs in the interpretation layer at the Gateway, keyed by agent type - never baked into custody.

Everything that **interprets, decides, summarizes, schedules, or recommends** belongs above the line:

- Interpretation of session state (working / needs-you / done / stuck) -> Gateway
- Turn briefs, explain, recap, summaries, recommendations -> Gateway brain
- Wingman action *decisions* -> Gateway brain (execution stays below, see 4.1)
- Comm-queue approval state, send-timing policy, jitter -> Gateway
- Work sequencing, claims, escalation -> Gateway (work lists / queue runner)
- Every human-facing surface *except the fallback tabs* -> Cockpit

The Director's only "state opinion" today is the dumb silence timer (`TerminalStateDetector`). Under this rule it is demoted from "the badge" to "a raw event the Gateway interprets" - surviving locally only as the degraded-mode fallback (section 6).

---

## 4. Violation inventory (CURRENT, 2026-06-11)

These are the places the current code disagrees with the target. Each maps to a migration track in section 9.

### 4.1 The Wingman brain runs in the Director

`WingmanService` (`src/CcDirector.Core/Wingman/`) spawns its own Opus side-calls for turn summaries, explain briefings, rule checks, goal assessment, and `DecideSessionActionAsync`. Meanwhile `GatewayTurnBriefAgent` does overlapping interpretation at the Gateway - which is exactly why issue #186's dual-owner tension exists.

**The fix splits the Wingman along its own existing seam:**

- **Decide** (LLM, read-only, tool-less) -> moves to the Gateway brain. The brain already pulls widgets and terminal tail over REST to build TurnPackages; explain/recap/summarize/decide are the same shape.
- **Execute** (`WingmanActionExecutor`) -> **stays in the Director.** It is mechanical: it receives a structured `WingmanAction` and turns it into bytes via `Session.SendInput`. All existing invariants survive unchanged - single write chokepoint, audit log, idempotency cooldown (`LastActedScreenHash`), self-injection guard. They are enforcement, not intelligence.

End state: the Director exposes a dumb `POST /sessions/{sid}/execute-action` verb; nothing in `CcDirector.Core` ever spawns claude.exe. Section 7 covers what the relocated Wingman *becomes*.

### 4.2 Two owners of session state

`TerminalStateDetector` + `SessionStatusWingman.ColorFromActivityState` produce a Director-local badge; the Gateway pushes `AssessedState` down as an override. Two interpreters, coordination complexity, and the known coarse-signal problem (10s silence cannot distinguish done / stuck / awaiting-permission - all go red).

**Fix:** Director emits raw activity transitions as events; the Gateway's `AssessedState` becomes the *only* interpreted state; every surface (Cockpit and the desktop fallback) renders what the Gateway says. The raw timer survives only as the degraded-mode badge when no Gateway is reachable (section 6).

### 4.3 The desktop app is a second smart UI (instead of a small permanent fallback)

`MainWindow.axaml.cs` (~5,000 lines), `CommManagerView` approval overlay, the Wingman tab, Agent View - a parallel cockpit that drifts from the real one.

**Fix - and this is a scope-down, not a retirement:** the desktop Director app is **permanent**. Its target scope is exactly two first-class surfaces:

1. **Terminal tab** - raw metal. Reviewed and improved as the ultimate fallback: rock-solid rendering, full keystroke fidelity, any agent in plain terminal mode. When the smart layers misbehave, this is where the user goes, so its quality bar is *higher* than convenience features, not lower.
2. **Source Control tab** - the existing git view (status, staged/unstaged, diff, open-in-editor). Machine facts rendered plainly; no interpretation.

Everything else in the desktop app (comm approval, wingman tab, agent view, settings beyond local diagnostics) migrates to the Cockpit and is then removed from Avalonia. New human-facing UI lands in the Cockpit, or it doesn't land - with the single exception of improvements *within* the two fallback tabs' scope.

### 4.4 The Engine half-belongs

`CcDirector.Engine` mixes machine-bound execution with orchestration policy:

- **Dispatch execution** (shelling to cc-gmail / cc-outlook / cc-browser) is machine-bound -> stays on the Director machine.
- **Queue state, approval state, scheduling decisions, send-timing/jitter policy** -> moves to the Gateway. The Gateway is also the always-on box, so scheduled sends stop depending on an Avalonia instance happening to be running (today: "when no Avalonia instance is running, nothing sends" - accidental coupling this split eliminates).

End state: the Gateway ticks, decides "send item X now via machine Y," and POSTs a mechanical `dispatch` verb to the owning Director.

### 4.5 Directors carry their own credentials

The Gateway `KeyVault` (`KeyVault.cs`, `VaultEndpoints.cs`) is built but Directors still use env vars / local credentials.

**Fix:** Directors pull keys on demand. This is what makes a fresh machine "install Director, point at Gateway" with zero further setup - the payoff of fungibility.

### 4.6 The Cockpit's foundation rung is not solid

The remote Cockpit terminal is not truly working on non-Gateway machines: `TerminalPane` is one-shot (no reconnect, no connecting-state), dictation failures surface as a generic "Connection closed." (#226), Cockpit logs are discarded (#199), and the optimistic-UI epic (#280) is open. Per the trust ladder, **this rung blocks everything above it** - briefs, recommendations, and voice are only trustworthy on top of a terminal the user never doubts.

---

## 5. The contract between the layers

The Director's entire API surface must be describable in three nouns. The contract types live in `CcDirector.Gateway.Contracts` (where `CockpitWsUrls` / `CockpitShotUrls` already live), and changes to the contract get PR-review gravity regardless of how small the diff is - the contract's stability is what buys the independent release cadence.

| Surface | Contents | Direction |
|---|---|---|
| **Facts** | sessions, buffers, git status, screenshots, tool inventory, machine info, exe/slot inventory | Gateway pulls, or Director pushes snapshots |
| **Events** | raw activity transitions, turn-end doorbells, session exit, prompt-detected, dispatch-completed | Director pushes (grow the existing doorbell + heartbeat-snapshot mechanism into the SSE/WS event hub already sketched in GATEWAY_DIRECTOR_RESPONSIBILITIES Phase 2.1) |
| **Verbs** | send-input, interrupt, escape, resize, create/kill session, execute-action, dispatch-comm, run-tool, pull-keys | Gateway/Cockpit invoke; every verb is mechanical |

Three consequences worth stating explicitly:

1. **Push becomes load-bearing.** Once all interpretation is at the Gateway, polling every Director for everything will not scale past a handful of machines. The event hub stops being a nice-to-have and becomes the nervous system - and it is also what fleet voice (section 8) consumes.
2. **A verb may not contain a decision.** If a verb's implementation needs to choose between behaviors based on session content, the choice was made too low - restructure so the caller passes the choice in.
3. **Facts and verbs are agent-agnostic.** Nothing in the contract may assume the session runs Claude Code. Agent-specific interpretation lives at the Gateway, keyed by an agent-type fact the Director reports.

---

## 6. Degradation story

Dumbness must never mean dependency. A Director with no Gateway configured (or an unreachable one) must still work as a complete local terminal manager:

- Sessions run; the desktop Terminal and Source Control tabs are fully functional; keystrokes flow; persistence and crash recovery work; any agent runs in raw terminal mode.
- The raw silence timer drives a local badge (the only place the Director is allowed an opinion, and only because nobody smarter is home).
- No briefs, no assessments, no scheduled sends, no wingman actions, no voice. Fine.

What the Director must never do is *block* on the Gateway. Conversely, the Gateway being the single brain is acceptable because it is the always-on box, and the Cockpit on top of it is stateless - the oversight layer can restart freely without touching work in progress (proven property; keep it). The desktop fallback is the floor under all of it: every layer above the Director can break simultaneously and the user still has raw metal.

---

## 7. The Wingman: from status reporter to decision-support staff

Moving the Wingman's brain to the Gateway (4.1) is a relocation. This section is the *promotion* that rides along with it. Target capability, in increasing order of ambition:

1. **Faithful triage (mostly shipped).** What is this session doing; does it need you; what is it asking. Turn briefs with `NeedsYou` / `AllClear`. Already contract-validated with honest degrade tiers - keep that property absolutely.
2. **Informed options (next).** When a session needs a decision, the Wingman assembles what the human would have gone looking for: the exact question, the relevant diff or output excerpt, what changed since the last decision, and the risk if the answer is wrong. Presented as real options with a **recommendation and the reason for it** - "Option B; it matches how you handled the same prompt in session X" - never just a transcription of the agent's question.
3. **Cross-session awareness.** The Wingman sees the fleet, the human's one set of eyes doesn't. It should notice and say: two sessions are about to collide on the same file; session 7's question is the same one you answered an hour ago in session 3; three sessions are blocked on the same broken build.
4. **Learned judgment, never autonomous judgment.** The Wingman may learn the user's patterns (from the decision log it accumulates) to make better *recommendations*. The actuation invariants do not loosen: strong model only, read-only side-calls, one write chokepoint (`WingmanActionExecutor`), request-driven, fail-closed. A better-informed advisor, not a more autonomous one. Auto-acting on a recommendation requires the same explicit per-action human consent it requires today.

Every Wingman improvement is measured against one metric: **how good is the human's decision per second of human attention spent.**

---

## 8. Voice: the car mode

Voice is the top rung of the trust ladder and the proof of the vision. Two modes, one pipeline:

1. **Per-session voice (foundation exists).** Talk to one agent: dictation in (Director captures audio - raw I/O; Gateway/Whisper transcribes and cleans - interpretation), brief/summary read back out (TTS). Already partially shipped via the dictate leg and turn summaries.
2. **Fleet voice (new surface).** One conversation with the Cockpit itself: *"What needs me?"* - the system walks the NeedsYou queue, one item at a time, each presented as a Wingman-informed option set ("Session 4, mindzie API, wants to delete the old migration files, I recommend yes, two similar approvals last week"). The human answers in a word; the system routes it as the session's input; next item. *"How is everyone doing?"* reads the fleet rollup.

Architecturally, fleet voice is **not a new intelligence layer** - it is a conversational renderer over streams that must already exist for the visual Cockpit: the event hub (5.1), the assessed-state queue, and Wingman option sets (7.2). That is the design constraint that keeps it honest: **if a decision cannot be presented as a Wingman option set, it is not ready for voice.** Voice quality is therefore mostly a function of Wingman quality - build section 7 first and the car mode becomes a rendering problem, not an AI problem.

Eyes-free requirements that bind earlier work: every NeedsYou must carry a speakable one-line form; every option set must be enumerable ("one... two... three"); every action taken by voice must be confirmable by voice ("sent; session 4 is working again").

---

## 9. Migration tracks

Ordered by the trust ladder: reliability before intelligence, intelligence before voice. Tracks A-C are sequential in spirit; work within a track is CenCon-issue-sized.

### Track A - Solidify the rungs (reliability)

| Step | What | Existing anchors |
|---|---|---|
| A1 | Finish the proxy backbone: screenshot proxy (#317, in flight) completes "the Cockpit never needs a Director address" | #268 merged; `SessionWsProxyEndpoints`, `CockpitShotUrls` |
| A2 | **Cockpit remote terminal truly works**: TerminalPane reconnect + connecting/disconnected states, dictation failure UX (#226), Cockpit file logging (#199), optimistic UI (#280) | cockpit-triage-2026-06-10 ready-dev queue |
| A3 | Desktop fallback hardening: Terminal tab raw-metal review (rendering fidelity, keystroke fidelity, **any agent in plain terminal mode**), Source Control tab review | `TerminalControl`, git endpoints |
| A4 | Single state owner: Director demotes its timer to a raw event; Gateway `AssessedState` becomes the badge everywhere | `SessionAssessments`, `TurnEndWatcher`, doorbell pings |

### Track B - Consolidate the brain (intelligence)

| Step | What | Existing anchors |
|---|---|---|
| B1 | Wingman decide/execute split: Gateway brain absorbs summarize/explain/recap/decide; Director keeps `WingmanActionExecutor` behind `POST /sessions/{sid}/execute-action`; remove claude.exe spawning from `CcDirector.Core` | `GatewayTurnBriefAgent`, `BrainSupervisor` |
| B2 | Wingman promotion: informed option sets with recommendation + reason (7.2), then cross-session awareness (7.3) | TurnPackage builder, brief contract |
| B3 | Cockpit absorbs approvals: permission prompts and the comm queue become Cockpit surfaces backed by Gateway state | comm queue schema; brief `NeedsYou` plumbing |
| B4 | Engine split: queue/approval/scheduling brain to Gateway; Directors keep a `dispatch` verb | `CommunicationDispatcher`, scheduler leader-election design |
| B5 | Desktop scope-down: retire each non-fallback Avalonia surface as its Cockpit equivalent proves out; end state = Terminal + Source Control + tray diagnostics | `CommManagerView` first |
| B6 | Event hub: Director-push SSE/WS replaces polling as the fleet's nervous system | doorbell + heartbeat snapshots |

### Track C - Close the loop (voice + fungibility)

| Step | What | Existing anchors |
|---|---|---|
| C1 | Per-session voice polish: dictate + spoken brief reliable end-to-end remotely | dictate leg, turn summaries, TTS |
| C2 | Fleet voice (car mode): conversational walk of the NeedsYou queue rendered from Wingman option sets; fleet status rollup by voice | event hub (B6), option sets (B2) |
| C3 | KeyVault pull + zero-config enrollment: fresh machine = install + point at Gateway | `KeyVault`, `VaultEndpoints`, `SeedKeyVaultFromEnvironment` |

### Standing vetoes (apply from today, no track required)

- No new claude.exe side-calls below the Gateway.
- No new human-facing UI in Avalonia outside the fallback scope (Terminal tab, Source Control tab, local diagnostics).
- No new interpreted state computed in the Director.
- No new Director endpoint that is not a Fact, an Event, or a mechanical Verb.
- No agent-specific assumptions in the custody layer or the contract.
- No voice feature for a decision that lacks a Wingman option set.

---

## Document History

| Date | Author | Change |
|---|---|---|
| 2026-06-11 | Claude (with Soren) | Initial version: boundary rule, violation inventory, Facts/Events/Verbs contract, migration phases |
| 2026-06-11 | Claude (with Soren) | Vision rewrite: desktop Director declared permanent fallback (Terminal + Source Control) instead of retirement track; trust ladder ordering (reliability -> intelligence -> voice); Wingman promotion to decision-support staff (section 7); per-session + fleet voice / car mode (section 8); agent-agnostic custody; migration re-cut into tracks A/B/C |
