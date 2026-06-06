# CC Recorder - Upload and Progress: Status Report

Date: 2026-05-24

This is a status report, NOT a success claim. There is still one error path
left (transcription of very long recordings) and one item I cannot verify
without you (the checkmarks screenshot, because the phone is locked). Both are
called out below under "What I need from you".

---

## Bottom line

| Item | State |
|------|-------|
| Upload decoupled from transcription | DONE and proven |
| Per-recording progress bar (moved out of the recording area into the Recordings list) | DONE and proven |
| Two checkmarks per row (Uploaded + Transcribed) | BUILT and installed, screenshot BLOCKED by locked phone |
| Transcription of very long recordings (the 60s failure) | ROOT CAUSE FOUND, fixed in code, NOT yet deployed |

---

## 1. Upload now works and is separate from transcription

The core mistake before was that the phone treated a transcription failure as an
upload failure. They are now two independent things:

- UPLOAD = move the audio bytes to the server. Succeeds the moment every segment
  is stored. Nothing to do with OpenAI.
- TRANSCRIPTION = a separate server step that runs afterward. It can be slow or
  fail without ever changing the upload result.

Proven on your real 99-segment recording ("Recording 2026-05-24 10:04"): it now
reaches "Uploaded", and the server confirms `chunksReceived: 99 / chunksTotal: 99`.
It used to read "Upload failed".

![Upload succeeded and live progress bar moving (no error at this point). This
same long recording's transcription later hit the 60s timeout - fixed in section 3.](evidence-upload-progress.png)

---

## 2. Progress bar - moved to the Recordings list, per row

- The recording area at the top is now clean: timer, Record, notes only. No
  upload status up there.
- The list (renamed from "Uploads" to "Recordings") shows each recording's own
  progress bar and status text, so several recordings can show their own state
  at once. Sending: "Sending 12/99". Then: "Uploaded - Transcribing 40/99".

![Decoupled result: the recording shows Uploaded even though transcription is a
separate step.](evidence-decoupled-state.png)

---

## 3. Transcription of very long recordings - root cause and fix

The 60-second error had nothing to do with uploading. It was the transcript
CLEANUP step (one OpenAI call over the whole 1h38m transcript) hitting a
60-second client timeout. The server had already transcribed all 99 segments
(`chunksTranscribed: 99/99`); only the final cleanup pass timed out.

The cleanup component is documented to "fail open" (ship the raw transcript on
any error rather than fail). But it was rethrowing its own timeout, because
`HttpClient.Timeout` raises a `TaskCanceledException`, which is an
`OperationCanceledException`, which the catch was rethrowing unconditionally.

Fix (one line, matches the documented contract):

```
// before
catch (OperationCanceledException) { throw; }

// after
catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
// -> an internal timeout now falls through to fail-open and ships the raw
//    transcript, so the recording finishes as "Transcribed" instead of erroring.
```

This is a server-side change (CleanupOrchestrator, in CcDirector.Core). It
compiles. It is NOT deployed, because the gateway runs inside one of your
running Director processes and deploying means restarting that process.

---

## What I need from you (2 things)

1. Unlock the phone (open it). adb cannot screenshot past a secure lockscreen,
   so I cannot capture the two-checkmark UI for final visual proof until you do.
   Once unlocked I will grab the screenshot and confirm no errors.

2. Approve restarting the Director that serves `https://machine-a.tail0123.ts.net`
   so I can deploy the transcription fix and prove the long recording finishes
   with no error. I will restart only that one and not touch your other
   Directors - or tell me which slot it is and I will use the Task Scheduler
   path.

---

## Files changed (not committed)

- Phone: `IngestUploader.cs` (split upload from transcription), `LocalManifest.cs`
  (separate upload vs transcription state), `AndroidAudioRecorder.cs` (two-phase
  flow), `MainPage.xaml` + `MainPage.xaml.cs` (per-row progress bar + two
  checkmarks), `test-upload.sh` (repeatable upload test harness).
- Server: `CleanupOrchestrator.cs` (fail-open on internal timeout - the
  transcription fix).
