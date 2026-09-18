# Review - the issue 3029 test fix

Written by the Reviewer on the mission "A test that does not depend on what this machine has ever
run", 18 September 2026, in the mission worktree `wt06`, on the two commits
`f54c7234d` (the change) and `81e1d238e` (the fix report and evidence), read against the base
`3921392ab`. This review is written to the brief at `REVIEW-BRIEF.md` beside it.

## Verdict

**No findings. Nothing here must change.** Every sharp question in the brief was answered against
the product's own code, and the two claims that could not be settled by reading - that the test
passes here, and that it still fails when the product is broken - I verified with my own runs
rather than the Developer's.

## Scope

**What I read.** The full diff of `3921392ab..HEAD` (one test file touched under `src/`, nothing
else); the test file at the tip in full; `MISSION.md`, `DEVELOPER-TASK.md`, `FIX-REPORT.md` and all
six evidence logs; and, in the product source, every piece of code the test's claims rest on:
`GrokSessionLocator`, `CodexRolloutLocator`, `PiSessionLocator`, the routing in
`SessionHistoryReader.ResolveTranscriptPath`, the whole `Turns` verb in `SessionReadExecutor`,
`ClaudeSessionReader.GetProjectFolder` and `GetJsonlPath`, and `SessionManager.CreateEmbeddedSession`
with `Session.ClaudeSessionId`.

**What I ran, on SOREN_NORTH, from this worktree, each rebuilt from source:**

| Run | State | Result |
| --- | --- | --- |
| The check itself | Tip of the branch | **Failed: 0, Passed: 6** |
| My own revert proof | `no_transcript` guard temporarily stamped `ok` (the pre-issue-2561 behaviour), product uncommitted | **Failed: 3, Passed: 3** - the Pi, Codex and Grok cases each fail with `Expected: "no_transcript"  Actual: "ok"` |
| Restore | `git checkout --` the product file, tree clean | **Failed: 0, Passed: 6** |

I also confirmed by hand that the machine condition the mission is about is real
(`%USERPROFILE%\.grok\sessions\C%3A%5CUsers%5Csoren%5CAppData%5CLocal%5CTemp` exists, holding the
Grok session directory the fix report names), and that after my runs there is neither a
`ccd-turns-unresolved-*` directory nor a `*ccd-turns-unresolved*` project folder left behind in
`~/.claude/projects`.

**What I could not reach.** The whole suite - that run is reserved for the Delivery Lead, so a
neighbour broken by this change is outside my evidence. Linux and macOS. The nine-failure baseline
and the drop from ten to nine - not measured by me. The Copilot and OpenCode readers, which this
test deliberately does not assert. And the Developer's runs themselves: I read their logs and the
source lines their stack traces cite match this tree, but I did not re-execute their runs three and
four; my own revert proof above stands in for them, in the shape of run four (the pre-issue-2561
break, which is the one that reproduces the reported symptom).

## The sharp questions, answered

### 1. Is the new directory genuinely unresolvable, or only unlikely to resolve?

Genuinely unresolvable, by construction, for every locator the verb can reach for the agents the
test asserts. I read each locator in the product, not the comment about it:

