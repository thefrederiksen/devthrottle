# Review: refuse a seat that Claude Code would stop on "Teach auto mode about your environment?"

Pull request #4180, branch `auto-mode-dialog`, reviewed at commit `ce2a27585` on 17 October 2026.
Reviewer seat of the Command Line Agent Handling mission, on a reviewer seat of a different
agent family from the writer. Read only: nothing was built or fixed here.

## Verdict

**Two findings, one of them in the rule itself.** The detector matches Claude Code 2.1.302 on every
condition but one: it treats Claude Code's own server setting as "offered unless recorded as off",
where Claude Code treats it as "offered only when recorded as on". On a machine that holds no such
record, every auto mode seat would be refused and the owner would be sent to answer a question that
never appears. The fix is one line and one test. The second finding is in the proof: the delivery
scenario cannot tell a fixed product from a machine that never asks, and the evidence for the fix was
recorded on a tree with uncommitted changes. Everything else I checked holds.

## Scope: what I read, what I ran, what I could not reach

**Read.**

- The whole diff of the pull request against `origin/main` (18 files): the detector
  `ClaudeAutoModeSetupOffer`, the shared hook `ClaudeStartQuestions`, the file reader
  `ClaudeSettingsFiles`, the session manager and Director wiring, the unit tests, the delivery rig's
  two new scenarios, its scratch configuration folder, its pass rule, and the evidence folder.
- Claude Code 2.1.302's own code, taken from the installed binary on devlinux
  (`~/.local/share/claude/versions/2.1.302`, the version the evidence names). I located the decision
  function behind the question and every helper it calls, and compared each condition with the
  detector. The functions are minified; I name them by what they do.
- My own Claude Code records on devlinux, read only, as a sanity check of what the files look like.

**Ran.** In a worktree cut from the branch, one suite at a time:

| Suite | Result |
|---|---|
| `Core.UnitTests`, filtered to the auto mode offer, the start-up questions and the bypass consent | 27 passed, 0 failed |
| `Core.UnitTests`, whole suite | 2298 passed, 5 failed, 23 skipped |
| `DeliveryQualification.Tests`, whole suite | 22 passed, 0 failed |

The five failures are Windows-path tests (`CcWorktreesPoolSlotsTests` x3, `RepositorySourceIndexTests`,
`WorktreePoolSettingsTests`) that assert on `D:\Repos\...` paths. They fail on Linux regardless of this
change, as the pull request says.

**Not reached.** I ran no real Claude Code session, as instructed. I did not run the delivery rig; I
judged its scenarios from their code and from the ledger rows committed as evidence. I could not
determine from the binary when Claude Code marks a session's auto mode as "from a fallback" (its
`fromFallback` flag); that branch of its rule is not relevant to a seat started with an explicit
`--permission-mode auto`, which is what the Director's preset passes, so I set it aside (see
observations).

## What Claude Code 2.1.302 actually does

For the reader who has to maintain the detector. The question is offered when all of these hold, and
Claude Code evaluates them **once, when its screen starts** (it stores the answer as "eligible"), then
again at the end of every turn; a session that was not eligible at start is never asked later in that
session:

1. The session is not a remote one, auto mode is available (true by default), and a classifier model
   is configured (always).
2. **Claude Code's server setting `tengu_auto_mode_config.envOnboarding` is exactly `true`.** When the
   server has sent nothing, Claude Code falls back to a built-in configuration that has no
   `envOnboarding` key at all, so the question is not offered. The server value is never fetched at
   all when telemetry is off (`CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC`, `DISABLE_TELEMETRY`,
   `DO_NOT_TRACK`, `DISABLE_GROWTHBOOK`) or when Claude Code talks to a third-party endpoint or a
   custom sign-in.
3. `skillOverrides["auto-mode-setup"]` is not `"off"` in the merged settings.
4. `autoMode.environment` is empty in the user, `--settings` and managed settings (project and local
   settings are ignored for this, with a warning).
5. `numStartups` is at least 5, counted after this start was added (so the value on disk before the
   start must be at least 4).
