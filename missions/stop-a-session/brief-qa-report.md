# The QA report - the brief

**You did not build this feature, and that is why you have this seat.** A builder photographing its
own work reaches for the path it already knows works. You reach for the path a user would.

**This report is the mission's goal.** Not the code, not the tests - this document. The mission is
finished when a person who was not here can read it and see the feature working. Section 1 of
`missions/stop-a-session.html` says so, and section 6 is the checklist you are held to. Read the
whole mission document before you start, and `missions/stop-a-session/architect-state.md` for what
was actually built.

## Where it goes

`missions/stop-a-session/qa-report.html` - one self-contained document, with its images committed
beside it in `missions/stop-a-session/qa/`, referenced by relative path so it reads from a checkout
with no server. Match the visual style of `missions/stop-a-session.html`; it is the sibling document
and they should look like one pair.

**Name the version it was run against and the date it was run**, at the top, so a later reader can
tell whether it still describes the product.

## THIS REPOSITORY IS PUBLIC - an Architect ruling you must follow

The owner's fleet carries client work, and its session names carry client names. **No screenshot in
this report may show a session, repository, mission or path belonging to unrelated or client work.**

- Do the run with a throwaway session in a throwaway repository, named for this mission.
- Where a frame has to show the roster (frames 1 and 3), crop it, or filter it, so only the rows
  this report is about are visible.
- Read every image before you commit it. If you cannot tell whether something in it is a client
  name, it does not go in.

This is not negotiable and it is not a preference. Getting it wrong publishes a client's name.

## The eight frames - section 6, and nothing less

Every item carries a screenshot. A line of prose saying it happened does not count, and neither does
a green test run. Where an item asks for two things "in the same frame", they must genuinely be in
one frame - two frames stitched together prove nothing about what was true at one moment.

1. **The fleet before.** A session running, with its session identifier and its process identifier
   both visible in the same frame, so the reader can match them against everything that follows.
2. **The stop, from the command line.** The command being issued - reason included, which Ruling 4
   makes mandatory - and the exact answer it gave back. Ruling 5 says that answer has to be in words;
   this frame is where the reader sees whether it is. **Beside it, a second frame:** the same stop
   attempted with no reason, refused, and saying plainly that a reason is what is missing.
3. **The fleet after.** `session list` without that row.
4. **The process is actually gone.** The machine itself showing that process identifier no longer
   exists. This is the frame that separates a row being hidden from a session being ended - so use
   the operating system's own tool, not the product's word for it.
5. **The second stop.** The same command run again, succeeding quietly instead of erroring. Ruling 3,
   shown rather than asserted. The answer must distinguish "already stopped" from "not on this
   fleet"; say in the report which one you got and why that is the right one.
6. **The stop control in the Director window.** The same thing done by clicking rather than typing,
   with the reason it asked for and the answer it showed.
7. **The stop control in the Cockpit.** The same again from the web. This is also the proof that the
   controls went through one route rather than growing a second implementation - so say how you know
   that from what you saw, not from what the code claims.
8. **The dirty working tree.** A session stopped while it has uncommitted changes in its worktree.
   Ruling 2 is settled, so this frame has a shape: the stop goes through without argument, and the
   answer names the worktree and says the changes were left untouched. **The frame after it shows
   those changes still on disk** - that second frame is the one that actually proves it.

## How to run it without breaking the owner's machine

- **Never kill a process to tidy up.** `CLAUDE.md` rule 0 has no exceptions. The owner runs several
  Directors at once and none of them are yours.
- If you need a Director of your own, it is **slot 5 or higher**, built with
  `scripts\local-build-avalonia.ps1 -Slot 5 -OutputDir "<repo>\scripts\local-build"`, launched
  through the `cc-director-launch` scheduled task - `CLAUDE.md` rule 0b explains why launching it
  from inside a session kills its children. Shut it down with the named signal, never a force kill.
- For the Cockpit frame, drive a browser the Director already owns - `cc-devthrottle skill get browsers`.

## Write it for the owner, not for the repository

