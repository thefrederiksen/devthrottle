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