6. `autoModeEnvSetup.denials` is at least 5.
7. For a session started with `--permission-mode auto`: the auto mode entry warning has been seen
   (`hasSeenAutoModeEntryWarning`), or a settings file sets `skipAutoPermissionPrompt`. The warning is
   a notice appended to the transcript 800 milliseconds after the screen starts, not a dialog, and it
   sets the flag in the same session; but eligibility was already decided at start, so a session that
   shows the warning is not asked.
8. `autoModeEnvSetup.dismissed` is not true ("Don't show again").
9. `autoModeEnvSetup.dismissedAt` is absent or more than 604,800,000 milliseconds (exactly 7 days) ago
   ("Not now").

The session's permission mode must be auto at the time of the check, and it must not be a background
session, a remote workspace, or a bridge reattach.

The detector matches items 3 to 9 exactly, including the +1 on `numStartups`, the 7-day window, the
settings sources for the environment, and the entry warning. Item 2 is the finding below.

## Finding 1: the server setting is tested the wrong way round (rule mismatch, needless refusal)

**Where.** `ClaudeAutoModeSetupOffer.WhatSettlesIt`:

```csharp
if (config["cachedGrowthBookFeatures"]?["tengu_auto_mode_config"]?["envOnboarding"] is JsonValue offer
    && offer.TryGetValue<bool>(out var offered) && !offered)
    return "Claude Code's own setting does not offer it";
```

**What Claude Code does.** `envOnboarding === true` is required for the question to exist; absence
means not offered.

**What the detector does.** Only an explicit `false` settles it. A cached `tengu_auto_mode_config`
without the key, a `cachedGrowthBookFeatures` without that object, or a configuration file with no
cache at all, is treated as "will be asked".

**The harm.** On any machine where the other conditions hold (4 or more starts on disk, 5 denials,
the warning seen, no environment described, nothing dismissed) and the cache lacks the key, every
auto mode seat opened by a session or a factory is refused, forever, with a sentence telling the owner
to open Claude Code and answer a question. The owner opens it, sends a message, and no question
appears, because Claude Code does not offer it there. Nothing the sentence names ends the refusal
except giving up auto mode. Two ways to get there:

- A machine where Claude Code runs without its feature service: telemetry turned off, or a
  third-party endpoint (Bedrock, Vertex, Foundry) or custom sign-in. Auto mode's denial counter still
  counts there; the cache never fills.
- Every machine in the fleet on the day the server stops sending `envOnboarding` (a rollout flag
  ends). The cache is refreshed without the key, the question stops being offered, and the detector
  starts refusing.

The existing control scenario cannot catch this: it settles the question with "Don't show again" and
proves nothing about the server setting.

**Evidence from the binary.** The gate reads `m2e() && S_()?.envOnboarding === !0`; `S_()` returns the
fetched `tengu_auto_mode_config` when there is one, otherwise the built-in default (which has
`twoStageClassifier`, `severityByModel` and so on, and no `envOnboarding`), and the fetch is skipped
entirely when telemetry is off.

**What would close it.** Settle unless the cached value is exactly `true`, with the log line saying
which it was ("the server setting is absent" versus "off"), and a unit test
`WhyItWouldStop_TheServerSettingAbsent_GoesAhead` beside the existing
`WhyItWouldStop_TheServerDoesNotOfferIt_GoesAhead`. Whether to make that change is the Developer's call.

## Finding 2: the delivery scenario can pass without the question ever being offered (proof)

**Where.** `PassRules.AutoModeQuestion` and the `auto-mode-question` scenario.

**What it does.** For the unsettled scenario, a start that is not refused passes whenever the next
prompt arrives. `questionShown` (whether the question was on screen before the send) is recorded in
the detail text but plays no part in the verdict.

**The harm.** On a machine where Claude Code does not offer the question (the same machines as
Finding 1, or a Claude Code version with a different rule), the scenario passes on main with
`ARRIVED`, so it cannot distinguish "the product is fixed" from "this machine never asks". The pull
request's claim that the scenario fails on main is true of devlinux on 17 October 2026 (the ledger
rows show the menu on screen and the prompt lost, twice) and not of the scenario in general. The
mission's own check, run on another machine, would then certify a refusal path it never exercised.

