# Dictation Fixes - Manager report, phase 1 (slices A to F)

    Written:   2026-09-16 by the Manager (session ae425f9a, SOREN_NORTH)
    Branch:    mission/dictation-fixes (devthrottle), mission/dictation-fixes-docs (devthrottle_internal)
    Status:    all six slices pushed; nothing opened or merged to main; not yet inspected

Every slice was built in a worktree of its mission branch, its test was watched failing with the fix
removed, and it was pushed only after the fix was restored and the test was green again.

## Slice A - the stored raw text is the model's full output (devthrottle#2926)

Commit 20880452e.

**What changed.** The pipeline method `TranscribeRawAsync`, which returned the gated text, is now
`TranscribeUncorrectedAsync` and returns the whole part: the model's full output, the delivered
(gated) text, and the dropped sentences. `GatewayTranscriptionService.TranscribeAsync` runs the
dictionary on the delivered text, returns the delivered text, stores the model's full output as
`RawText`, and carries the dropped sentences on `GatewayTranscriptionResult.DroppedSentences`. The Notes
segment method `TranscribeSegmentRawAsync`, which also returned gated text under a "raw" name, is renamed
`TranscribeSegmentUncorrectedAsync` (behaviour unchanged).

**What proves it.** `GatewayTranscriptionServiceTranscriptStoreTests.Transcribe_GateDropsASentence_StoredRawKeepsIt_CleanedAndReturnedTextDoNot`
drives the SERVICE with a clip whose middle sentence sits over silence: the stored `RawText` holds the
sentence, `CleanedText` and the returned text do not, and the dropped sentence is on the result. With
the service storing the delivered text again, it fails with exactly the reported symptom (the stored raw
text is missing "Thank you.").

**Not proven.** That production rows now show removals - that needs the owner's release (ruling R2).
internal#2037 (measuring removals at the last position) is therefore still open and pending.

## Slice B - the ready cue is blanked out of the transcribed audio (devthrottle#2925)

Commit a778337bc.

**What changed.** `DesktopAudioCue.PlayReady` takes a callback it calls only when NAudio reports
`PlaybackStopped` with no error; a failed or timed-out playback never reports an end. The Speak dialog
tells the recorder the cue started, and hands the cue's end to the recorder the cue was played into.
`BatchDictationRecorder.NoteReadyCueFinished` records the captured byte position plus 50 ms, and both the
transcribe path and the save-then-transcribe path (the background Send) write zeros over every byte
before it. Length never changes; capture health reads the microphone's own counters, untouched. No
reported end means nothing is blanked, logged as a warning when a cue did play (ruling R5). Resume and a
microphone switch build a new recorder and play the cue again, so the rule is per start.

**What proves it.** `BatchDictationRecorderReadyCueTests` (four tests: bytes before the cue's end reach
transcription as zeros and bytes after are untouched, on both paths; no reported end blanks nothing; only
the first report counts) and `SpeakDialogReadyCueBlankingTests` (the dialog wires the cue's end to its
recorder). Removing the blanking turns the recorder tests red; removing the dialog's hand-over turns the
dialog test red.

**Not proven.**
- That `PlaybackStopped` fires on a real output device at the moment the cue actually ends. The tests
  use a seam in place of the speakers; no test plays sound.
- That 50 ms of ring-down is enough on real hardware. It is the value the review's removal experiment
  used. The captured byte position can lag the sound in the room by up to one capture buffer (50 ms),
  so a cue tail could survive on some machines. The after-release figure (desktop transcripts opening
  with a short sentence, about 39% today) is the real test.
- A Send pressed before the cue's end is reported blanks nothing, by design.

## Slice C - 250 ms of capture after Send and Pause (devthrottle#2927)

Commit fcbc23854.

**What changed.** The desktop recorder waits 250 ms (`StopTailMs`) before asking the microphone to stop,
on both stop paths; the stop drain and the 600 ms run-out pad are unchanged. The dialog still closes (Send)
or shows TRANSCRIBING (Pause, Insert) at once. The web recorder (`packages/client-core/src/dictation/recorder.ts`)
waits the same 250 ms (`STOP_TAIL_MS`) before its flush-and-stop, and fails loudly instead of hanging if
Cancel releases the microphone during the tail.

**What proves it.** `BatchDictationRecorderStopTailTests` (audio delivered 60 ms after Send is in the
transcribed clip and in the saved clip) and the `MicRecorder.stop tail` tests in `recorder.test.ts` (audio
delivered 100 ms after Send is in the clip; the recorder is not stopped before 250 ms; a cancel during the
tail fails loudly). Removing either tail turns its tests red.

**Not proven, and one behaviour to know about.** On the phone and in the Cockpit, the dictation dialog
awaits `stop()` before it closes on Send, so those surfaces now close 250 ms later than before. The
brief's "the dialog still closes at once" holds on the desktop only. Nobody has listened to a clip to
confirm the last word is now whole.

## Slice D - microphone enumeration off the interface thread (devthrottle#2929)

Commits 4ba257964 and 5ee8c5c22.

