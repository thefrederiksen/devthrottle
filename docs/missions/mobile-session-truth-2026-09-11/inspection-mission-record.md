# Independent inspection: mission record

Date: 2026-09-12

Inspector: fresh seat, seated for this inspection only. Inspected commit
`529567a9bb19273542383f5d3914b265f59a96b0` against base `53b8abfc3167125b6d2b7ab5ef33a1e7014ced53`
(origin/main at the time of inspection). The record's own QA report, handoff, and slice reports were
treated as self-testimony and were not the source of any verdict below; every checkable claim was
re-derived from git history, the GitHub interface, the workflow logs, the deployment artifacts, and
re-run focused tests.

## Verdict

**PASS**

## What was checked, and against what evidence

### Scope of the record delta

- `git diff --stat 53b8abfc..529567a9b` touches exactly thirteen files, all under
  `docs/missions/mobile-session-truth-2026-09-11/`. No product code, no file outside the mission
  record directory. The candidate commit is the pushed head of
  `origin/fix/wingman-terminal-narration-1991`.

### Commits and pull requests

- `e51b9205172f876defdcd4910d20ceac14e0f8e5` exists and contains exactly the eight files
  `roster-card-report.md` names, with no ninth file.
- `7499d8d76ec6624a477c6804dcdc7abbbe4fd68d` exists and contains exactly the four test/proof files
  the report names; all four are test files, so "no product behaviour change" holds.
- `6f47321fb`, `ccaf8c4f3` (only `VoiceSweepBudgetTests.cs`), and `3392a6f3b` (only `GatewayHost.cs`
  and `WingmanVoiceService.cs`) exist as described in `narration-repair-report.md`. `ccaf8c4f3` is
  the direct child of `6f47321fb` on the branch, matching the guard-on-top-of-production claim.
- All of `3392a6f3b`, `e51b92051`, and `7499d8d76` are contained in `origin/main`.
- Pull request `#2813` is MERGED with merge commit `3e04cc8d68a3a2aa330c1cdae55a73169fe2ab74`; pull
  request `#2814` is MERGED with merge commit `53b8abfc3167125b6d2b7ab5ef33a1e7014ced53`. Both match
  the handoff.

### Continuous-integration runs (all four re-read from GitHub, not from the record)

- Run `34664743385` (CI, head `ccaf8c4f3`): conclusion failure. The log shows the Gateway suite at
  2,459 total, 2,411 passed, 1 failed, 47 skipped, and the single failure is
  `VoiceSweepBudgetTests.Cached_audio_cannot_hide_a_terminal_failure_after_a_later_user_message` at
  `VoiceSweepBudgetTests.cs:211` — exactly what the mutation-proof section claims, including the
  assertion location.
- Run `34667658171` (CI, head `3392a6f3b`): conclusion success. The log shows Gateway 2,459/2,412
  passed/47 skipped, Gateway unit 4,251/4,243/8, Core 4,413/4,405/8, and installer 25 of 25 and 541
  of 541 — every inventory the repair report lists.
- Run `34672537780` (CI, head `7499d8d76`): attempt 1 failed with the one Launcher fact
  `RestartAsync_OnlyIfEmpty_RefusesWhenTheMachineHoldsAResolvedConflict`, the installer step skipped,
  web and Python jobs green, Gateway 2,462 total with 2,415 passed and 47 skipped, and all three
  `AgentToolDisplayRouteTests` facts passed by name. Attempt 2 (the GitHub attempt counter confirms
  the run is attempt 2) passed: the exact Launcher fact passed twice, the three route facts passed
  by name, and the installer projects reported 25 of 25 and 541 of 541. This matches the handoff and
  slice report line for line.
- Run `34679499149` (Deploy hosted Gateway, head `53b8abfc3`): conclusion success. Its
  `deploy-metrics` artifact, downloaded and read directly, reports: time-to-healthy 35 seconds, total
  unavailable 8.9 seconds, external outage 4.4 seconds, swap gap 0.0 seconds, rollback `no`, serving
  commit `53b8abf`, migration `unknown`. Every number the record quotes is the artifact's own value,
  and the record honestly calls the migration comparison unknown.

### Independent production check

- An unauthenticated request this Inspector made to production `/healthz` returned HTTP 200 with
  `commit: 53b8abf`. Production is serving the exact merge commit the record claims was deployed.
  This corroborates the deployment claim from outside the record.

### The deployed code says what the record says it says

Read from `origin/main`, not from the worktree or the record:

- `AgentToolDisplayFold.For` reads only the agent token, never `CurrentModel`, maps the named
  spellings, and returns the explicit "Agent tool not reported" for an absent token.
