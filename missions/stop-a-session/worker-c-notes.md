# Worker C - the two commands. What was built, what was proved, and what was not.

Phase A, item 7 of the seven, and only item 7. Nothing outside
`tools/cc-devthrottle/src/session_ops.py`, `tools/cc-devthrottle/src/cli.py` and
`tools/cc-devthrottle/tests/` was touched.

Nothing is committed, nothing is pushed. Three files are sitting in the worktree for the Manager:

    M  tools/cc-devthrottle/src/session_ops.py
    M  tools/cc-devthrottle/src/cli.py
    ?? tools/cc-devthrottle/tests/test_session_stop.py

---

## What was built

### `cc-devthrottle session stop <session> --reason "why"` (`-r` as well)

`stop_session` in `session_ops.py`, wired as `session stop` in `cli.py`. It calls
`POST /sessions/{sid}/stop` with `{"reason": "..."}` and then prints the Gateway's `headline`
followed by each line of `details`, in the order given, and nothing else. It composes no sentence,
decides nothing about what a verdict means, and re-words nothing. Ruling 5.

It was built against the outcome contract in `missions/stop-a-session/handoff-phase-a.md`, not
against a running Gateway - Worker B was building the route at the same time.

**Exit 0 for all three verdicts** - `stopped`, `alreadyStopped`, `notOnFleet`.

**Non-zero for three things, each saying which one it was:**

1. no reason was given - refused here, before the call, and the message names the reason as what is
   missing and shows the flag;
2. the Director could not be reached - the shared client's own sentence, which names the address, or
   names the MACHINE when the Gateway stamped the fault as a Director-side one;
3. the process would not die - the Gateway's sentence, printed as written.

### The `notOnFleet` trap - handled as the brief describes

`_stop_target` deliberately does NOT go through `_resolve_target`. That resolver prints
"No session matches" and exits 1 when the roster has nothing, which would turn the not-on-this-fleet
case into a client-side error and mean the Gateway's careful 200 was never asked for, let alone read.
So an unmatched target goes out to `POST /sessions/{target}/stop` exactly as it was typed and the
GATEWAY rules on it.

An AMBIGUOUS target stays an error, unchanged. The ambiguity refusal was lifted out of
`_resolve_target` into `_refuse_ambiguous_target` so both callers refuse in the same words rather
than growing a second wording.

### `cc-devthrottle session done --undo [<session>]`

`undo_done` in `session_ops.py`, wired as the `--undo` flag on the existing `session done`. Calls
`DELETE /sessions/{sid}/request-deletion`, defaults to this session, and asks for no reason.

**`--undo` with `--reason` is refused, not silently dropped.** The decision, and why: dropping the
reason lets the caller walk away believing something was recorded that never was; recording it would
attach a justification to the one session operation that needs none, and put a sentence in the trail
for an act that is not an intervention. So the two are declared incompatible and the caller chooses.
Written into the code at the refusal.

---

## Two things I added that the brief did not name. Say if either is unwanted.

1. **Action catalogue entries `session-stop` and `session-done-undo` in `cli.py`.** Agents find verbs
   by `cc-devthrottle actions --json`, and this repository's own test file says a verb missing from
   the catalogue does not exist as far as the fleet is concerned. The `session-stop` description
   states the two things an agent must learn without running it: the reason is required, and stopping
   something already stopped succeeds.

   **A pre-existing gap I did NOT close:** `session done` itself has no catalogue entry and never
   had one, so `session-done-undo` now appears without its counterpart. Closing that means touching
   an existing command's discoverability, which is outside item 7. Flagging it rather than doing it.

2. **An answer with no `headline` is reported as "No answer" and exits non-zero.** This is a broken
   instrument, not a fourth verdict - it means the Gateway did not understand the request. Exiting 0
   and printing nothing would be exactly the button that accepts a click and says nothing, which is
   what this mission exists to remove. The sentence says we do not KNOW whether it stopped, rather
   than naming an outcome nobody was told. If the Manager reads "non-zero for exactly three things"
   strictly, this is a fourth - so it is called out here rather than buried.

---

## Tests, and every one of them watched failing on purpose

`tools/cc-devthrottle/tests/test_session_stop.py` - 20 tests, all passing.

**Exact command run, exact result:**

    C:\ReposFred\devthrottle-stop-a-session\tools\cc-devthrottle> python -m pytest tests -q
    2 failed, 261 passed, 2 warnings in 9.53s

    C:\ReposFred\devthrottle-stop-a-session\tools> python -m pytest cc_shared/tests -q
    1 failed, 126 passed in 4.56s