**Also.** The eight rows for the fix carry the product commit `b18b5423932a+local-changes`: they were
recorded on uncommitted code, not on `ce2a27585`. The refusal sentence in those rows is
character-for-character the one in the committed detector, so I have no reason to think the rule
changed after the recording, but the ledger cannot tie them to the commit under review.

**What would close it.** For the unsettled scenario, a start that was neither refused nor showed the
question is a setup failure ("this machine does not offer the question"), not a pass; and two
attempts of each scenario recorded on the committed tree. The Developer decides.

## Checked and found sound

- **The 7-day window.** 604,800,000 milliseconds in Claude Code; `TimeSpan.FromDays(7)` in the
  detector, measured on an injected clock at the moment of the check. Claude Code measures at its own
  start, seconds later. The pull request's noted gap (a seat still running when "Not now" expires) is
  real and correctly described as not covered: eligibility is fixed at start, so such a seat is not
  asked either. No gap there.
- **The start count.** Claude Code adds this start before deciding; the detector adds one. A disk
  value of 3 lets the seat through, 4 refuses it; both match.
- **Denials during the seat's first turn.** Not a gap: eligibility is decided at start from the count
  on disk, so a seat that starts with 3 denials and collects 2 more in its first turn is not asked.
  The detector's reading of the disk value is the right one.
- **The entry warning.** The detector lets a seat through when the warning is still to be shown. That
  is correct: the warning flips its flag 800 milliseconds after the screen starts, but eligibility was
  decided before that.
- **The environment's sources.** User, managed, and the `--settings` file; project and local ignored.
  The detector reads user and managed and skips any line carrying `--settings`. The test that an
  environment only in the repository's settings still refuses is right.
- **Never answers for the owner.** The change reads files and refuses; it writes nothing to any Claude
  Code record. The rig writes `dismissed: true` only into its own scratch copy for the control
  scenario, under a `CLAUDE_CONFIG_DIR` of its own. The owner's `~/.claude.json` is not touched (the
  evidence README reports it unchanged before and after, and my own copy on devlinux still reads
  `{"denials": 5}` with the environment described in settings).
- **The Director's own `--settings`.** The Director appends a hook settings file to the effective
  line; the detector's "line brings its own `--settings`" exemption looks only at the user's own
  arguments, so the Director's file does not switch the check off.
- **No fallback programming.** An unreadable or unparsable file is treated as holding no record and
  the start is refused in words (the comment says so), never started blind. The bypass consent
  check's behaviour is unchanged by the move of its helpers into `ClaudeSettingsFiles`; its tests pass.
- **Logging.** The settled and exempted branches log their reason; the refusal is logged once by the
  session manager with the full sentence.

## Observations (no change asked; the Developer may ignore these)

- **"Never been answered" is not always true.** After "Not now" expired (more than 7 days ago), the
  refusal still says the question "has never been answered on {machine}". The owner did answer it.
  The same sentence is given when the configuration file cannot be read; the real reason is only in
  the log.
- **"Yes" is not described.** The sentence explains "Don't show again" and "Not now" but not "Yes",
  which runs a setup that reads the owner's shell history and other repositories, and which settles
  the question for good by describing the environment. The owner should know that before pressing it.
- **A missing configuration file refuses.** `WhatSettlesIt` returns "not settled" when the file is
  absent, while Claude Code with no file has a start count of 1 and would not ask. Such a seat would
  be stuck on first-run onboarding anyway, so the harm is only the wrong sentence.
- **`skillOverrides` is read from any source, not the merged value.** Claude Code reads the merged
  settings, where a higher-precedence file could set the key to another value. Theoretical.
- **The rule is pinned to one version with no guard.** Claude Code auto-updates. When its thresholds
  or keys change, the detector drifts silently in either direction; only the delivery scenario would
  notice, and only on a machine that offers the question (Finding 2).
- **The fallback-auto branch is not modelled.** When Claude Code enters auto mode by fallback rather
  than an explicit flag, the warning condition is replaced by the "auto mode by default" notice. The
  Director's preset passes the flag, so this does not affect seats today.
