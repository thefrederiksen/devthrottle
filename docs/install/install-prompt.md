# DevThrottle - install prompt (Windows + macOS)

The **prompt you hand to a coding agent you already have** to install the latest DevThrottle. One
prompt, OS-aware: the agent detects the platform and uses the **command-line installer** (never the
graphical wizard) to lay everything down **per-user, with no admin/sudo**, so the built-in
**auto-update can replace it in place**. Never `Program Files` (Windows) or `/Applications` (macOS).
Philosophy: [../PHILOSOPHY.md](../PHILOSOPHY.md).

This is the flagship easiest path: paste one prompt, get a working Director.

**It installs in one step and stops.** Step 1 - the Director, every `cc-*` tool, and the Launcher -
needs **no account and no gateway**, so the agent runs it start to finish unattended. Step 2,
connecting a gateway, is a separate choice made by the person, later, in the app. The prompt does not
sign anyone in and does not pick a gateway for them; it finishes by putting the choice in front of
them.

The command-line installer is the **same install engine** the graphical wizard uses, so a human and
an agent end up with an identical setup.

Copy everything in the block below into a coding agent session on the target machine.

---

```text
Install the latest release of DevThrottle on THIS machine, headlessly. Use the command-line
installer, NOT the graphical wizard, and never require admin/sudo. Detect the OS and follow the
matching section. STOP with a clear message if any step fails; do not silently work around it or
build from source.

SCOPE: install the Director ONLY. It needs NO account and NO gateway - run the whole thing
unattended and do NOT sign anyone in, do not create an account, and do not choose a gateway. When
the install is done, show the person the Step 2 choice at the end (text supplied below) and stop.

DOWNLOADS: https://devthrottle.com/download/files
Find the latest release: GET https://devthrottle.com/download/files/stable.json - its "tag" names the
release folder. The manifest is https://devthrottle.com/download/files/<tag>/release-manifest.json and it
must list this OS's asset below. ALWAYS verify the downloaded asset's
SHA-256 against the manifest's entry for that asset before running it; mismatch = STOP. Assets are at
https://devthrottle.com/download/files/<tag>/<asset-name>.

== WINDOWS ==
ASSETS: devthrottle-setup-cli-win-x64.exe  +  release-manifest.json
1. Download both to %TEMP%\ccd-install.
2. Verify: Get-FileHash -Algorithm SHA256 of devthrottle-setup-cli-win-x64.exe == the manifest's
   sha256 for that asset, else STOP.
3. Install (per-user, no admin). `prereqs` reports whether a coding agent is present; `install`
   lays down the Director app, the cc-* tools (added to PATH), and the Launcher under
   %LOCALAPPDATA%\cc-director. Add --json to either for machine-readable output:
      devthrottle-setup-cli-win-x64.exe prereqs
      devthrottle-setup-cli-win-x64.exe install --json --log-file %TEMP%\ccd-install\install.log
   Do NOT install a .NET runtime. Every Windows binary DevThrottle places - the Director, the
   Launcher, the setup CLI - is self-contained and carries its own runtime. There is nothing that
   has to be on the machine first.
4. Verify: `devthrottle-setup-cli-win-x64.exe status --json` - every component present, at the
   release version.

== macOS (Apple Silicon) ==
macOS has no fully-headless path today: the app is ad-hoc-signed, not Apple-notarized, so a browser
download is Gatekeeper-blocked. Run the official curl bootstrap - a curl download is never
quarantined. It downloads the setup app, verifies its SHA-256 against the manifest, installs it to
~/Applications, and opens it:
      curl -fsSL https://devthrottle.com/install-mac.sh | bash
Then the person completes the short setup app - it installs, and asks for nothing else. macOS is
Director-only; it cannot host a self-hosted gateway.

== BOTH ==
5. Report: the release tag installed, the install path, the SHA-256 you verified, and the log path.
   Windows logs are under %LOCALAPPDATA%\cc-director\logs\; macOS uses the app's log dir.

6. STOP HERE and print this, verbatim, as the last thing you say:

   ---
   DevThrottle is installed. It works right now on this machine - open it and start an agent;
   nothing else is required.

   Step 2, whenever you want it: connect a gateway. That is what puts your agents on your phone,
   turns on voice, and sends you the morning report. DevThrottle will ask you on its second setup
   screen, and "Not now" is a real answer you can change later in Settings.

     * Hosted gateway (recommended) - we run it. Sign in and this machine is enrolled. Included with
       Pro, and with the free 14-day Pro trial that new accounts get when they first connect:
       https://devthrottle.com/pricing
     * Self-hosted gateway - run it on your own machine and join it. Windows only. Advanced setups.
     * Not now - local-only on this machine.

   Either gateway asks you to sign in with Google, GitHub, or email. That is the only sign-in in
   DevThrottle, and it belongs to you, not to me - I am not going to do it for you.
   ---

DO NOT: sign in, create an account, or enroll a gateway on the user's behalf (Step 2 is theirs);
download or run the graphical installer on Windows (use the CLI installer); require admin/sudo; build
from source; place files in Program Files or /Applications; set OPENAI_API_KEY or ask for any API key
(DevThrottle has no bring-your-own-key - inference routes through the account-minted key the runtime
mints itself); or skip SHA verification.
```