**The three failures are pre-existing and none of them is mine.** Proved rather than asserted: the
tree at `HEAD` was extracted with `git archive HEAD tools` into a scratch directory and the same
tests were run there, before any of my changes existed. They fail identically.

  * `test_email_cli.py::test_owner_has_no_recipient_option` and
    `test_spawn_ops.py::test_type_option_is_removed` both render `--help`, and help rendering is
    broken across the WHOLE tool in this environment: every `--help` raises
    `TypeError: TyperArgument.make_metavar() takes 1 positional argument but 2 were given` out of the
    installed typer and click pair. `session hold --help`, written long before this mission, fails
    the same way.
  * `cc_shared/tests/test_lazy_imports.py` fails on a missing module. `cc_shared` was not touched.

**`.\scripts\test-local.ps1` was NOT run and says nothing about this work** - it runs no Python tests
at all. The two commands above are the only runs that cover it.

### The mutations, and what the red said

Each change was reverted in turn, the suite run, and the file restored. All sixteen were caught, and
in every case the test that claims to catch the defect is the one that went red.

| Reverted | Went red |
|---|---|
| route the stop through `_resolve_target`, the way every other verb does | the raw-target test |
| drop the client-side refusal of a missing reason | both reason tests |
| accept a whitespace-only reason as a reason | the blank-reason test |
| swallow the server's sentence and write our own instead | the 400, the unreachable, and the would-not-die tests |
| stop adding the flag hint after the Gateway's own refusal | the 400 test |
| make anything but `stopped` an error, the way it works today | already-stopped, not-on-fleet, and raw-target |
| print the headline and none of the detail lines | the stopped test and the order test |
| re-order the detail lines instead of printing them as given | the order test |
| treat an answer with no headline as a silent success | the no-answer test |
| guess the first match instead of refusing an ambiguous target | the ambiguity test |
| make `--undo` flag the session instead of clearing the flag | all three undo tests |
| silently drop a reason passed with `--undo` | the undo-with-reason test |
| leave `--undo` unwired | all four undo tests |
| leave the two verbs out of the action catalogue | the discovery test |
| drop the `-r` short form | the declaration test |
| let the target default to THIS session instead of being required | the declaration test |

Two verbatim reds, for the two that matter most:

    # routed through _resolve_target
    >       assert calls, "the stop was never sent - the client ruled on the target itself"
    E       AssertionError: the stop was never sent - the client ruled on the target itself
    E       assert []

    # anything but 'stopped' made an error
    >       assert result.exit_code == 0
    E       assert 1 == 0
    E        +  where 1 = <Result SystemExit(1)>.exit_code

---

## What is NOT proved. Read this before anyone calls item 7 done.

Named as gaps in the test file itself as well, so a later reader does not have to find this note.

1. **Every Gateway answer in these tests is a STUB written from the outcome contract.** Nothing here
   proves the route actually answers 200 with `verdict: notOnFleet` for an unknown id, that it
   refuses a missing reason with 400, or that its headline reads as the contract says. If Worker B's
   route drifts from the contract, this suite stays green and the command is wrong. **Only an
   end-to-end run against a real Gateway catches that, and I did not do one** - the route did not
   exist while I was writing. Somebody must stop a real session with this command before the phase
   is reported.
2. **The audit record is not proved from here.** Nothing observable at the command line shows the
   stop reached `GovernanceAuditLog`, and asserting on this client's own request body would be
   proving the wrong thing.
3. **The client cannot itself tell "the Director could not be reached" from "the process would not
   die".** The shared client raises one exception type and does not carry the status code, so the
   distinction is carried entirely by the SENTENCE the server sends. The tests prove each sentence
   survives intact and exits non-zero; they do not prove - and cannot prove - that this client could
   tell the two apart on its own. The same limitation is why the flag hint after a 400 is decided by
   looking for the word "reason" in the server's text. A sentence that does not mention a reason
   simply gets no hint, which is the harmless direction to be wrong in. Fixing this properly means
   putting the status code on `GatewayError` in `tools/cc_shared/gateway.py`, which is outside the
   files I was told to touch.
4. **Nothing proves the help text reads well**, because `--help` cannot render in this environment at
   all (see above). The flag declarations are asserted directly instead.
5. **`session stop` has no `--json` output.** Not asked for, not built. An agent parsing the answer
   today reads Rich-rendered text that wraps to the console width.
