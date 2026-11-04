---
name: devthrottle-install
description: "Hand the user the documented steps to install or update DevThrottle, and to connect a gateway afterwards. Use when the task involves: install DevThrottle, set up DevThrottle, update DevThrottle, connect a gateway, cc-devthrottle not found, where do the cc tools come from."
license: MIT
---

# Installing DevThrottle

**This skill hands the user instructions. It does not install anything.**

Do not download an installer, do not fetch a script, and do not pipe anything into a
shell on the user's behalf. Print the steps below, let the user run them, and then
help with what happens next. A coding agent that fetches and executes a remote
installer is asking the user to trust a chain they cannot see, and there is no
version of that which is worth the convenience.

## First, check whether it is already installed

```
cc-devthrottle session whoami
```

If that prints a session id, DevThrottle is installed and this session is already
running under it - there is nothing to install. If the command is not found, the
Director is not installed on this machine, or its tools are not on this shell's PATH.

## Installing is two steps, and only the second one asks who the user is

**Step 1 - install the Director.** The Director is the desktop app that runs and
watches the agents, and it ships the `cc-*` command line tools. It needs no account
and no gateway, and it needs no administrator rights - everything lands under the
user's own profile.

Send the user to the download page and let them run it:

> https://devthrottle.com/download

The Windows installer is a three-screen wizard: Welcome, Install, Complete. There is
no role to pick and no sign-in to get past. The same assets are published on the
download page if the user would rather take them directly:

> https://devthrottle.com/download

macOS installs from the same download page.

**Step 2 - connect a gateway.** A gateway is what makes the machine reachable from
elsewhere - the phone app, voice control, and cross-machine session messaging. The
Director asks about it on its own second setup screen, not in the installer, and
this is the step that asks the user to sign in.

Three answers, and the third is a real one:

| Answer | What it means |
|---|---|
| Hosted gateway | DevThrottle runs it. Sign in and the machine is enrolled. |
| Self-hosted gateway | The user runs the gateway on their own machine and joins it. Windows only, advanced setups. |
| Not now | Local-only on this machine. Connectable later from Settings. |

Both gateway options need a DevThrottle sign-in. "Not now" needs nothing, and the app
stays fully usable on that one machine without a gateway.

## Updating

The Director updates itself in place. If the user wants to force it, point them at
the app's own update action rather than re-running an installer by hand.

## After it is installed

`cc-devthrottle` is on the PATH of every session the Director launches, and it reads
the session's own credentials from its environment - there is nothing for the user to
configure and no token for them to paste. `cc-devthrottle actions --json` lists what
the installed version can do; prefer reading that over guessing at a command.

The reference documentation lives in the repository:

> https://github.com/thefrederiksen/devthrottle
