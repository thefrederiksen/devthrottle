# Proof - Issue #370: first-run onboarding wizard (lean v1)

This folder holds the verification evidence for the first-run onboarding wizard added in
`feature/370-onboarding-wizard`. The wizard is the in-app desktop first-run flow in
`CcDirector.Avalonia` (NOT the installer prerequisite wizard).

## How the proof was produced

- Built a dedicated test Director to slot 6 with `scripts\local-build-avalonia.ps1 -Slot 6`.
- Pointed an isolated config root at `D:\cc-dir-onboard-proof` via the user environment variable
  `CC_DIRECTOR_ROOT`, with a `config.json` that has NO `gateway.url` and NO onboarding-complete
  marker, so the wizard fires on first launch.
- Launched the test Director through the `cc-director-onboard6` Windows scheduled task (per the
  project rule about launching outside the Claude ConPty).
- Drove the wizard controls via Windows UI Automation and captured the window.

The user's workstation locked partway through the capture session, so the live screenshots that
could be taken before the lock are included here, and the remaining steps are evidenced by the
Director log (`director-log-excerpt.log`) plus the unit tests. No screenshots were fabricated.

## Gateway-step parity patch (auto-detect, brings the wizard to parity with Settings)

The first cut of Step 1 was a bare manual URL text box plus a Test button - it did NOT scan or detect,
even though the Settings gateway tab does. This patch brings the wizard's gateway step to parity with
the Settings gateway tab:

