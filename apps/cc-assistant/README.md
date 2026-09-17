# Wilson

A voice assistant for the kitchen. Say its name and talk to it. The name is whatever each person
decides it should be.

Live at **https://cc-assistant-bice.vercel.app** — open it, press Start, and say "Wilson".

It listens with the browser's own recogniser, thinks with a hosted model, speaks with the browser's
own voice, and keeps timers itself. There is no sign-in, because the page carries no credential of
any kind.

## What it can do

| | |
| --- | --- |
| **Answer questions** | Anything the model knows. Replies are one or two spoken sentences, never a paragraph. |
| **Timers** | Named or not. Start several, stop one by name, stop them all, ask what is running. |
| **Silence an alarm** | While one is ringing, "stop" or "shut up" works with **no wake word**. |
| **Weather** | Where you are, or anywhere you name. Needs a home town in settings. |
| **Look things up** | Anything live or recent: prices, news, results, who holds a job. Takes two or three seconds because it searches the web. |
| **Interrupt it** | Say the wake word while it is talking and it stops mid-sentence. |

It cannot play music, control anything in the house, read a calendar or a list, or remember anything
between sessions. It says so plainly when asked rather than pretending.

## How a turn works

```
asleep --wake word--> listening --you stop talking--> thinking --> speaking --> asleep
```

It goes straight back to sleep after answering. It does not linger listening.

Three lanes, chosen by how fast each has to be:

| Lane | Handles | Cost |
| --- | --- | --- |
| Instant, local | Silencing a ringing alarm | no network |
| Fast model with tools | Timers, weather, ordinary questions | ~200 ms |
| Search model | Anything live, recent, or the fast model is unsure of | 2-3 s |

The fast model routes by itself: when it decides a question needs looking up it calls the `look_up`
tool, and the server answers that with a second, slower model that has Groq's built-in web search.
There is no separate classifier in front, because that would add a round trip to every turn.

## Ears

Two ways to hear a command, chosen in Settings:

- **cloud** (default): the browser's recogniser hears only the wake word. From there Wilson records
  from its own microphone until you go quiet (a quarter second of loudness at a time; three quiet
  quarters end it), sends the clip to Whisper on Groq with the household's names and places as
  spelling hints, and the words that come back are the command. About 300 ms, four cents an hour of
  speech. This is the path the kitchen box will use, since a Raspberry Pi has no platform recogniser.
- **browser**: the platform recogniser's own transcript, as before. Free, instant, and only exists
  where the OS ships one.

## The rules, and the bugs that produced them

Every one of these came from something that actually went wrong, and every one is kept as a test so
it cannot come back unnoticed.

**The device says what happened, never the model.** The model only decides which tool was meant; the
sentence spoken afterwards is built from the real outcome. Asked to set a timer before this existed,
it answered "Timer set for ten minutes" without setting anything. A confident false confirmation is
worse than any refusal, because it is trusted.

**Nothing said while it is speaking, or for 1.5 seconds after, is a command.** Its own voice came
back through the microphone, was transcribed like anything else, and was answered — one question
became four turns of it talking to itself. Two guards now, time and text, because either alone leaks.
The text one has to be fuzzy: it said "Alright." and heard itself as "all right".

**There is no local shortcut for a command that has a name in it.** There was one for timers, to save
four hundred milliseconds, and it grabbed any sentence containing a duration and threw the name away.
"Set a timer called barbecue for three minutes" worked; the same sentence with one word misheard
silently became an unnamed timer. A shortcut that is sometimes wrong is worse than a delay that is
always right.

**Never ask the platform for something it has not got.** Requesting on-device speech recognition on a
device whose model is merely downloadable yields a recogniser that reports nothing at all, which
looks exactly like a dead microphone. Check first.

**Show what was ignored.** A guard that works silently is indistinguishable from one that is broken.
Suppressed speech, recogniser notices and the microphone level are all on screen.

## Where everything lives

| | |
| --- | --- |
| Source | this directory, on `main` |
| Deployed | Vercel project `cc-assistant`, scope `soren-frederiksens-projects` |
| Deploys | automatically, on any merge to `main` that touches this directory |
| Model | Groq, `qwen/qwen3.6-27b` with reasoning off; `openai/gpt-oss-120b` with `browser_search` for look-ups |
| Keys | `GROQ_API_KEY`, optionally `ASSISTANT_MODEL` and `ASSISTANT_SEARCH_MODEL`, Vercel production environment only |
| Weather | Open-Meteo, no key needed |

## Running it

