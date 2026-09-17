# Wilson: picking this up later

Written 28 August 2026, at the point where Wilson works and is being put down for a while.

The README says what Wilson is and how it works. This says what state it is in, what was decided and
why, and what the next person has to decide before they can go further. Read the README first.

## Where it stands

It works. It is deployed, it deploys itself on merge, and it does four things: answers questions,
keeps timers, tells you the weather, and shuts up when told to.

**Update, 29 August, evening.** Wilson now has its own service (`server/wilson.mjs`) and, when it
runs there, a memory: people with profiles and remembered facts, a per-household soul document,
resolved place names, and a turn log, all as plain files under `%LOCALAPPDATA%\wilson`. It speaks
with Orpheus streamed from Groq, ticks while it thinks, and has a kitchen screen (one circle) and a
debug screen. Voice identification is built (WavLM x-vector in the browser, matching on the
service) and NOT yet proven: it needs two real voices enrolled in a real browser. Cloud ears (#2620) are in: after the wake word Wilson records the command itself, ends it on
silence, and Whisper on Groq writes it down with the household's spellings as hints; the browser
recogniser is left with the wake word only. The endpointing numbers live in `cloudEars.ts` and are
the first thing to tune if clips end early or late. A local wake-word model for the Pi is the one
remaining piece of hearing. Issues #2612 to #2622 on the repo are the map. The Vercel deployment has none of the memory: its functions run
without the service and say so.

Everything below is either a decision that has already been made and should not be relitigated
without a reason, or a decision nobody has made yet.

**Parked, 29 August, afternoon.** The service is stopped and the "Wilson" logon task disabled, on
Soren's call, because the phone could not open the Tailscale URL
(https://soren-north.taildb08ed.ts.net:8443/cc-assistant/) even though it answered 200 from every
check on the PC: curl, a real Chrome window, MagicDNS on, and the phone showing active and direct on
the tailnet. The phone-side error text was never captured; that is the first thing to get next
time (DNS failure, timeout, or certificate warning each mean something different). The Vercel
project was deleted, so the Tailscale URL is the only Wilson. To resume: `Enable-ScheduledTask
Wilson`, or `cc-secrets run groq-api-key --timeout 0 -- npm run serve` here.

## Decisions already made, and why

**The browser's own recogniser does the listening, not Whisper.** Whisper was measured and works —
`whisperListener.ts` is written and wired to nothing — but the platform recogniser needs no download,
no model, and starts instantly.

There is a trap here that cost most of a day. The recogniser **starts and immediately ends, with no
error and no results, when driven over the browser automation protocol.** It works perfectly in a
browser a person is using. That produced a confident and wrong conclusion that Web Speech was dead on
this machine, and nearly a rewrite onto Whisper. **A browser driven by automation is not evidence
about a browser a person is using.** If it ever genuinely fails on a real device, `whisperListener.ts`
is the replacement and the calibration screens already say which model that device should run.

**The model translates; the device executes and speaks.** The model is given tools and returns tool
calls only. The page runs them and composes the sentence from what actually happened. This is not
style: it is why Wilson cannot tell you a timer is set when it is not. Any new skill should follow
it. If a future skill needs the model to phrase something, that is a real departure and worth arguing
about first.

**No local shortcuts for commands with arguments.** A regular-expression fast path for timers was
built, shipped, and deleted the same day because it silently discarded names. Four hundred
milliseconds is not worth a wrong answer.

**Timers live in the page.** Deliberately. A browser cannot reliably wake itself, so a background
timer would be a lie. If timers ever need to survive the app being closed, that is a native
application or a push notification from the Gateway, not a cleverer web trick.

## Decisions nobody has made yet

**Search is done (29 August), and it needed no key.** Groq has web search built into the
gpt-oss models (`tools: [{ type: "browser_search" }]`), so the `look_up` tool in `api/talk.js` is
answered by a second call to `openai/gpt-oss-120b` with search on. The fast model decides when to
call it; there is no classifier in front. Measured: 2-3 seconds for a live price, correct where the
plain model had invented one. Citation markers are stripped before speech.

**The fast model is now `qwen/qwen3.6-27b`, reasoning off** (was gpt-oss-120b on low). About 200 ms
a turn and right on arithmetic. Two traps found choosing it, both kept in the code comments:
gpt-oss on low reasoning can spend the whole token budget thinking and return an empty answer, and
`qwen/qwen3.8-27b` says "I'm setting a timer" in words instead of calling the tool, every time.
qwen3.6 did the same one time in three until the prompt said outright that words are not a timer;
with that line it was eleven for eleven. Any model change must re-run that check.

**The Groq account is on the free tier (8,000 tokens a minute).** That, not the model, was behind
several failures during testing. The Dev tier is a click in the console and a human decision.

**Knowing who is speaking is three different products and nobody has said which.** They need
different designs and have very different failure costs:

- *Do not answer the television.* Forgiving. A wrong answer is a missed command.
- *Know one person from another, and answer differently.* Harder. Needs enrolment per person.
- *Only obey one person.* A security feature, and it will lock you out when you have a cold.

Technically all three need a speaker-embedding model in the browser, an enrolment step, and raw audio
— which the platform recogniser does not give us, so `pcmCapture.ts` would have to run alongside it.
That much is known. **What is not known is which of the three was wanted**, and the answer changes
the design enough that building before asking would be waste.

Note also that none of it can be verified by one person: proving it distinguishes anybody needs at
least two voices.

**The directory is still called `cc-assistant` while the product is called Wilson.** Renaming is
cheap but it moves the Vercel root directory and the eventual Gateway mount, so do it deliberately.

**Wilson is not connected to DevThrottle at all.** No gateway, no device key, no fleet. That was
right for getting it working and is the open question for what it becomes: a DevThrottle feature
behind a sign-in, or its own thing. The decision recorded during the build was "a tool in
DevThrottle, because I cannot start another business", but nothing in the code commits to it yet.

## Things that will bite

**The service worker will serve you stale code.** It is network-first now, which fixes it, but if a
deploy ever seems not to have landed, add `?fresh=1` before concluding anything.

**Two watchers reported CI green when it was still running.** One tested a jq expression that returns
empty on error, so an error looked identical to success; the other detached, so the tool saw the
outer shell exit. If you write a watcher, make its pass condition a **presence** — the run reaching a
terminal state — never an absence.

**The Vercel deployment is connected to GitHub now.** It builds only when this directory changes.
That means a fix can no longer be hand-deployed; it has to be committed and merged. Slower, and
correct.

## If you want to keep going

In the order I would take them:

1. **Search**, keyless first. It is the biggest gap in usefulness and needs no decision from anybody.
2. **Warm the function**, so the first question of the day is not nine seconds.
3. **Endpointing.** Turn-taking is still whatever the browser does. The design for adaptive
   endpointing and an acknowledgement sound was worked out and never built; it is the difference
   between talking to it and operating it.
4. **Voice identity**, once somebody says which of the three it is.
