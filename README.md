<p align="center">
  <img src="docs/assets/images/devthrottle-logo.png" alt="DevThrottle" width="420">
</p>

<h1 align="center">DevThrottle</h1>

<p align="center"><b>Mission control for command-line coding agents.</b><br>
Run a whole fleet of coding agents at once -- each on its own repo -- from one Windows app, and steer them by voice from your phone.</p>

<p align="center">
  <a href="https://devthrottle.com"><img src="https://img.shields.io/badge/Get%20DevThrottle-devthrottle.com-2EA44F?style=for-the-badge" alt="Get DevThrottle at devthrottle.com"></a>
</p>

![DevThrottle](docs/assets/images/cc-director-main.png)

Your job stopped being typing code -- it's watching agents work. DevThrottle is where you watch them: the orchestration tool for command-line coding agents.

## Why DevThrottle

- **Run many agents at once -- and many *different* agents.** Claude Code, OpenAI Codex, Cursor, GitHub Copilot, Gemini, opencode, Grok, Pi, or any CLI you bring -- side by side, each on its own repository, in one app.
- **Know the moment an agent needs you.** DevThrottle watches every session and surfaces the ones waiting on you -- on your desktop or your phone. The watching part nobody else built.
- **Supervise from anywhere.** Voice control and a voice-first mobile app, over a gateway we host for you -- so the machines at home and at the office are reachable wherever you are, with nothing of your own to keep running.
- **Up and running in about five minutes.** A dead-simple installer -- no admin, no terminal wrangling, no WSL. From download to your first agent in minutes.
- **Install it without an account.** The Director is free and installs and runs with no sign-up. You sign in later, only when you connect a gateway.
- **Signed, yours.** The Windows installer is code-signed.

## Get DevThrottle

Installing DevThrottle is two steps, and only the second one asks who you are.

### Step 1 -- install the Director (free, no account)

The Director is the app: it runs your agents, watches them, and ships the `cc-*` command line tools. It installs and runs **with no account and no gateway**. Nothing is gated behind a sign-up, and nothing needs administrator rights -- everything lands under your own user profile.

Three ways to get it, all the same install:

| | How |
|---|---|
| **Download it** | **[devthrottle.com/download](https://devthrottle.com/download)** -- the Windows installer, and the one-line install for macOS. *"Free, no account needed."* Go to `/download` directly; the home page's **Get started** button takes the account-first route instead. |
| **By prompt** | Paste [one prompt](docs/install/install-prompt.md) into a coding agent you already have and it installs the Director for you, unattended. |
| **From a release** | The same assets, direct: [download page](https://devthrottle.com/download). |

The wizard is three screens -- Welcome, Install, Complete. There is no role to choose and no sign-in to get past.

### Step 2 -- connect a gateway (this is where you sign in)

The gateway is what makes DevThrottle reachable beyond the one machine: your agents on your phone, voice control, and the morning report. The Director asks you about it on its second setup screen, and **"Not now" is a real answer** -- the app stays fully usable on this machine without one.

| | |
|---|---|
| **Hosted gateway** *(recommended)* | We run it. Sign in and the machine is enrolled -- phone, voice and the morning report work immediately. Part of **Pro**; see [pricing](https://devthrottle.com/pricing). New accounts start on a 14-day Pro trial, no card. |
| **Self-hosted gateway** | Run the gateway on your own machine and join it. Windows only, and it still needs a DevThrottle sign-in. For advanced setups. |
| **Not now** | Local-only on this machine. Connect any time from Settings. |

Either gateway needs a DevThrottle login -- Google, GitHub, or email. The email route sends you a sign-in link rather than asking for a password, so Google or GitHub is the quicker way in. There is no bring-your-own-key: inference always routes through DevThrottle, which is exactly why connecting a gateway asks you to sign in.

<p align="center">
  <a href="https://devthrottle.com"><img src="https://img.shields.io/badge/Start%20free-devthrottle.com-2EA44F?style=for-the-badge" alt="Start free at devthrottle.com"></a>
</p>

## Skills for your agents

This repository is also a Claude Code plugin marketplace. Add it once:

```
/plugin marketplace add thefrederiksen/devthrottle
```

Then install either plugin:

```
/plugin install agent-discipline@devthrottle
/plugin install devthrottle@devthrottle
```

### `agent-discipline` -- three skills about evidence

Nothing to sign up for, nothing to install, no mention of any product in the skills
themselves. They work on the first repository you point them at.

- **`checks-that-fail-open`** -- a check whose pass condition is an ABSENCE certifies a run that never happened. "No errors found" is satisfied by finding nothing because nothing ran. Restate it as a specific presence.
- **`proof-covers-the-wrong-thing`** -- the harder sibling: the evidence was gathered, the argument is sound, and it is about something adjacent to what you changed. It reads exactly like a proof, because it is one -- of a different claim.
- **`destructive-sweeps-lean-to-keep`** -- a destructive operation acts only on what it can positively prove is disposable. Enumerate what to DELETE, never what to skip.

These were not written from first principles. Each one is a defect a fleet of unattended
coding agents hit over and over, in a different medium every time, until somebody put the
instances side by side and saw one shape. That is the part worth having: the war stories
are still in the skill bodies, because the shape is easier to recognise from the examples
than from the rule. **This is what running a lot of agents at once teaches you, and it is
the reason we built the thing that runs them.**

Each skill carries only the fields in the [agentskills.io](https://agentskills.io)
specification, so the same files load under other agents that implement the standard --
not only Claude Code.

### `devthrottle` -- for people who run it

Install guidance and session-to-session messaging. Useful once you have DevThrottle,
and honestly useless before that, which is why it is the second plugin and not the first.

**The `agent-discipline` skills never mention the product, and no skill here pitches it.**
A skill body is loaded straight into your agent as instructions, so a pitch there would be
an advert injected into your context. The two skills above are about DevThrottle because
documenting it is what they are for -- they tell you how to install it and how to use it,
and they do not sell it. The selling stays on this page, where you chose to read it.

---

<sub>The Director installs and runs free, with no account; a DevThrottle login is needed only to connect a gateway. The product experience, onboarding, and support live at <a href="https://devthrottle.com">devthrottle.com</a>.</sub>