- **The rig copies the owner's sign-in file** into its scratch configuration folder (mode 600, deleted
  on dispose, refused when the token has under 30 minutes left). The folder is a sibling of the
  scratch repositories, not inside one, so it cannot be committed. A process kill before dispose would
  leave the copy on disk.
- **Entry logging.** `WhyItWouldStop` writes no entry line and no line for "not auto mode"; the
  coding standard asks for one. Style only.

## The Developer's answers (commit `cd0a3d38d`)

**Finding 1: fixed.** I confirmed it in the binary. Claude Code's built-in `tengu_auto_mode_config` (used when nothing
was fetched) holds no `envOnboarding` key, and the gate is `envOnboarding === !0`. The question is now settled unless
the cached value is exactly true, and the log line says which case it was:
- the key absent: "the server setting is absent, and it offers the question only when that is true";
- the key off: "does not offer it".

New tests:
- `WhyItWouldStop_TheServerSettingAbsent_GoesAheadBecauseClaudeCodeAsksOnlyWhenItIsTrue`
- `WhyItWouldStop_NothingEverFetchedFromTheServer_GoesAhead`
- `WhyItWouldStop_NoConfigurationFileAtAll_GoesAhead`

The last one takes up your observation: with no configuration file, Claude Code's count would be 1, and it does not
ask.

**Finding 2: fixed.** The unsettled scenario now proves the machine asks, at two points:
- **Before anything starts:** it needs the owner's cached `envOnboarding` to be true. If it is not, the attempt is a
  setup failure with that reason.
- **When the seat was not refused:** it waits for the question on screen. If the question never appears, the attempt
  is again a setup failure, not a pass, because it says nothing about the fix.

The fix rows were recorded again on the committed tree, product `cd0a3d38d132`: `auto-mode-question` 2 of 2 refused
in words, `auto-mode-question-settled` 2 of 2. The two main rows meet the new test: the question is on the screen
recorded in each.

**Observations taken up:**
- The refusal says "not settled", not "never been answered".
- It now says what "Yes" does: it runs a setup that reads this machine's shell history and other repositories.
- The check writes a log line when it starts and when the session is not in auto mode.

**Observations left:**
- reading `skillOverrides` from merged settings (theoretical);
- the fallback-auto branch (the preset passes the flag);
- a version guard. The scenario is the guard: it now fails loudly, as a setup failure, when Claude Code stops asking.

## Round 2

Reviewed at commit `9cf67126b` (the fix is `cd0a3d38d`; the answers and the re-recorded evidence are `9cf67126b`) on
17 October 2026, on a Reviewer seat of a different agent family from the writer. Read only: nothing built, nothing fixed.

**Verdict: both findings are fixed. No new finding.** The "Not now" window is right as well. Three observations
follow, none of which asks for a change before merge.

### Scope: what I read, what I ran, what I could not reach

**Read.**

- The diff of `cd0a3d38d` in full: `ClaudeAutoModeSetupOffer`, its tests, the rig's scratch configuration
  (`ScratchClaudeConfig.ForAutoModeQuestion`) and the `auto-mode-question` scenario.
- The evidence folder at `9cf67126b`: the README, every row of `ledger-rows.jsonl` (16 rows), the three rates files, and
  how the rig stamps a row's product commit (`StampBuildCommit` in the rig's project file).
- Claude Code 2.1.302's own decision again, in the installed binary on devlinux, to check the fix against the rule rather
  than against my round 1 reading of it. The gate is `envOnboarding===!0` on the fetched-or-built-in configuration, and
  the eligibility function returns false when `dismissed` is set, or when `dismissedAt` is set and
  `Date.now()-dismissedAt<604800000`. Both are quoted from the binary.

**Ran.** In a detached worktree at `9cf67126b`, one suite at a time:

| Suite | Result |
|---|---|
| `Core.UnitTests`, filtered to the auto mode offer, the start-up questions and the bypass consent | 30 passed, 0 failed (27 in round 1, plus the three new) |
| `DeliveryQualification.Tests`, whole suite | 22 passed, 0 failed |

