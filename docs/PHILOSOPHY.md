# CC Director: Philosophy and How to Run It

**Status:** DRAFT
**Date:** 2026-05-31
**Author:** Soren
**Audience:** Anyone evaluating, installing, or contributing to CC Director. Read this before the installation guide.

## TL;DR

CC Director is not a self-contained app you double-click and configure through wizards. It is a thin runtime that sits on top of an AI coding agent. The agent does the setup work. There are exactly two hard requirements: a Claude subscription (so you can run Claude Code) and an OpenAI API key (so audio and the other AI tools work). Everything else, CC Director installs itself by handing prompts to Claude Code. Bring up a clean Windows or Linux machine, install Claude Code, and the rest is automatic.

## 1. The Core Idea: The Agent Installs the Product

The old way to ship desktop software is an installer that bundles every dependency, checks prerequisites with hand-written logic, and walks the user through wizard screens. CC Director is moving away from that.

The new model is simple: **once Claude Code is running, Claude Code installs everything else.** You do not write a brittle installer. You write prompts. CC Director ships the instructions, and the agent on the machine carries them out: checks prerequisites, installs what is missing, places tools on PATH, wires up config, and verifies the result.

This is why a Claude instance is a hard requirement and not an optional add-on. The Claude instance is not just how you use CC Director. It is how you install and bring up CC Director in the first place. There is, in effect, no traditional installation step. There is a prompt, and an agent that executes it.

The payoff: bring up a brand-new machine (Windows or Linux), install Claude Code, point it at CC Director, and the rest runs automatically. The same prompt-driven setup works regardless of the starting state of the machine, because the agent inspects the machine and adapts instead of assuming.

## 2. The Two Hard Requirements

You need both of these. They are not optional, and one does not substitute for the other.

| Requirement | Why it is required |
|-------------|--------------------|
| **A Claude subscription** | Required to run Claude Code. The minimum paid tier is enough. Claude Code is both the agent you work with and the mechanism that installs and configures everything else. Without it, there is no way to bring the product up. |
| **An OpenAI API key** | Required for audio (text-to-speech, transcription, voice mode) and for the other AI-powered tools. This is a standard OpenAI SDK API key with billing enabled. |

If you have both, you can run CC Director. If you are missing either one, core functionality will not work, and CC Director will tell you exactly what is missing rather than silently degrading.

## 3. Beyond the Minimum: Bring Your Own Agents

The two requirements above are the floor, not the ceiling. Claude Code is the default and the agent CC Director uses to install itself, but you are not locked to it for your actual work. You can choose to run other agents alongside or instead (other CLI coding agents and assistants). CC Director manages sessions; the agent inside a session is a choice.

The point of the minimum is to guarantee a known-good baseline so setup is deterministic. Once you are up, the mix of agents is yours.

## 4. Platform Philosophy: Windows First

CC Director is built for Windows first. It does run on Macs, but it runs better on Windows, and Windows is where it is most complete.

- **Windows is the primary, best-supported platform.** This is the daily-driver environment the product is developed and used in.
- **Mac is supported but secondary.** It works, but expect rough edges relative to Windows. Do not expect feature parity in the same release.
- **The Gateway is currently Windows-only.** The central gateway, so far, is only supported on Windows. Mac and Linux gateway support is not there yet.
- **Linux is a first-class target for the prompt-driven setup.** The agent-installs-everything model is meant to bring up a clean Linux box just as readily as a Windows one, even where the full desktop and gateway experience is not yet complete there.

When in doubt, run it on Windows. That is where the experience is strongest today.

## 5. Prerequisites the Agent Checks

Because the agent does the setup, the prerequisite list is short and the agent verifies it for you rather than you verifying it by hand. The agent checks for and, where possible, installs:

- **Python** -- required for the tools and pipelines.
- **Brave browser** -- used as the testing/automation browser. CC Director standardizes on Brave for browser-driven work so behavior is consistent across machines.

The agent inspects the machine, reports what is present, installs or instructs for what is missing, and confirms the end state. If a prerequisite cannot be satisfied, you get a clear error and the exact fix, not a silent fallback to a degraded mode.

## 6. What This Means in Practice

To stand up CC Director on a fresh machine:

1. Make sure you have a Claude subscription and an OpenAI API key.
2. Install Claude Code (Windows or Linux).
3. Hand Claude Code the CC Director setup prompt.
4. Let the agent check prerequisites (Python, Brave), install the tools and skills, wire up config, and verify.
5. Start working. On Windows you also get the full desktop and gateway experience.

There is no separate installer to babysit and no wizard to click through. The agent is the installer.

## Related Documents

- [README.md](../README.md) -- product overview, features, download
- [docs/goals/PRD_CC_DIRECTOR.md](goals/PRD_CC_DIRECTOR.md) -- the architecture and vision (Session Supervisor, Gateway)
- [docs/public/getting-started/02-installation.md](public/getting-started/02-installation.md) -- the current step-by-step install guide (the "old way" this philosophy is moving away from)

## Document History

| Date | Author | Change |
|------|--------|--------|
| 2026-05-31 | Soren | Initial draft capturing the philosophy, the two hard requirements, platform stance, and the agent-installs-everything model |