**What changed.** The Speak dialog shows GETTING READY first, then resolves the saved microphone and lists
the devices on background threads. The microphone opens as soon as the saved choice is resolved; the
selector is filled whenever the list arrives. The second commit removed a close check I had added after
the resolve: removing it left every test green, because the recorder start already refuses once the window
has closed - that existing check is the guard, and removing it turns the close test red.

**What proves it.** `SpeakDialogMicEnumerationOffUiThreadTests`: with the device list held at a barrier,
the dialog shows GETTING READY, has opened the microphone on the resolved device, and the selector is still
empty; releasing the list fills it with the right device selected. A second test closes the window while
the microphone is being resolved and proves no recorder is built. Moving the queries back onto the
interface thread turns the first test red. The existing close-during-startup tests still pass.

**Not proven.** `MicAudioCapture`'s constructor still asks Windows for the default device's name, on the
interface thread, when the recorder starts. It was left in place: moving the recorder start off the
interface thread would change which thread NAudio reports the end of recording on, a riskier change than
this slice called for. The 57-243 ms saving was not re-measured in the running app.

## Slice E - first-audio log line and measured start times in the selector (devthrottle#2928)

Commit 7ac359b0d.

**What changed.** Every recorder start logs one line: device, milliseconds from the click (open, Resume
or microphone switch) to first audio, and milliseconds from asking the device to start recording to first
audio. The dialog records the second figure per device in `config.json` under
`dictation.mic_start_ms`, beside `dictation.mic_device_name`, in the background. The selector shows the
median of the device's last 20 starts, as "takes 0.7 s to wake up"; a device with no recorded start shows
its name alone (ruling R7).

**What proves it.** `MicStartTimesTests`: the median (not the mean), the 20-start cap, never-used shows no
figure, the label text, a round trip through `config.json` that leaves the saved choice alone, the recorder
measuring its first audio, and the dialog recording each start and the selector carrying the figure.
Removing the dialog's recording, the selector's figure, or the recorder's measurement each turns a test
red.

**Not proven, and two choices made.** The selector's figure is the time from asking the device to start
recording to first audio - the device's own wake-up - not the click-to-first-audio time, which includes our
own start-up work (both are in the log line). The "Windows default" entry keeps its own history under its
own name ("Default - ..."), separate from the same physical device's entry. Neither the log line nor the
selector text was looked at in the running app.

## Slice F - the research correction (internal#2038)

Commit aa2e1c6d on `mission/dictation-fixes-docs` in devthrottle_internal.

**What changed.** `docs/research/transcription/2026-09-09-unspoken-words.md` sections 7, 8 and 9, and the
v2.0 and v2.1 entries of `docs/architecture/transcription.html`, now say the fourteen opening "doubt"
segments were the recorded ready cue, with the evidence and a pointer to the OpenSuperWhisper review
section 1; that the "doubtful touched" column reads the other way round; and that no version of the gate
can catch a loud non-speech artefact. Per ruling R9 the label file is not edited, and both documents say
relabelling is a separate, recorded decision.

**What proves it.** Read against the label file on this machine (read only): all fourteen opening doubt
segments start at 0.00-0.06 s and the speech detector found no speech in any of them - exactly the fourteen
the detector-only rule removed. The research also listed "you" once; there are two, and the count is fixed.

**Not proven.** Two of the sixteen speech-level doubt segments ("Cool." with speech detected, and a
mid-clip "Thank you.") are not explained by the cue and are left as doubt. The label file is gitignored,
although the research lists it among the tracked artefacts - noted, not changed.

## Gates run

- Default local gate (`scripts\test-local.ps1`) on the final branch: all 8 suites completed and green
  (Avalonia 495, Core.UnitTests 479, Engine 63, HostedAgent 88, Launcher 195, Terminal 25, setup 25,
  setup-engine 553).
- Parked Gateway suite, filtered to transcription, dictation, voice and recording: 259 of 259 green.
- Gateway.UnitTests in full, after slice A: 4,815 passed, 8 failed - all eight could not connect to
  PostgreSQL, because they were run directly rather than through the gate that starts one. Not a
  regression, but not a green either.
- Web: the whole client-core suite (104 files) green; the phone and Cockpit dictation tests green; client-core
  type check clean.
- NOT run: the full `-Parked` release gate, Core.Tests, Python tests.

## What I got wrong

After the slice D revert proof I restored a file with a move that kept its OLD timestamp, so the build did
not recompile it and the next test run used the mutated binary: one red test. That red run's commit
(5ee8c5c22) had already been pushed by the same command. The source on the branch was correct - a forced
rebuild ran 488 of 488 green - but for a few minutes the branch carried a commit whose recorded run was red.

## Phase 2 - the five inspection-one findings

Manager dd140801, 2026-09-16. Each fix follows the Architect's phase 2 ruling in HANDOFF.md, and each was
committed BEFORE its revert proof, then the fix was removed, the tests watched red, and the fix restored and
rebuilt (no `--no-build` run after a restore).

