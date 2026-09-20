# Phase 3: the decisions taken while building and proving it

Written by the Developer seated for the phase 3 revert proofs, 19 September 2026. It follows the shape
of `phase-2-decisions.md`: everything here is a judgement the mandate did not settle and that a reader
of the code would otherwise have to reconstruct. A Reviewer from a different agent family reads the
phase before it merges; these are the places to look hardest.

Sections 1 and 2 record decisions taken before this seat opened, read off the rulings and the code as
it stands rather than remembered. Sections 3 to 11 are the decisions this seat took while proving the
phase. The proof itself is `phase-3-proof.md`.

## 1. Judgement 5 was overruled, and refusal 1 reads an explicit list declared in `CcStorage`

The plan proposed to find the protected paths by reflecting over `CcStorage` and taking every method
whose name contains "vault", "credential" or "secret". The Delivery Lead overruled that in
`phase-3-rulings.md`, on evidence: the name test caught `Vault()` and `SecretsStore()` by luck of
naming and was blind to `Config()`, whose own comment says it holds OAuth tokens and credentials, and
to the account key vault file, which no method named at all.

What is built instead, as the ruling asked:

- `CcStorage.ProtectedPaths()` in `CcDirector.Core` is one explicit enumeration, declared beside the
  paths it names. It carries eight entries today: the vault, the secrets store, the config folder, the
  account key vault file, the two account credential blobs, the automation browser root and the
  browser connections folder. Each entry composes from the live method rather than restating a path,
  so the environment overrides those methods honour are honoured at the moment the list is read.
- The reclaim tool carries no copy. `Runner.Reclaim` reads the list at the moment of the run and hands
  it to the gate, which refuses the path and everything under it and quotes the resolver's own
  sentence for what the path holds.
- The staleness the mandate feared is caught by
  `src/CcDirector.Core.Tests/Storage/CcStorageProtectedPathsTests.cs`, which enumerates the public
  static path members of `CcStorage` and fails when one is neither protected nor explicitly declared
  not protected. Adding a storage path forces a decision.

This seat did not touch `CcStorage` and did not run those Core tests; the Delivery Lead's gate does.

## 2. The other seven judgements stand as the plan wrote them

Judgements 1, 2, 3, 4, 6, 7 and 8 of the plan's section 12 were accepted in the rulings and are built
as written. Two of them turned out to matter to the proof, and are picked up below: judgement 3
(refusal 9 is a property of the run, so an item in a dry run is reported ELIGIBLE and the only thing
that stops it moving is one branch of the runner - section 5) and judgement 8 (the user folders are a
parameter the caller supplies, so the gate can be perfect and the caller can still hand it nothing -
section 4).

## 3. A name-only red is recorded as name only, and only refusal 5 got a second test

Three numbered refusal tests went red only on the NAME of the refusal when their refusal was deleted:
4 (caught by 10), 5 (caught by 8) and 7 (caught by 8). The brief requires a new test only for an
unheld refusal, and a name-only red is not unheld. The decision was to ask, for each, whether the
refusal can EVER be the only thing in the way, and to write a test exactly where the answer is yes.

- **Refusal 5: yes.** A rule that goes on offering an item inside its own age gate leaves refusal 5 as
  the only defence, and nothing stops a rule being written that way - phase 5 turns rules into data.
  `Reclaim_AnItemARuleOffersInsideItsOwnAgeGate_IsRefusedByTheGateWhenNoOtherCheckWould` was added and
  goes red with the item ELIGIBLE.
- **Refusal 7: no, by construction.** The fold empties a broken rule's offer, so refusal 8 always
  stands behind it. A test that made refusal 7 the only defence would first have to break the fold.
  No test was added.
- **Refusal 4: no, on Windows.** The final path of anything reached through a link differs from its
  spelling, so refusal 10 always stands behind it here. No test was added, and the proof says plainly
  that on macOS and Linux refusal 4 would stand alone and nothing here runs there.

The order of the checks was NOT changed to make the names come out differently. Moving refusal 8
ahead of 5 and 7, or 10 ahead of 4, would change what the owner is told and is a design decision, not
a proof task.

## 4. The command line tests change environment variables, in a collection that runs alone

Mutations 12 and 13 showed that nothing watched what the reclaim command hands the gate. The brief
asked for a test that drives the command line entry point against a fixture, with the storage root
pointed at the fixture by the variable `CcStorage` already reads, never at the real vault and never
with the apply flag.

The command builds its rules from this machine's real rule set, so the only way to make it find a
fixture item is to point the places those rules look INTO the fixture. Three judgements follow.

- **The temporary folder is redirected with `TMP` and `TEMP`.** That is what makes the real scratch
  folder rule look in the fixture. No product code was changed to make the command testable: adding a
  rules parameter to the command for the sake of a test would have moved the thing under test.
- **The protected path used is the config folder, not the vault.** A session on this machine carries
  `CC_VAULT_PATH`, which points `Vault()` at the REAL vault whatever the storage root says. The test
  asks the resolver for the config folder after redirecting the root, and refuses to go on unless the
  answer is inside its own tree, so it cannot be pointed at a real protected folder by an environment
  it did not expect.
