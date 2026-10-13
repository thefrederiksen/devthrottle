# The mutation-proof pin: how to take a baseline and a mutation arm that mean something

## What a mutation proof is, and the one way it lies

We prove a security guard is real by mutating the one primitive it depends on, running the whole suite
again, and reconciling the arithmetic: the tests that pass plus the tests that fail under the mutation must
add up to the tests that passed under the restored run. If they add up, the suite is held to have observed
the primitive.

There is exactly one failure that reconciliation cannot see, and it is the one that matters most.

Suppose the baseline is taken on a working tree where a security guard block has **already been silently
deleted and left uncommitted**. That is not a hypothetical - it is what a killed run leaves behind, because
a process that is killed skips its cleanup. The tree still compiles. The suite still runs. The baseline
still looks green and complete. Then the mutation arm runs on that same contaminated tree, and the two runs
reconcile **perfectly** - because they agree with each other, not because either one measured anything.

The cheapest detector we own passes while measuring nothing, and nothing anywhere in the process says so.

On 2026-07-19 a worker found exactly that on disk in its own worktree. A sweep of the mission's working
trees that night found four dirty trees.

## Why telling people to check their tree does not work

The worker who **inherits** a contaminated tree is, by definition, the one who does not know there is a
mutation in it. It looks clean to them: it compiles, and the tests pass.

This is the same shape as the concurrent-suite problem the per-user test-suite lock was built for. Four
workers were each told not to run two suites at once; each complied with everything it could observe; four
suites ran concurrently anyway. A rule that requires global knowledge cannot be obeyed by an actor holding
only local knowledge. That one was solved by a mechanism rather than a briefing, and so is this.

## How to run a proof

Set the pin once, before the baseline. From that moment until you release it, **every** test run in that
working tree is checked - the baseline, the arm, and any re-run of either.

```
# 1. Before the baseline. Refuses if the tree is already dirty.
./scripts/mutation-proof-pin.ps1 set -Phase baseline -Note "tenant isolation guard, issue 1901"

#    Run the suite. Record the numbers against the pinned head it printed.

# 2. Apply the mutation, then declare the arm, naming what it touches. This does NOT re-pin: it carries
#    the baseline's head forward, and refuses if the tree has moved off it.
./scripts/mutation-proof-pin.ps1 set -Phase arm -Mutates src/CcDirector.Gateway/Api/GatewayEndpoints.cs

#    Run the suite again. Reconcile.

# 3. Restore the mutation and release the pin.
./scripts/mutation-proof-pin.ps1 release
```

`status` shows the current pin and the tree beside it. `ledger` prints every proof run ever recorded on
this machine.

## What the guard refuses, and what it never touches

With a pin active it requires the head to be the pinned head, and the working tree to carry **exactly** the
declared changes:

- **more** than was declared is a contaminated run, and is refused naming each undeclared file;
- **less** than was declared is refused too, and that half matters as much: an arm with no mutation in it
  is a second baseline wearing an arm's name, and it reconciles perfectly against the first while proving
  nothing.

A baseline is simply an arm that declares no mutations, so one rule runs both phases.

**With no pin, no run is ever refused.** A worker mid-rework is legitimately dirty and must be able to run
whatever it likes. This is deliberate and is not an oversight to be tidied up later: a blanket refusal on
any dirty tree would be switched off within a day, and a guard that gets switched off protects nothing.

### The pinned head never moves, and that is enforced twice

A proof's pinned head is its identity. A baseline and its arm must be taken at the same commit, or the
reconciliation compares two different programs and the arithmetic means nothing while still adding up.

The first version of this tool got that wrong in the most dangerous possible way: every `set` recomputed
`git rev-parse HEAD`, so the documented baseline-to-arm transition silently **re-pinned** to the new head
whenever HEAD had moved between the two runs. The guard then compared the arm against the new head, found
an exact match, and admitted it. The supported happy path walked into the exact event the guard exists to
refuse.

Two mechanisms now hold it:

1. `set -Phase arm` **carries the baseline's head forward** and never recomputes it. If the tree has moved
   off the pinned head it refuses, naming both. Setting an arm with no baseline refuses too, and pinning a
   baseline over an existing pin refuses - re-pinning requires an explicit `release`, which is loud and
   discards the old proof identity along with its numbers.
