# Dictation pipeline - how we validate it

The dictation pipeline must be trustworthy: when you talk, every word you say
must end up in the transcript. The failure we fixed was losing the opening of a
recording because the app connected to OpenAI *before* turning on the mic, so
anything said during the (slow) connect was never captured.

The fix is `DictationPipeline` (in `CcDirector.Core/Dictation`): it starts mic
capture FIRST, buffers captured audio in an ordered channel while the connection
opens, then drains it FIFO once connected. Capture order in == provider order
out, with zero loss regardless of connection latency.

There are three validation layers, weakest dependencies first.

## 1. Offline unit tests (always run, no network, no mic)

`src/CcDirector.Core.Tests/Dictation/DictationPipelineTests.cs`

A fake audio source emits known PCM chunks on demand; a gated fake provider lets
the test hold the "connection" open while audio is fed in. This makes the
no-loss invariant provable in CI.

```
dotnet test src/CcDirector.Core.Tests/CcDirector.Core.Tests.csproj \
  --filter "FullyQualifiedName~DictationPipelineTests"
```

Key test: `SlowConnection_DeliversEveryCapturedByteInCaptureOrder` - feeds audio
during the connect window and asserts every byte is delivered, in order. This is
the regression guard for the bug.

## 2. Live end-to-end test (real OpenAI API, real recorded audio)

`src/CcDirector.Core.Tests/Dictation/DictationPipelineLiveTests.cs`

A `ReplayAudioSource` streams a real clip starting the instant the pipeline
starts, on its own thread - so the first frames are emitted while the real
WebSocket is still connecting, reproducing the exact timing that used to drop
the opening. The test asserts the clip's opening word survives in the
transcript and that `CapturedBytes == DeliveredBytes`.

Self-skips without `OPENAI_API_KEY`, without ffmpeg on PATH, or without the
phase0 clips. Costs a few cents per run.

```
dotnet test src/CcDirector.Core.Tests/CcDirector.Core.Tests.csproj \
  --filter "FullyQualifiedName~DictationPipelineLiveTests" \
  --logger "console;verbosity=detailed"
```

Reference result (real run): `primed_chunks=6` (≈300ms captured before the link
was up), `captured == delivered`, transcript = "I sent the CC Director patch ...".
The opening "sent" survived; under the old code those 6 frames were lost.

## 3. Harness - one-command, eyes-on validation

`playground/dictation-harness` drives the real desktop streaming path and prints
a report. Requires ffmpeg + `OPENAI_API_KEY`.

```
dotnet run --project playground/dictation-harness -- <audio-file> --stream
# or, with the bundled phase0 sample:
dotnet run --project playground/dictation-harness -- --stream
```

It prints `primed frames` (audio captured before connect), a `no audio lost:
PASS/FAIL` line (captured vs delivered bytes), and the raw/cleaned transcript so
you can eyeball that the opening is intact.

## Browser / HTML recorder (the /dictate.html overlay)

The web recorder is a separate code path (`dictate-client.js` +
`DictationEndpoint.cs`) that does NOT go through the C# `DictationPipeline`. It
now uses the same capture-first guarantee, implemented client-side: the mic and
the WebSocket open in parallel, frames captured before the server signals
`started` are buffered and flushed in order, then frames stream live. The UI no
longer says "do not speak yet" - it flips to "recording" the instant capture is
live and shows a non-blocking "Connecting transcriber..." note until the link is
up.

The no-loss core is the pure `createCaptureBuffer()` router in
`dictate-client.js`, unit-tested in Node against the real shipped file (no
browser, mic, or socket needed):

```
node docs/features/dictation/browser/dictate-capture-first.test.mjs
```

It asserts the same invariant as the C# tests: frames captured before ready are
buffered and delivered in exact capture order with zero loss. The surrounding
Web Audio plumbing (getUserMedia, AudioWorklet) is browser code that fails
visibly; the final on-device check is opening `/dictate.html` from the phone and
talking from the first instant.

## Note on VAD

Server-side VAD stays OFF (`turn_detection = null` in `OpenAiRealtimeProtocol`).
Enabling it would let the API auto-cut at a natural pause or filter the lead-in
to zero audio - i.e. it would reintroduce exactly the kind of loss we fixed. End
of speech is marked by the user releasing the button, and the buffer is
committed on stop.