Plain English. No abbreviations. Lead with what the feature does for him, then the frames. Say what
is proven and how you know. **Say what is NOT proven, by name** - a gap you admit is worth more than
a claim he has to check. If a frame could not be captured, say so and say why; a missing frame
honestly reported is a finding, and quietly dropping one is the failure this whole mission is about.

## When you are done

Commit the report and its images to `mission/stop-a-session` and push. Send the Architect
(`e66d53fb`) ONE single-line message pointing at the report file - fleet messages truncate at the
first newline. Do not merge anything to main; the Architect lands it.

---

# ADDED AFTER PHASES B AND C, AND THE FIRST INSPECTION - read this part too

Much has changed since the top of this brief was written. Where the two disagree, this part wins.

## READ THIS BEFORE YOU RUN ANYTHING: the trap that nearly published client names

`missions/stop-a-session/qa-recipe-notes.md`, addition 1. **An installed `cc-devthrottle` on the
path inherits `CC_GATEWAY_URL` and answers from the HOSTED Gateway** - printing the owner's entire
live fleet, every session and repository name on every machine, while looking completely normal
doing it. A previous QA seat staged a path edit that was silently dropped by `cmd` re-parsing a
quoted string, and its first frame captured the owner's real roster with client repository names in
it. **It was read and deleted before it was committed, and that is the only reason this is a note
and not an incident.**

That is why "read every image before you commit it" is the rule that is not negotiable. It is also
why you verify which Gateway you are talking to BEFORE you photograph anything, not after.

## Where to start

1. `missions/stop-a-session/local-stack-recipe.md` - the recipe. It has been repeated once already
   and it works end to end.
2. `missions/stop-a-session/qa-recipe-notes.md` - what the recipe does NOT warn you about. Eight
   additions found the hard way. Read it second and read it fully.

## What the feature does now, which is not what the top of this brief assumed

- **Ruling 3 has FOUR verdicts, not three:** `stopped`, `alreadyStopped`, `notOnFleet`, and
  `stoppedNotDescribed`. The fourth has THREE causes - an older Director, a liveness check that
  threw, and no process identifier to check at all - and the answer says which.
- **The second stop answers `notOnFleet`, not `alreadyStopped`.** Section 6's illustration sketches
  `already stopped`, and the illustration is looser than the ruling: the first stop removed the row,
  so there is no machine left to ask. If your frame shows `not on this fleet`, that is CORRECT. Say
  so in the report, so a later reader does not call it a mismatch.
- **An independent inspection found eight defects after the builders had reported everything green**,
  four of them P1, and a later phase fixed them. Frames 5, 6 and 7 sit directly on top of those
  fixes, which is why you were stood down and re-seated rather than allowed to photograph earlier.

## Two additions to what the report must contain

1. **WHO BUILT IT.** The owner asked, while the fix phase was running, that the report show every
   session that built and fixed this feature and how many there were. The roster is
   `missions/stop-a-session/who-built-it.md` - fold it in rather than re-deriving it. **Six of the
   fifteen cannot be named**, and that is not sloppiness, it is the finding below.
2. **THE SENTENCE THIS MISSION EARNED.** The feature you are photographing WRITES an audit row naming
   who stopped a session and why. The polite path this fleet has used for years - the deletion flag -
   writes nothing at all, which is exactly why six of this mission's own seats can no longer be
   named. **The gap this mission was raised to fix is the same gap that ate its own paper trail.**
   Put that in the report in one honest sentence. It is not a joke and it is not a flourish; it is
   the clearest evidence that the problem was real. It is filed as its own issue, not fixed here.

## No product or vendor names in the report

The standing rule is absolute and this repository is public. Say **roles and families** - "the seats
that built it", "an inspector from a different agent family than the builders" - never a product or
vendor name. The load-bearing fact survives without brands: a different family inspected than built,
and it found eight defects in work every builder had reported green.

Naming a process in evidence, or in a command a reader must actually run, is functional rather than
attributional and is fine. Naming who wrote the code is not.
