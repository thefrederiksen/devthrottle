# Target Architecture Implementation Plan (Hands / Brain / Face)

**Status:** PLANNED
**Date:** 2026-06-11
**Audience:** The implementing Director session running on the gateway machine (primary executor of this plan), the remote test session on SORENLAPTOP, the Product Agent (cuts issues from the phases), and Soren (approves phase transitions).

## Related documents

- [DIRECTOR_DUMB_WRAPPER_TARGET.md](DIRECTOR_DUMB_WRAPPER_TARGET.md) - the target architecture this plan implements (vision, trust ladder, boundary rule, violation inventory, contract)
- `hands-brain-face-target.d2` / `.png` - the target topology diagram
- [../plans/cc-launcher.md](../plans/cc-launcher.md) - CC Launcher v1 plan (issue #243), expanded cross-machine in Phase 1
- [../cencon/DEVELOPMENT_METHOD.md](../cencon/DEVELOPMENT_METHOD.md) - every work item in this plan flows through the CenCon pipeline
- [gateway/GATEWAY_DIRECTOR_RESPONSIBILITIES.md](gateway/GATEWAY_DIRECTOR_RESPONSIBILITIES.md) - feature-by-feature decision matrix

---

## 1. Mission and operating context

This plan upgrades the Director framework, Gateway, and Cockpit into the hands/brain/face target: a fleet one human supervises from anywhere. It is written to be **executed by agents, supervised by Soren**, under the CenCon method.

### 1.1 Execution topology

| Role | Where | Autonomy granted |
|---|---|---|
| **Implementing session(s)** | Director on the **gateway machine** | Full autonomy to start/stop the **Gateway and Cockpit** on that machine; start/stop Director **sessions** on both machines; build/deploy to agent slots (5+) |
| **Remote test session** | Director on **SORENLAPTOP** | Receives feature-verification handoffs; the "remote machine" in every cross-machine test; may start/stop Director sessions locally |
| **Soren** | Anywhere (Cockpit) | Approves phase exits; answers `flow:needs-human`; final merge oversight per CenCon |

### 1.2 Standing rules for the implementing agents

1. **CLAUDE.md rules 0 and 0b apply unchanged.** Never kill the user's Directors (main exe, slots 1-4). Launch test Directors via the scheduled task or, once Phase 1C ships, via CC Launcher. Test builds go to slot 5+.
2. **Restarting the Gateway/Cockpit is explicitly permitted** on the gateway machine (that grant is the point of running there) - but announce it in the session log first, and never while a queue-runner drain is mid-flight.
3. **Every change flows through CenCon**: issue -> `flow:ready-dev` -> implementation loop -> QA with proof -> merge. This plan's phases are the issue backlog, not a license to bypass the pipeline.
4. **Cross-machine proof is the default proof.** A feature that only works same-machine is not done. The standard verification is: exercise it from the Cockpit against a session on the *other* machine.
5. **The contract gets PR-review gravity.** Any change to `CcDirector.Gateway.Contracts` is called out explicitly in the PR description.

### 1.3 Verification loop (how the two machines work together)

```
implementing session (gateway box)
  -> builds + deploys Director/Gateway/Cockpit changes locally
  -> restarts the affected component (CC Launcher / scheduled task)
  -> hands a verification brief to the SORENLAPTOP test session:
       "feature X shipped; from your machine, do Y via the Cockpit; report Z"
  -> test session exercises the feature as the REMOTE machine
  -> result (screenshot + report) committed as CenCon proof
```

The handoff itself uses the existing handover/queue endpoints - eating our own dog food is part of the test.

---

## 2. Evidence baseline (why Phase 1 is the Director)

Observed live on SORENLAPTOP's Director (Control API :7880, v0.6.22, 2026-06-11):

- **Session DTOs ship empty identity fields:** `machineName: ""`, `user: ""`, `tailnetEndpoint: ""`, `viewUrl: ""` on every session. The fleet's atoms don't know where they live - every cross-machine feature above them inherits the gap. This is the user-reported "right Tailscale names" problem, confirmed.
- **The Cockpit's remote terminal is not truly working** from non-Gateway machines (TerminalPane one-shot, no reconnect; dictation failures opaque #226; Cockpit logs discarded #199; optimistic UI gaps #280).
- **The desktop fallback is entangled** with smart-UI surfaces (~5,000-line MainWindow) instead of being the small, bulletproof escape hatch.
- **Restarting a Director remotely requires a human at that machine** (or a hand-registered scheduled task) - the implementing agents cannot yet roll out their own Director updates across machines. CC Launcher (#243) is spec'd but unbuilt.

Conclusion: the hands must be completed first. Everything smart sits on Director identity, Director REST completeness, and the ability to restart Directors remotely.

---

## 3. Phase 1 - Complete the Director (desktop first)

**Goal:** the Director is a *finished* hands layer: correct tailnet identity, complete agent-agnostic REST surface, a hardened permanent fallback UI, and remote lifecycle control. Exit means a fresh machine can join the fleet and be fully driven and updated without anyone touching its keyboard.

### 1A. Identity and addressing (the Tailscale-names fix)

- Every Director resolves and advertises its **real MagicDNS tailnet endpoint** (`http://<machine>.<tailnet>.ts.net:<port>`) at registration and heartbeat - never loopback, never empty. Detection order: Tailscale local API -> `tailscale status --json` -> explicit config override; **fail loudly** (status surfaced in UI + logs) when none resolves. No silent fallback to loopback for the advertised address (loopback stays correct for local binding).
- **Session DTOs always carry** `machineName`, `user`, `tailnetEndpoint`, `viewUrl` - populated at the Director, not patched in by the Gateway. The empty-fields evidence in section 2 becomes a regression test.
- Two-way verification (#223/#224) runs against the advertised endpoint, so a Director that advertises an unreachable name is flagged within one heartbeat cycle.
- **Acceptance:** from the gateway box, `GET /sessions` (Gateway-aggregated) shows every SORENLAPTOP session with correct machine name and a tailnet endpoint that answers `/healthz` from the *other* machine.

### 1B. REST completeness (the Facts / Events / Verbs audit)

- Audit the Control API against the contract taxonomy (target doc section 5). Produce `gateway/CONTRACT_AUDIT.md`: every endpoint classified as Fact, Event, or Verb - or flagged as a boundary violation (judgment below the line) with its migration phase.
- Add the missing **verbs**: `POST /sessions/{sid}/execute-action` (mechanical WingmanAction executor entry), `POST /dispatch` (comm dispatch primitive for Phase 3), `POST /tools/run` (invoke a cc-* tool with args, streamed result).
- Add the missing **facts**: `agentType` per session (already partially present: `agent: "ClaudeCode"`), driver capabilities (present - keep), tool inventory with versions, launcher presence/port.
- Add the missing **events**: extend doorbell + heartbeat snapshots toward the push event stream (full SSE/WS hub lands in Phase 3; Phase 1 ensures the Director *emits* everything the hub will need: raw activity transitions, session created/exited, prompt-detected).
- **Acceptance:** the audit doc exists with zero unclassified endpoints; the three new verbs pass cross-machine smoke tests.

### 1C. CC Launcher - local v1, then cross-machine (the early lever)

Build [#243 per the existing plan](../plans/cc-launcher.md) (tray app, clean-parentage LaunchService, DirectorSupervisor, loopback REST :7900) - then immediately the cross-machine fast-follow, pulled forward because **it is the implementing agents' own update mechanism** for the rest of this plan:

- Launcher registers with the Gateway (machine name, port, token handshake via the existing registration pattern).
- Gateway relays lifecycle verbs: `POST /machines/{machine}/director/restart|start|stop`, `POST /machines/{machine}/launch` - token-gated, audit-logged, tailnet-only.
- Director update rollout becomes: deploy new build -> ask the remote Launcher to restart the Director -> verify version via `/healthz`. No human at the remote keyboard.
- **Acceptance:** the implementing session on the gateway box restarts SORENLAPTOP's slot-5 test Director via the Gateway relay and observes the new version, end-to-end, with zero manual steps. (Production Directors and slots 1-4 remain protected by rule 0 - the relay refuses non-agent slots unless explicitly confirmed by the human.)

### 1D. Desktop fallback hardening (Terminal + Source Control)

- **Terminal tab raw-metal review:** rendering fidelity, keystroke fidelity (Esc/Ctrl+C/arrows/paste), resize correctness, throughput under stress - treated as the highest-quality-bar surface in the product, per the trust ladder.
- **Any-agent support:** create a session running an arbitrary CLI (Codex CLI, aider, plain pwsh) in raw terminal mode. Custody code must not assume Claude Code; agent-specific interpretation stays above the line (the `agentType` fact from 1B is the key).
- **Source Control tab review:** status/stage/diff/commit against large repos; plain machine facts, no interpretation.
- **Acceptance:** with the Gateway process stopped (permitted on the gateway box), the desktop Director remains fully usable: sessions run, terminal is solid, source control works, any-agent sessions function. The degradation story (target doc section 6) becomes a tested property, not prose.

### 1E. Phase exit criteria

1. Identity acceptance (1A) green from both machines.
2. Contract audit (1B) committed; new verbs/facts live.
3. Remote restart via Launcher relay (1C) demonstrated cross-machine.
4. Fallback hardening (1D) proven with the Gateway stopped.
5. All shipped through CenCon with cross-machine proof artifacts.

---

## 4. Phase 2 - The remote terminal rung (Cockpit reliability)

**Goal:** the Cockpit terminal against a remote machine is something the user never doubts. This is the rung everything smart stands on.

- **TerminalPane lifecycle:** reconnect with backoff, explicit connecting/disconnected/reconnecting states, buffer replay on reconnect (never a silent blank pane).
- **Dictation failure UX (#226):** surface the WS close code/reason and the Director-reported cause; offer retry with preserved audio.
- **Cockpit file logging (#199):** persisted INFO sink so remote failures are diagnosable after the fact.
- **Optimistic UI (#280 family: #227/#228/#229/#230):** every Cockpit action acknowledges <100ms.
- **Single state owner** (target doc 4.2): Director demotes its silence timer to a raw event; Gateway `AssessedState` becomes the only badge, rendered identically in Cockpit and desktop. Kills the #186 dual-owner tension.
- **Exit criterion:** a scripted cross-machine soak - from the gateway box's Cockpit, drive a SORENLAPTOP session for 30+ minutes through network blips (Tailscale restart mid-session) with the terminal recovering unaided every time; verified by the SORENLAPTOP test session in the reverse direction too.

---

## 5. Phase 3 - Consolidate the brain (Gateway)

**Goal:** all judgment in one place; the Director finishes going dumb.

- **Wingman decide/execute split** (target doc 4.1): the Gateway brain absorbs summarize/explain/recap/decide; `CcDirector.Core` stops spawning claude.exe; Directors keep `WingmanActionExecutor` behind the Phase-1B verb. All actuation invariants preserved verbatim.
- **Event hub:** Director-push SSE/WS replaces polling as the fleet's nervous system (consumes the Phase-1B event emissions). Cockpit and fleet voice both subscribe to it.
- **Engine split** (target doc 4.4): queue/approval/scheduling policy to the Gateway (always-on, no longer tied to a running Avalonia instance); Directors keep the mechanical `dispatch` verb.
- **Work lists persistence:** SQLite backing so a Gateway restart no longer loses queues or consumer claims - the "restart anything safely" property extended to the orchestration layer.
- **Exit criterion:** zero claude.exe spawns below the Gateway (build-time audit); a Gateway restart mid-drain resumes the work list; assessed state, briefs, and dispatch all keep working across the restart.

---

## 6. Phase 4 - The smart multi-agent interface (Cockpit + Wingman promotion)

**Goal:** supervising 15 agents feels like a briefing call.

- **Wingman option sets** (target doc 7.2): every NeedsYou arrives with the exact question, the relevant excerpt/diff, what changed, the risk if wrong, and a **recommendation with the reason**. The option set is the unit of human decision - and the prerequisite for voice.
- **Approvals in the Cockpit:** permission prompts and the comm queue become first-class Cockpit surfaces backed by Gateway state. The remote human can finally approve, not just watch.
- **Cross-session awareness** (target doc 7.3): same-file collision warnings, repeated-question detection, shared-blocker rollups.
- **Product/machine grouping:** the rail groups by product and machine; triage view spans the fleet.
- **Desktop scope-down completes:** each non-fallback Avalonia surface (CommManagerView first, then wingman tab, agent view) retires as its Cockpit equivalent proves out. End state: Terminal + Source Control + tray diagnostics.
- **Exit criterion:** a full CenCon issue is driven start-to-finish from the Cockpit alone - triage, decisions via option sets, permission approvals, comm approvals - without touching the desktop app or a raw terminal.

---

## 7. Phase 5 - Voice (the car mode)

**Goal:** the trust ladder's top rung.

- **Per-session voice polish:** dictate + spoken brief reliable end-to-end against remote machines (builds directly on Phase 2's dictation work).
- **Fleet voice:** one conversation walking the NeedsYou queue, each item rendered from its Phase-4 option set ("Session 4, mindzie API, wants to delete the old migrations, I recommend yes - two similar approvals last week"); answers route as session input; fleet status rollup on demand. Constraint enforced: **no option set, no voice feature.**
- **Eyes-free requirements:** speakable one-line forms on every NeedsYou, enumerable options, spoken confirmation of every action taken.
- **Exit criterion:** Soren clears a real NeedsYou queue (5+ items across both machines) by voice alone from a phone, hands- and eyes-free.

---

## 8. Phase 6 - Fungible fleet (enrollment)

**Goal:** adding a machine adds capacity, not configuration.

- **KeyVault pull:** Directors fetch credentials from the Gateway vault on demand; env-var seeding retired.
- **Zero-config enrollment:** fresh machine = install (Director + Launcher) + point at Gateway -> registered, verified, key-provisioned, remotely restartable, visible in the Cockpit.
- **Exit criterion:** a clean Windows VM joins the fleet and runs a supervised session within 15 minutes, no manual config beyond the Gateway URL + token.

---

## 9. Lessons from Factory AI

Factory (factory.ai, "Droid") is the closest comparable: $1.5B valuation (Series C, Apr 2026), enterprise agent-development platform, CLI + web + Slack/Jira/Linear surfaces, persistent remote agent machines ("Droid Computers"). A full research pass (primary docs + critical coverage, adversarially verified) yields two strategic confirmations and a set of concrete features to absorb.

### 9.1 Strategic confirmations (our lanes are open)

- **Fleet supervision is unclaimed.** Factory parallelizes via git worktrees and `xargs -P`; their own docs call multi-agent orchestration "an open research question." There is no first-party dashboard for one human watching 10-15 *parallel* agents. The Cockpit's core premise is differentiation, not catch-up.
- **Voice is a first-mover gap.** No first-party voice supervision exists at Factory (verified absent as of June 2026). The car mode (Phase 5) is not a me-too feature.
- **Real-time briefing is absent there too.** Factory Analytics is retrospective dashboards; their live UX is per-session logs. The Gateway brain's "3 need decisions, 2 blocked, one-line reason each" is the product gap.

### 9.2 Features to absorb (mapped to phases)

| # | Lesson | Phase |
|---|---|---|
| 1 | **Outbound-only enrollment.** Droid Computers' BYO machines dial *out* to the backend via a daemon - no inbound firewall config, locked-down user, state persists, idle pauses. Adopt for remote Director/Launcher enrollment alongside Tailscale. | P1 (1A/1C design input), P6 |
| 2 | **Graduated autonomy with risk-scored actions.** Session autonomy level (off/low/medium/high) gating per-action risk scores; allowlists scoped user/project/org; **deny always wins** (even inside `$(...)`); an org-level autonomy *ceiling* - for us, a fleet ceiling the Gateway enforces on every Director. | P3 (Gateway policy), P4 (Cockpit surface) |
| 3 | **Fuse approval with autonomy in one tap.** Factory's spec-approval dialog offers "proceed manually / at Low / Medium / High / keep iterating" - one decision sets *whether* and *how unsupervised*. Make this the shape of Cockpit option sets, and a single voice utterance: "approve, medium autonomy." | P4 (option sets), P5 (voice) |
| 4 | **Plan-before-mutate, structurally.** Spec Mode hard-blocks file mutation while drafting acceptance criteria; specs persist as dated files. CenCon already has the issue gate; add the *structural* read-only planning phase for session-level work, with criteria the brain later verifies results against. | P3/P4 |
| 5 | **Never trust the agent's own success report.** Factory was publicly burned by Droids claiming green builds over broken TypeScript. Our QA-agent philosophy already says this; extend it to every fleet brief - the Gateway independently verifies (build/test evidence from the terminal, not the agent's claim) before telling the human "done." | P3 (brief pipeline), P4 |
| 6 | **Notification hooks by need-state.** Factory's four hook events (needs-input / stop / subagent-stop / session-end) are the right taxonomy. Route them through the event hub to Cockpit toasts, push notifications, and voice. Attention routing *is* the product. | P3 (event hub), P4/P5 |
| 7 | **Diffs-first, phone-first unblocking - with small diffs by policy.** "Inspect tool calls, approve diffs, unblock from your phone." Factory's most-cited review pain is giant autonomous diffs; cap diff size per approval and make agents split work. | P4 |
| 8 | **Sessions as shareable/forkable objects.** Live session URLs with watch/comment/take-over semantics; fork before risky steering. We have resumable sessions; add shareable view URLs (the Phase-1A `viewUrl` fact is the seed) and fork. | P4 (stretch) |
| 9 | **Mid-run message queueing is our edge - keep it.** Factory only supports interrupt-and-redirect; our PromptQueue ("when tests finish, also update the changelog") is undocumented anywhere at Factory. Promote it in the Cockpit rather than treating it as plumbing. | P2/P4 |
| 10 | **Autonomy ratio as the supervision KPI.** Tool calls per human message, per agent - the best fleet-health metric found. Cheap to compute from data we already have; put it on the rail. | P4 |
| 11 | **Cost legibility in real time.** Factory's worst community damage was "token blackhole" billing opacity. Surface per-session/per-task cost in the brief ("this refactor has cost $4.10 so far"). | P4 |
| 12 | **Headless contract discipline.** `droid exec` is read-only by default, escalates explicitly, fails fast with non-zero exit on permission violations, emits JSON/stream. Apply the same discipline to our Verbs surface and cc-* tools. | P1 (1B) |

### 9.3 Pitfalls to avoid (from their documented failures)

- Agents lying about success (their worst trust failure) -> independent verification everywhere (lesson 5).
- Giant diffs shifting cognitive load to PR review -> small-diff policy (lesson 7).
- Orchestration tax on small tasks -> keep the raw terminal one keystroke away (our permanent fallback already covers this).
- Confusing multi-pane UI with hidden approval dialogs -> Cockpit approvals are never more than one tap from the triage rail.
- Support/billing opacity -> not architectural, but cost legibility (lesson 11) is the engineering share of it.

---

## 10. Issue-cutting guidance (for the Product Agent)

- Each lettered item in Phase 1 (1A-1D) is one epic; cut 2-5 `flow:ready-dev` issues per epic with the acceptance lines above as the proof targets.
- Phases gate sequentially, but issues within a phase parallelize across implementation loops (issue-level claims, #298, prevent collisions).
- Every issue names its **cross-machine proof**: which machine exercises it, which machine hosts it, what the screenshot/report must show.
- Phase exits are explicitly approved by Soren before the next phase's issues go `flow:ready-dev`.

---

## Document History

| Date | Author | Change |
|---|---|---|
| 2026-06-11 | Claude (with Soren) | Initial plan: desktop-Director-first Phase 1 (identity, REST audit, CC Launcher cross-machine, fallback hardening), then Cockpit reliability, brain consolidation, smart interface, voice, enrollment; execution topology + autonomy grants for the gateway-machine handoff |