---

## Why this shape

- **Command-line installer, not the wizard** - the CLI front-end (`devthrottle-setup-cli`) drives the
  same install engine as the graphical wizard, so there is nothing to click through. Step 1 has no
  human step at all, so the agent runs it end to end.
- **It stops at the end of Step 1, on purpose.** The prompt used to finish by running `signin` (or
  `enroll`) and calling the browser sign-in "the ONLY step that needs a human". That framing made the
  account part of installing. It is not: the Director is fully working before anyone signs in, and the
  gateway is a choice with three real answers - hosted, self-hosted, or not now. An installer that
  signs you in has quietly made that choice for you.
- **Per-user, user-writable target** (`%LOCALAPPDATA%\cc-director` / `~/Applications`) - the
  auto-updater overwrites the running app's own path, so a user-writable location means updates need
  **no admin/sudo**. `Program Files` / `/Applications` would force elevation on every update or fail.
- **SHA-256 against `release-manifest.json`** - the same trust check the auto-updater uses, so install
  and update share one verification.
- **No runtime step.** Every Windows binary is published self-contained, so the prompt tells the agent
  *not* to install .NET. It used to say the opposite: the Director shipped framework-dependent and the
  prompt ran `winget install Microsoft.DotNet.AspNetCore.10`. That is no longer true, and an agent that
  installs a runtime nobody needs is an agent doing unrequested work on someone's machine.
- **No key, and no sign-in.** The prompt is forbidden from setting `OPENAI_API_KEY` or signing anyone
  in. There is no bring-your-own-key in the product, and the one sign-in that exists belongs to Step 2,
  which is the person's decision to make.
- **One OS-aware prompt** - hand the same prompt to any machine; the agent picks the right branch.

## Try it / next steps

1. Run the prompt in a coding agent on the target machine. The current latest release (with
   `devthrottle-setup-cli-win-x64.exe`, `cc-director-mac-arm64.zip` and `release-manifest.json`) is on
   the [download page](https://devthrottle.com/download).
2. Confirm the machine is genuinely usable **before** any sign-in: open the Director, and start an
   agent on a repository. If that needs an account, Step 1 is not done - that is the regression this
   prompt exists to prevent.
3. Cut a newer release; launch the installed build and confirm **auto-update** pulls it (the build
   must be a CI release build, i.e. `UpdaterEnabled=true`).

The public website carries the same guidance for an agent that reads it:
[devthrottle.com/docs/install#install-agent](https://devthrottle.com/docs/install#install-agent) and
`devthrottle.com/llms.txt`.
