# Review brief: phase 3, removal with holding

For the Reviewer who reads phase 3. **This phase writes the code that deletes things**, so it is
reviewed BEFORE it merges, not after, and it is the one phase where a missed defect can cost the
owner data rather than time.

You are a different agent family from the seat that built it. Disprove, do not confirm.

## The laws that bind you while reviewing

- **Remove nothing on this machine, ever.** Not to test a refusal, not on a folder that looks safe.
  Removal is proven only on fixture trees the tests build and destroy. If you want to see the tool
  remove something, run the tests - they build a tree, remove inside it, and destroy it.
- **Never run the built tool with `--apply` against any path that is not a fixture tree a test just
  built.** If you think you need to, stop and ask the Delivery Lead.
- Never sign anything. Plain English, no abbreviations, ASCII only.

## Read first

`mission.md` section 5 ("Removal is a move, and it is late", "The refusal list"), then
`mandate-phase-3.md`, `phase-3-plan.md` and `phase-3-rulings.md` - the plan is what the phase
promised and the rulings changed one thing materially. Then the code.

## The question this phase turns on

**Is each of the ten refusals real, or is it a comment?** A refusal with a green test that stays
green when the refusal is deleted is not a refusal. The phase owes a revert proof for every one, and
your job is to distrust those proofs:

- **Was each revert run against the WHOLE suite?** Phase 2 reported two red tests where there were
  three, purely because it ran that proof under a two-name filter, so the third was invisible rather
  than absent. A proof that names a filter is a proof that could not have seen anything else.
- **Was the check DELETED, or neutered?** `if (false)` does not work in this repository - warnings
  are errors, so unreachable code becomes a build failure and no test runs at all. A build failure is
  not a red test.
- **Was there a rebuild before the restore run?** `git checkout --` restores the source, not the
  assembly. A `--no-build` run after a restore certifies the mutated binary.
- Pick at least three refusals and **re-run their reverts yourself**. Do not take the document.

## Refusal 1 is the one to spend your time on

It protects the owner's credentials, and the Delivery Lead overruled the plan's original design for
it. The plan proposed reflecting over `CcStorage` for method names containing "vault", "credential"
or "secret"; that misses `Config()`, whose own documentation comment reads "Tool settings, OAuth
tokens, credentials, app state", and misses `keyvault.json`. It must now be an explicit enumeration
declared in `CcStorage` beside the paths it names, plus **a test that fails when a new storage path
member is neither protected nor explicitly classified**.

Check three things by running, not reading:

1. That the staleness test actually fails when a path member is added and left unclassified. Add one
   and watch it go red.
2. That every protected path is refused with a fixture stand-in under a rule that matched it, and
   that the refusal is NAMED in the output rather than the item just quietly not appearing.
3. That the enumeration covers `Vault()`, `SecretsStore()`, `Config()` and the `keyvault.json` file.

## The other things worth suspicion

- **Enumerated, never inferred.** For every item the gate must report the outcome of all ten checks -
  passed, refused, or not-reached - so nobody can read an unchecked refusal as a passed one. Check
  that `not-reached` really appears and is not collapsed into `passed`.
- **The dry run must not be a simulation.** Every check that would refuse an item must refuse it in
  the dry run too, so dry run and apply can never disagree about what is eligible. Try to make them
  disagree.
- **The gate must run AGAIN immediately before each move**, because the disk changes between one
  item's move and the next. Find the second call, or find that it is missing.
- **Space is not freed until holding is purged, and the report must say so plainly** rather than
  claiming the space early.
- **Purge is its own command and never part of a removal.** Check that nothing calls it implicitly.
- **Lean to keep.** Where a check cannot answer - a path that will not resolve, a file that will not
  open, a folder that will not list - the answer must be refuse. Each is a test; try the cases the
  tests do not cover.
- Nothing raises itself to administrator. No real cleanup command is ever run by a test.

## The proof the phase owes, which you are checking

Local gate green **with `-Parked`** (this phase changes `CcDirector.Core`, so the parked suites
genuinely apply and phase 2's reason for declining them does not carry over); every numbered refusal
test shown by name; every refusal proven red with its refusal removed; the whole flow on a fixture
tree with measured bytes before and after at each step; and **the failure cases, not one success
run**.

## How to finish

Write `review-phase-3.md`, commit and push it, and message the Delivery Lead - session `09f9da41` -
one line with approved or not approved and where the file is. Say plainly what you re-ran and what
you only read. A review that does not say where it stopped reads as covering everything.
