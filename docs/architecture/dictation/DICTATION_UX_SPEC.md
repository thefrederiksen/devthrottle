# Dictation Dialog: The One Canonical User Experience

Status: SPEC. Audience: anyone implementing or changing the "Dictate" / "Speak"
voice-to-text dialog on ANY surface (desktop Avalonia, Blazor Cockpit, plain
HTML page, phone). Every surface MUST implement exactly this contract so the
feature looks and behaves the same everywhere. Where a surface deviates, the
deviation must be listed in section 8 with a reason.

This document is the single source of truth for the dialog's behaviour. The
text-cleanup safety contract (the model proposes edits, code applies them) lives
separately in `EDIT_DOCUMENT_CLEANUP.md` and is unchanged by this spec.

---

## 1. The core idea: batch, with a pause checkpoint

Dictation is **batch only**. While you are speaking, NO text appears. Your
speech is turned into text only when you ask for it - by pressing **Pause**, or
by committing with **Insert** or **Send**. The whole captured clip is
transcribed in one call.

Why: transcribing the whole utterance at once is materially higher quality than
streaming word-by-word. Streaming partials also lightly reword phrasing as they
revise. We removed streaming on purpose (issue #589) and we are not bringing it
back. We ARE bringing back the ability to pause.

**Pause is a checkpoint, not an ending.** Press Pause and the dialog transcribes
everything you have said since the last checkpoint, appends it to the transcript,
and shows it to you. You cannot resume while that transcription is running -
Resume re-enables only once the text has landed. Then you may Resume and keep
talking; the next Pause transcribes the new audio and appends it. The transcript
grows segment by segment.

This means a "segment" is one stretch of speech between checkpoints, and the
visible transcript is the accumulation of all transcribed segments, which the
user may also edit by hand while paused.

---

## 2. States

```
            +-----------------------------------------------+
            v                                               |
  (open) -> RECORDING --Pause--> TRANSCRIBING --> PAUSED ---+ (Resume)
               |                      ^             |
            Insert/Send               |          Insert/Send
               |                      |             |
               +---> TRANSCRIBING ----+             v
                          |                      (commit)
                          v
                       (commit)

  any stage --(unrecoverable error)--> FAILED
```

| State | Meaning | Mic | Transcript box |
| --- | --- | --- | --- |
| RECORDING | Capturing audio. No text yet. | live | empty; explanatory placeholder shows through; read-only |
| TRANSCRIBING | Turning the current segment into text. | stopped | shows accumulated text so far (if any); read-only |
| PAUSED | Checkpoint reached; reviewing. | stopped | accumulated text; EDITABLE |
| FAILED | Recording or transcription failed unrecoverably. | stopped | the error message; read-only; only Cancel/Close remains |

There is deliberately NO separate "connecting" state on batch surfaces: capture
starts immediately and no network connect precedes it. (The web surfaces may
show a brief "starting" flash only until the microphone permission resolves.)

---

## 3. Controls (labels, positions, behaviour)

Two groups. Recording controls / back-out on the LEFT, commit actions on the
RIGHT, with space between so Pause is never mis-clicked for Send.

LEFT group:

- **Cancel** (neutral). Closes the dialog, discards everything, returns no text.
  In FAILED it reads **Close**.
- **Pause / Resume** (neutral, the two-bar glyph - see section 4):
  - In RECORDING, shows the two-bar Pause glyph. Pressing it transcribes the
    current segment, appends it, and moves to PAUSED.
  - In PAUSED, reads the word **Resume**. Pressing it starts a fresh recording
    segment (RECORDING) that will append to the (possibly edited) transcript.
  - Disabled during TRANSCRIBING. This is the "you cannot resume until it has
    been transcribed" rule.

RIGHT group:

- **Insert** (green). Commits the transcript and closes WITHOUT auto-submitting,
  so the caller drops the text at the caret for the user to review/edit in place.
  From RECORDING it first transcribes the current segment and appends it.
- **Send** (blue, primary). Same as Insert but the caller auto-submits the
  prompt. From RECORDING it first transcribes the current segment and appends it.

Commit from PAUSED uses the text currently IN THE BOX (the user's edits win),
not the raw accumulator.

Keyboard: Enter = Send (except while the focused transcript box is being edited
in PAUSED, where Enter inserts a newline). Escape = Cancel.

---

## 4. The Pause glyph

Two vertical bars, built from two solid rectangles (NOT a Unicode pause
character - the project forbids Unicode in any output). Each bar is 4 wide by 14
tall, 5 apart, in the same neutral foreground colour as the button text. When
the button is in its Resume state it shows the plain word "Resume" instead.

---

## 5. Transcript box placeholder text

The empty box must explain the batch model rather than imply live text. Use:

```
Speak naturally - your words are turned into text when you pause or finish,
not while you talk (it is more accurate that way). Press Pause any time to
see what you have said so far.
```

Surfaces with a narrow box (phone) may use the shortened form:

```
Your words appear when you pause or finish - press Pause to see them so far.
```

---

## 6. Microphone selector

A dropdown at the top listing capture devices, defaulting to the system default
and persisting the chosen device by NAME (indices reorder across replugs).
Changing device restarts the current segment on the new device; audio buffered
on the abandoned device is discarded (mixing two devices into one clip is not
meaningful). The phone has NO microphone selector (it uses the OS default).

---

## 7. Equalizer, timer, level hint

- A nine-bar equalizer driven by the real microphone level. Red while RECORDING,
  amber while TRANSCRIBING, parked grey while PAUSED/FAILED.
- A timer showing total elapsed capture across all segments (it freezes during
  TRANSCRIBING and PAUSED and resumes adding when recording resumes; it never
  ticks in FAILED).
- A one-line hint row (reserved height so the layout never jumps) used for
  "speak up" when the input is too quiet and for "Transcribing..." while busy.

---

## 8. Per-surface deviations (the ONLY allowed differences)

| Surface | Allowed deviation |
| --- | --- |
| Desktop Avalonia | none - this is the reference implementation |
| Blazor Cockpit | hosted as an in-page overlay rather than an OS window |
| Plain HTML | hosted as an in-page overlay; same JS module as Cockpit |
| Phone | smaller layout; no microphone selector; shortened placeholder |

Anything else that differs between surfaces is a bug against this spec.

---

## 9. Implementation notes

- Desktop captures and transcribes in-process via `BatchDictationRecorder`; each
  segment is one recorder instance, transcribed once on Pause / commit, and the
  dialog accumulates the cleaned segments with `DictationText.Join`.
- The web surfaces share ONE JavaScript module driving the `/dictate` WebSocket.
  Pause maps to a batch "flush" control frame: transcribe the buffer so far,
  return one final segment, keep the socket open for Resume. No `partial` frames
  are consumed; the streaming path is not used.
- Fail-open on cleanup is unchanged (see `EDIT_DOCUMENT_CLEANUP.md`): a turn with
  no dictionary term comes back byte-identical to the raw transcription.
- Every surface writes the same `DictationSessionRecord` JSONL audit line with
  its own `Source` tag.
