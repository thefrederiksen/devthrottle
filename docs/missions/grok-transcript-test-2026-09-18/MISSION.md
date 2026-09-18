# Mission - A test that does not depend on what this machine has ever run

Mission document. Written 18 September 2026 by the Delivery Lead of "Implement the DevThrottle
Method v1", which designs this mission and hands it over. This is phase 5 of that mission: one piece
of real work, run end to end under the method, to find out whether the method can be used at all.

**Conduct:** `cc-devthrottle skill get devthrottle-method`. That skill is the rules. This document is
this job. Where they disagree, this document wins - and it overrides nothing, so nothing here
replaces a rule.

## 1. The mission

Make `Turns_SupportedAgentWithNoTranscriptYet_ReportsNoTranscript_NotOk` prove what it says it
proves, on any machine, whatever has been run on it before.

## 2. The why

The test builds a session on `Path.GetTempPath()` and states in its own words that "no locator can
resolve a transcript for it". On SOREN_NORTH that premise is false: somebody once ran Grok from the
temporary directory, so `%USERPROFILE%\.grok\sessions\C%3A%5CUsers%5Csoren%5CAppData%5CLocal%5CTemp`
exists, the locator resolves a real transcript, and the verb correctly answers `ok`. The product is
right. The test's assumption about the machine is wrong.

Two things follow, and the second is the one that matters.

- **It fails on clean `main` on this machine.** It is one of the ten failures in the committed
  baseline every mission here now measures against, so every run has to carry an explanation for it.
- **A test whose premise depends on machine history can pass for the wrong reason.** On a machine
  that has never run Grok from the temporary directory it goes green - not because the product
  reports an unresolved transcript correctly, but because the directory happens to be empty. What it
  guards is real: issue 2561 records a Pi session that sat silent for 48 minutes because this verb
  reported an unresolved transcript as a successful read of an empty conversation, and voice
  narration recorded "nothing to narrate", which is never retried. A guard that can go green by
  accident is not guarding that.

## 3. The goal

The Grok case of that test passes on SOREN_NORTH on a clean checkout, for the right reason, and the
Gateway suite's failure count on this machine drops from ten to nine with nothing else moving.

Proven by a QA report committed beside the code that shows the test **failing before the change and
passing after it on the same machine** - not one green run.

## 4. Decisions

Carried from the owner's standing rules and from issue 3029, so this mission does not re-ask them.

| Decision | Where it comes from |
| --- | --- |
| Do not weaken the assertion, and do not skip the Grok case. | Issue 3029, in the issue's own words. It names issue 2561 as what the assertion guards. |
| No fallback programming - fix the root cause, never add something that hides it. | Law 1 of the method, and the repository's own rules. |
| Done means proof committed beside the code. | Law 14. |
| A Reviewer runs a different agent from whoever wrote the work. | Law 9 and law 12. |

## 5. Design

The fix is to give the test a working directory that **no locator can ever resolve**, instead of one
that merely usually is not resolvable: a fresh directory created by the test under its own temporary
path, unique per run, removed afterwards - not `Path.GetTempPath()` itself.

In scope:

- `src/CcDirector.Gateway.Tests/TurnsVerbUnresolvedTranscriptTests.cs` (the file that holds the
  test - confirm the path before trusting this line).
- Whatever that test shares its fixture with, if the same assumption is made there.

Out of scope, and deliberately:

- The `HostedSchemaRefusesAnUnownedRowTests` failures - eight of the baseline's ten. They are a
  PostgreSQL connection failure in a fixture's `Reset()`, a different mechanism, and fixing them is
  not this mission.
- `GatewaySessionConcurrencyStoreTests.OldHourlyBuckets_ArePruned_ButAllTimePeakRemains`, which
  issue 3029 mentions separately as an intermittent pooled-connection collision. If it turns up,
  file it; do not fix it here.
- Any change to the product. The product is right. If the work starts changing product code, stop
  and say so - that means the diagnosis in issue 3029 is wrong and this mission's premise with it.

## 6. Phases

One phase. It is a single test file and a well-understood cause, so it goes straight to a Developer
with no Tech Lead - which is the method's own rule for work with no real back and forth in it.

## 7. The check

Run by the Delivery Lead itself before it accepts anything, not read off a report.

**Before the change**, on a clean checkout of `main`, in the mission's own worktree:

```
dotnet test src/CcDirector.Gateway.UnitTests --filter FullyQualifiedName~TurnsVerbUnresolvedTranscriptTests
```

Expected: the Grok case FAILS with `Expected: "no_transcript"  Actual: "ok"`. If it passes here, the
machine no longer has the directory that causes it, and this mission must stop and say so rather
than "fix" something it cannot see fail.

**After the change**, same command: every case passes.

**Then the whole suite**, because a test fix that breaks a neighbour is not a fix:

```
dotnet test src/CcDirector.Gateway.UnitTests
```

The bar on this machine is **no failure outside the named baseline**, never "the suite is green".
The baseline is ten failures on clean `main` at `c135d44b2`, named test by test in
`docs/missions/method-v1-workflow-four-seats/BASELINE.md` on the branch `method/workflow-four-seats`
(and on `main` once pull request 3092 merges). After this change the expected result is **nine**
failures, all on that list, with the Grok one gone.

## 8. Merge plan

One pull request, merged when the check passes and the review has landed. It is one test file; there
is nothing here that needs a branch to live more than a day.

## 9. Where it ends

**Merged** to `main` in `thefrederiksen/devthrottle`, closing issue 3029, with the QA report
committed beside the code in `docs/missions/grok-transcript-test-2026-09-18/`.

Not in production. This mission deploys nothing.

## 10. Questions

None open. Everything this mission needs is decided above or in issue 3029.

If something genuinely undecidable turns up - in particular if the test passes before the change, or
if the fix cannot be made without touching product code - that is a real question and it goes to the
seat that opened you, with a recommendation. Do not guess, and do not stop on everything else while
you wait.

## Notes for the seats, from the mission that designed this one

**Two agent families are unavailable today, measured rather than assumed.** On 18 September, Codex
answered "You've hit your usage limit ... try again at Sep 22nd, 2026 4:46 AM" and Grok answered
"You hit your free usage limit", in four and two freshly opened sessions respectively. Copilot
answered normally. So the different-agent Reviewer this mission needs should be opened on **Copilot**
unless something has changed; trying Codex or Grok first will cost an hour and look like a broken
spawn rather than a usage limit, because a session that hits one disappears from
`cc-devthrottle session list` shortly afterwards.

**A fresh worktree needs its agent's trust before the agent will take a prompt in it.** Codex keeps a
per-directory trust list in `~/.codex/config.toml`; an untrusted directory swallows the opening
prompt. A worktree handed out by `cc-worktrees` is always a fresh directory.
