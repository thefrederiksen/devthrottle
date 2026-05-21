# Goal: Voice Manager - chat-first, drive-safe, talking to my private repo

**Status:** ACTIVE GOAL
**Date:** 2026-05-20
**Owner:** the agent assigned to build this
**Audience:** the implementing agent; everyone touching cc-director

---

## The one-sentence goal

> I want to be driving in an hour and use my phone to talk to a Manager. The Manager talks to a Claude Code agent that is working on this private repo. I speak to the Manager, the Manager relays to the agent, the agent does work, the Manager summarises and reads the result back to me through the car speaker.

The Manager is **the translator between me and the private repo.** Nothing more, nothing less.

---

## The acceptance test (this is what "done" looks like)

It's an hour from now. I'm in the car. My phone is on the passenger seat. I open one URL in mobile Brave / Safari. I see a chat interface. I tap one big button. The screen turns into "I'm recording you." I say:

> "Look at the Finance folder and tell me what's the latest receipt"

I tap the second button. The phone plays a subtle waiting sound while my audio uploads. The sound changes briefly to a different "still trying" version because I just hit a dead zone on the highway, then switches back. The thinking sound plays while the agent works. Then the agent's reply lands in the chat as text AND the phone speaker says, out loud, something like:

> "The latest receipt is from Costco on May 18, 247 dollars 12 cents."

It does NOT read back the raw multi-paragraph Claude output. It summarises into something a human can hear in the car.

If that scenario works end-to-end without me looking at the screen, this goal is done.

---

## The design (what to build)

### 1. Manager IS chat

When I open the Manager URL, the primary surface is a chat interface. Not a cards grid. Not a session list. **A chat.** Like ChatGPT or Claude.ai's chat, but it talks to a Claude Code agent on this repo on my behalf.

There may be a secondary "what sessions are running" view that I can open if I want to, but it is not the default. The default is the chat.

### 2. Voice is just chat with the mic

The voice mode is NOT a separate feature. It is one of the input methods for the same chat:

```
text input  --type--+
                    |
voice input --mic-->|--> chat message --> Manager --> agent --> reply --> chat + TTS
                    |
```

Voice input flow:
1. Mic does its thing
2. Whisper transcribes audio -> text
3. Transcript drops into the chat input box
4. "Enter" gets pressed automatically (in walkie-talkie mode)
5. The chat message sends, exactly as if I had typed it

This means voice mode and text mode share 100% of everything from the chat input downward. **They can be tested independently**: I can use chat by typing without ever touching the mic, and the audio path is tested separately by uploading a clip to the voice endpoint.

### 3. Walkie-talkie buttons (drive-safe)

NO push-to-talk. NO hold-the-button. I will be driving.

- **One big button: START RECORDING.** Tap once. Screen goes full-screen with a giant "I'm recording you" indicator and a stop button. The button is huge, the text is huge, the visual is unmissable in peripheral vision.
- **One big button: STOP + SEND.** Tap once. Recording ends, audio uploads.

Two distinct taps. No hold. Targets must be large enough to hit with a thumb without looking.

When the user taps STOP + SEND, the recording uploads to Whisper, transcript fills the chat input, and the chat sends. The full-screen recording indicator transitions into the audio-cue state machine (next section).

### 4. Audio cues for everything that takes time

I am not looking at the screen. Every state has to be audible.

| State | Sound | Duration | Notes |
|---|---|---|---|
| **Uploading** | Subtle waiting loop A | While the audio is being POSTed | Gentle, ambient. Not annoying. Think soft ticker. |
| **Bad connection / retrying** | Slight variant B of the same loop | Until upload succeeds, indefinitely | Distinct enough that I can hear "we're still trying" but not jarring. Auto-retries forever with exponential backoff. |
| **Thinking** | Subtle loop C | While the agent processes the message | Different again from A and B but in the same family. |
| **Reply landing** | Single soft chime + the TTS reply | Once | The TTS reply (next section) starts on its own. |

These are subtle and short. Pre-load three small audio files. Loop them while the relevant fetch is in flight.

