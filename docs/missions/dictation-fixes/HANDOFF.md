# Dictation Fixes - handoff note

    Updated: 2026-09-16 (Architect, seat 1)

- Phase: 1 - build slices A to F (see BRIEF.md). Manager seated: ae425f9a on SOREN_NORTH.
- Branch: mission/dictation-fixes on origin. Build and test on SOREN_NORTH in a worktree of it:
  `git fetch origin && git worktree add ..\devthrottle-dictation-fixes mission/dictation-fixes`
  (in D:\ReposFred\devthrottle).
- Order: A, B, C, D, E in devthrottle; F in devthrottle_internal (its own branch from its origin/main,
  `mission/dictation-fixes-docs`).
- Each slice: one commit set on the mission branch, pushed as soon as it is green, its test watched
  failing with the fix reverted. Do NOT open pull requests to main and do NOT merge - the Architect
  lands every slice after inspection.
- Next: the Manager seats Workers for the slices and reports to the Architect once, when all six are
  pushed, with a short per-slice note in docs/missions/dictation-fixes/REPORT.md (what changed, which
  test proves it, what is not proven).

## Update 2026-09-16 - phase 1 done

- Slices A to F pushed (devthrottle head 60f96d9e, internal mission/dictation-fixes-docs aa2e1c6d).
  Builders' account: REPORT.md. Manager ae425f9a stopped.
- Inspection one seated: Codex session 11ae1e2f on SOREN_NORTH, writing INSPECTION-1.md onto this
  branch. Next: failures go to a fresh Manager; a pass means the Architect opens one pull request per
  slice and lands them in order A to F.

## Update 2026-09-16 - inspection one FAILED, phase 2 = fix its five findings

Read INSPECTION-1.md. Fix every finding on the same branches, each fix with a test watched failing.
Architect rulings for the fixes:

- F1 (cue tail survives). Do not use a guessed constant. When playback finishes, mark the cue end as
  PENDING; the blanking boundary is the captured byte count after the first capture buffer that
  arrives AFTER that moment, plus the 50 ms ring-down. Log the delivered-bytes lag between the two.
  Test: hold the last cue capture buffer until after playback-finished fires, and prove every cue byte
  and the 50 ms after it are zeros on both the transcribe and the background Send paths.
- F2 (device query on the interface thread). Compute the device description off the interface thread
  together with the device resolution, and hand it into `MicAudioCapture` so its constructor no longer
  queries Windows. Leave where recording is started unchanged (the NAudio thread concern stands). Same
  for the microphone switch path. Test the production constructor does not query devices.
- F3 (no-audio start has no diagnostic). When the ready window times out with no audio, log one line
  with the device name and the click and start timings, the same shape as the first-audio line. Test it.
- F4 (web inactive recorder). After the 250 ms tail, if the recorder is already inactive, do not call
  stop(); return the chunks already delivered. Make the test fake throw InvalidStateError on stop()
  when inactive, as browsers do, and test that path.
- F5 (research prose). Rewrite the operative sentences in sections 7 and 9 (lines around 156 and
  220-223) so no sentence states the openings were the owner's speech; the old reading may appear only
  as history. Branch mission/dictation-fixes-docs in devthrottle_internal.
- Update REPORT.md with a short "Phase 2" section. Push. One single-line message to the Architect.

## Update 2026-09-16 - inspection two FAILED, phase 3 = fix its three findings

Read INSPECTION-2.md. My phase 2 ruling F1 was wrong: "the first buffer after the cue's end" does not
contain the cue's last samples when capture is more than one buffer behind. It is replaced below.

- G1 (cue boundary, replaces F1). Use the clock, not buffer arrival. When the first audio buffer arrives
  (capture live - the moment the cue is triggered) record the captured byte count B0 and a monotonic
  time T0. When playback reports its end at time T1, the boundary is
  B0 + bytes(T1 - T0) + 50 ms ring-down + 50 ms margin for the first buffer's own delivery lag,
  clamped to the captured length at snapshot time. Log B0, T1 - T0 and the boundary. Capture backlog
  after T0 no longer matters. Tests: a backlog of zero, one, two and three buffers at the cue's end must
  all produce zeros through the cue and the ring-down, on both TranscribeAsync and StopAndGetWavAsync.
  The time source must be injectable so the tests do not sleep.
