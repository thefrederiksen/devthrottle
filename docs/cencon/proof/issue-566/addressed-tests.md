# Issue 566 - Deflake CI Build and Test proof

Goal: make the continuous integration "Build & Test (.NET)" gate deterministically
green by skipping or quarantining tests that depend on the runner environment (a live
external provider, an installed external command-line tool) or on real-process timing,
following the precedent already in the tree (the static `[Fact(Skip = ...)]` used for
`SessionAskRunnerTests.AskAsync_ClaudeCode_RealDriverWithFixtureTranscript_ReturnsParsedAnswer`,
`SessionEdgeCaseTests.ConcurrentSessionCreation_AllSucceed`, and `NulFileWatcherTests`).

Both test projects use xUnit version 2 (`xunit` Version `2.*`), which has no clean
dynamic skip (no `Assert.Skip`). So the precedent-matching mechanism is the static
`[Fact(Skip = "<reason>")]` attribute, with the reason naming why.

## Each addressed test

### 1. CcDirector.Core.Tests.Recording.RecordingIngestServiceTests.Worker_TranscribesQueuedRecording_EndToEnd

- Mechanism: static quarantine, `[Fact(Skip = ...)]`.
- Reason: irreducible background-worker plus filesystem timing race. The test starts the
  real background worker (`runWorker: true`), a timer-driven thread that drains the queue,
  transcribes, and persists status to disk, then polls for the "transcribed" state. Pass
  or fail depends on the worker's tick plus the status-file write winning a race against
  the poll deadline; it has intermittently failed on continuous integration at
  `RecordingIngestService.SaveStatus` (observed on pull request 561 and on the post-merge
  main run, passing on a plain re-run). There is no external dependency to probe.
- Remaining deterministic coverage: the exact transcription plus SaveStatus path is
  covered by `Complete_AssemblesCleansAndFiles` and the other tests in the same file that
  drive `ProcessRecordingAsync` synchronously (no background worker). The queue-only
  enqueue behavior is covered by `Complete_OnlyEnqueues_DoesNotTranscribeInline`. The only
  thing not exercised is the background worker thread draining the queue on its own timer.

### 2. CcDirector.Gateway.Tests.DictationEndpointTests.FullPipeline_transcribes_phase0_clip2_with_realtime_provider

- Mechanism: static quarantine, `[Fact(Skip = ...)]`. (The existing runtime early-return
  guards for the absent-dependency case are kept inside the body, but the static skip now
  also stops the racy assertion from running where the dependency is present.)
- Reason: needs a live OpenAI Realtime streaming speech-to-text provider (a reachable
  WebSocket plus a valid `OPENAI_API_KEY`) and ffmpeg on the command path to decode the
  Phase 0 clip - none of which the continuous integration runner has. Even where the
  dependency is present it asserts `partialsObserved >= 1`, which races the live provider's
  nondeterministic mid-stream partial emission; the Realtime application programming
  interface does not guarantee a partial transcript arrives before the final within the
  deadline (observed failure "expected at least 1 partial transcript, got 0").
- Remaining deterministic coverage: the dictate WebSocket served assets and the
  400-on-non-upgrade behavior are covered by `DictatePage_is_served`,
  `NonWebSocketGet_to_dictate_returns_400`, and `WorkletScript_is_served` in the same file;
  the realtime provider's connect, retry, and protocol parsing are covered offline by
  `OpenAiRealtimeProviderConnectTests` and `OpenAiRealtimeProtocolTests`; the capture-first
  pipeline is covered by `DictationPipelineTests`. The only thing not run on continuous
  integration is the single live full-stack transcription, which requires the
  environment-gated provider.

### 3. CcDirector.Core.Tests.SessionLifecycleTests.KillAllSessions_KillsAllRunning

