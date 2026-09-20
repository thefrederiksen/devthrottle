# Phase 3: the Delivery Lead's rulings on the plan

19 September 2026. The phase 3 plan is **accepted**, and it is a good one - in particular the single
`RefusalGate`, reporting all ten outcomes for every item so an unchecked refusal can never be read as
a passed one, and running the gate again immediately before each move because the disk changes
between one move and the next.

Section 12 records the judgements it takes rather than leaving them silent, which is exactly right.
Judgements 1, 2, 3, 4, 6, 7 and 8 stand as written. **Judgement 5 is overruled, and not in the
direction the plan expected.**

## Ruling on judgement 5: refusal 1 is not safe as designed

The plan proposes to find the protected paths by reflecting over `CcStorage` and taking every public
static method whose name contains "vault", "credential" or "secret". It then notices that the account
key vault file is not protected, because `keyvault.json` is assembled inside `KeyVault` at
`CcStorage.Root()` and no method names it, and it correctly declines to hand-type that path because
the mandate forbids a typed list.

**Declining to type it was right. The conclusion to draw was that the instrument is wrong.** I
checked `CcStorage` on main rather than reasoning about it, and the name test misses more than the
key vault:

- `Vault()` and `SecretsStore()` are matched, by luck of being named well.
- **`Config()` is not matched, and its own documentation comment reads "Tool settings, OAuth tokens,
  credentials, app state."** A folder documented as holding credentials is invisible to a check that
  looks for the word "credential" in a method's NAME.
- `keyvault.json` is not matched, as the plan says.

So refusal 1, which is the most important refusal in the tool, would have protected two paths by
accident of naming and silently left the OAuth tokens and the key vault exposed. **A check that
protects the right things only when somebody happens to name a method well is the same class of
instrument as an empty record set: it answers confidently and it answers wrongly**, and it fails in
the direction that loses the owner's credentials.

### What to build instead

**One explicit enumeration, declared in `CcStorage` itself, beside the paths it names**, and read by
the gate:

- Add a member to `CcStorage` in `CcDirector.Core` that returns every path that must never be
  touched: at least `Vault()`, `SecretsStore()`, `Config()`, and the `keyvault.json` file. The gate
  reads that one member and refuses the path and everything under it.
- This still honours the mandate. "Read from the resolver, never typed" means the reclaim tool does
  not carry its own copy of the list - and it does not. The list lives with the definitions, where
  the person adding a storage path is looking.
- **The staleness must be caught, because that is the whole reason the mandate forbade a typed
  list.** Add a test in Core that enumerates the public static path members of `CcStorage` and fails
  when one is neither in the protected list nor in an explicit not-protected list. Adding a new
  storage path then forces a decision instead of defaulting to unprotected. That test is the thing
  that makes this safe, so write it first and prove it red by adding a member to a stub.

This changes `CcDirector.Core`, so `Core.Tests` is a parked suite your change genuinely touches.
**Run `.\scripts\test-local.ps1 -Parked` for this phase** - do not carry over phase 2's reasoning for
declining it, which was true only because phase 2 touched no Core source. `-Parked` needs Docker
running and will say so rather than skipping.

## On the key vault, plainly

Nothing in this mission may ever offer the owner's credentials for removal. Refusal 1 exists for that
one purpose, and it must be demonstrably complete rather than complete by coincidence. Prove it with
a test that puts a fixture stand-in for each protected path under a rule that matched it, and shows
every one refused by refusal 1 and named.

## Two things from the phase 2 review that bind this phase

1. **A revert proof runs against the WHOLE suite, never under a `--filter`.** Phase 2 reported two
   red tests where there were three, purely because it ran that proof with two test names in the
   filter, so the third was invisible rather than absent. This phase owes ten refusal reverts, which
   is ten chances to repeat it. Commit before you mutate; never `--no-build` on the restore run.
2. **Switching a check off with `if (false)` is not a revert proof here.** Warnings are errors, so
   unreachable code becomes a build failure, and a build that does not run has told you nothing about
   a test. Delete the check instead.

## And one observation the phase 2 Reviewer left for later

The package cache rules satisfy the fold's must-not-be-empty requirement with a control whose count
is the constant `cache-folders-looked-for: 1` - it meets the letter but can never itself reach nought
and alarm. Not a defect, and nothing in phase 3 depends on it. **Do not copy that shape into any new
control you write**: a control that cannot fail by construction is decoration. It is recorded in the
phase 5 mandate as a question for rules-as-data.
