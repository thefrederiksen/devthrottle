# Wingman error and retry - the proof

Taken on 20 September 2026 from a local Gateway built from this branch. Every picture is a real page: the
Cockpit's Sessions page and the phone app's home page, served by that Gateway from the bundles this branch builds.

## How the judge was made to fail

The address of the model service is a constant in the product, and changing the provider is outside this mission.
So nothing was stubbed. The rig's key store was given a key the model service rejects, and the production call
was really made and really refused ("401 Unauthorized", stored as the failure word `unavailable`, shown as
"The model could not be reached."). Putting the machine's real key into the rig's key store is what made the
next booked retry succeed. The key is read by the rig and is never printed.

The rig is a small program, not a test in any suite. It boots the real `GatewayHost`, joins a stand-in Director
on the real tunnel (the same `FakeTunnelDirector` the hosted tests use), pushes three sessions, answers the
screen reads, and lets the Gateway's own forty-five second sweep carry the retries. Its source is in `rig/`
(`Program.cs.txt`, `Rig.csproj.txt`, `shot.js.txt`); the screenshots are taken by that script with Playwright.
It was built outside the repository and named `CcDirector.Gateway.Tests` so it can use the same internal entry
points the hosted tests use. It ran as its own process on a port the operating system chose, with its own data
folder, so it touched no running Gateway.

## The pictures

Each has a `-cockpit` and a `-mobile` version. The block is the one shared component in both.

1. `1-failed-counting-down` - two sessions whose reading failed for real. Neither is in voice mode. The tag, the
   plain reason, "retry 1 of 8 in 51 seconds" counting down to the Gateway's booked time, and the button.
2. `2-ask-again-pressed` - the button was pressed on the first session. One attempt was made at once, it failed
   too, and the Gateway's answer is under the button: "That attempt failed too. The model could not be reached.
   The schedule is unchanged." The rig printed `retriesMade 0 -> 0` and the same booked time before and after.
3. `3-schedule-used-up` - "The Wingman could not read this stop. Nothing more is scheduled." The button is still
   there. The second session, beside it, still has a retry booked and says so.
4. `4-retry-succeeded-tag-cleared` - the real key went in, nobody pressed anything, the second session's booked
   retry ran and succeeded, and its error is gone; the card now carries what the Wingman read. The first session
   stays used up, because nothing is booked for it.
5. `5-picker-as-the-model-read-it` - a third session stopped on a picker and the real model read it.
6. `6-bad-button-list-no-buttons-no-error` - see below.

## What is seated rather than produced, said plainly

- **The used-up schedule (picture 3).** Waiting out eight real retries takes seventy-eight minutes. The first
  session's stored failed reading was moved to its last step - seven retries made, the eighth due in two
  seconds - and then left alone. The Gateway's own sweep found it due, made the real eighth attempt, which
  really failed, and booked nothing. The exact schedule, one retry at a time from the first to the eighth, is
  proved by the test `TheSchedule_IsOneOneOneFiveFiveFiveThirtyThirty_AndThenItStops` with a clock the test moves.
- **The bad button list (picture 6).** The real model read the picker correctly and offered two good options,
  so it could not be made to show this case. The real reading from picture 5 was copied onto a fourth session
  and changed the way the contract check changes an answer that offers one option: the whole list removed, the
  reading kept, the reason recorded in the contract's own words. That record was stored by the rig, not produced
  by a model call. What the picture shows is the display half: a reading with a dropped button list has its label
  on the card and NO Wingman error. The roster card never draws buttons - they live on the session page - so
  pictures 5 and 6 look alike, which is the point. That a bad list is dropped, kept, narrated and recorded is
  proved by the fourteen renamed contract tests and by
  `ABadButtonList_IsNotAFailure_TheReadingIsKeptAndNarrated_AndNothingIsRetried`, which drives the real service.

## What this proof does not cover

- A real Director and a real agent. The Director here is a stand-in that pushes its roster every five seconds.
  The first version of the rig pushed once, and the retry carrier rightly treated a roster older than twenty
  seconds as not live and started nothing - silently. It now writes a log line when a due retry is not started,
  with the reason; the retry stays due.
- The voice screens. Their Wingman error card is covered by the fold tests, not photographed here.
- Why the model does not answer. With the real key in, one run of this rig saw the model fail to answer within
  thirty seconds three times in a row; the schedule retried at one, two and three minutes as booked. That is the
  fault this mission puts a schedule around, not one it explains.

## One defect the pictures found

The first press photographed badly: pressing "Ask again" makes the row "being read", the Gateway rightly stamps
no error while it is being read, and the block - with the press's answer - unmounted under the press. The press
now lives per session outside the mounted row, the block keeps its place while a press is in flight, and
`keeps its place, and then the answer, when the Gateway clears the error while the press is being read` pins it.