**Not reached.** I ran no real Claude Code session and did not run the delivery rig, as in round 1. The rig's product
stamp marks a row `+local-changes` only when `CcDirector.Core` or `CcDirector.ControlApi` is dirty at build time, not
when the rig's own project is; so the rows prove the detector they exercised is the committed one, and say nothing
either way about the scenario code that drove them. The refusal sentence in the rows is the committed detector's,
character for character, and the settled rows carry the new scenario's detail text, so I have no reason to doubt them.

### Finding 1: fixed

`WhatSettlesIt` now switches on the cached value. A null (the key absent, `tengu_auto_mode_config` absent,
`cachedGrowthBookFeatures` absent) settles it with "the server setting is absent"; a value that is not exactly `true`
settles it with "does not offer it"; only `true` falls through to the owner's records. A missing configuration file
settles it before that, with its own reason. This is the binary's rule: `S_()` returns the built-in configuration when
nothing was fetched, and the built-in has no `envOnboarding` key.

The tests the answer names exist and pass: the key absent among other keys, the whole cache absent, and no file at all.
The three cases of round 1's harm (telemetry off, a third-party endpoint, a rollout flag withdrawn) all land in one of
them.

### Finding 2: fixed

Two gates, both of which turn the attempt into a setup failure (before the first send, so it is left out of every rate
and reported as `SETUP-FAILED`, never as a pass):

- `ForAutoModeQuestion` reads the owner's own configuration, before `CLAUDE_CONFIG_DIR` is pointed at the scratch
  copy, and throws unless the cached `envOnboarding` is exactly `true`. This runs for the control too, which is right:
  on such a machine the control would go ahead for a reason the scenario does not mean to measure.
- When the seat was not refused, the unsettled scenario waits up to 15 seconds for the question on screen after the
  first turn and throws if it never appears. The settled control keeps its 3-second look and records the answer in the
  detail text.

The evidence is re-recorded on the committed tree: rows 13 to 16 carry product `cd0a3d38d132` with no local-changes
mark, two attempts of each scenario, `auto-mode-question` refused in words twice and the control arrived twice. The two
main rows (product `c7ec59930ba4`) both record "question on screen before the send: True", so they meet the new rule and
the pull request's claim that the scenario fails on main stands for devlinux on 17 October 2026.

### "Not now" within the last seven days: settled, correctly

Claude Code: `if(C?.dismissedAt&&Date.now()-C.dismissedAt<I_t)return!1` with `I_t=604800000`. The detector:
`dismissedAt > 0 && now - dismissedAt < TimeSpan.FromDays(7)` on the injected clock. `WhyItWouldStop_NotNowThreeDaysAgo_GoesAhead`
proves the inside of the window and `WhyItWouldStop_NotNowEightDaysAgo_RefusesBecauseItIsAskedAgain` the outside; both
pass.

### Observations (no change asked)

- **The version guard is weaker than the answer says.** The answer to the "version guard" observation is that the
  scenario fails loudly when Claude Code stops asking. That holds only when the product lets the seat start, which is to
  say on main or on a machine where some record settles it. On the fixed product, with `envOnboarding` still cached
  `true` and nothing settled, the detector refuses at start and the scenario passes `REFUSED-IN-WORDS` without the
  machine ever having been shown to ask; so if a later Claude Code raises a threshold or drops the question while the
  cache keeps the key, the needless refusal is certified, not caught. The cached key is a proxy for the offer, not proof
  of it. A calibration attempt with the detector set aside would prove it, if that ever matters; today, on 2.1.302, the
  main rows are that proof.
- **A non-boolean `envOnboarding` falls through.** The switch handles null and a `JsonValue`. An object or an array in
  that position matches neither case and is treated as "will be asked", where Claude Code's `===!0` says not offered.
  The server has no reason to send one; theoretical.
- **The control is now unrunnable on a machine that is not offered the question.** Deliberate and, I think, right (see
  Finding 2), but the rates file for such a machine will show both scenarios as setup failures, and a reader may take the
  control's failure for a rig fault. The thrown message names the cause, which is enough.
