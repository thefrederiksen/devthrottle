# Report Answers by Voice - proof

Pull request 4749. Every result below was taken on commit `b87fd9861` (the four feature commits rebased onto
origin/main `082e0ff72`, v2.40.0), on SOREN_NORTH, on 8 November 2026. The proof driver and this file are added in
the commit after it; neither changes product code.

## What the work does

- **Dictation in every text box of a dev report**, in the Cockpit and the phone app. Each comment box under a
  question, the "Something else" box and the note box that Add a note opens gets a microphone row under it. Tap to
  start, tap the red square to stop. The report frame cannot open the microphone (it is sandboxed without
  same-origin), so it asks the host app over the port; the host records with the existing recorder and sends the
  audio through the same Gateway path the session prompt uses (`POST /wingman/utterance/{id}/complete`). The words
  come back verbatim and are **appended at the end of the box**, never at the cursor, and nothing is sent by
  talking. One box records at a time. A plain report file with no host draws no microphone.
- **"Something else" on every question.** The page script adds it as the last option of every question. Picking it
  opens its own box; Queue answer stays disabled until that box has words. The Gateway stores it as its own kind of
  answer: the reserved option value `own-answer`, the words in the answer's text, and the label "Something else"
  set by the Gateway, not by a client. A report whose author uses the value `own-answer` is refused at the shape
  check. On the team path, a member's own words go to the person who sent the report, never into the session (the
  existing team privacy rule).

## Red, then green

The new behaviour was removed from production code only (never the tests) by a script, the tests run, then the
source restored with `git checkout` and the tests run again.

| Side | Removed | Red | Green |
|---|---|---|---|
| Gateway | shape-check refusal, own-answer handling in the send, the prompt, the answers view, the item the panel reads, the team path | 14 failed of 151 (DevReportOwnAnswerTests, DevReportShapeCheckTests, DevReportAnswersTests, TeamQuestionsTests) | 151 of 151 |
| Client | the added option, the host's "I record" flag, the words going into the box, the frame host passing requests on, one-box-at-a-time, verbatim text, the panel's own words, the protocol check | 21 failed of 171 in `src/devreports` | all of client-core: 2,568 of 2,568 in 195 files |

One of the 21 client reds, "keeps only one option checked at start", fails because the mutation that removes
"Something else" also inverts the radio scan that test uses; it is not a test of this feature.

## The gate

- `.\scripts\test-local.ps1` (default): every suite green except one test in `CcDirector.Avalonia.Tests`,
  `NewSessionDialogGatewayListTests.TheDialogShowsTheGatewaysList_InTheGatewaysOrder`, which failed with "Access is
  denied" renaming a temporary `repositories.json` (something held the file for ten seconds). Re-run alone with
  `-RerunFailed`, it passed: it failed on correct code and is outside this change. It should be deleted in its own
  test-only pull request on main, under the owner's 13 October rule; this pull request does not touch it.
- `.\scripts\test-local.ps1 -Parked -Configuration Release`: **GREEN at b87fd9861, tree clean.** Core.UnitTests
  3,690; Avalonia.Tests 1,296; Engine.Tests 85; HostedAgent.Tests 85; Launcher.Tests 236; Terminal.Avalonia.Tests
  111; Reclaim.Tests 307; DeliveryQualification.Tests 151; RuleHook.Tests 70; setup 43; setup-engine 1,029 of 1,035
  (6 skipped); setup-cli 78; Gateway.Tests 426 + 482 + 439 (3 skipped) + 489 in four shards; Core.Tests 4,644 of
  4,662 (18 skipped); Gateway.UnitTests 17,700. Every suite reported outcome Completed.
- client-core vitest: 2,568 of 2,568.
- `tools/cc-dev-reports` pytest: 61 of 61.
- `npm run typecheck` (every workspace): exit 0. eslint on the 16 touched script files: exit 0.

## End to end, with no deploy

The rig in `docs/missions/dev-reports/proof/rig.ps1` stood up, from this tree at b87fd9861: its own Gateway (port
7951, its own `CC_DIRECTOR_ROOT`, and the log line `CC_GATEWAY_NO_TAILSCALE=1; Tailscale Serve auto-provisioning
disabled`), the Cockpit and phone app built into that Gateway, a launcher, a slot 5 Director, one real Claude Code
session, and the sample report published by that session. The Director was built with `-NoRuleHook` because this
machine lacks the native C++ build tools for the Session Rules hook; no rule is involved in this proof. The
transcription key was put into the rig's own vault from cc-secrets without being printed.

`proof-run.mjs` (in this folder) drives the built apps in headless Chromium. The microphone is Chromium's fake
capture device playing a clip made with Windows text-to-speech, so everything after the microphone is real: the
app's recorder, the Gateway endpoint, the speech model (whisper-large-v3 through devthrottle.com), and the words
posted back into the frame.

**Comment box, Cockpit and phone - all PASS:**

- a microphone under the box; while recording the box says `Recording 0:06 - tap the square to stop`, with the red
  square and the level bars - `screens/cockpit-comment-recording.png`, `screens/phone-comment-recording.png`
- the words landed: `Please run the gateway suite first and tell me how long it takes.` (Cockpit) and `Please run
  the Gateway Suite first and tell me how long it takes.` (phone), with "Added what you said. Read it before you
  queue." - `screens/cockpit-comment-landed.png`, `screens/phone-comment-landed.png`
