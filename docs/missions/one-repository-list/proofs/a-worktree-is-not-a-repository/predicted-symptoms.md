# What I predict each attack will do, written and committed BEFORE any of them runs

A proof nobody has watched fail is decoration. And the lesson this mission has already paid for twice:
**a revert proves the guard catches THAT defect, not the class its name claims** - three seats validated
one guard by reinstating the exact line they had deleted, all honestly, and all missed the same defect in
the other direction. So half of what is below is not a revert at all: it is a **wrong rule substituted
for a rule nobody removed**, which is the shape a defect actually arrives in.

For each one: what I change, which test I expect to catch it, the symptom I expect to read, and - the
part that is easy to leave out and is the most informative - **how many tests I expect to stay GREEN
while the defect is present.** A guard that only fires when everything else does is not telling anyone
anything.

The counts I am predicting against, measured on this branch before any attack:

| Suite / class | Tests |
|---|---|
| `LinkedWorktreeTests` (Core) | 29 |
| `AWorktreeIsNotARepositoryTests` (Core) | 12 |
| `RepositoryUsageTests` (Core, the precedence rule) | 10 |
| `AWorktreeIsNotARepositoryWireTests` (Gateway unit) | 3 |
| `KnownRepositoryObservationTests` (Gateway unit) | 9 |
| `TheCatalogueCollapsesAWorktreeTests` (Gateway unit) | 17 |
| `DiscoveredRepositoryObserverTests` (Gateway unit) | 20 |
| `AWorktreeIsNotARepositoryTunnelProofTests` (PARKED Gateway.Tests) | 6 |

---

## A - REVERT. The stamp is not taken at session creation

Comment out `StampPrimaryRepository(session)` in `SessionManager.RaiseSessionCreated`.

- **Expect to fail:** the Director-side stamp tests in `AWorktreeIsNotARepositoryTests` - the flow, the
  restore case, and the one that says the repository moves up the list rather than the worktree. Around
  four of its twelve. And the two flow tests in the tunnel proof.
- **Symptom:** `Assert.NotNull()` / `Assert.Equal()` on a `PrimaryRepoPath` that is null, and the tunnel
  proof serving the worktree's own path where it expected the repository's.
- **Expect to stay GREEN:** all 29 of `LinkedWorktreeTests`, all 10 precedence tests, all 9
  `KnownRepositoryObservationTests` and all 17 collapse tests - every one of those hands itself the
  answer. That is the point of predicting it: **the rule can be entirely unwired from the product and
  about seventy tests still pass.**

## B - REVERT. The answer is not put on the wire

Delete `PrimaryRepoPath = s.PrimaryRepoPath` from `ControlEndpoints.Map`.

- **Expect to fail:** `AWorktreeIsNotARepositoryWireTests.Map_CarriesTheResolvedRepositoryOntoTheDto`,
  and the tunnel proof's flow.
- **Symptom:** `Assert.Equal()` expecting the repository and finding null.
- **Expect to stay GREEN:** everything on the Director side, including all 12 of
  `AWorktreeIsNotARepositoryTests`, and all 26 Gateway-side catalogue tests. **This is the joint: one
  deleted line stops the feature working in the product while every test that is not about the wire
  passes**, because the Gateway is forbidden to work the answer out for itself.

## C - WRONG RULE, nothing removed. The guard becomes "git can answer" instead of ".git is a FILE"

Change `LinkedWorktree.IsOne` to accept any folder that exists, rather than one holding a `.git` FILE -
which is what "just ask git" looks like when somebody writes it.

- **Expect to fail:** `LinkedWorktreeTests.APlainFolderInsideARepository_IsLeftAlone`, and
  `AWorktreeIsNotARepositoryTests.ASessionStartedInAFolderThatIsNotARepositoryAtAll_CarriesNothing`.
- **Symptom:** a null expected and a repository path found - a session started in `repo/src` credited to
  `repo`, a repository the person never picked.
- **Expect to stay GREEN:** every other test in both classes, including every worktree case, because the
  wrong rule gets all of those right. **Two tests out of about forty stand between this rule and
  crediting people's sub-folders to repositories they did not choose.** It is the Delivery Lead's
  condition 1, and it is why that sentence is in the code beside the guard rather than only in a record.

## D - GUARD REMOVED. A submodule is treated as a worktree

Make `LinkedWorktree.RepositoryGitDirectoryOf` accept any second-to-last segment instead of git's
`worktrees` directory.

- **Expect to fail:** `LinkedWorktreeTests.ASubmodule_IsLeftAlone_BecauseASubmoduleIsARepository`, and
  `RepositoryGitDirectoryOf_AnythingElse_IsNull` for the `modules` row.
- **Symptom:** the superproject returned where null was expected.
- **Expect to stay GREEN:** everything else. **One behavioural test.** The Delivery Lead asked for it
  precisely because the protection is currently free - a submodule's git directory does not end in
  `.git` - and "free" is what stops being true when somebody refactors.

## E - WRONG RULE, nothing removed. The collapse claims everything under the repository's folder

Replace the collapse's "is this path one the Director NAMED as a worktree" test with "does this path sit
under the repository's folder" - the obvious rule, which nobody wrote.

- **Expect to fail:** `TheCatalogueCollapsesAWorktreeTests.AFolderInsideTheRepository_IsNotCollapsedIntoIt`.
- **Symptom:** a row for a person's own working sub-folder deleted, and its time folded into a
  repository.
- **Expect to stay GREEN:** the other 16 collapse tests and all 6 tunnel tests. **One unit test stands
  between the rule and the prefix rule, and the end-to-end proof cannot see the difference at all** -
  which is exactly what the catalogue-forgets work found when it attacked its own parent test.

## F - REVERT. The collapse runs after the forgetting rule instead of before it

Move the collapse block below the forgetting block.

- **Expect to fail:**
  `TheCatalogueCollapsesAWorktreeTests.AWorktreeThatIsBothGoneAndNamed_IsCollapsedRatherThanForgotten`.
- **Symptom:** the repository's last-used time stuck at its own old value, because the worktree's row -
  and the newer time on it - was deleted before anything could account for it.
- **Expect to stay GREEN:** every other test in the class, including `TheForgettingRuleStillWorksBesideIt`.
  Both rules are still "working"; the only thing lost is a last-access time, silently.

## G - GUARD REMOVED. A warm-start push's worktrees are believed

Take the worktree statements from provisional rows as well as verified ones.

- **Expect to fail:** `DiscoveredRepositoryObserverTests.ObserveSnapshot_AProvisionalPush_CollapsesNothing`.
- **Symptom:** a row deleted on the strength of a cached list rather than of what git says now.
- **Expect to stay GREEN:** the other 19 observer tests and all 17 store tests - the store is handed its
  statements either way, so it cannot tell where they came from.

## H - GUARD REMOVED. The named repository need not be in this push

Drop the `snapshot.ContainsKey(repositoryKey)` condition from the collapse.

- **Expect to fail:** `TheCatalogueCollapsesAWorktreeTests.AWorktreeOfARepositoryThisPushDoesNotReport_IsNotCollapsed`.
- **Symptom:** a worktree's row deleted with nothing in this push to fold it into, so its last-access
  time is lost outright.
- **Expect to stay GREEN:** everything else, including the whole tunnel proof.