- G2 (Send before the cue's end). If a cue was started and its end has not been reported when the stop
  tail finishes, wait for the end report, bounded by the cue's own existing completion allowance, then
  compute the boundary as G1 and snapshot. If it never reports, blank nothing and log (ruling R5 stands).
  The dialog still closes at once; this wait is on the background path. Test: Send before the report,
  report arriving during and after the tail.
- G3 (resolution slower than the ready window). When device resolution completes, start the microphone
  only if the dialog is still in GETTING READY; otherwise build nothing. If the ready window runs out
  while resolution is still outstanding, log one line saying so, with the elapsed time and the saved
  device name that was being resolved. Test: resolution held past the timeout with the dialog open -
  no recorder is built, and the line is logged.
- Update BRIEF.md ruling R4 to describe G1, add a Phase 3 section to REPORT.md, push, one single-line
  message to the Architect.

## Update 2026-09-16 - inspection three FAILED, phase 4 = two findings

Read INSPECTION-3.md.

- H1 (the 50 ms first-buffer margin may blank the start of speech). The margin must be MEASURED, not
  assumed. Measure it from real audio: in the local corpus on this machine
  (`tools/transcription-lab/corpus/clips`, and the cue search in
  `docs/research/opensuperwhisper/evidence/cue_scan.py` if it is present on this machine), for every
  clip where the cue is found, measure the gap from the cue's end to the first speech (the speech
  detector the lab already uses, or a clear energy rise). Write the distribution (count, minimum,
  5th percentile, median) and the script to `docs/missions/dictation-fixes/evidence/`. Audio and
  transcripts stay local - commit only numbers and the script.
  Then decide by the rule below, and state which case applied in REPORT.md:
  - if the 5th percentile gap is at least 150 ms (the 50 ms ring-down plus the 50 ms margin plus
    50 ms to spare), keep the margin, and cite the figures in the code comment beside it;
  - otherwise remove the margin (boundary = B0 + bytes(T1 - T0) + 50 ms ring-down), and say in the
    code comment that a late first buffer can leave up to one buffer of cue tail.
  In either case add a zero-lag test (first buffer delivered the moment it fills) that proves speech
  starting just after the boundary is untouched, alongside the existing backlog tests.
- H2 (a first measured start time does not appear in the open selector). When the dialog records a
  start time, refresh that device's entry in the selector in place, keeping the current selection and
  without triggering a microphone switch. Test: a device with no history, first audio arrives, the
  open selector shows its figure.
- Add a Phase 4 section to REPORT.md and push. Do not send any fleet message: the Architect watches the
  branch for the Phase 4 section.

## Update 2026-09-16 - phase 4 done; inspection four seated

H1 applied its second case (margin removed; evidence in evidence/). H2 fixed. BRIEF R4 amended by the
Architect. Next: inspection four writes INSPECTION-4.md. A pass means the Architect lands slices A to F.

## Update 2026-09-16 - inspection four FAILED, phase 5 = measure the first callback's lag

Read INSPECTION-4.md. A fixed margin cannot be right in both directions (inspection three: it erases
speech; inspection four: without it, a first callback up to 150 ms late leaves cue). So the lag must be
MEASURED from the capture itself. Ruling J1, replacing the T0 anchor of G1/H1:

- Capture buffers are filled on the device's own steady clock: buffer k finishes filling at
  F0 + (C_k - C_0)/byteRate, where C_k is the cumulative captured byte count at the end of buffer k,
  and it is delivered at arrival time a_k >= its fill time. So F0 <= a_k - (C_k - C_0)/byteRate for
  every k, and the estimate is F0 = the minimum of that over the buffers delivered in the first
  10 seconds of capture (bounded so clock drift stays negligible). One promptly delivered buffer
  anywhere in that window makes the estimate exact.
- The estimate is computed at SNAPSHOT time (after the stop tail, the drain and the G2 wait), when the
  arrival times are known. Record each buffer's arrival time and cumulative byte count as it arrives,
  on the injectable clock.
- Boundary = C_0 + bytes(T1 - F0) + 50 ms ring-down, clamped to the captured length. T1 is the
  playback-end report time, as now. No other margin.
- Log C_0, the estimated first-callback lag (a_0 - F0), T1 - F0 and the boundary.
- If every buffer in the window is uniformly late, the residual cue equals the smallest lag observed;
  say so in the code comment - that is the only remaining gap, and it requires the whole first
  10 seconds of capture to be stalled.
- Tests, both paths (TranscribeAsync and StopAndGetWavAsync): first callback late by 0, 50, 100 and
  150 ms followed by promptly delivered buffers - every cue byte and the ring-down are zeros AND the
  first 50 ms of speech right after the ring-down is untouched. Keep the backlog-at-playback-end tests.
  Add one test pinning the uniformly-late residual. Each watched red with the estimate replaced by
  the old first-callback anchor.
- Update REPORT.md with a Phase 5 section and push. Do not send any fleet message. The Architect
  amends BRIEF R4.

## Update 2026-09-16 - phase 5 done; inspection five seated

J1 built (041ff4d2, cf8a39b9). Next: INSPECTION-5.md; a pass means the Architect lands slices A to F.

## Update 2026-09-16 - inspection five FAILED, phase 6 = find the cue in the audio itself

Read INSPECTION-5.md. Two more timing faults (a dropped capture buffer; NAudio's padded output buffer
makes PlaybackStopped up to ~100 ms late). Five inspections have now shown that placing the cue by
CLOCKS cannot be made right: every clock (capture callbacks, playback callbacks, output latency) has
its own unmeasured slack, and any slack either leaves cue or erases words. The research itself did not
use clocks - it FOUND the cue in the recorded audio (cue_scan.py, found in 30 of 44, control 1 of 44)
and silenced exactly those samples. Ruling K1 replaces J1, G1 and H1:

- Detect, do not predict. At snapshot time, synthesise the exact ready cue (share ONE generator with
  DesktopAudioCue - same sweep, duration and envelope) at the CAPTURE sample rate, and search the
  captured PCM from its start to 1.5 s after the first-audio position with normalised
  cross-correlation. Threshold 0.5 (the research's value). If found, write zeros from the match start
  minus 10 ms to the match end plus 50 ms ring-down, clamped to the clip. Length never changes.
- Blank only when a cue was actually played on this recorder AND it is found in the audio. Not found
  (quiet speakers, headset, echo-free device) - blank nothing and log the best score. There is no
  clock-based alternative behind it (no fallback).
- Keep the playback-end report ONLY as a wait: if Send comes before the cue has finished playing, the
  background path waits for the report (bounded, ruling G2) so the whole cue is in the capture before
  the search. It no longer sets any position.
- Remove the J1 arrival-time machinery (CueClockWindow, F0 estimate, the clock-based boundary) and its
  tests; nothing should compute the cue position from time any more.
- Log per snapshot: cue played yes/no, best score, match position, blanked span.
- Proof on REAL audio, not only fakes: run the C# detector (a small test-only harness or a console
  entry in the test project, not product code) over the local corpus clips on this machine
  (tools/transcription-lab/corpus/clips) and write to docs/missions/dictation-fixes/evidence/ the
  per-clip result next to cue_scan.py's (found / not found, position), plus, for every clip where it
  blanks, the gap between the blanked span's end and the first speech (the lab's speech detector).
  Acceptance: it finds the cue in the same clips the research did (explain any difference), finds
  nothing in the three May clips recorded before the cue shipped, a search over the middle of each
  clip finds at most the research's 1 of 44, and no blanked span overlaps detected speech. Commit
  numbers and scripts only - no audio, no transcripts.
- Unit tests, both paths: cue present at 0, 50, 100, 150 ms positions; a dropped buffer before the
  words; a late playback report; no cue played; cue played but not audible (blank nothing); words
  right after the ring-down untouched; a speech-like signal (not the cue) in the window is not
  blanked. Each watched red with its part of the fix removed.
- Update REPORT.md with a Phase 6 section and push. No fleet messages. If the real-audio acceptance
  cannot be met, write a BLOCKED Phase 6 section with the figures, push, and stop - do not tune the
  threshold to pass.

## Update 2026-09-16 - phase 6 done; inspection six seated

K1 built (afbbc864 to 301a3474); real-audio proof in evidence/. Accepted limit: a buffer dropped inside the cue can blank up to that buffer past the ring-down (pinned). Next: INSPECTION-6.md.

## Update 2026-09-16 - inspection six FAILED, phase 7 = four findings

Read INSPECTION-6.md. Search, speed (45 ms on a 60 s clip) and the real-audio proof all held. The
two cue findings share one cause: the "cue played" gate means "we tried to play it", not "it played".
Rulings:

- L1 (findings 1 and 2). The gate is: playback REPORTED SUCCESSFUL COMPLETION on this recorder
  (PlaybackStopped with no error) - that is when the whole cue is known to have sounded. Set the gate
  there, not before playback. A playback error, an initialisation failure, or no report within the
  bounded wait (G2) means blank NOTHING and log why (ruling R5, restored to its original sense). The
  pre-stop wait for the report stays. Tests: playback error after a partial cue (use the inspector's
  timeline: 50 ms of cue at 20% amplitude, speech after the ring-down) blanks nothing; playback never
  started blanks nothing; the dialog path proves the gate is set only by a successful completion
  report. Each watched red with the old gate.
- L2 (finding 3). The Wake Word test dialog: after the background device query, build nothing if the
  window has closed; and if a recorder is built, make sure a close can always stop it. Test that holds
  the query while the dialog closes and proves no microphone is left recording.
- L3 (finding 4). devthrottle_internal branch mission/dictation-fixes-docs: make the research header
  say fourteen of the doubt segments were the cue, matching section 7.
- Phase 7 section in REPORT.md, push. No fleet messages. BLOCKED section if blocked.

## Update 2026-09-16 - phase 7 done; inspection seven seated

L1-L3 built. Accepted limits: a buffer dropped inside the cue; a driver ending playback early without an error. Next: INSPECTION-7.md.

## Final - 2026-09-16

Inspection seven passed. Landed on main: devthrottle#2967 (#2926), #2968 (#2927 web), #2969 (#2925,
#2927 desktop, #2928, #2929); devthrottle_internal#2070 (#2038). The mission is finished. internal#2037
stayed open: it runs once the owner's next Gateway deploy puts #2926 live and a few days of rows exist.
The .NET continuous integration job failed on unrelated tests in different places on each run
(streaming, admin feedback, status palette); #2968 changed only web files and was merged on that
basis.