- Mechanism: static quarantine, `[Fact(Skip = ...)]`.
- Reason: irreducible real-process timing race. The test spawns two real stand-in shell
  processes (cmd.exe or sh) and asserts both report `SessionStatus.Running` immediately
  after creation, before killing them. That post-create assertion races the real process:
  under load on a continuous integration runner the stand-in can already be observed as
  exited by the time the assertion runs (observed failure "Expected Running, Actual
  Exited"), even though the kill behavior under test is correct. There is no external
  dependency to probe.
- Remaining deterministic coverage: the kill and lifecycle behavior is still covered by
  `KillSession_RunningSession_TransitionsToExited`, `KillSession_AlreadyExited_DoesNotThrow`,
  and `KillAllSessions_NoSessions_DoesNotThrow` in the same file, plus
  `Dispose_DisposesAllSessions`.

## Sweep of the rest of the test projects

Searched both test projects for tests that hit a live network or provider, an installed
external command-line tool, or assert on real multi-process timing. All of the following
already self-skip cleanly (early `return`) when their dependency is absent, so they do not
run and cannot fail on the bare continuous integration runner (which sets neither
`OPENAI_API_KEY` nor an opt-in variable, and has no ffmpeg):

- `CcDirector.Core.Tests.Dictation.CleanupOrchestratorLiveTests` - self-skips without `OPENAI_API_KEY`.
- `CcDirector.Core.Tests.Dictation.DictationPipelineLiveTests` - self-skips without `OPENAI_API_KEY`, ffmpeg, or the Phase 0 clips.
- `CcDirector.Core.Tests.Dictation.OpenAiRealtimeProviderIntegrationTests` - self-skips without `OPENAI_API_KEY` (and ffmpeg for the real-audio case).
- `CcDirector.Core.Tests.Wingman.WingmanAnswerLiveTests` - self-skips unless `WINGMAN_LIVE_TESTS=1` and `OPENAI_API_KEY` are set.
- `CcDirector.Gateway.Tests.RecordingEndpointsE2ETests` - self-skips unless `CC_REC_E2E=1` and `OPENAI_API_KEY` are set.
- `CcDirector.Core.Tests.Recording.RecordingIngestServiceTests.EndToEnd_RealTranscription_Phase0Clips_PreservesCompanyTerms` - self-skips without `OPENAI_API_KEY` or the clips.
- `CcDirector.Gateway.Tests.DictationEndpointTests.ClientCloseWithoutStop_recovers_transcript_instead_of_discarding` - self-skips without `OPENAI_API_KEY`, ffmpeg, or the clip. (Its assertions are not the flagged failure; left as a runtime self-skip.)

Deterministic by construction (no live calls): `LivePreviewTranscriberTests` (fake HTTP
handler), `VoiceTurnEndpointTests`, `GatewayVoiceTurnAsyncTests`, `VoiceEndpointTests`,
`BriefBuilderTests` (all null out `OPENAI_API_KEY` to force the no-key path),
`KeyVaultTests`, `VaultEndpointsTests`, `VaultGatewayIntegrationTests` (in-process loopback,
no external service).

No other test was found that would fail on a bare continuous integration runner.

## Local build and test results

- `dotnet build cc-director.sln -c Release`: Build succeeded, 0 warnings, 0 errors.
- `dotnet test src/CcDirector.Core.Tests`: Passed, Failed 0, Passed 2141, Skipped 6.
- `dotnet test src/CcDirector.Gateway.Tests`: Passed, Failed 0, Passed 837, Skipped 1.

The three addressed tests confirmed as Skipped in the run output:
- `SessionLifecycleTests.KillAllSessions_KillsAllRunning`
- `Recording.RecordingIngestServiceTests.Worker_TranscribesQueuedRecording_EndToEnd`
- `DictationEndpointTests.FullPipeline_transcribes_phase0_clip2_with_realtime_provider`

## Acceptance note for the human and quality assurance reviewer

The acceptance criterion of three consecutive green continuous integration runs on a
no-op change is verified post-merge by a human or the quality assurance agent; this change
does not push three times. The diff and the local green run above are the developer-side
proof.
