# Destructive Sweeps Lean To Keep

**A destructive operation acts ONLY on what it can positively identify as safe to delete, refreshes
its safety signal on every real use, and when in doubt KEEPS.**

Written up 2026-07-20 after one sweep deleted the owner's own recorded audio and, separately, deleted
an actively-resuming upload mid-use. Both failures came from the same two shapes, and both shapes look
completely reasonable while you are writing them.

## Why it leans all the way to one side

The two failure directions do not cost the same:

- A false DELETE is **unrecoverable**. Here it was the owner's own recorded audio, gone.
- A false KEEP is **merely disk**. It accumulates, and a later, more careful pass reclaims it.

Because the costs are asymmetric, the operation must not sit in the comfortable middle of "delete what
looks stale". It leans all the way toward keeping, and buys the small recoverable cost to avoid the
large unrecoverable one. A sweep that reclaims 80 percent of what it could have, and has never once
destroyed something live, is working correctly.

## The two shapes this forbids

### 1. A deny-list where it must be an allow-list

"Delete every aged directory EXCEPT the names I listed" fails OPEN for any shape nobody thought to
list. It deleted an aged future-partition directory and its sentinel, because no one had thought to
list them.

**Enumerate what to DELETE, never what to skip.** The correct boundary admits only what it can
positively prove is disposable: an aged directory whose name IS a canonical 32-hex upload id, and
nothing else. Malformed names, partials, partition containers, future siblings - all survive, because
none of them can be PROVEN disposable. Their survival is the boundary working, not the boundary
leaking.

This is the same shape as [[checks-that-fail-open]]: a pass condition that is an absence ("not on my
skip list") is satisfied by anything at all, including things that did not exist when the list was
written.

### 2. A safety signal the code does not refresh

The sweep judged staleness by last write, and the design comment claimed a live upload resets its own
clock. A successful re-register and a successful idempotent chunk both wrote nothing - so an actively
resuming upload aged out and was deleted mid-use.

**The safety signal must be touched on EVERY successful operation**, not only the ones that happen to
write bytes. And the sweep must not race a resume between its age check and its delete. If that race
cannot be cheaply proven closed, widen toward KEEP.

Note where the defect actually lived: in a COMMENT asserting a guarantee the code did not implement.
The author wrote it believing it, which is why the author is the last person able to see it false -
see [[proof-covers-the-wrong-thing]]. Make the code deliver what the comment says, or change the
comment. Never leave them disagreeing.

## Before you run one, in order

1. **Canonicalize before you act.** A destructive boundary needs a positively validated canonical
   identifier, not a skip-list. The same shape let a non-canonical path traversal escape once already.
2. **Say out loud what you can PROVE is disposable.** If the sentence needs an "except", it is a
   deny-list and it is wrong.
3. **Take the backup in the same breath as the delete**, not as a separate earlier step. A gap between
   snapshot and sweep is a window another writer can fill.
4. **Check who else is live in the same place.** Read the registry; never broadcast to ask. A shared
   working tree with another session in it is not yours to reset.
5. **Prefer reversible.** Push a backup ref, move to a quarantine directory, rename rather than remove.
   Something you can undo tomorrow is worth far more than the disk it costs tonight.

## The one-line test

> If this sweep were wrong about one item, could I get it back?

If the answer is no, the boundary must be an allow-list of things positively proven disposable, and
everything it cannot classify survives.
