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
