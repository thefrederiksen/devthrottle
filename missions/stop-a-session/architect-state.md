# Stop a session - the Architect's state note

This is the Architect's durable memory. Everything the Architect knows about this mission can be
rebuilt from this file plus `missions/stop-a-session.html`. Nothing important lives only in a
session's conversation.

**Status:** ACTIVE. Started 8 September 2026.

| | |
|---|---|
| Mission document | `missions/stop-a-session.html` (all six rulings settled) |
| Issue | thefrederiksen/devthrottle #2633 |
| Mission id on the Gateway | `e1141592` - "You can start a session - you have to be able to stop one", the one the owner seeded |
| Branch | `mission/stop-a-session` |
| Worktree | `C:\ReposFred\devthrottle-stop-a-session`, cut from `origin/main` at `6710a86c` |
| Conduct | `cc-devthrottle workflow instructions mission` - not restated here |

---

## What the Architect established in the code before settling anything

Read from `origin/main` at `6710a86c`. These are the load-bearing facts the rulings rest on, so a
later reader can check them rather than take them on trust.

1. **The thing that actually ends a session already exists.** `SessionCommandExecutor.KillAsync`
   (`src/CcDirector.ControlApi/SessionCommandExecutor.cs`) is the Director's `kill` verb. It signals
   the agent first and forces the process after a short grace, then removes the row, and it is
   already best-effort about a process that had already died. It answers `{ killed, removed }` and
   nothing else - no process identifier, no worktree, no verdict.
2. **A Gateway route already reaches it.** `DELETE /sessions/{sid}`
   (`src/CcDirector.Gateway/Api/GatewayEndpoints.cs`) forwards the `kill` verb over the Director
   tunnel and answers `{ killed: true }`.
3. **A session key may not call it.** `SessionKeyGuard` (`src/CcDirector.Gateway/Util/`) is an allow
   list; its `DELETE` branch does not include `/sessions/{sid}`, and its `POST /sessions/{sid}/*`
   switch allows `request-deletion` but nothing that ends a session. This is exactly the refusal
   Ruling 4 removes.
4. **Un-flagging is one command away, not new plumbing.** `DELETE /sessions/{sid}/request-deletion`
   and the Director's `cancel-deletion` verb both exist. The allow list refuses the route and no
   command calls it. Section 3 of the mission document already said this; it checks out.
5. **There is an append-only audit ledger to record a stop in.**
   `GovernanceAuditLog` / `governance_audit_events`, category `intervention`, with an actor and a
   short control-flow note. A stop reason is exactly the shape of note it takes, and it explicitly
   must not carry prompt content.

## Two facts the mission document got wrong, corrected here

The mission document says the Director window and the Cockpit "both show a live session and neither
can end one". That is not true of the Cockpit, and it changes Seat 3's work from *add a button* to
*re-point a button and make it speak*.

- **The Cockpit can already close a session today.** `apps/cockpit/src/sessions/SessionMenu.tsx`
  calls `killSession` from `packages/client-core/src/api/client.ts`, behind a confirmation. It sends
  no reason, and on success it shows nothing at all - it just closes the dialog. That is the exact
  defect this mission exists to fix, already built, in a nicer font.
- **The mobile app has the same control**, `apps/mobile/src/components/useSessionManage.ts`, calling
  the same shared `killSession`. Mobile is not named in the mission's three seats, but it shares the
  client-core function, so it cannot be left behind: changing the shared function without changing
  mobile would either break mobile or leave it as the silent second path Ruling 5 forbids. Mobile is
  therefore IN, as a consequence of the shared function, and for no wider reason.
- **A native phone client also calls the route** - `phone/CcDirectorClient/Voice/GatewayClient.cs` -
  and that one does NOT ship inside the Gateway container image, so it does not deploy in lockstep
  with the Gateway. This is why `DELETE /sessions/{sid}` is kept rather than deleted; see the route
  decision below.

## The route decision (mechanics under Ruling 5, decided by the Architect)

**`POST /sessions/{sid}/stop`, body `{ "reason": "..." }`, is THE stop.** It matches every other
session verb on the Gateway (`prompt`, `interrupt`, `hold`, `request-deletion` are all
`POST /sessions/{sid}/<verb>`), a body is the natural place for a sentence, and it is one word added
to the allow list's existing three-segment `POST` switch - which is the shape Ruling 4 describes.

**`DELETE /sessions/{sid}` is kept, and stays REFUSED to session keys.** It is kept because a native
phone client that does not deploy with the Gateway calls it, and removing it would break an app in
somebody's hand. It is not a second implementation: it becomes a thin forward into the same stop
handler and the same fold, so there is one stop and one audit trail behind two doors. Keeping it
refused to session keys is what preserves the owner's ruling exactly - **an agent's key can only ever
stop a session with a reason attached.**

## The phases, and what each one proves

| Phase | Seat | What lands | Proven by |
|---|---|---|---|
| 1 | Seat 1 - the Director ends it | The stop returns a structured, honest outcome: the process identifier, whether a process was ended or was already gone, whether a row was removed, and the worktree it leaves behind with whether that worktree had uncommitted changes. Reconciles row against process inside the owning Director. | Unit tests per state, each watched failing on purpose |
| 2 | Seat 2 - the route and the command | `POST /sessions/{sid}/stop` with a required reason; the refusal that names the reason as what is missing; the audit record; the fold that writes the one sentence; the allow-list entries for the stop and for cancelling a deletion; `cc-devthrottle session stop` and `session done --undo` | Unit tests plus the command line answering for real |
| 3 | Seat 3 - the controls | The Cockpit's close re-pointed at the stop, asking for a reason and showing the Gateway's answer; the same in the Director window; mobile carried along because it shares the function | Component tests, then the screenshots in the report |
| 4 | Inspection | A Codex session, adversarial, reviewing the whole branch diff against the rulings. Writes to a file, replies one line. Never fixes. | Its written review, and the fixes that answer it |
| 5 | The QA report | The document of section 6, written by a seat that did not build the feature, driving the real product | The report itself - this is the mission's goal |
| 6 | Landing | The Architect merges the slices to `main`, lands this record, and emails the owner | Merged pull requests |

## Running log

- **8 Sep 2026** - Architect seated. A second Mission was created here by mistake and then removed;
  every seat attaches to the seeded Mission `e1141592`. Worktree confirmed clean at
  `origin/main` `6710a86c`. Rulings 1, 3, 5 and 6 settled in the mission document. Code facts above
  established and the two document errors corrected.

- **8 Sep 2026** - Phase A landed on the branch: the Director's honest answer, the stop route and its
  fold, the allow list, the audit record, and the two commands. 71 new tests, each watched failing on
  purpose. Ruled on its four open decisions in
  `missions/stop-a-session/architect-ruling-on-phase-a.md`: the reason belongs on the POST door
  (confirmed - the handoff was self-contradictory and the Manager was right to read past it); the 502
  on an un-foldable answer is REVERSED; stops must not be counted as interventions in the outcome
  ledger; the rest confirmed. **Ruling 3 gained a fourth verdict word**, `stoppedNotDescribed`, and
  the mission document is amended rather than contradicted by a note.
- **The largest remaining risk is the end-to-end run.** Nothing has stopped a real session; every
  layer was proved against a stub of the one below it. The QA run needs a Gateway built from this
  branch, and the branch cannot merge before the QA run exists - so the stack is stood up LOCALLY.
  Phase B de-risks that and writes down the recipe; the QA seat repeats it independently.