The real thing is Wilson's own service, which serves the page, runs the functions, and keeps the
memory. It wants a Groq key in `GROQ_API_KEY`, which cc-secrets supplies from the entry
`groq-api-key` (the key never reaches a file or a terminal):

```
npm install
npm run build
cc-secrets run groq-api-key --timeout 0 -- npm run serve
```

cc-secrets hands back a command's output only when it ends, so Wilson writes its running log to
`service.log` in its data directory as well as to the console.

Then <http://localhost:5183/cc-assistant/>. Everything Wilson keeps lands in `%LOCALAPPDATA%\wilson`
(`WILSON_DATA_DIR` to move it): `household.json`, `soul.md`, `turns.jsonl`, all readable, all
deletable. Add `?debug=1` to the address for the debug screen or `?debug=0` for the kitchen screen;
a PC defaults to debug and a phone to the circle, and the choice is remembered.

For front-end work without the service, `npm run dev` runs Vite with `/api` proxied to the deployed
copy on Vercel (set `ASSISTANT_API_ORIGIN` to point it elsewhere). That copy has no memory.

**On a phone the microphone will not work over plain HTTP.** Browsers allow it only on a secure
connection, and `localhost` is the sole exception, so a LAN address loads the page and then refuses
the microphone. Put Tailscale in front of the service:

```
tailscale serve --bg --https 8443 http://127.0.0.1:5183
```

Then on the phone: <https://soren-north.taildb08ed.ts.net:8443/cc-assistant/>. Tailscale cannot
proxy straight to Vercel, because it keeps the tailnet name as the Host header and Vercel answers
`DEPLOYMENT_NOT_FOUND`; the local server in between is what rewrites it.

## What is in here

| File | What it does |
| --- | --- |
| `src/assistant/AssistantScreen.tsx` | The app: the loop, the states, the two screens |
| `src/assistant/DebugPanel.tsx` | The debug screen: turn log, people and memory, places, soul editor, voice enrolment |
| `src/assistant/speech.ts` | Listening with the browser's recogniser; chirps, thinking ticks and cues |
| `src/assistant/voice.ts` | Wilson's voice: the Orpheus stream played as it arrives |
| `src/assistant/speakerId.ts` | Who is speaking: a voice embedding from the last few seconds of audio |
| `src/assistant/echoGuard.ts` | Stops it answering its own voice |
| `src/assistant/micLevel.ts` | Proof the microphone is open, in pixels |
| `src/skills/timerParse.ts` | Reading durations out of a sentence |
| `src/skills/timerLogic.ts` | Name matching, silencing, and the sentences |
| `src/skills/useTimers.ts` | The timers themselves, and the alarm |
| `src/skills/weather.ts` | Turning a reading into words |
| `src/wakeWord/wakeWordMatcher.ts` | Finding the chosen word in a transcript |
| `api/talk.js` | The brain: soul + rules + memory in, a sentence or a tool call out; `look_up` runs here |
| `api/speak.js` | The voice: Orpheus on Groq, streamed |
| `api/hear.js` | The ears: a clip in, Whisper on Groq writes it down, with the household's spellings as hints |
| `src/assistant/cloudEars.ts` | Recording a command until the person goes quiet, and sending it to be heard |
| `api/weather.js` | Open-Meteo, with misheard names corrected and remembered |
| `api/turn.js` `api/people.js` `api/soul.js` `api/voice.js` | The log, the people and their memory, the soul document, voice enrolment and matching |
| `api/result.js` | Where diagnostics reports go |
| `server/wilson.mjs` | Wilson's own service: serves the page, runs the functions, owns the data |
| `server/store.mjs` | What Wilson keeps: `household.json`, `soul.md`, `turns.jsonl` |
| `server/memory.mjs` | What a turn brings in from memory, and what it leaves behind |

Behind the **Diagnostics** link at the bottom of the app are the measurement screens that settled
which speech model a device should run. They are not the product but they answered real questions.

Run the tests with `npm test`.

## Known limitations, all deliberate

- **Timers only run while the app is on screen.** A browser cannot reliably wake itself. This is
  written on the screen rather than left to be discovered when dinner burns.
- **Nothing is remembered between sessions** except the wake word and the home town.
- **Every question costs money.** Small, but a device asked things all day is a running cost.
- **The Groq account is on the free tier: 8,000 tokens a minute.** A turn costs several hundred, so
  a burst of questions, or a look-up, can hit the limit; Wilson then says it is rate limited. The
  Dev tier in the Groq console lifts it.
- **The first request after a quiet spell takes several seconds**, because the serverless function
  cold-starts. Everything after is 200 to 400 ms.
