# Fleet Manager Improvement - handover to the Delivery Lead

20 September 2026. From the Architect, who has shut down. You answer to the owner, not to the
Architect and not to the Fleet Manager.

## Read first, in this order

1. `cc-devthrottle workflow instructions mission` and `cc-devthrottle skill get devthrottle-method`
   (your seat: Delivery Lead).
2. `MISSION.md` beside this file. It is what you take your answers from. The owner approved it with
   the words "I want you just to start the implementation".
3. `design-report.html` beside this file: the design with its reasoning, as the owner read it
   (https://gateway.devthrottle.com/r/53ccb050-cf2f-41b2-b7a0-428933b3afa1).
4. The repository's `CLAUDE.md`, `docs/CodingStyle.md` and `docs/VisualStyle.md`.

## The state you inherit

- These three files are UNTRACKED, in the worktree
  `D:\ReposFred\devthrottle-fleet-manager-improvement` on branch `fleet-manager-improvement-design`,
  cut from origin/main on 19 September 2026. Nothing is committed. The Architect was told not to
  commit; landing the record is your step. Do not remove this worktree before the three files are on
  main - a forced removal deletes them.
- Nothing has been built. No issue exists yet: the method wants one per implemented piece of work,
  pointing at `MISSION.md`.
- The shared checkout `D:\ReposFred\devthrottle` is on a detached HEAD with someone else's uncommitted
  changes and is hundreds of commits behind. Never read it or work in it. Every Developer gets its own
  worktree from origin/main.

## What the Architect verified by reading origin/main, and where

- The message limits: `src/CcDirector.Gateway/Messaging/FleetMessagePolicy.cs` (the
  `FleetMessageExemption` enum; `Decide`). The route picks the exemption at
  `src/CcDirector.Gateway/Api/GatewayEndpoints.cs`, near "req.Everyone ? ... HumanGrant".
- What a session key may call: `src/CcDirector.Gateway/Util/SessionKeyGuard.cs` (`Check`,
  `IsAgentInput`, `IsAllowed`, `IsFleetManagerRoute`), applied in
  `src/CcDirector.Gateway/Util/AuthMiddleware.cs`. Memory of this repository warns that adding a
  Gateway route does not add it to the guard: verbs answer 403 while every test stays green.
- The Cockpit page: `apps/cockpit/src/fleetmanager/` (`FleetManagerView.tsx` reads the conversation
  through `useSessionChat` and sends through `sendPrompt`). Its clients are already shared, in
  `packages/client-core/src/fleetmanager/`.
- The phone: no Fleet Manager screen. `apps/mobile/src/routes.tsx` carries the earlier ruling this
  mission reverses; `apps/mobile/src/pages/Home.tsx` pins the row.
- Voice and dictation are shared and keyed by session id: `packages/client-core/src/voice/`,
  `packages/client-core/src/dictation/`; the phone's view is `apps/mobile/src/pages/VoiceMode.tsx`.
- The Director has no special handling of the Fleet Manager's session for input
  (`src/CcDirector.Core/Sessions/Session.cs`, `InputGuardedBackend.cs`).

## What is NOT verified - do not take it as fact

- Why the Cockpit page did not reflect what the owner typed in the Director. Not reproduced, no cause
  found. Phase 4 starts by reproducing it.
- How narration is switched on for the Fleet Manager's own session, and whether the phone gets push
  notifications for cards. Settle each before planning the phase that needs it.
- The built-in skill rule applies: the `fleet-manager` skill and workflow have one source under
  `src/CcDirector.Gateway/Skills/Content/` (check where the workflow's source lives before editing),
  and reach the fleet only through a Gateway deploy.

## Things only the owner grants

Deploying the Gateway and releasing the Director are not part of this mission's end state (merged to
main). Phases reach the owner only after a deploy, and phase 4 needs a Director release; when a phase
is merged and ready, tell him so as a decision for him, with the `deploy-hosted-gateway` and
`release-manager` skills as the only ways. Never deploy on your own.

No assistant's name on anything. Plain English, no abbreviations.
