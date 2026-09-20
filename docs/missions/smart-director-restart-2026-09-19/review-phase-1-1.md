# Review - Smart Director Restart, phase 1, task 1: the contract types, the record marks, the session verbs, the request text

Reviewed by the Reviewer seat, opened by the Tech Lead of phase 1. The commit under review is `4d0ec0e1e`
on branch `smart-restart/p1-contracts`; the diff reviewed is `origin/main...HEAD`, which is the code
commit `52e4b3228` and the proof commit `4d0ec0e1e`.

## Scope

What I read:

- The mandate for this review, the mission document, `phase-1-interface.md`, and the Developer's
  mandate.
- The whole diff `origin/main...HEAD`: every file it touches, in full, plus the parts of the tree the
  change leans on that the diff does not show - `SessionCommandExecutor.cs` (the interrupt and kill
  verbs), `Session.cs` and the agent drivers behind the interrupt path, `WorkspaceValidation.cs` in
  full, `DrainReportBlock.cs`, `DirectorDrain.cs`, and the existing `SessionManagerDrainControl`
  methods around the new ones.
- The Developer's proof. Read as self-testimony and checked, not trusted: every claim in it that I
  could test by reading code or running the check, I did.

What I ran:

- `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"`
  on this commit, in the foreground: **475 passed, 0 failed**. This matches the Developer's figure and
  the Tech Lead's, and the new count is consistent with the baseline (418 plus 57 new cases, and I
  counted the 57 by hand from the test files).
- My own reading of the product source for every path the change claims to reuse (below), and my own
  search of the whole tree for any caller of the two new verbs other than the tests.

What I could not reach:

- Nothing in the product calls the new types, verbs or messages yet - the engine is the next task. So
  no review can watch a real run of any of it; the verb tests prove the path beneath the seam, the
  message tests prove the words, and the engine's own tests must prove they are sent.
- The older reader in the round-trip test is a stand-in class with the same unknown-field bag, not the
  older build (the older type no longer exists in this tree). It proves the mechanism, not the older
  build.
- No real agent process and no real Director were started; the interrupt test proves the key reaches
  the backend, not that a live agent stops.
- The one full-project failure the Developer reported (`GovernanceAuditLogTests`) is outside the
  filter this mandate set, was reported and not explained, and I did not chase it.

What I verified against the specific questions in my mandate, in order:

1. **The contract types against `phase-1-interface.md`.** I compared name for name and shape for shape:
   both interfaces (every member, in the document's order), the four enums (every member, in order),
   the five records, and the static `SmartShutdownTimes` with its five allowed times and its ten minute
   default. They match exactly. `SmartShutdownRequest` refuses a time outside the five both on
   construction and on any copy made with `with`, and the refusal message names what was sent and what
   may be sent. Nothing was added that a screen building against the document would trip over, and
   `CreateSmartShutdown()` is correctly absent (it is the next task's).
2. **The record marks across builds.** The two document-level marks land in the unknown-field bag of an
   older reader and are written back out, so they are kept, not silently lost; the test proves the
   mechanism through the real store. A seat drain state `ended-at-limit` is refused by an older Gateway
   on save - that is the known and reported limit of the interface document's section 5, not a new
   finding. A record written before the marks reads with both fields null and is accepted unchanged
   (tested through the real store).
3. **The two validation rules the Developer added beyond its mandate.** I worked through every record
   the engine will legitimately write: a plain capture (no marks, accepted), an ignore-all record
   (`shutdownKind` of `ignore-all`, no cancel, accepted), an operating system shutdown record written by
   `RecordAndLetEndAsync` (no marks at all, accepted), a smart shutdown record, and the same record
   after a cancel (`shutdownKind` of `smart shutdown` and a cancel time, accepted). Neither rule refuses
   any of them; the authored rule can only bite a record that also claims half a dozen other things an
   authored workspace may never carry. The one open question here - that the closed list has no value
   for an operating system shutdown record, so the way up cannot find such a record by this field - is
   already raised by the Developer in the proof for the Tech Lead; my reading confirms it, and it is
   not a defect in this change, whose mandate named exactly two kinds.
4. **The two verbs on the real seam.** `InterruptAsync` goes through
   `SessionCommandExecutor.InterruptAsync` to `Session.InterruptAsync` to the agent driver - the same
   path the Director's interrupt route uses. `EndAsync` goes through `SessionCommandExecutor.KillAsync`
   to `SessionManager.KillSessionAsync` - the Director's existing stop path, the same one the stop route
   uses. No second way of killing was written. Every failure the drivers and backends can type comes
   back as a typed answer: an agent with no safe interrupt answers "could not" with the driver's own
   words, a backend that refuses the stop answers "could not" and the session stays listed, and a stop
   that leaves the row in place (a pooled worktree that would not be taken back) answers "could not"
   rather than "ended". The only things that can throw out of either verb are exceptions no driver
   defines, which is the same exposure the existing `SendAsync` has after its wedged catch; and the
   older drain never calls either verb, so nothing can throw into it. That last fact is proved, not
   said: `Drain_OnTheOlderPath_NeverInterruptsAndNeverEndsASession` runs the real older drain on the rig
   over a clean, a blocked, a silent, a wedged and a never-reaped session, first asserts the run really
   met all five cases, then asserts neither verb was called and that `ended-at-limit` was never written.
   The fake records the call, not the outcome, so a refused call would still be seen. I also searched
   the whole tree myself: no code outside the tests calls either verb, and the older drain's source
   calls only `SendAsync` and `MarkForDeletion`.
5. **The request texts.** The sentence about not being killed is gone from the smart shutdown message
   and from the short second message (a test asserts its absence in both, ignoring case), and it is
   still in the older message, which has no changed line. The closing block is asked for in exactly
   the words the older message uses - a test holds the two paragraphs character for character equal,
   and I checked those words against what `DrainReportBlock` actually parses: the opening
   `drain-report`, the `state` line, `restore`, `why`, `covered` with its two parts, `question`, and
   `blocked-reason` all match the parser.
6. **The harness change** (`tools/harnesses/drain-index-diff/Program.cs`). It was needed: the harness
   implements the seam's interface, and two new members had to be implemented before it would compile.
   Throwing rather than answering is the right choice for that harness - it exists to diff two runs of
   the OLDER drain, and a call to either verb from it would mean the older drain had started forcing,
   which is a run whose diff nobody should trust.
7. **Tests that would stay green on a revert.** The verb tests watch the real path beneath the seam
   over a real `SessionManager`; the marks tests go through the real store on an isolated on-disk
   database; the never-force test watches the real older drain; the contract tests pin names, members
   and order against the document. The Developer went further and mutated the code itself, with the
   counts of what went red each time, and the tests that stayed green under each mutation are named
   and explained rather than hidden. I checked those explanations against the code and they hold.

## Findings

1. **The short second message does not tell a session it may leave questions for the owner, or say it
   is blocked - and it is the one message some sessions will ever see.**
   `src/CcDirector.ControlApi/Drain/DrainMessages.cs`, the `HandOverNow` method (around lines 180 to
   196; the block description is at lines 190 to 193).

   The message's own doc comment says why it names the path and the block again: "the session it
   reaches may never have had either from the Director: a session under a lead is asked by its lead,
   not by us." For exactly that session - one whose lead relayed a terse instruction, or none, and
   which is still mid-turn at two thirds and so is interrupted and receives only this message - the
   block description here is the whole description it will ever get. And it describes only `state:
   drained`, `restore`, and `why`. The `question:` line is absent, and the `blocked-reason:` line is
   absent, while the first message asks for both.

   What breaks, and for whom: a question that session would have left on the owner is never asked for
   and never reaches the record the owner reads after the restart - and the closing block exists, by
   its own doc comment, partly to carry exactly those questions, one of the four facts only the session
   can supply. And a session that cannot reach a clean stop in the time left is told to write `state:
   drained`, which the record would read as a clean handover from a session that had not finished. The
   parser accepts both lines if they are written; nothing tells this session they exist. One message
   edit closes it; the Developer may also answer that an interrupted session is out of time for
   questions, but that is an answer to give, not a gap to leave unremarked.

That is the only finding. Within the scope stated above - the types against the interface document,
the marks across builds, the two verbs on the real seam, the request texts, the harness change, and
the tests - nothing else was found that must change.
