# Ruling - the way up must offer a record whose sessions all ended without a handover

Made by the Delivery Lead (session 150) on 20 September 2026, from the mission document. It is a
reading of the document where two parts of it disagree, not a new decision and not an override. If the
owner reads it differently, his word wins; it goes in his report.

## The question phase 3 raised

`DirectorWayUp.IsOfferable` offers a record only when `OwedSeats(doc).Count > 0`, and a seat is "owed"
only when its restore decision is `restore` and it has not come back. That is the literal wording of
mission document 5.3 item 10.

A record whose every session ended at the limit holds no such seat. The operating system shutdown
record is exactly that: `DirectorDrain.RecordOperatingSystemShutdownAsync` writes every seat as
`EndedAtLimit` with decision `Undecided` (DirectorDrain.cs, lines 2002 to 2006). So it is never offered
at start-up, and its saved conversations are reachable only through Restart history.

## The ruling

**Widen the check.** A record is offered at start-up when it came from a smart shutdown, was not
cancelled, and holds at least one seat that can still be acted on:

- a seat decided `restore` that has not come back (as now), OR
- a seat that ended without a handover, whose saved conversation can be reopened and has not been.

A record with neither is not offered and stays in the history, as now.

## Why the document says so

Two owner-accepted rulings describe the outcome for exactly this case, and both are more specific than
the mechanism sentence in 5.3 item 10:

- **10.3**: a session ended at the limit or that never answered "is listed on the way up as 'ended
  without a handover' with one button: reopen its saved conversation ... Unticked by default." A
  session cannot be listed on the way up if its record is never offered.
- **10.5**, the operating system shutting down: the Director "writes the record at once (names,
  repositories, conversation ids, no handovers), lets the sessions end, **and on the way up offers the
  saved conversations as in 10.3**." Such a record has no handovers by construction, so under the
  literal check it could never be offered - which would make 10.5 impossible to satisfy.

5.3 item 10 was written describing the ordinary shutdown, where at least one session hands over. The
rulings state what the owner asked to see; the mechanism serves them.

## What this does not change

- A record from "shut down and ignore all sessions" is still never offered automatically (5.3 item 8).
- A cancelled record is still never offered (5.3 item 7).
- Ended-without-a-handover seats stay UNTICKED by default and keep their single reopen button (10.3).
  Widening the check must not tick them, and must not bring any session back by itself.
- Codex stays noted only: its driver ignores the conversation id (5.4).