- **Grok** - `GrokSessionLocator.Scan` matches a directory under `~/.grok/sessions` whose
  percent-decoded name equals the repository path. Grok writes that directory only when it RUNS in
  the working directory. The fixture's directory is `Path.GetTempPath()\ccd-turns-unresolved-<new
  globally unique identifier>`, created microseconds before the assertion; no Grok process can ever
  have run there, so no matching directory can exist. This is not freshness, it is the
  impossibility of a record existing for a directory that never existed until the run made it -
  and the identifier in the name rules out even a coincidence of names. The old premise failed
  precisely because the temporary directory IS a place agents get run from; the new directory
  cannot be, structurally.
- **Codex** - `CodexRolloutLocator.Scan` matches a rollout's recorded `session_meta.cwd`. The same
  argument: a recorded working directory is a directory something ran in.
- **Pi** - `SessionHistoryReader` routes Pi to `PiSessionLocator.Resolve(session.ClaudeSessionId)`.
  I verified that `CreateEmbeddedSession` never sets `ClaudeSessionId`, so the locator receives
  null and returns null without touching disk. Machine-independent, as the comment says.
- **Gemini** resolves to null by design (it reads the terminal buffer, empty for an embedded
  session), and the store-backed agents, Copilot and OpenCode, resolve to a machine-wide database -
  both locators are deliberately not asserted as unresolved by this test, and the class comment
  says so and says why. That exclusion is honest and predates this change; bringing them in is out
  of scope here.

The locator caches are keyed by Director session identifier, which is a fresh globally unique
identifier per test session, so no cross-test or machine-history pollution is possible.

### 2. Could a constant be substituted, or the product be broken, and this test stay green?

No. The assertions pin the exact status strings - `no_transcript` for Pi, Codex and Grok,
`unsupported` for Cursor, `empty_history` for Gemini and Claude Code - against a response
deserialized from the verb's own output, so a substituted constant would have to be the product's
word. The Grok case is still present and still asserted; nothing was skipped or weakened (I read
the before and after side by side). And my own revert proof above shows the failure mode has not
moved: with the verb returned to its pre-issue-2561 behaviour, the Grok case goes red with
byte-for-byte the reported symptom, `Expected: "no_transcript"  Actual: "ok"` - which is the
defect of issue 2561, the false success that left a session silent for 48 minutes. Under the OLD
test that break could not be caught here at all on this machine, because the locator resolved a
real transcript and the Grok case failed for the machine's reason; under the fixed test it fails
for the product's reason. That is the whole mission, verified.

### 3. Does anything the change claims go beyond what the code supports?

No. I checked every claim in the class comment, the commit message and the fix report against the
product source: the description of how each locator matches, the claim that an embedded session
has no agent session identifier, the claim that the store-backed agents are excluded, and the claim
that Claude Code names its project folder after the sanitised working directory (verified in
`ClaudeSessionReader.GetProjectFolder` - and the globally unique identifier survives the
sanitisation unchanged, so the folder name is unique per run). The one claim that cannot be
settled by reading - "nothing holds it open" in the Dispose comment - is settled by the runs: eight
executions of the class across the Developer's logs and mine, all cleaning up.

### 4. Is any cleanup able to delete something it did not create?

No. The fixture deletes exactly `Path.GetTempPath()\ccd-turns-unresolved-<globally unique
identifier>` - a directory this run created and nothing else can have: the identifier is fresh per
run, and even a hypothetical collision would land on an empty directory left by a crashed earlier
run of this same test. It cannot be the shared temporary directory itself, which sits one level up
under a different name. The Claude Code test deletes a project folder named after the fixture's own
working directory - unique by the same argument, so it can only be the folder this run created.
If construction fails part way, the constructor throws, the disposal never runs, and the worst
case is a leaked empty directory in the temporary area - no deletion of anything else is reachable.
(One cosmetic observation, not a finding: in the Claude Code case, if `Directory.CreateDirectory`
itself were to throw, the delete in the `finally` would throw a not-found error that masks the
original one. The test is still red either way; no harm to the guard.)

### 5. Was any product code changed?

No. `git diff 3921392ab..HEAD -- src/` touches exactly one file,
`src/CcDirector.Gateway.UnitTests/TurnsVerbUnresolvedTranscriptTests.cs`, and the working tree at
the tip has no uncommitted product changes. The mission's premise holds.

## Residual risk, named rather than found

These are limits the change itself already names, repeated here so the verdict carries its edges:

- A future locator keyed on a machine-wide store rather than the working directory or the agent
  session identifier would not be covered by the fixture's argument - the fix report says so, and
  the class comment says so.
- The test still reads the real user home on the machine running it - the locators use the user
  profile directory directly, and the test collection does not isolate it. That is inherent to an
  integration test of real locators, the machine-history independence comes from construction
  rather than isolation, and the Claude Code case writes into the real `~/.claude/projects` (and
  removes it). Within this mission's design, that is a choice, not a defect.
- Everything here is one machine, one test class, and the six tests in it. The whole suite is the
  Delivery Lead's run, not mine.
