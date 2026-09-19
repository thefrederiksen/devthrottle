# Phase 4 mandate: the remaining Windows rules

From the Delivery Lead, 19 September 2026. Phase 4 adds rules and **touches no removal code at all**,
which is why the mission allows it to run beside phase 3 once phase 2 is merged. Keep it that way: if
you find yourself editing anything phase 3 owns, stop and tell me instead of reaching across.

A Developer seat owns this phase. The Reviewer reads it before it merges.

## Read these first, in this order

1. `docs/missions/reclaim-the-disk-2026-09-18/mission.md`, all of it. Section 5's "The one idea",
   "Every rule fails closed" and the rules table are your mandate; this document only expands them.
2. `mandate-phase-2.md`, `phase-2-decisions.md`, `phase-2-proof.md`, and the phase 2 code itself -
   `src/CcDirector.Reclaim/Rules/` and `src/CcDirector.Reclaim.Windows/`. **You are writing more of
   exactly what is already there.** Copy its shape rather than inventing a second one.
3. `cc-devthrottle skill get devthrottle-method` and `cc-devthrottle skill get destructive-sweeps-lean-to-keep`.
4. `docs/CodingStyle.md`, `docs/axi-standard.md` and `CLAUDE.md`.

## The law that binds this phase

**Nothing on this machine is ever removed by this mission**, and in this phase that is easy to honour
because you write no removal code. But it binds one thing specifically: **never run an owner's cleanup
command to see what it does.** A rule that names `Dism.exe /StartComponentCleanup` prints that command
for the owner; a test that runs it clears the real component store on this machine and cannot be
undone. The machinery that runs such a command is proven with a harmless command the test supplies,
which is the rule phase 3 works to as well.

## What you build

### The three remaining rules

Each one is a rule exactly as phase 2 defines one: an id, a name, one of the three proof kinds, what
it removes, why it is safe, what is lost, how to get it back, an age gate, whether it needs
administrator, the command to run, where it looks, and **controls, at least one of which must not be
empty**. The fold will refuse a rule that declares no load-bearing control, so this is not advice.

1. **The Windows component store.** Proof: the owner's own command, `Dism.exe /Online /Cleanup-Image
   /StartComponentCleanup`. Needs administrator. The mission has never measured it here, so the rule
   must be able to report honestly that it does not know rather than guessing - if the size cannot be
   determined, that is a control counting nought and the rule says broken, not nothing to remove.
   Items cleared by an owner's own command are **not offered for holding**, because that command does
   not put anything back, and the rule's "what is lost" says so.
2. **What Windows' own Disk Cleanup offers.** Proof: the owner's own command, read from the
   `VolumeCaches` registry list rather than from a list you type. **Enumerate what that list actually
   contains on this machine; never hard-code the handlers.** A typed list goes stale invisibly, which
   is the same defect the credential-path list in phase 3 is forbidden to have. Some handlers need
   administrator and the rule says which.
3. **Crash dumps, error reports and the recycle bin.** Proof: a record. The recycle bin measured 2.6
   gigabytes here. The recycle bin is the one place on the machine that is already a holding folder,
   so say plainly in "what is lost" that emptying it is the end of the line, and treat per-user and
   per-volume bins as what they are rather than as one thing.

**The Gradle cache is deliberately absent from phase 2 and stays absent unless you can prove it.** It
is in the mission's rules table under the package caches, but phase 2 left it out on purpose. If
Gradle is not installed here, a rule for it has nothing behind it; if it is, add it with the same
proof as the other caches. Decide it on evidence and write down which you found.

### The categories that are reported and never offered

The mission names them: DevThrottle's own session logs and history, recordings, application data such
as `ProgramData\mindzie`, Hugging Face models, Playwright browsers, Docker and virtual machine disks,
Android emulators, browser profiles. **Sized and dated, never offered for removal.**

These are not rules and must not be able to become candidates. Whatever you build for them, a reader
must not be able to mistake a report line for an offer, and no code path may put one of these into
the candidate list. **A test holds that** - name it for what it declines.

## What phase 4 must NOT contain

- No removal code, and no change to anything phase 3 owns. If a rule needs something from phase 3,
  tell me rather than building it twice.
- Nothing that raises itself to administrator. A rule that needs one says so and prints the command.
- No cleanup command run for real, ever, including in a test.
- No rule whose every control may be empty. The fold refuses it and it would be a defect anyway.
- No typed list where the machine holds the real one - the `VolumeCaches` handlers above all.

## The proof you owe

1. `.\scripts\test-local.ps1` green in a worktree of its own, with `CcDirector.Reclaim.Tests` in it.
2. `dotnet test src\CcDirector.Reclaim.Tests` with the new tests named, and **the BROKEN case shown
   for each new rule** - what makes that rule unable to do its work, and the test that proves it says
   broken rather than nothing to remove.
3. **A revert proof for each new rule's fail-closed check**, run against the WHOLE suite, never under
   a filter. Phase 2 got a count wrong precisely because it ran one under a two-name filter and the
   third failing test was invisible rather than absent; do not repeat it. Commit before you mutate.
4. A read-only `recommend "C:\" --json` run on this machine with the new rules in, its output
   committed to `evidence/`, and the new rules' controls read out of the parsed output - never out of
   a search of the printed text.
5. **What the proof does not cover, stated plainly**, as phase 2's proof does in its last section.

## How it ends

Local gate green, a Reviewer from a different agent family reads it, every finding answered in
`review-phase-4-answers.md` accepted or declined with the reason, then merge on local green plus that
review. Report to me, session 09f9da41, not to the owner.