- `GatewayEndpoints.cs` stamps `AgentToolDisplay` from that fold during roster assembly.
- `Home.tsx` derives the chip from `session.agentToolDisplay` alone with the loud missing-value
  fallback, and renders it inside `li.row` as `row-chip row-chip-agent`. The production enumeration
  method the record claims (derive `li.row`, then require `.row-chip-agent` within each) is therefore
  a real derivation over real classes, not a count of already-stamped elements.
- `WingmanVoiceService.PrepareSweepGenerationAsync` and the `NeedsLiveScreen` selection exist on
  `origin/main` as the repair reports describe, and the production-path regression test is present
  in `VoiceSweepBudgetTests.cs`.

### Focused suites re-run by this Inspector

- `npm test --workspace @devthrottle/mobile -- src/pages/HomeRosterCard.test.tsx`: 5 passed.
- `npm test --workspace @devthrottle/client-core -- src/fleet/rosterRetention.test.ts`: 24 passed.
- `dotnet test src/CcDirector.Gateway.UnitTests ... --filter AgentToolDisplayFoldTests`: 16 passed.

All three match the record's "Final verification" table exactly.

### The specific adversarial questions

- **Does the record keep the Mission active and the live Wingman narration unproven?** Yes. The
  brief, the QA report, the handoff, and `production-live-proof.md` all say so, in the same words in
  every file, and none marks the mission complete.
- **Does any sentence treat the cached clip, a stored conversation, or the dead route as proof?**
  No. Every file that mentions the cached narration says the opposite: `production-live-proof.md`
  states that neither replaying the old clip nor regenerating from the stored conversation exercises
  the changed source-selection path; the QA report says it "cannot be counted as a pass"; the handoff
  says treating it as a pass "would cover the wrong source-selection path". No file anywhere leans on
  that evidence for the changed behaviour.
- **Does the production roster evidence derive all cards and require one chip per card?** Yes, as
  written: the enumeration starts from the card population (`li.row`) and asserts the chip within
  each, and both `production-live-proof.md` and `roster-card-report.md` state that method and that it
  "did not count only already-stamped elements". The classes involved exist in the deployed commit.
- **Deployment, continuous-integration, pull-request, commit, and screenshot claims consistent?**
  Yes — cross-checked in both directions as listed above; no number or hash disagrees with any other.
- **Secrets?** None. The only long alphanumeric strings in the record are commit hashes and
  GitHub URLs. No credential, device key, or token appears; the record says so explicitly and the
  grep agrees. The owner's own email address in his own repository is not a secret.

## What this Inspector could not independently reach

- **The `testing pi` closure facts** (closed at `2026-09-12T00:45:31.690641Z`, agent kind `Pi`,
  model `gpt-5.6-terra`). These come from the production authenticated history endpoint, which needs
  the owner's enrolled device; this Inspector has no production credential and did not use one. The
  closure is consistent with everything else (it precedes the first hosted run on the branch, the
  merges, and the deployment), but the timestamp itself is the mission's testimony.
- **The 23-cards / 23-chips live enumeration and the observed `Claude Code`, `Pi`, `Codex` values.**
  Same reason: the production roster requires the enrolled device. The method is verified as sound
  against the deployed code, and production is verified as serving the right commit, but the count
  itself is the mission's testimony.
- **Screenshot pixel content.** This seat cannot read images. Verified: all three are genuine
  PNG files, committed in the candidate commit, at exactly 390 by 844 pixels as claimed. What they
  depict is the mission's testimony.
- **The local mutation runs** (the route-stamp bypass and the model-substitution mutations) and the
  local narration gate runs. These were local foreground runs; this Inspector verified the named
  tests exist and pass now, and the hosted runs corroborate the same facts, but did not re-apply the
  mutations.
- **Agent-family independence of the recorded inspections.** The four inspection files read as
  genuine, adversarial, and specific (the two FAIL reports contain real, checkable findings that the
  repairs then closed), but which agent family wrote them cannot be verified from the files.

## Observations, not findings

- A cancelled CI run (`34671746671`, head `e51b92051`) exists and is not mentioned in the record.
  It was superseded by the inspection-failing repair cycle the record does describe, so its absence
  is not a misrepresentation.
- The QA report's "closed while the build, inspection, continuous integration, and deployment were
  running" is loose: the first hosted run on the branch began at 00:59, after the recorded 00:45:31
  closure. The exact timestamp is stated wherever it matters and the operative fact (closed before
  the final live check could run) holds either way.

**PASS — every reachable claim in the record matches independent evidence; the unreachable claims
are named as such in the record itself rather than overstated.**
