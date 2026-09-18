# Dictation Fixes - mission brief

    Status:    FINISHED 2026-09-16 (landed on main). internal#2037 was left open, waiting for the owner's release.
    Mission:   069ab8c3 (Dictation Fixes)
    Tracking:  thefrederiksen/devthrottle_internal#2040
    Branch:    mission/dictation-fixes (cut from origin/main at dce1391a)
    Conduct:   cc-devthrottle workflow instructions mission

This brief describes the work. How the mission is conducted - who builds, who inspects, who lands
what on main - lives only in the workflow above.

## Why

The owner dictates all day, and dictation is getting his words wrong in three ways: words he never
said appear at the start ("Yeah.", "you", "Bye."), the last word is sometimes cut off, and some
microphones are slow to start. The OpenSuperWhisper review of 2026-09-16 (full text in
internal#2040) found the causes:

- About 39% of desktop dictations open with an invented word, and the cause is our own ready cue:
  the Director plays it while the microphone is live, the microphone records it, and the speech
  model writes it down. Silencing only the cue removed the invented word in 13 of 16 clips.
- The stored raw transcript is still the post-gate text on every production path, so no sentence the
  evidence gate removes can be seen - the verbatim rule's audit is blind, and "words lost at the end"
  cannot be measured.
- Capture stops the instant Send is pressed; a word said on the Send click can be clipped.
- Microphone start time is not logged, the user cannot see which microphone is slow, and the Speak
  dialog enumerates devices on the interface thread (57-243 ms per open).

When this mission is finished: the cue never reaches the model, the stored raw text shows every gate
removal, 250 ms of audio after Send is kept, each start is logged with its first-audio time, the
microphone selector shows how slow each microphone is, and the dialog no longer waits on enumeration.

## The work, in landing order

Each item is one pull request into main from the mission branch, a fix and its test together.

| Slice | Item | Repository |
|---|---|---|
| A | devthrottle#2926 - service stores the pipeline's raw text, delivers the gated text, carries dropped sentences | devthrottle |
| B | devthrottle#2925 - blank the cue out of the audio sent for transcription | devthrottle |
| C | devthrottle#2927 - 250 ms capture tail after Send and Pause, desktop and web recorder | devthrottle |
| D | devthrottle#2929 - microphone enumeration off the interface thread | devthrottle |
| E | devthrottle#2928 - first-audio log line per start, measured start time in the selector | devthrottle |
| F | internal#2038 - correct section 7 of the 9 September research, and the transcription architecture doc | devthrottle_internal |
| - | internal#2037 - measure gate removals at the last position | waits (see rulings) |

Each issue's own "Done when" is the acceptance test for its slice.

## Rulings

Stated by the owner:

- R1. Only the "Now" list of internal#2040 is in scope. The "Waiting" list (devthrottle#2930
  warm microphone, devthrottle#2931 phone opening sentence, internal#2039 model switch) is on hold by
  the owner - do not start it.
- R2. internal#2037 waits for the owner's own next release to put #2926 live. The mission does not cut
  or deploy a release. The mission reports with #2037 open and named as pending.

Inferred by the Architect from the issues and the code (not stated by the owner):

- R3 (#2926). The fix is in `GatewayTranscriptionService`, the seam production uses. It stores
  `PartTranscript.Raw` as `rawText`, returns `Delivered`, and carries `Dropped`. The method named
  `TranscribeRawAsync` must not return gated text: rename it or remove it. The test is at the service
  level, never only the pipeline.
- R4 (#2925). The cue is played by `SpeakDialog` (`_audioCue.PlayReady()` on capture live), not by the
  recorder. The cue is FOUND in the captured audio and only its own samples (minus 10 ms, plus a 50 ms
  ring-down) are written as zeros in the WAV sent for transcription (phase 6, ruling K1 in HANDOFF.md).
  Placing it by clocks was tried in phases 1 to 5 and failed inspection each time: every clock involved
  has unmeasured slack that either leaves cue or erases words. The search uses the exact synthesised
  cue, normalised cross-correlation, threshold 0.5, in the first 1.5 s after first audio, and runs only
  when playback reported successful completion on that recorder (ruling L1, phase 7). If the user stops before the cue has finished playing, the
  recorder waits for the playback-end report (bounded, ruling G2) so the whole cue is captured before
  the search. Capture-health byte counts stay on the unblanked capture. Length never changes.
- R5 (#2925). If no cue was played, or the cue is not found in the audio, blank NOTHING and log it. Blanking what we cannot prove was the cue could delete the owner's real words.
  Resume starts a fresh recorder and plays the cue again, so the rule applies per recorder start.
- R6 (#2927). 250 ms after Send and after Pause, then the existing stop drain and the 600 ms run-out
  pad, unchanged. The dialog still closes at once. The web recorder in
  `packages/client-core/src/dictation/recorder.ts` gets the same tail before its flush-and-stop.
- R7 (#2928). "Typical start time" is the median of that device's most recent 20 starts, kept on this
  machine next to the persisted microphone choice. A device never used shows no figure - never a
  guessed one. The selector text is laid out by the client; the number is measured, not derived.
- R8 (#2929). Resolve the device number off the interface thread, open the microphone as soon as it is
  resolved, fill the selector when the list arrives. The close-during-startup tests must still pass.
- R9 (#2038). Correct the documents; do NOT edit `tools/transcription-lab/corpus/labels-final.json`.
  Say in the document that relabelling is a separate recorded decision.

## Where it runs

The desktop dictation code is Windows audio (NAudio). Code and tests are built and proven on the
Windows machine SOREN_NORTH, in a worktree of `mission/dictation-fixes` there. The mission record
(this folder) lives on the same branch.

## Out of scope

- Everything in the "Waiting" list (R1).
- Changing the speech model, its settings, or the evidence gate's thresholds.
- A phrase blacklist for invented words, or moving the cue before the microphone opens (both rejected
  by the review).
- Committing the OpenSuperWhisper review document itself - its author session owns that.
- Cutting or deploying a release (R2).
