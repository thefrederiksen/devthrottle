# Fleet Manager Improvement - mission document

Mission id: 1be19189-9217-4167-91ae-f8a2d1e5d51f. Final, 20 September 2026. APPROVED: the owner said
"I want you just to start the implementation" on 20 September 2026.

The design in full, with the reasoning, is the dev report beside this file: `design-report.html`
(published as https://gateway.devthrottle.com/r/53ccb050-cf2f-41b2-b7a0-428933b3afa1).

## 1. The mission

Let the owner raise a session - the Fleet Manager first - to act with his own permissions, put the Fleet Manager on
the phone as its own screen that the owner can talk to, and make that screen the only way to reach it.

## 2. The why

Put to the owner in the report in these words; he read it, called the question not important, and
said to start. The Fleet
Manager is how the owner runs all his work from the couch, by voice. Today it is throttled like a
chatty session and is missing from the phone, so he falls back to talking to a raw session.

## 3. The goal

Put to the owner the same way. From the phone, by voice, the owner asks the
Fleet Manager to pass two instructions to the same session a minute apart; both arrive; he sees its
cards and answers one; and at no point does he open the Fleet Manager's session directly.

## 4. Decisions

The owner's words, dictated 19 September 2026, unchanged:

> We really need a new feature. We need to be able to escalate.  or we need to be able to raise  some sessions, so they're allowed to maintain other sessions with no limits.  We put...  Limits on message sending.  because we didn't want this incessant chatter between agents they started just  blabbering and  And also,  interrupting other agents.  But the fleet manager needs...  the ability to do that. [...] and the other thing that's just as important,  is we need to add Fleet Manager.  to the phone. and it needs to be not a session because right now I'm talking directly to the session  and then the fleet manager is not updated on the screen.  So that's a problem.  If this is...  the fleet manager session and it runs in the director we probably also need to update the  director  So you cannot...  interact directly  with this session, but it has to go through,  the fleet manager UI.  And then we need to implement the fleet manager UI on the phone,  But on the phone, I'd want to talk to it.  So I'd want the wingman...  to be able to chat with it just like I do with a regular session.

**20 September 2026, on who is raised and how far, dictated, unchanged:**

> If the user chooses to escalate a session,  it should then  be able to  have the same permissions  as the user does.  And so a fleet manager,  basically has the same permissions as the user. It's got delegated permissions.  It's actually kind of interesting because GoDaddy has this feature,  of you can invite other people in  And they can choose to have the same permissions as somebody else.  But you have to choose it every time with GoDaddy.  But here, if we...  give  Any session...  user level permissions and we should also allow this actually when we do the  the factory agents, they should have user level permissions.

This replaced the first draft's two questions about the message limits. For raised sessions only, it
reverses the Message Load ruling of 16 September 2026 that no session types into another.

**20 September 2026, on the ceiling of a raised session, dictated, unchanged:**

> Yeah, no, you can have a session.  shut down the Gateway because he...  User can't shut down a gateway. Only I can, who's the developer.  So it can't have more permissions than a normal user would.  They can only deal with things in their own tenet.  I don't know what other questions you have, but can you try to make some of those decisions on your own and then only bring me the questions that are left that you truly do not want to make a decision on?

("tenet" is the transcription of "tenant".) On that instruction the Architect decided the following;
the owner may overrule any of them:

- A raised session acts as the owner inside his own account only, and never does what only the
  developer of DevThrottle can do (shutting down the Gateway).
- It does not get the admission surface for now: devices, sign-in and sign-out, billing.
- Only the owner raises a session, from his own device. A raised session never raises another.
- Raised lasts until the session ends or the owner lowers it; for the Fleet Manager it follows the mark.
- The Fleet Manager's session is read-only everywhere, with an owner-only unlock, recorded.
- Voice on the phone is press to dictate and the answer read aloud. Hands-free is out.
- The pinned Fleet Manager row on the phone always opens the Fleet Manager screen.

**20 September 2026, on what was not updated, and go, dictated, unchanged:**

> I talked to the...  Session directly instead of through the UI. That was the problem.  If I talk to the session in the director directly,  the UI on the...  cockpit doesn't reflect it, right?  It doesn't get notified that changes remained.  So the user should not be allowed to talk  And this is a director change that's going to take a little bit longer because we have to cut a new director.  But this session should be.  On the director, it should be visible, but the user should not be able to interact with it.  because it needs to be directed through the fleet manager. So if you understand this, and the other  one, I don't think it's really important. I want you just to start the implementation  If you have enough information, otherwise ask me new questions.

His "right?" is a question the Architect could not answer from the code: the Cockpit page draws the
marked session's own history, and no cause for it falling behind was found or reproduced. Phase 4
therefore starts by reproducing it. No question remains open with the owner.

This mission reverses one earlier ruling: the first Fleet Manager mission (step 9) ruled "the Fleet
Manager is on the Cockpit only" (`apps/mobile/src/routes.tsx`). The owner's words above replace it.

## 5. Design

Four pieces, each extending something that exists. The recommendations below stand until the owner
answers otherwise.

- **A raised session.** The Gateway holds a per-account list of raised sessions, written only from
  the owner's own device (raise, lower) and by setting up the Fleet Manager; it follows the Fleet
  Manager mark through a restart or a move and ends with the session. `AuthMiddleware` consults it
  where it applies `src/CcDirector.Gateway/Util/SessionKeyGuard.cs`: a raised key passes the agent
  input refusal (prompt, interrupt, escape, fan-out, answering a judged stop) and the owner-only
  Fleet Manager routes. The admission surface stays refused, and so does raising another session
  (section 4). Tenant binding is the session key's own and is not loosened. `FleetMessagePolicy` gains an exemption for a raised sender
  that waives the relationship rule and both rates; the duplicate rule stays. Every raised action is
  recorded with the session that took it. The shipped `fleet-manager` workflow and skill change
  their words in the same pull request. The factory grant is out of this mission and builds on the
  same list.
- **Phone screen.** The page in `apps/cockpit/src/fleetmanager/` moves into
  `packages/client-core/src/fleetmanager/`, following critical rule 8 (one page, two surfaces). Each
  app keeps only its frame. The phone adds a route, opens it from the pinned first row and the menu.
- **Voice.** The phone screen mounts the shared dictation and narration pieces against the marked
  session. The Gateway adds one spoken line to each outcome card (critical rule 7: the client never
  composes it). The transcription rule is untouched: no new path from speech to text.
- **One front door.** A Gateway fold stamps the marked session as reached through the Fleet Manager
  screen, with the sentence and link to show. Cockpit, phone and Director render it read-only. The
  Gateway's typing routes refuse direct input to it. An owner-only unlock, recorded.

Out: hands-free conversation, pull request and report events, any change to the Wingman.

## 6. Phases

1. Raise a session - the Gateway list, guard and message exemption first; then the raise and lower
   control in the shared package. Tech Lead; two Developers in that order.
2. Phone screen - shared package, Cockpit, phone. Tech Lead; the move and the phone frame can run as
   two Developers once the move has merged.
3. Voice - Gateway spoken line, then the phone. One Developer each, in that order.
4. One front door - first reproduce the owner's report (words typed into the Fleet Manager's session
   in the Director not reflected on the Cockpit page) and record the cause; then the Gateway fold and refusals first, then clients and Director in parallel. Tech Lead.
   Must not merge before phase 2 is deployed, or the phone loses its only way to the Fleet Manager.

## 7. The check

Every phase: `.\scripts\test-local.ps1 -Parked` from the phase's own worktree (the Gateway's unit tests
are parked and every phase touches the Gateway), plus `npm test` in `packages/client-core`,
`apps/cockpit` and `apps/mobile` for phases 2 to 4, which the local gate does not run. Phases 2 to 4
also owe a QA report with screenshots covering the flow and the failure cases listed in the report.

## 8. Merge plan

One pull request per Developer task, merged the day it is opened, reviewed by a different agent first.
Safe because each phase is additive until phase 4, and phase 4's lock is one Gateway fold that can be
switched to stamp nothing.

## 9. Where it ends

Merged to main. Deploying the Gateway and releasing the Director are the owner's separate decisions.

## 10. Questions

None open. Every question asked, and its answer or the Architect's decision, is in section 4 and in the
dev report.

Repository guides for the seats that build: `docs/CodingStyle.md`, `docs/VisualStyle.md`.