### 5. Bad connection = keep trying, with sound

I drive through dead zones. The voice flow MUST handle:

- POST `/voice/command` failures -> auto-retry with exponential backoff. Don't give up.
- Once retries start, switch from sound A to sound B so I know audibly.
- When the network comes back and the upload succeeds, switch to sound C (thinking).
- A "reply" never gets lost. The audio + transcript + reply are kept until the round-trip completes.

The implementing agent should pick a sane backoff (e.g. 1s, 2s, 4s, 8s, capped at 30s). The user can tap "cancel" to bail.

### 6. Spoken reply is SUMMARISED, not raw

The chat panel can show the full reply text (for when I'm not driving and I do look at the screen).

What the phone SPEAKS aloud must be a short summary - human-friendly, ear-friendly, ideally one to three sentences. Raw Claude Code output is full of formatting, code blocks, "Read tool" / "Edit tool" verbiage. None of that is for the ear.

The implementing agent decides how: the cleanest approach is a small Haiku side-call ("summarise this Claude reply for someone listening in a car, one to three sentences") that runs after the agent's reply lands. The chat shows the full reply. TTS reads only the summary.

The TTS itself uses the browser's `SpeechSynthesis` API (free, local, low latency). OpenAI TTS is out of scope for v1.

### 7. Mobile first

The Manager URL is what I open on my phone. Tap targets must be huge. Layout must be vertical-first. Viewport must respect `safe-area-inset-*`. The chat list must reach close to the screen edges. The two walkie-talkie buttons must be the dominant visual when voice mode is active.

The desktop case is a happy side-effect; the phone-in-a-car case is the design target.

### 8. What the Manager actually talks to: ONE Claude Code session on this private repo

A `cc-director` instance runs on a machine with this repo (`D:/ReposFred/private/`) as a known session. The Manager UI on that Director is configured to relay every chat message to that ONE session by default. No session picker, no routing logic in v1.

The Director's existing `POST /sessions/{sid}/prompt` endpoint already sends a text prompt to a session and returns the agent's reply. The Manager's `/chat` endpoint is a thin wrapper around that.

What about "list sessions" / "what's running"? In v1, **don't build it.** The user has one session, the Manager always relays to it. If the user says "what sessions are running" out loud, that message goes to the agent, and the agent can answer ("you're talking to me, I'm the only session"). Smarter routing is a v2 question.

---

## What's already built in `D:/ReposFred/cc-director/` (use, do not duplicate)

Everything below is in cc-director and works today. Reuse, do not rebuild.

| Building block | Where | Status |
|---|---|---|
| `cc-director.exe` (the Director) | `src/CcDirector.Avalonia/` | runs today; can also be hosted headlessly |
| `ControlApiHost` (REST API + Manager HTML) | `src/CcDirector.ControlApi/` | full session API ready |
| `POST /sessions/{sid}/prompt` | `ControlEndpoints.cs` | sends text to a session, returns reply when `waitForIdle: true` |
| `GET /sessions/{sid}/buffer` | same | streaming buffer access |
| `POST /sessions/{sid}/recap` | same | Haiku-based summary of a session - PROBABLY the right primitive for the TTS summary step |
| `VoiceService` + `POST /voice/command` | `src/CcDirector.Core/Voice/VoiceService.cs` + `ControlEndpoints.cs` | Whisper transcription pipeline. **Today** it also tries to classify intent and respond directly - in the new design we want it to just return the transcript and let the chat layer drive the message into `/chat`. |
| `voice-test-host` | `tools/voice-test-host/` | headless Director launcher; safe to start from a Claude Code parent because it never creates Claude Code sessions itself |
| Manager `manager.html` | `src/CcDirector.ControlApi/Web/manager.html` | currently cards-first with a voice panel bolted on. v2 redesign: chat-first. |
| `appsettings.json` / `OPENAI_API_KEY` env var | loaded by `AgentOptions.ResolveOpenAiKey()` | already wired through to `VoiceService` |
| Tailscale-friendly binding | `ControlApiHost` binds `0.0.0.0` in non-ephemeral mode | accessible from phone over Tailscale today |

---

## What needs to change vs. what's there today

| Today | Target |
|---|---|
| Manager UI is a cards grid with a voice panel sliding up at the bottom | Manager UI is a chat conversation; cards become a secondary "list sessions" view at most |
| Push-to-talk: hold space or hold the mic button | Walkie-talkie: tap to start, tap to stop and send |
| `VoiceService` classifies intent (ListSessions, OpenSession, ...) and crafts its own reply | `VoiceService` just transcribes; the transcript becomes a chat message to the agent |
| Single round-trip with no audio feedback | Three audio-cue states (upload, retry, thinking) with a soft chime on reply |
| No retry on network failure | Exponential-backoff auto-retry with audible state change |
| Browser `SpeechSynthesis` reads the entire reply verbatim | Summarisation step (Haiku side-call) produces a short "speak this" string; only that gets TTS'd |
| No mobile considerations | Mobile-first layout; safe-area insets; large tap targets; vertical-first chat |

---

## Implementation phases

Build in this order. Each phase is independently testable and ships value.

### Phase 1 - Chat works end-to-end (text only, no voice)

**Goal:** I can open the Manager URL on my phone, type a question, get an answer from the private-repo agent. No mic involved yet.

Deliverables:
- A `/chat` endpoint on the Director that accepts `{ text: "..." }`, sends it to the configured session, waits for the agent's reply, returns `{ reply, displayText, summary }`.
- A new chat UI as the default view at `/`. Vertical message list. Big text input at the bottom. Send button.
- Configuration: which session is "the configured session" - probably the first session in `SessionManager.ListSessions()` whose repo path matches a value in `appsettings.json` (e.g. `Chat.SessionRepoPath = "D:/ReposFred/private"`).
- Acceptance: send "hello" from a phone over Tailscale, get an agent reply, see it in the chat.

### Phase 2 - Voice input drops into the chat

**Goal:** Mic input becomes a chat message via Whisper.

Deliverables:
- Walkie-talkie buttons: START and STOP+SEND. Big, mobile-first, no hold.
- Full-screen recording overlay between START and STOP.
- Refactor `VoiceService` so its primary output is `{ transcript }` (intent classification is optional / behind a flag for legacy use).
- Audio uploads via the existing `/voice/command`. Transcript fills the chat input. Auto-press-Enter.
- Acceptance: tap START, say "what files have you read recently?", tap STOP. The transcript appears in the chat input, the chat sends it, the agent replies.

### Phase 3 - Audio cues + auto-retry

**Goal:** I can use this without looking at the screen.

Deliverables:
- Three small audio files (`upload.mp3`, `retry.mp3`, `thinking.mp3`) preloaded. Each is a 1-3 s loop.
- Client-side state machine:
  - `recording` -> no sound (mic on)
  - `uploading` -> upload.mp3 looping
  - `retrying` -> retry.mp3 looping (network error caught, backoff timer running)
  - `thinking` -> thinking.mp3 looping (server got the audio, agent processing)
  - `reply` -> single soft chime, then TTS starts
- Exponential-backoff retry on the audio POST. Cap at 30 s. Cancel button visible.
- Acceptance: pull the network cable mid-record on desktop. Hear the retry sound. Plug back in. Hear it transition to thinking. Get the reply.

### Phase 4 - Summarised TTS

**Goal:** The phone speaker says something a human can listen to in a car.

Deliverables:
- After the agent replies, the Director makes one Haiku side-call to summarise the reply for the ear ("one to three short sentences a human in a car can understand"). Cached per-message.
- The chat shows the full reply text. The TTS only reads the summary.
- `SpeechSynthesis` plays the summary at slightly above default rate (1.1x or so).
- Acceptance: ask a multi-paragraph question, the phone speaks 2 sentences max, the chat shows everything.

### Phase 5 - Mobile polish

**Goal:** Looks and works great on a phone in a car mount.

Deliverables:
- Layout: vertical only on mobile widths. Chat fills the screen. Buttons are 64+ px high. Text is at least 16 px.
- Safe-area insets for notch phones.
- Pre-cache the audio cues with a service worker so they don't fail on a flaky network.
- Acceptance: open on the actual phone, in landscape and portrait, in a car mount. Use it without touching anything but the two buttons.

---

## Where to host the Director

In v1, the Director runs on the user's home machine (the same one cc-director is being developed on). The phone reaches it over Tailscale. URL is `http://<home-machine-tailnet-ip>:<port>/`.

The implementing agent should NOT try to package the Director as a VPS-deployable thing in v1. Get it working over Tailscale first. Cloud hosting is a future problem.

---

## Out of scope (do NOT build this in v1)

- Authentication other than Tailscale trust. The Tailnet is the boundary.
- OpenAI TTS (browser `SpeechSynthesis` is fine for the ear).
- Cross-Director / multi-machine federation. ONE Director, ONE machine.
- Cross-session routing. ONE session, configured.
- Cards / detail view as the primary UI. Chat is primary.
- "Smart" voice intent parsing (ListSessions, OpenSession, etc.). The agent handles all of that since it's just a chat message.
- iOS Shortcut / Android Intent integration. Open the URL in mobile browser.
- Offline-first / queued messages. If the network is fully dead, the retry loop keeps trying; that's enough.

---

## Open questions for the implementing agent

These need a decision. The agent should pick a default and document it.

1. **Where exactly is "the configured session" pinned?** Recommend: `appsettings.json` has `Chat.SessionRepoPath = "D:/ReposFred/private"`. On Director startup, look for an existing session in `SessionManager` whose `RepoPath` matches; if none, auto-create one with that repo path and ClaudeCode as the agent.
2. **What is the maximum recording duration?** Recommend: 60 s soft cap (warn the user via a colour change on the recording overlay), 120 s hard cap (auto-stop and send).
3. **What's the chat history persistence?** Recommend: per-browser localStorage for v1. Server-side history is a v2 question. The agent has its own JSONL transcript independently.
4. **What does the summariser do when the reply is already short (<=200 chars)?** Recommend: skip the Haiku call, just speak the raw reply. Cheaper, faster, no quality loss.
5. **What does the Manager say while waiting?** Recommend: the three audio cues are the answer. No "still thinking..." voice; that's a TTS-burnover-when-the-real-answer-arrives nightmare.
6. **How does the user know which Director / agent they're talking to?** Recommend: the chat header shows the repo path and the session's CustomName (when set). Static, set once on page load.
7. **What if the agent fires an `AskUserQuestion` mid-turn?** Recommend: surface the question text in the chat as a regular message from the agent. The user replies with their next chat message. This punts the structured-interaction work to v2.

---

## How the implementing agent should work

1. Read this doc top to bottom. Then read the cc-director `docs/architecture/gateway/` docs for context on where things live.
2. Build Phase 1 first. Get a text-only chat round-trip working over Tailscale to a phone.
3. Only after Phase 1 ships, move to Phase 2.
4. Use the `voice-test-host` (`D:/ReposFred/cc-director/tools/voice-test-host/`) for local development - it spins up the same Control API without disturbing any real `cc-director.exe` on the machine.
5. Don't touch any running `cc-director.exe` or `cc-director-gateway.exe` processes. Use the start/stop scripts in `cc-director/scripts/voice-test/` for the test host.
6. When unsure, default to the simpler thing. The whole point of v1 is the driving scenario - skip anything that doesn't move that needle.

---

## The single success scenario, written in present tense

I'm in the car. I have one URL bookmarked on my phone. I open it. I see a chat. I tap the big START button. The screen tells me clearly that it's recording. I say what I want. I tap the big STOP button. A soft waiting sound plays as the upload happens. I hear it change briefly to the "still trying" sound when we pass under an overpass. It comes back. The thinking sound plays. A soft chime. Then the phone speaks the answer. Short and human. The chat on the screen also shows the full text in case I want to read it at a stoplight.

That's it. That is the goal.
