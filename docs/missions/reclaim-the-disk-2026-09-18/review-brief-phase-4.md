# Review brief: phase 4, the remaining Windows rules

For the Reviewer who reads phase 4. This phase adds rules and **writes no removal code**, so it
cannot delete anything - which makes it lower risk than phase 3 but not low stakes: a rule that
answers "nothing to remove" when it could not do its work is the failure the whole mission exists to
prevent, and this phase adds four new places for it to happen.

You are a different agent family from the seat that built it. Disprove, do not confirm.

## The laws that bind you while reviewing

- **Remove nothing on this machine**, and **never run an owner's cleanup command to see what it
  does.** A rule that names `Dism.exe /StartComponentCleanup` or `cleanmgr` prints that command for
  the owner; running it clears the real component store or the real caches on this machine and cannot
  be undone. If a test runs one, that alone is a blocking finding.
- Never sign anything. Plain English, no abbreviations, ASCII only.

## Read first

`mandate-phase-4.md` and `mission.md` section 5, then phase 2's `phase-2-decisions.md` and the rule
code on main - this phase is more of what is already there, so a rule that invents its own shape is
worth a question.

## The question this phase turns on

**For each new rule: what makes it unable to do its work, and does it say BROKEN rather than nothing
to remove?** Every rule must declare at least one control marked must-not-be-empty - the fold now
refuses a rule that declares none - but meeting that letter is not enough:

**A control whose count cannot fail by construction is decoration.** `looked-for: 1` is always 1 and
can never alarm, so a path whose only must-not-be-empty control is a constant has no gate at all. Go
through each rule's early-return and unavailable paths and ask what could make that path wrong, then
ask which control would catch it. The Delivery Lead found one of these before review and sent it to
the seat; check it was actually fixed and check for others.

**`Directory.Exists` and `File.Exists` swallow their errors.** Both return plain `false` for "not
there" AND for "could not tell" - access denied, an input-output error, a path that cannot be
evaluated. Every use of them on a path that decides an answer is a place where could-not-tell can be
reported as nothing-to-remove. This is not hypothetical on this machine: the committed scan evidence
`evidence/scan-c-2026-09-19.json` records `C:\$Recycle.Bin\S-1-5-18` refusing a listing with
accessDenied. **Grep for both and judge every hit.**

Watch also for a **partial** silent skip: a loop that `continue`s past a place it could not read,
where the count of places read still looks healthy. The all-failed case may be caught by a
must-not-be-empty control while the some-failed case is invisible. Ask for a counted control naming
what would not be read, the way the recycle bin rule already counts bins that would not be listed.

## The rest, in order of how much time to give it

- **The Disk Cleanup handlers must be read from the `VolumeCaches` registry list, never typed.** A
  typed list of handlers goes stale invisibly, which is the same defect refusal 1 in phase 3 was
  redesigned to avoid. Check the registry is actually read, and that an unreadable key reports broken
  rather than an empty category list.
- **`NeedsAdministrator` must be measured or admitted, not reasoned.** A wrong value either sends the
  owner to an elevated prompt he did not need, or fails silently when he did. If the rule cannot
  determine it, the rule should say so rather than guess.
- **The reported-but-never-offered categories must not be able to become candidates** - DevThrottle's
  own session logs and history, recordings, `ProgramData\mindzie`, Hugging Face models, Playwright
  browsers, Docker and virtual machine disks, Android emulators, browser profiles. Try to find a code
  path that puts one into the candidate list. A test should hold this; check the test would fail if
  the protection were removed.
- **Gradle.** Phase 2 left it out deliberately. Phase 4 was told to decide it on evidence and write
  down which it found. Check there is evidence, not a preference.
- **The revert proofs.** Run against the WHOLE suite, never under a `--filter` - phase 2 reported two
  red tests where there were three for exactly that reason. `if (false)` is not a revert proof here,
  because warnings are errors and unreachable code becomes a build failure with no test run at all.
  Re-run at least two yourself.

## The proof the phase owes

Local gate green; the new tests named; the BROKEN case shown for each new rule; a read-only
`recommend "C:\" --json` run with the new rules in, committed to `evidence/`, with the new rules'
controls read out of the parsed output rather than out of a search of the printed text; and **what
the proof does not cover, stated plainly.**

## How to finish

Write `review-phase-4.md`, commit and push it, and message the Delivery Lead - session `09f9da41` -
one line with approved or not approved and where it is. Say what you re-ran and what you only read.