- **The tests run in a collection with parallel running switched off.** An environment variable
  belongs to the whole process, and every fixture tree in this suite is made under the temporary
  folder. A test that moved that folder while a neighbour was building its tree would send the
  neighbour's tree somewhere it did not ask for. `ChangesTheProcessEnvironment` is the one collection
  for any test that must do this, and every variable is put back when the test ends.

The user folders are reached through `OneDrive`, the one of the five the command reads from the
environment. Documents, Pictures, Videos and Desktop come from the operating system's own resolver
and cannot be stood in for without touching the real ones; the proof names that as not covered.

## 5. The tests for the second check change the disk from inside a rule

Mutation 11 showed that nothing watched the check made again immediately before each move. To watch
it, a test has to change the disk between the report pass and a move, inside one call to
`ReclaimRunner.Run` - and the runner offers no hook between the two, rightly.

The decision was a wrapper, `InterferingRule`, around a REAL rule: it answers exactly as the rule
inside it answers, and before each examination it tells the test which examination this is. The test
uses two rules, and has the second rule's report pass examination write into the FIRST rule's item.
That ordering is the point: the write lands after the report pass called the first item eligible and
before the first item's own move, and - unlike a write hung on the first rule's own third examination
- it still happens when the second check is taken out, so the mutation shows the item MOVING on a
stale answer rather than the write quietly never happening.

A thread racing the runner would have been the other way, and was rejected: a proof that passes or
fails by timing is not a proof.

The second test needs no wrapper: two offers, one inside the other. Once the outer has moved, the
inner must carry the answer of a check made against the disk as it then is.

## 6. The premise of a test is asserted last

`Reclaim_AnItemWrittenToBetweenTheReportPassAndItsOwnMove_...` pins its own premise: that the second
rule examined three times. On the first mutation run that assertion came first, and the test went red
saying "expected 3, actual 2" - true, and useless to a reader, who is owed "the item moved". The
premise was moved to the end and the mutation run again. A revert proof is read by its failure text,
so the first thing a test says when its check is gone should be what the missing check costs.

## 7. The flow test measures with its own instrument, and does not assert the volume

The mandate asks for measured bytes before and after at each step. The flow test now measures every
file outside holding, the bytes inside holding, and the whole tree, after every step, with the
framework's own file enumeration - deliberately not `FolderMeasures`, which is the code under test. A
measuring bug shared by the product and the test would otherwise agree with itself.

The volume's free space is written to the test's output and never asserted. In the run the proof
quotes, it rose by 16,384 bytes across an apply that freed nothing, because the volume is a live one.
An assertion on it would fail for reasons that have nothing to do with this tool, and would be
deleted the first time it did.

The numbers reach the proof document through the test's own output (`ITestOutputHelper`), read from a
detailed-verbosity run, so the document quotes a run and not an intention.

## 8. The reclaim command is never run with the apply flag, even on a fixture

The command's holding root is fixed at `cc-reclaim-holding` at the root of the volume, and the
reclaim command takes no flag to move it (judgement 1 of the plan). An apply through the command on a
fixture tree would therefore create a folder at the root of the owner's real disk. That is outside
every fixture, so it was not done. The apply is proven through the engine, with holding inside the
fixture; the command is proven as a dry run. The proof names the gap.

If the Reviewer wants the command proven end to end with the apply flag, the way to do it is a
`--holding-root` flag on `reclaim`, which is a product decision and not this seat's to take.

## 9. One existing test was left alone, and reported

`RunnerTests.Run_EveryAnswerThisToolCanGive_IsPlainAscii` calls the real command with the apply flag
on a fixture tree. It is safe because no real rule's folder sits inside a fresh fixture, so no rule is
selected and the run ends broken before any item exists - but that is safety by rule selection, and
the plan says the real rule set is never constructed with the apply flag in any test, which is not
literally true. The decision was to change nothing and say so in the proof: rewriting another seat's
passing test on a judgement call is not what this task was opened for.

## 10. Every mutation was run twice

The first round, against the 253 tests as they stood, is what found the three unheld mutations. But a
proof document that quoted first-round counts would describe a suite that no longer exists. So after
the new tests were committed, all thirteen mutations were run again, one at a time, with a prediction
written down before each. The counts changed where they should have (1 and 3 gained the wiring tests,
5 gained its new test, 8 gained two) and nowhere else. Mutation 9 was run a third time after the last
two tests were added, because it is the one mutation whose path they go near.

## 11. The class comment says what was measured

The comment at the top of `ReclaimRefusalTests.cs` said every refusal "has been proven to fail with
its refusal taken out". When this task began that was not true - no revert proof had been run. It is
now true, and it says more than it did: which refusals went red with the item eligible, which went
red on the name only and why, and where the record is. A sentence next to the code is a claim, and a
claim nobody checked is how this mission's earlier phases were bitten.

## 12. What this task deliberately does not contain

- No change to any product behaviour. Every mutation showed the refusal in place; the three unheld
  ones were holes in the tests, not in the code. No file under `src/CcDirector.Reclaim`,
  `src/CcDirector.Reclaim.Windows`, `src/CcDirector.Core` or `tools/cc-cleanup-storage` differs from
  `3677958dd`.
- No removal of anything on this machine, and no run of the built tool with the apply flag.
- No run of `.\scripts\test-local.ps1` or the `-Parked` gate.
- No pull request and no merge.