- each through `200 POST /wingman/utterance/{id}/complete`. The rig Gateway log for the first:
  `rawChars=65, chars=65, droppedSentences=0`, `cleanupApplied=False` - the words reached the box exactly as the
  speech model wrote them.

**Something else, Cockpit - all PASS:**

- Queue answer disabled until the box has words; the microphone under the box records and the words land:
  `Neither of those.  Split the sweet in half and run both halves overnight.` ("sweet" is the speech model
  mishearing the synthetic voice's "suite", passed through verbatim, as the transcription rule requires; the double
  space is also the model's) - `screens/cockpit-own-recording.png`, `screens/cockpit-own-landed.png`
- Queue answer enabled, queued, sent; the Conversation panel shows the question, then
  `Something else: "Neither of those. Split the sweet in half and run both halves overnight."` and "Delivered to
  the session" - `screens/cockpit-own-sent.png`. (The run was made twice, so two identical sends are listed; the
  first run's only failure was this proof's own text comparison, which did not collapse the double space the way
  the page displays it. Fixed in the driver, then re-run.)

**As the agent reads it** - this tree's `cc-dev-reports answers`, run as the rig session:

```
  questions[1]:
    unpark-first: Which parked suite should we bring back into the default run first?
      answer: OWN ANSWER (not one of your options)
      said: Neither of those.  Split the sweet in half and run both halves overnight.
```

and in `--json`: `"optionValue": "own-answer"`, `"optionLabel": "Something else"`,
`"ownAnswer": "Neither of those.  Split the sweet in half and run both halves overnight."`,
`"answerLabel": "OWN ANSWER (not one of your options)"`, `"status": "delivered"`.

## What is NOT proven

- **A real microphone and a real voice.** The audio was a synthetic voice through Chromium's fake capture device,
  in headless Chromium on Windows. No phone, no Safari, no real browser permission prompt.
- **The phone's "Something else"** and the **note box dictation** were not driven end to end; they are covered by
  the client tests only. The phone end to end covered the comment box.
- **The prompt text the session received** was not read back: the session's terminal buffer is drawn in
  cursor-addressed fragments. The prompt wording is covered by the Gateway compose tests (the own answer "is not
  one of the options" and carries the words verbatim).
- **The team path** (a team member's own answer going to the sender) and the **Team Questions page** are covered by
  Gateway tests only; the page's user interface is unchanged and was not exercised.
- **The failure states** (microphone does not open, transcription busy) are covered by client tests only.
- **The desktop Director** is out of scope, as the owner decided.

## A visible difference from the mock-up

The "Something else" box and the state chip under each microphone are drawn in the page script's own dark theme
(the same theme as its notes tray and question-state chip), so on a light report the box is black with white text,
while the mock-up draws it like the report's own light field. The comment box under it keeps the report's styling.
It reads clearly, but it does not match the mock-up; it is left for the coordinator to rule on rather than changed
here, because the theme is shared by every control the script draws.

## Review

A separate review session read the diff at 96718e9a6 (the first reviewer seat refused its model and the next seat
took it). It found no blocker and confirmed: the words reach the box byte for byte; every own-answer label is set on
the Gateway; a team member's own words go only to the sender; the shape check refuses an author option valued
`own-answer`; the frame can ask only to start and stop; and no new test passes with the feature removed. Its
findings and what was done:

1. **Should fix - fixed.** A cancel while the microphone was still opening (leave the report, or the agent
   republishes, while the permission prompt is up) left the microphone live with no owner: the cancel's dispose ran
   before the stream existed. `DevReportDictation.start` now checks after the open and disposes the recorder when
   the page has gone. Test: "lets go of a microphone that finishes opening after the page went away".
2. **Should fix - fixed.** A microphone that opens but never delivers audio (muted, dead input) left the box on
   "Starting the microphone" for ever with the button disabled and the microphone held. The host now arms the Speak
   dialog's six-second backstop and fails with the Speak dialog's own sentence, letting the microphone go; the
   first audio disarms it. Tests: "says so and lets the microphone go when it opens but never delivers audio" and
   "disarms the not-capturing backstop the moment audio arrives".
   Red then green for both: with the two production lines removed, 2 of 17 dictation host tests fail; with them,
   all of client-core passes, 2,571 of 2,571. Typecheck and eslint clean. The two apps were rebuilt, staged into the
   rig and the comment-box run repeated: PASS in the Cockpit and on the phone.
3. **Minor - not changed.** The host starts recording on a `dictation-start` with no user-gesture check and draws no
   recording sign of its own outside the frame. The reviewer agrees it is safe today: the frame's policy runs only
   the injected script, so a report's own markup cannot send the message. Left as hardening for later.
4. **Minor - not changed, deliberate.** Capture health for a report dictation is logged under the same surface tags
   as the Speak dialog, so the Transcription Health report counts them together. Chosen so a lost recording shows in
   the existing report; separating them is a later decision.
5. **Minor - stated.** The team path accepts an own answer and the tests prove where the words go, but no client
   sends one yet: the Team Questions page is unchanged. "Something else" reaches dev reports only in this change.
   The pull request says so.
