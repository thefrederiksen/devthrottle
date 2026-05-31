# CC Director: Philosophy and How to Run It

**Status:** DRAFT
**Date:** 2026-05-31
**Author:** Soren
**Audience:** Anyone evaluating, installing, or contributing to CC Director. Read this before the installation guide.

## TL;DR

Installing CC Director should be one simple step on Windows or macOS, and from then on it keeps itself current: the next time you run it, it auto-updates. Getting there has one rule. **Anything deterministic is code; only the parts that need judgment are a prompt to Claude Code.** Downloading the right build for your OS, verifying it, installing it, registering the Gateway service, and updating everything later are deterministic, so they belong in a small installer/updater tool, not in a prompt that has to be re-derived every time. Claude Code is for the messy, machine-specific parts: getting Claude Code itself onto the box, signing into Tailscale, the one admin step, and troubleshooting.

## 1. Deterministic work is code; judgment is a prompt

The original idea was "Claude Code installs everything by following a prompt." That is the right instinct for the *fuzzy* parts of setup, but it is the wrong tool for the *deterministic* parts. Asking the agent to re-figure-out "which asset for this OS, where to put it, how to verify it" on every run is slow and adds points of failure.

So the line is:

- **Deterministic -> code.** Detect OS, download the correct release artifacts, verify their checksums, install them, register the Gateway as a service, put things on PATH, and update them later. Same every time, no judgment required. This lives in a real, cross-platform **installer/updater tool**.
- **Judgment -> Claude Code.** Installing Claude Code itself (Anthropic's own installer does this), signing into a Tailscale account, the single admin elevation the Gateway service needs, and diagnosing whatever is weird about a specific machine. This is where an agent earns its keep.

The installer is boring on purpose. Boring is reliable.

## 2. Install once, update forever

The whole experience reduces to two promises:

1. **One simple install on Windows or macOS.** A single command (or one click) downloads and installs everything that is always needed together: the Director and the Gateway. It feels like a normal installer ("Install everything? [Y/n]"), not a scavenger hunt across releases.
2. **It updates itself.** The next time you run CC Director, it is current. Nobody re-pastes anything. The Director updates itself per-user; the Gateway runs as a service (LocalSystem on Windows) and updates itself and the Cockpit on its own, using the privilege it already has. After the first install there are no more admin prompts.

A single release manifest is the source of truth for the versions of all the pieces (Director, Gateway, Cockpit, tools), so one updater can reason about the whole set.

> **Reality check (2026-05-31):** this is the target, not the current state. Auto-update does not reliably work today, the Gateway and Cockpit are not yet shipped as release artifacts, and the installer is Windows-leaning. The spec and tracking issue for closing that gap are linked at the bottom of this document.

## 3. Two front doors, one installer underneath

There are two ways to start, and they call the same deterministic installer so there is one source of truth:

- **The normal installer:** a one-line command (`irm .../install.ps1 | iex` on Windows, `curl -fsSL .../install.sh | bash` on macOS) for people who just want it installed.
- **The Claude Code prompt:** for people already inside Claude Code. The prompt does not reinvent the install; it triggers the installer tool.

## 4. Requirements

- **A Claude subscription (hard requirement).** The minimum paid tier is enough. Claude Code is how you actually use CC Director (it manages Claude Code sessions), and it is what handles the judgment parts of setup. The free Claude.ai plan does not include Claude Code.
- **An OpenAI API key (feature-gated, not required to run).** Needed only for voice and image features (text-to-speech, transcription, voice mode, image tools). The base product runs without it; those specific tools fail with a clear "add your key" message until you provide one.

Beyond that, the minimum is a known-good floor, not a cage: Claude Code is the default agent, but you can run other agents in your sessions once you are up.

## 5. Platform philosophy

- **Install and auto-update must work equally well on Windows and macOS.** This is non-negotiable for the experience above. The installer/updater is cross-platform by design.
- **The Director runs on Windows and macOS (Apple Silicon).** Windows is currently the most complete experience.
- **The Gateway is Windows-only for now.** It runs as a Windows Service (LocalSystem). macOS Gateway support (a launchd daemon) is tracked as a separate feature request.

## Related Documents

- [README.md](../README.md) -- product overview and the current install steps
- [docs/install/install-prompt.md](install/install-prompt.md) -- the Claude Code install prompt (current)
- Spec: unified install + auto-update (Director + Gateway + Cockpit) -- see the tracking issue below
- [docs/architecture/gateway/](architecture/gateway/) -- Gateway architecture
- [docs/plans/phase1-https-via-tailscale.md](plans/phase1-https-via-tailscale.md) -- Tailscale remote access

## Document History

| Date | Author | Change |
|------|--------|--------|
| 2026-05-31 | Soren | Initial draft (agent-installs-everything model) |
| 2026-05-31 | Soren | Reframed: deterministic install/update is code, judgment is a prompt; install-once-update-forever; OpenAI is feature-gated, not a hard requirement |