**F1 - the cue's tail survived (#2925), 2a2b8f3b7.** `NoteReadyCueFinished` no longer fixes the boundary. It
marks the cue end PENDING and records the bytes delivered so far; the first capture buffer that arrives after
that sets the boundary at the captured byte count plus the 50 ms ring-down, and logs the delivery lag between
the two. If no buffer arrives before the drain ends, the boundary is fixed at stop, so everything captured is
blanked - every byte was delivered before the cue's reported end. Proof: new tests hold the last 50 ms cue
buffer until after playback-finished fires, then deliver 50 ms of ring-down and the words, and check every cue
byte and the ring-down are zeros on both `TranscribeAsync` and `StopAndGetWavAsync`. With the fix removed, six
of eight ready-cue tests went red with byte 9600 (the start of the ring-down) surviving. The existing tests
now deliver audio in 50 ms buffers like the real capture.
*Cost, accepted by the ruling:* when capture is NOT behind, the first buffer after the report is the user's,
so up to 50 ms after the cue plus the 50 ms ring-down is blanked. *Not proven:* real capture/playback timing
on hardware - a lag of more than one buffer still leaves cue samples in later buffers.

**F2 - device queries on the interface thread (#2929), 130d537b7.** `MicAudioCapture` now takes the device's
display name and never queries Windows in its constructor; `BatchDictationRecorder`'s production constructor
requires it. The Speak dialog resolves number and name together in the background
(`MicDevices.ResolveWithDescription`), and the microphone switch uses the name from the list already enumerated
in the background. Where recording is started is unchanged. The wake-word test dialog also resolves the name
with `Task.Run`. Proof: `MicCaptureConstructionQueriesNoDeviceTests` uses device number 4096, which no machine
has, so any query throws. With the constructor query and the switch-path query put back, all three went red
(`MmException: BadDeviceId` for the constructor and the recorder's production microphone; the dialog reached
ERROR on the switch). *Not proven:* where `StartRecording` itself runs is untouched, as ruled.

**F3 - a no-audio start had no diagnostic (#2928), 53d2a129e.** When the ready window runs out, the dialog
asks the recorder for one line in the first-audio shape:
`no first audio: device="...", clickToTimeoutMs=N, startRecordingToTimeoutMs=M`. If the recorder never finished
starting, the dialog writes the same line with the device it asked for and `startRecordingToTimeoutMs=unknown`.
Proof: a recorder test (line present when no audio arrived, absent once audio did) and a dialog test through
the real timeout handler, both reading the redirected log. With the call removed both went red. The dialog test
fires the handler through a test seam, because the headless platform does not run dispatcher timers - the
six-second timer wiring itself is unchanged and not re-proven.

**F4 - web recorder inactive during the tail (#2927), 714797611.** After the 250 ms tail, an inactive recorder
is not stopped; the chunks already delivered are returned, the stream released. The test fake now throws
`InvalidStateError` on `stop()` when inactive, as browsers do. Proof: a new test turns the recorder inactive
during the tail after a final delivery and expects all 7 bytes; with the fix removed it rejected with the
InvalidStateError message, and the existing inactive-recorder test also went red. *Not proven:* a browser
that has made the recorder inactive but not yet dispatched its final `dataavailable` - those bytes would be
missed; the ruling returns what was delivered.

**F5 - research prose (internal#2038), devthrottle_internal 2e6a33a3 on mission/dictation-fixes-docs.**
Sections 7 and 8 of `2026-09-09-unspoken-words.md` now state in the main text that the fourteen openings were
the recorded ready cue; the 9 September reading appears only under a marked *History* note, withdrawn. The
label file is untouched. Check: the three operative phrases the inspector cited ("the owner's real leading
interjections", "deletes 14 of the owner's real", "the detector misses short words") are present at aa2e1c6d
and absent now. The architecture page already described that reading in the past tense and was not changed.

### Gates run in phase 2

- Default local gate on the final branch: 7 of 8 suites green (Avalonia 503, Core.UnitTests 479, Engine 63,
  HostedAgent 88, Terminal 25, setup 25, setup-engine 553). **Launcher 193 of 195: two
  `LauncherDeclaredCapabilitiesTests` failed** - they ask the kernel whether this machine's launcher restart
  signal is armed, and a launcher on SOREN_NORTH now has it armed. The same two fail on the phase 1 code
  (e5befffb5) that was green this morning, and this branch changes no launcher code. Environmental, not a
  regression - but the gate is not green on this machine right now.
- Web: whole client-core suite, 104 files and 1,174 tests green; client-core type check clean.
- NOT run: `-Parked`, Python tests.

## Phase 3 - the three inspection-two findings

    Written:   2026-09-16 by the Manager (session 8440bb06, SOREN_NORTH)
    Commit:    092faed40 on mission/dictation-fixes (code and tests); this record in the commit after it
    Status:    pushed; nothing opened or merged to main; not yet re-inspected

Each fix was committed first, then removed by a mutation, rebuilt and run (red), then restored, rebuilt and
run (green). One mutation attempt did not apply (an empty diff) and its "pass" was thrown away and redone.

**G1 - the cue boundary is placed by the clock (#2925, replaces phase 2's F1).** `BatchDictationRecorder`
records B0 (bytes captured) and T0 (monotonic time) when the first audio buffer arrives. On the playback
report at T1 the boundary is B0 + bytes(T1 - T0) + 50 ms ring-down + 50 ms first-buffer margin, rounded up
to a whole sample, clamped to the captured length when blanked. The "first buffer after the report" logic
is gone. The log line carries B0, T1 - T0 and the boundary. The time source is an injected `TimeProvider`
(test constructor only; production uses the system clock). Proof: `BatchDictationRecorderReadyCueTests`
builds a faithful timeline - first buffer delivered 50 ms late, a 200 ms cue, 50 ms of ring-down - and
reports the end with capture 0, 1, 2 and 3 buffers behind, on `TranscribeAsync` and `StopAndGetWavAsync`
(8 cases): every cue and ring-down byte is zero and every word byte untouched. With the clock replaced by
bytes delivered since first audio, the 1, 2 and 3 buffer cases went red on both paths and the 0 buffer case
stayed green, as it should. A clamp test covers a boundary past the captured audio. The dialog wiring test
was moved to the clock rule.

**G2 - Send before the cue's end is reported (#2925).** After the stop tail and the drain, if a cue was
played and its end is unreported, the recorder waits for the report, bounded by what is left of
`DesktopAudioCue.PlaybackEndAllowance` (the cue's existing 3 seconds, now a named value) counted from when
the cue started; then it blanks as G1. Past the allowance it blanks nothing and logs (R5). The dialog still
closes at once. Proof: four cases, report 100 ms after Send (during the tail) and 600 ms after (after it), on
both paths. With the wait removed, both 600 ms cases went red; the 100 ms cases stayed green.

**G3 - resolution slower than the ready window (#2929).** When the saved microphone resolves, the dialog opens
it only if it is still in GETTING READY; otherwise it builds nothing and logs why. If the ready window runs out
while resolution is outstanding, it logs `ready window ran out while the saved microphone was still being
resolved: elapsedMs=..., savedDevice="..."` instead of the old line with an empty device name. Proof:
`ResolutionOutlastsTheReadyWindow_DialogOpen_BuildsNoRecorderAndLogsTheResolution` holds resolution, fires the
timeout, releases resolution with the dialog open: no recorder is built, the dialog stays in ERROR, the line is
logged once. Removing the stage check turned it red (a recorder was built); removing the log line turned it red.

**Not proven.**
- Real hardware. All of this runs on a fake microphone, a fake clock and a fake cue. That T0 is close to when the
  cue actually starts sounding is an assumption: the cue is played after a hop to the interface thread, which
  makes T1 - T0 slightly LONGER than the cue (more is blanked, never less). A first buffer delivered more than
  50 ms late would still leave cue audio; the margin is the ruling's, not a measurement.
- The wait's bound counts from when the dialog noted the cue, a moment before the cue's own 3 second clock starts,
  so a report landing in that sliver after our wait gives up blanks nothing (logged).
- A recorder disposed during the wait throws after the wait ends, up to 3 seconds later, rather than at once.
- The G3 test fires the timeout through the existing test seam; the six-second timer itself is not re-proven.

### Gates run in phase 3

- Default local gate on the final code: all 8 suites completed and green (Avalonia 512, Core.UnitTests 479,
  Engine 63, HostedAgent 88, Launcher 195, Terminal 25, setup 25, setup-engine 553). The Launcher failures
  noted in phase 2 did not recur.
- The gate named a coverage gap in the parked Gateway suites. That comes from slice A's Gateway change on this
  branch, not from phase 3, which touches only the desktop app; `-Parked` was NOT run.
- NOT run: web tests (no web code changed in phase 3), Python tests.

## Phase 4 - the two inspection-three findings

    Written:   2026-09-16 by the Manager (session f74e517a, SOREN_NORTH)
    Commit:    36523a6d9 on mission/dictation-fixes (code, tests, evidence); this record in the commit after it
    Status:    pushed; nothing opened or merged to main; not yet re-inspected

Each fix was committed first, then removed by a mutation, rebuilt and run (red), then restored, rebuilt and
run (green).

**H1 - the first-buffer margin was measured, and REMOVED (#2925).** The rule's second case applied.

The measurement: `evidence/cue_to_speech_gap.py` searches the first 1.5 s of every local corpus clip for the
exact cue the Director synthesises (the research's cue search; head score at least 0.5, which the same search
over the middle of each clip reaches on 1 of 44). The cue was found in 30 of 44 clips. For each, the gap from
the cue's end to the first speech was measured with two instruments. Per-clip numbers and the summary are in
`evidence/cue_to_speech_gap.json` (clip file names and times only - no audio, no transcripts).

| Instrument | Count | Minimum | 5th percentile | Median |
|---|---|---|---|---|
| Lab speech detector (Silero, no start padding) | 30 | 180 ms | 219 ms | 737 ms |
| Sustained energy rise (100 ms held, 10 ms frames) | 30 | 58 ms | 120 ms | 570 ms |

The two disagree. The detector alone would keep the margin; the energy rise, the other instrument the ruling
names, puts the 5th percentile at 120 ms, under the 150 ms the rule requires. Looking at the clips rather than
the summary: in one clip a sustained sound begins about 90 ms after the cue's end and runs straight into
speech the detector only calls at 180 ms (its 32 ms windows lag an onset); in another a loud sound 58 ms after
the cue's end is not speech to the detector and could not be identified without listening. A first attempt
at the energy instrument without the "held for 100 ms" test counted a last bump of the cue's own decay as a
rise (32 ms); that version was corrected, and its figures are not the ones above. The keep condition is not
shown, so the margin is gone.

Now the boundary is B0 + bytes(T1 - T0) + 50 ms ring-down. The code comment beside it cites these figures and
says a first buffer delivered late can leave up to one buffer (50 ms) of cue tail.

Proof, in `BatchDictationRecorderReadyCueTests`:
- The backlog tests (0 to 3 buffers, both paths) and the Send-before-the-end tests now run at a ZERO first-buffer
  lag - the first buffer delivered the moment it fills - instead of assuming the full 50 ms lag.
- New: `ZeroLagFirstBuffer_SpeechStartingRightAfterTheRingDown_IsUntouched` (both paths) - words start the instant
  the ring-down ends and every byte of their first 50 ms must survive.
- New: `LateFirstBuffer_LeavesAtMostOneBufferOfCueTail_AndTheWordsUntouched` - records the accepted cost exactly, so
  it cannot grow unnoticed.
- `SpeakDialogReadyCueBlankingTests` and the first-report test moved to the new arithmetic.
- Mutation: the 50 ms put back. 17 of 19 went red, the zero-lag test with "byte 14400 (300 ms) is the first 50 ms of
  the owner's words and must be untouched" - the inspection's symptom. The two that stayed green (end never
  reported; boundary past the captured audio) do not depend on the margin.

**H2 - a first measured start time now shows in the open selector (#2928).** `MicStartTimes.Record` returns the
device's new typical figure. After the background write, the dialog replaces that device's entry with one carrying
the figure, restores the selection, and suppresses the change handler so the microphone is not switched. The figure
is also kept for the dialog's lifetime, so a device list that arrives after the measurement still carries it.
Proof: `Dialog_DeviceWithNoHistory_FirstAudioArrives_TheOpenSelectorShowsItsFigure` runs the real record path
(config redirected to a temp folder, not the test seam): a device with no history shows its name alone, first audio
arrives, and the open selector shows "... takes N s to wake up" with the measured figure; the other entry is unchanged,
the selection stays, and exactly one recorder was built. Mutations: the refresh removed - red ("Strings differ");
the selection restore removed - red (selected index -1, expected 1).

**Not proven.**
- The corpus measures when the owner starts speaking after the cue in recordings from the older recorder, not the
  delivery lag of a first capture buffer on real hardware. How often a sliver of cue tail now survives, and whether the
  model still writes a word for 50 ms of it, is unmeasured. That needs a hardware run and a re-transcription.
- The 58 ms sound in one clip was not identified - nobody listened to it.
- The two instruments' disagreement was settled by the rule's burden (keeping needs the evidence), not by deciding
  which instrument is right.
- H2: the path where the figure arrives before the list (kept, then applied when the list fills) has no test of its own.
  The "did not switch the microphone" check could not be made to fail by removing only the suppression flag, because a
  re-selection of the device already in use is a no-op anyway.
- BRIEF.md ruling R4 still describes the 50 ms first-buffer margin. It is the Architect's document; it was not edited here.

### Gates run in phase 4

- Default local gate on the restored code: all 8 suites completed and green (Avalonia 516 - four more than phase 3,
  Core.UnitTests 479, Engine 63, HostedAgent 88, Launcher 195, Terminal 25, setup 25, setup-engine 553).
- The gate again names a coverage gap in the parked Gateway suites, from slice A's Gateway change; phase 4 touches only
  the desktop app. `-Parked` was NOT run.
- NOT run: web tests (no web code changed), Python tests.

## Phase 5 - the first callback's lag is measured (inspection four, ruling J1)

**What changed.** `BatchDictationRecorder` no longer anchors the cue on the first buffer's arrival. It records every
buffer's arrival time and cumulative byte count for the first 10 seconds after the first buffer arrives
(`CueClockWindow`), and the playback report now records only its time T1. At snapshot time - after the stop tail, the
drain and the wait for a late report - it estimates the first buffer's fill time F0 as the minimum over those buffers
of a_k - (C_k - C_0)/byteRate, and blanks through C_0 + bytes(T1 - F0) + 50 ms ring-down, clamped to the captured
length. No other margin. The arithmetic is done in whole bytes: a first version in fractional seconds rounded the
boundary one sample into the words at a 50 ms lag, and two tests caught it. One log line at snapshot carries C_0, the
estimated first-callback lag, the number of buffers read, T1 - F0 and the boundary. The code comment names the one
remaining gap: if every buffer in the window is late, that smallest lag of cue tail is left.

**The tests had to change, not just grow.** The old recorder tests emitted many buffers at one clock reading, so
audio arrived before it could have been captured. The old anchor ignored that; the new estimate correctly reads it
as lag and blanks into the words. Every test now moves the clock to each buffer's arrival, never earlier than its fill
time. `SpeakDialogReadyCueBlankingTests` had the same fault and was fixed the same way.

Tests in `BatchDictationRecorderReadyCueTests` (25, both the transcribe path and the saved-recording path where it applies):
- `LateFirstCallback_ThenPromptBuffers_CueSilentAndFirstWordsUntouched` - first callback late by 0, 50, 100, 150 ms;
  buffers that filled during the stall arrive with it, later ones promptly. Every cue and ring-down byte is zero, the
  first 50 ms of words right after the ring-down are untouched, and the rest of the words too. Both paths.
- `CaptureBacklogAtTheCueEnd_CueAndRingDownAreZero` - the kept backlog tests, 0 to 3 buffers behind at the report, both paths.
- `EveryBufferUniformlyLate_LeavesExactlyThatLagOfCueTail_AndTheWordsUntouched` - pins the residual: 100 ms uniformly
  late leaves exactly 100 ms of cue, zeros before it, words untouched after.
- `PromptBufferAfterTheClockWindow_IsNotUsedForTheEstimate` - a prompt buffer just past the 10 second window must not
  move the boundary.
- The Send-before-the-report tests now model a Send inside the cue with the rest of the capture delivered by the stop
  drain: a report during the tail blanks through the ring-down and leaves the words; a report after the tail blanks
  the whole clip, never nothing.
- Unchanged in intent: end never reported blanks nothing; only the first report counts; a boundary past the capture clamps.

**Watched failing, each on a committed fix, restored by copy and rebuilt before the next run:**
- Estimate replaced by the old first-callback anchor (lag forced to zero): exactly the six late-callback cases went red
  (50, 100, 150 ms on both paths); the zero-lag cases and all eight backlog cases stayed green. Restored: 25 of 25.
- Window bound removed: only `PromptBufferAfterTheClockWindow_IsNotUsedForTheEstimate` went red.
- The uniformly-late pin CANNOT go red under the old anchor - both leave the same residual, which is the point of the
  pin. It was watched red instead with a fixed 50 ms allowance added to the estimate (21 of 25 red, including the pin
  and the zero-lag words check).
- The dialog test with its call to hand the cue's end to the recorder replaced by a no-op: red. Restored and rebuilt: green.

**Not proven.**
- All of this runs on a fake microphone and a fake clock. No real capture callback lag was measured, and whether a
  real driver's buffers meet the premise - filled on a steady clock, delivered no earlier than filled, no bytes
  dropped - was not checked on hardware. If a driver drops audio (the capture-health deficit), byte counts no longer
  map to time; the estimate then stays an upper bound on F0, so it can leave cue but cannot reach further into the words,
  but that too is reasoning, not a test.
- The 10 second window is the ruling's figure; drift between the device clock and ours over it was not measured.
- BRIEF.md ruling R4 already describes J1 on the branch; not edited here.

### Gates run in phase 5

- Default local gate on the final code: all 8 suites completed and green (Avalonia 523 - seven more than phase 4,
  Core.UnitTests 479, Engine 63, HostedAgent 88, Launcher 195, Terminal 25, setup 25, setup-engine 553). The first gate
  run was red on `SpeakDialogReadyCueBlankingTests` (the unrealistic clock above); fixed, then rerun green.
- The gate again names a coverage gap in the parked Gateway suites, from slice A's Gateway change; phase 5 touches only
  the desktop app. `-Parked` was NOT run.
- NOT run: web tests (no web code changed), Python tests.

## Phase 6 - the cue is found in the audio, not placed by clocks (inspection five, ruling K1)

**What changed.**
- `src/CcDirector.Core/Audio/ReadyCue.cs` is new and holds the ONE cue generator (`Synthesize`, the same sweep,
  length and envelope as before) and the search (`Find`). `DesktopAudioCue` now plays `ReadyCue.SynthesizePcm16(44100)`.
  The search is the research's: the template is made zero-mean and unit-length, the score at each offset is
  |dot(window, template)| / length(window), a match must lie wholly inside the searched region, and a score of 0.5 or
  more is the cue. Found, it returns the span match start minus 10 ms to match end plus 50 ms, clamped to the clip.
- `BatchDictationRecorder` searches its snapshot at 24 kHz from the start of the clip to 1.5 s past the capture
  position at first audio (the end of the first buffer, when the cue is triggered). It blanks only when the dialog
  told it a cue was played AND the search found it. Not found blanks nothing. One log line per snapshot: played yes
  or no, best score, match position, blanked span or "blanked nothing". Length never changes; the capture-health
  counts are still read before blanking.
- The playback report is now only a wait, and the wait moved: it runs after the 250 ms stop tail and BEFORE the
  microphone is stopped, bounded by the cue's 3 s allowance as ruling G2 had it. Waiting after the stop, as before,
  could not add the rest of the cue to the clip.
- Removed: `CueClockWindow`, the arrival-time list, the first-fill estimate, `CueEndByte`, `CueRingDownMs` and every
  test of them. Nothing computes a cue position from time.

**Tests.** `BatchDictationRecorderReadyCueTests` was rewritten on real waveforms (the synthesised cue at 0.3 gain over
room noise, and a voiced, syllable-paced speech stand-in in `ReadyCueTestClip`), not byte markers. Each case runs on
both the transcribe path and the saved-recording path; the expected span is written as literals from the ruling (10,
50, 200, 1500 ms), not read from `ReadyCue`, after the first round of mutations showed that reading the product's own
constants let a changed ring-down stay green.
- Cue at 0, 50, 100, 150 and 1300 ms, words starting the instant the ring-down ends: exactly the span is zero, every
  other byte identical.
- A dropped buffer before the cue, and a dropped buffer between the ring-down and the words: span found where the cue
  is, words identical.
- Playback reported 850 ms after capture ended: nothing moves. Playback never reported and its allowance spent: the
  cue in the audio is still blanked.
- No cue played, with a cue-shaped sound in the clip: identical. Cue played but not in the audio: identical. Speech
  over the whole search window (precondition asserted: it scores under 0.5): identical. A cue-shaped sound after the
  search window: identical. A faint cue in a noisy room scoring between 0.5 and 0.7 (asserted): blanked.
- Send inside the cue, the rest of the cue arriving with the playback report 20 ms and 600 ms later: the whole cue is
  blanked on both paths.
- `Find` on an exact cue at 137 ms returns that sample and span; a region shorter than the cue finds nothing.
- `SpeakDialogReadyCueBlankingTests` now plays a real cue into the dialog's recorder and expects exactly 40 to 300 ms
  zeroed and every other byte identical.

**Watched failing.** Fix committed first; each mutation applied alone, the cue tests rebuilt and run, the file restored
from the commit, and a clean rebuild and run of all 36 green at the end.
- Blanking removed: every case that expects a blank went red (positions, both dropped-buffer cases, the inside-the-cue
  pin, late report, never reported, both Send-inside cases, the dialog).
- The played gate removed: exactly `NoCuePlayed_ACueShapedSoundIsNotBlanked`, both paths.
- Threshold 0.0: cue not in the audio, speech in the window, cue after the window, and the short-region `Find` test.
  Threshold 0.7: exactly the faint-cue test, both paths.
- Search to the whole clip: exactly the cue-after-the-window test. Search window 1400 ms: exactly the 1300 ms position.
- Ring-down 0, ring-down 100, lead-in 0: 23 to 25 of 36 red, including every position case and the dialog test.
- The wait moved back after the microphone stop: exactly the Send-inside case with the report after the tail, both paths.
- The dialog's call telling the recorder a cue was played removed: the dialog test.

**Real audio** (`evidence/cue_detector_corpus/`, a console harness outside the solution that calls the product's
`ReadyCue.Find`; `evidence/cue_detector_corpus.py` compares it with `cue_scan.py`'s search and the lab's Silero speech
detector; results in `evidence/cue_detector_corpus.json`, numbers and file names only). All 44 local clips:
- Found in 30, the same 30 clips the research found, at the same position in every one (no difference over 1 ms). The
  largest score difference, 0.06, is on two clips neither finds: the product searches to 1550 ms and the research to
  1500 ms, and those clips' best offsets (1327.5 and 1330.8 ms) only fit the longer window. The corpus does not record
  the first-audio position, so the harness assumes one 50 ms buffer.
- The three May clips recorded before the cue shipped: nothing found (0.38, 0.25, 0.21).
- Middle-of-clip control: 1 of 44 at or over 0.5 (dictation-20260907-134605, 0.51), the same single clip as the research.
- No blanked span overlaps any detected speech span in any of the 30. From each blank's end to the next speech: minimum
  129.5 ms, 5th percentile 169.0 ms, median 686.8 ms.

**Not proven.**
- One limit is left and pinned (`DroppedBufferInsideTheCue_WordsWithinThatLengthOfTheRingDown_LoseThatMuch`): a buffer
  dropped INSIDE the cue shortens the cue in the clip, but the blank is the full cue length from the match, which lands
  on the cue's loud start. The blank then reaches that buffer's length past the cue's real end, and words starting that
  soon after the ring-down lose it - 50 ms in the pinned case, inspection five's timeline. It needs a capture drop
  during a 200 ms window and speech within 50 ms of the ring-down; the corpus's closest speech is 129.5 ms after a blank.
- The real-audio run searched saved clips, not a live capture: the first-audio position is assumed, and no clip was
  recorded with a dropped buffer or a Send inside the cue. Nothing was run on hardware.
- That the player and the search share one waveform is by construction (`DesktopAudioCue` calls `ReadyCue`); no test
  plays audio, so a future player that stops calling it would not be caught.
- The search costs about 37,000 offsets of a 4,800-sample dot product per snapshot on the background path; not timed.
- BRIEF.md ruling R4 already describes K1; not edited here.

### Gates run in phase 6

- Default local gate on the final code (commit 1cd33312): all 8 suites Completed and green - Avalonia 533 (ten more than
  phase 5), Core.UnitTests 479, Engine 63, HostedAgent 88, Launcher 195, Terminal 25, setup 25, setup-engine 553.
- The gate names a coverage gap in the parked Core.Tests and Gateway suites. This phase adds one new Core file used only
  by the desktop app, and changes no Gateway code. `-Parked` was NOT run.
- NOT run: web tests and Python tests (no web or Python product code changed).

## Phase 7 - the four inspection-six findings (rulings L1 to L3)

Commits on `mission/dictation-fixes`: cb661757 (L1), fd7d6424 and 997b1863 (L2). Internal
`mission/dictation-fixes-docs`: e5f7d65d (L3).

### L1 - blank only after playback reports it completed (findings 1 and 2)

**What changed.** The recorder's gate is now `_cueCompleted`, set only by `NoteReadyCueFinished` - the player's
successful-completion report. `NoteReadyCuePlaying`, which the dialog still calls before playback, only arms the
pre-stop wait. `DesktopAudioCue.PlayReady` takes a second callback, `onPlaybackFailed(reason)`, and calls exactly one of
the two per play: failure on synthesis failure, an initialisation or play exception, `PlaybackStopped` with an error, or
no end report within the three-second allowance. The recorder's new `NoteReadyCueFailed` records the reason, blanks
nothing, and releases a stop waiting for the cue. Only the first report counts, so a completion arriving after a failure
does not open the gate. The snapshot log line now reads `completed=no, <not played | playback did not complete (reason) |
playback never reported completion>` or `completed=yes, found/NOT found ...`.

**Tests** (`BatchDictationRecorderReadyCueTests`, `SpeakDialogReadyCueBlankingTests`):
- `PlaybackErrorAfterAPartialCue_ScoringOverTheThreshold_NothingIsBlanked` - the inspector's timeline: cue at 150 ms,
  50 or 100 ms of it, 20% amplitude, a quiet word (peak 1500) right after a 50 ms ring-down; both paths. The test first
  asserts the partial cue IS found (score 0.68 at 50 ms, 0.89 at 100 ms, measured) and that the blank would reach into
  the word, so it cannot pass by the search simply missing.
- `PlaybackNeverStarted_ACueShapedSoundIsNotBlanked` - a failure report and a whole matching sound in the audio; both paths.
- `PlaybackEndNeverReported_NothingIsBlanked` - replaces phase 6's `..._TheCueInTheAudioIsStillBlanked`, which pinned the
  old behaviour; both paths.
- `CompletionReportedAfterAFailure_DoesNotSetTheGate`, `SendInsideTheCue_FailureReported_StopsWaitingAtOnce`.
- The dialog test is now a theory over Completed / Failed / NeverReported on the same clip holding a whole cue: only
  Completed blanks. It proves the dialog hands the recorder both reports and that playing the cue alone sets nothing.

**Watched red.** Old gate restored (`_cuePlaying || _cueCompleted` in place of `_cueCompleted`): 11 red - the four
partial-cue cases, both never-started, both never-reported, the completion-after-failure case, and the dialog's Failed
and NeverReported cases; the Completed control and the other 35 stayed green. Failure report not releasing the wait:
the wait test red ("the stop waited 3273 ms"). Restored, rebuilt, 46 of 46 green.

### L2 - closing the Wake Word test window during its device query (finding 3)

**What changed.** `WakeWordTestDialog` sets `_closed` in its close handler. After the background device query, a closed
window builds nothing. After the microphone starts, a window that closed meanwhile has the start dispose the recorder
itself, because the close handler already ran and found no published recorder. Two internal test seams were added
(`DescribeDefaultDeviceForTests`, `RecorderFactoryForTests`), null in production.

**Tests** (`WakeWordTestDialogCloseDuringStartTests`, new): the query held while the window closes, then released - no
microphone recording and no recorder built; the window closing from inside the microphone's start - the microphone is
stopped; and a control that an ordinary close after listening started still stops it.

**Watched red.** Query guard removed: the held-query test red (a recorder was built; the start guard then stopped it).
Start guard removed: the close-during-start test red ("a microphone was left recording after the window closed").
Both removed - the phase 6 code: both tests red with that message. Restored, rebuilt, 3 of 3 green.

### L3 - the research header (finding 4)

The header of `docs/research/transcription/2026-09-09-unspoken-words.md` now says fourteen of the 22 doubt segments -
the fourteen clip openings - were the ready cue, and the other eight stay doubt for other reasons, matching section 7
(14 openings, 2 other oracle-omitted, 6 partial matches). `labels-final.json` untouched.

### Not proven

- `DesktopAudioCue`'s new failure reporting has no test: it drives NAudio's real output device, and no seam was added
  there. That each failure path calls `onPlaybackFailed` exactly once is by reading, not by a run. The recorder and dialog
  tests prove what happens once a report arrives.
- A `PlaybackStopped` with no error is taken as completion, as ruled. If a driver ends playback early WITHOUT an error,
  the gate opens on a partial cue and finding 1 returns for that device. Not observed; not testable without hardware.
- The close-during-start test simulates the close landing inside the microphone's start by closing from the fake
  microphone's `Start`; `StartAsync` has no real await today, so in production that window is the synchronous start
  itself. Nothing was run with a real microphone.
- The partial-cue test uses the synthetic speech stand-in, not the inspector's corpus speech (audio is not committed).

### Gates run in phase 7

- Default local gate on commit 997b1863: all 8 suites Completed and green - Avalonia 546 (13 more than phase 6),
  Core.UnitTests 479, Engine 63, HostedAgent 88, Launcher 195, Terminal 25, setup 25, setup-engine 553.
- The gate names a coverage gap in the parked Core.Tests and Gateway suites (the mission diff against main touches
  them). This phase changed only desktop app code and its tests. `-Parked` was NOT run.
- NOT run: web tests and Python tests (no web or Python code changed in this phase).