2. The pin carries a **proof identity**, and every run records it to the ledger with the head it measured.
   If any earlier run of the same proof was measured against a different head, the run is refused - however
   the pin file came to say what it says. That covers a hand-edited pin, a copied file, and a future change
   to this script. Fixing the script fixes the instance; this covers the class.

### What an unpinned run costs

It is not free, and an earlier version of this document wrongly said it was. Every run of every test
assembly invokes git twice and appends one line to a log outside the tree - roughly a fifth of a second per
assembly against a suite measured in minutes. That is the deliberate price of the ledger below: it is the
entire reason a **forgotten** pin is still answerable afterwards.

## Two kinds of mechanism here, and one is easy to delete by mistake

Some of this **prevents** a failure: the refusal on an undeclared change, the refusal on a moved pin. Those
either work or they do not.

The rest **cannot prevent anything**, and is not meant to. The ledger does not stop a bad proof; it records
what each run verified so the question can be answered later. Requiring the pinned commit to exist on a
remote does not stop a stashed repair; it makes the commit fetchable so a reader can see what was in it.

The honest sentence is that they **convert a private failure into a public one**. A contaminated proof
nobody can detect afterwards lives on one machine and dies with it. The same failure recorded in a ledger,
or attributed to a commit anyone can fetch, is still a failure — but one a second person can find without
being told where to look. That matters here more than it would elsewhere, because this whole proof
structure rests on somebody else being able to re-derive a claim.

So do not remove the ledger or the publication requirement on the grounds that they "do not stop
anything". They are not supposed to. The question to answer first is whether the failure each one makes
visible has any other way of being seen.

## The second job: the record outlives the tree

Refusing a bad run is only half of it.

Somebody was asked whether four **already-merged** security proofs had been taken on contaminated trees. The
answer could not be given - not because the evidence was hard to find, but because it no longer existed.
Those proofs were run in worktrees, and the worktrees were removed after merging. That removal is correct
hygiene, and it destroyed the only artifact that could have answered the question. Those four are recorded
as unknown and will stay unknown.

The **pin** lives in the repository's git directory, found by asking `git rev-parse --absolute-git-dir` -
the same question the guard asks, so there is no second derivation to disagree with. (There used to be one,
computed from the working tree path in both PowerShell and C#, and the two copies did not agree off
Windows: the tool printed `PINNED` while the guard read nothing at all. Continuous integration runs on
Linux.) That location is per-worktree by construction, is invisible to `git status` so the pin cannot dirty
the tree it guards, and survives `git clean -xdf`.

The **ledger** has the opposite requirement - it must outlive the tree - so every run appends a line to a
file **outside every working tree**, under the per-user local
application data directory alongside the suite lock's own state. `git worktree remove`, `git clean -xdf`,
and deleting the whole checkout cannot reach it.

The ledger writes on **admission** as well as refusal, and an admission line states which head was verified
and that the tree matched it. That is the point: a log written only when something goes wrong cannot
afterwards distinguish "verified clean" from "the guard never ran". Both are silence, and silence reads as
success.

Unpinned runs are recorded too - never refused. That is what makes a **forgotten** pin recoverable: when a
proof's numbers are questioned later, the record still says whether the tree was dirty at the moment the
baseline ran.

## Where it lives

- `src/TestInfrastructure/MutationProofPinGuard.cs` - the mechanism. Read the comment at the top before
  changing it.
- `Directory.Build.props` - links the mechanism and its per-assembly sentinel (`MutationProofPinGuardArmedTests.cs`) into every project whose name ends in `.Tests`, so a test project
  added next year is covered without anybody remembering. It keys on the project name rather than the
  `IsTestProject` flag because one of the seven existing test projects does not set that flag.
- `scripts/mutation-proof-pin.ps1` - set, status, release, ledger.

## The one thing that is not automatic

A worker can still forget to set the pin at all, and then nothing is checked. That limit is the price of
the scope gate, and it is stated here rather than papered over.

Two things narrow it. The pin is the only place the pinned head is written down, so a proof that skipped it
has no pinned head to cite and is not a proof anybody can write up. And the ledger records unpinned runs
anyway, so a forgotten pin leaves evidence instead of a hole.