- It now AUTO-SCANS for a gateway when the step first appears (via the window `Loaded` event, off the
  UI thread, with the spinner and the status line "Scanning the tailnet and this machine for a gateway
  ..." - exactly the Settings copy).
- It adds a "Detect" button next to Test that re-runs the same scan on demand.
- Both the auto-scan and the Detect button reuse the SAME shared detector the Settings tab uses
  (`SettingsDetectionService.DetectGatewayAsync`, the Tailscale-first scan then loopback). No new
  scanner was written.
- On a hit it pre-fills the URL field and shows the green success line "Found gateway at <url>. Click
  Next to connect, or edit the address above.", and marks the test passed so the user can just click
  Next. On a miss it shows "No gateway found on this network (<n> address(es) scanned)..." and leaves
  the field for manual entry; the Test button still validates a typed URL.
- The misleading "Leave it blank to run local-only - you can set it later in Settings" copy was removed.
  Version 1 expects a gateway (aligns with #442): the step copy now reads "Enter or detect the address
  of your CC Director Gateway so this Director shows up there...", a blank Next nudges the user to
  detect or enter one (the "Skip for now" button still exits without bricking first-run), and the Done
  summary no longer presents local-only as a normal mode.

The detect call stays purely in the dialog code-behind, exactly like the Settings `BtnDetectGateway_Click`,
so there is no new `OnboardingModel` logic to unit-test - the existing 20 `OnboardingModelTests` stay
green and the full `CcDirector.Core.Tests` suite passes (2143 passed, 4 skipped, 0 failed).

### Auto-detect proof (live, on a tailnet with a real gateway)

Produced the same way as below (slot 6 test Director, isolated `CC_DIRECTOR_ROOT` with an empty
`config.json`, launched via a per-slot scheduled task), on a machine whose tailnet has a live gateway
answering at `http://soren-north.taildb08ed.ts.net:7878`.

- `04-step1-scanning.png` - the gateway step on entry, mid auto-scan: empty URL field, Detect and Test
  buttons disabled, the indeterminate spinner running, and the green status "Scanning the tailnet and
  this machine for a gateway ...".
- `03-step1-autodetect-found.png` - the same step after the auto-scan finished: the URL field is
  PRE-FILLED with `http://soren-north.taildb08ed.ts.net:7878` and the green success line "Found gateway
  at http://soren-north.taildb08ed.ts.net:7878. Click Next to connect, or edit the address above." -
  the user can just click Next.
- `autodetect-log-excerpt.log` - the matching Director log, showing the wizard firing on first launch
  and the auto-scan running on entry:
  `[OnboardingWizardDialog] StartGatewayAutoScan` ->
  `[OnboardingWizardDialog] DetectGatewayAsync` ->
  `[SettingsDetectionService] DetectGateway: scanned=3, found=http://soren-north.taildb08ed.ts.net:7878` ->
  `[OnboardingWizardDialog] DetectGatewayAsync: found http://soren-north.taildb08ed.ts.net:7878`.

These screenshots were captured live from the running app via Windows UI Automation; none were fabricated.

## Screenshots

- `01-step1-gateway-empty.png` - Step 1 of 3 ("Connect to a Gateway") on first launch. The wizard
  appeared automatically over the main window with an empty Gateway URL field (watermark
  `e.g. http://gateway-host:7878`), a Test button, and Skip / Next navigation. Proves acceptance
  criterion 1 (wizard appears on first launch) and the lean-v1 scope (no OpenAI key / voice steps).

- `02-step1-test-fail.png` - The Test button driven against an unreachable URL
  (`http://no-such-gateway.invalid:7878`). The wizard shows the actionable red error
  "Cannot reach http://no-such-gateway.invalid:7878: No such host is known." This reuses the
  existing `SettingsDetectionService.TestGatewayAsync` probe and proves acceptance criterion 2
  (clear pass/fail with actionable error; an unreachable URL is reported, not silently accepted).

## Log evidence (`director-log-excerpt.log`)

- First-launch trigger fires:
  `[OnboardingModel] ShouldShowOnboarding: hasGatewayUrl=False, completed=False, result=True`
  followed by `[MainWindow] OpenOnboardingWizardAsync` and `[OnboardingWizardDialog] ShowStep: step=0`.

- Gateway test PASS against the real tailnet gateway:
  `[OnboardingWizardDialog] BtnTestGateway_Click` then
  `[SettingsDetectionService] TestGateway: http://soren-north.taildb08ed.ts.net:7878/healthz`
  (that gateway's `/healthz` answers `{"status":"ok","directors":4,"sessions":7,...}`, so the
  step shows the green success line).

- An earlier run (observed live before the root was reset) showed the user driving the wizard
  end to end: `BtnTestGateway_Click` -> `BtnSkip_Click` -> `FinishAsync: wantsNewSession=False`
  -> `[OnboardingModel] MarkComplete: marker set`. After the marker is set,
  `ShouldShowOnboarding` returns false and the wizard does not auto-open again - this is the
  "does not reappear after completion" behavior (acceptance criterion 1), and is covered directly
  by the unit test `ShouldShowOnboarding_AfterMarkComplete_ReturnsFalse`.

## Unit tests

`src/CcDirector.Core.Tests/Onboarding/OnboardingModelTests.cs` - 20 tests, all green
(`dotnet test`), covering:

- The trigger condition: shown when no marker and no `gateway.url`; hidden once the marker is set;
  hidden when a non-blank `gateway.url` exists; still shown when `gateway.url` is blank (local-only).
- Gateway URL validation: well-formed http/https URLs accepted (and trimmed); blank, scheme-less,
  non-http, and malformed URLs rejected with a message.
- Persistence: a valid URL writes `gateway.url`; an invalid one throws and persists nothing;
  `MarkComplete` merges the marker without dropping the gateway section (no data loss).
- Agent availability: the check always returns a coherent verdict with a non-empty message
  (never dead-ends) and a resolved path exactly when available, reusing the #448 PATH resolution.

## Acceptance-criteria mapping

1. Appears on first launch, not again after completion - `01`, log trigger line, unit tests.
2. Working Test with actionable pass/fail; valid URL saves, invalid reported - `02`, log PASS line,
   `PersistGatewayUrl_*` and `ValidateGatewayUrl_*` tests.
3. Agent step reports availability with install guidance, never dead-ends - `OnboardingModel.CheckClaudeAvailable`
   + the agent-step UI (badge AVAILABLE / NOT FOUND + "Open install guide" link), unit tests.
4. Final step confirms readiness and routes to a first session - the Done step's
   "Create first session" button sets `WantsNewSession`, and `MainWindow` opens the New Session dialog.
5. Re-runnable from Settings - the "Re-run setup wizard..." button on the Settings Gateway tab
   (`BtnRerunOnboarding_Click`).
6. No OpenAI key / transcription / TTS / voice step - the wizard has exactly three steps
   (Gateway, Agent, Done); see `OnboardingWizardDialog.axaml`.
7. Immediate UI feedback, async test with spinner, clean build - the Test and agent checks run on
   background threads with an indeterminate `ProgressBar`; build is 0 warnings / 0 errors.
