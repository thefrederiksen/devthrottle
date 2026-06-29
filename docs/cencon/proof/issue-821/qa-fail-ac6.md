# QA verdict: FAIL (flow:qa-failed) - issue #821

QA Agent independent verification (fresh context, own isolated Director slot 15,
build from PR branch issue-821-address-by-number, commit c6def071).

## Summary

The CLI resolver change (AC1-AC5) is correctly implemented and proven. AC6
(move-session accepts a three-digit number as the session to move) is NOT met by
this PR. One acceptance criterion unmet = FAIL per the QA contract.

## Acceptance criteria

| # | Criterion | Result | Evidence |
|---|-----------|--------|----------|
| 1 | message send <number> hits exactly that session | MET (resolver) | resolve_target number-first; 14 cc_shared tests pass; local /sessions returns numbers 100/101/102; standalone fleet/sessions Map() includes Number |
| 2 | message ask <number> routes/answers from that session | MET (resolver) | same resolver path |
| 3 | session rename <number> renames that session, keeps number | MET (resolver) | rename uses resolve_target_or_current -> resolve_target |
| 4 | unused number -> standard "no session matches" error | MET | resolver number branch returns [] when no holder; _resolve_target prints "No session matches '<n>'" + Exit(1); covered by test_resolve_target_unused_number_no_match |
| 5 | id-prefix and name addressing still work (regression) | MET | number branch only triggers for 100-999 with a holder; falls through to full-id/prefix/name; regression tests pass (full id, unique prefix, ambiguous prefix, exact name) |
| 6 | move-session flow accepts a three-digit number as the session to move | NOT MET | see defect below |

cc_shared 14 passed, cc-devthrottle 51 passed. Director slot 15 built clean
(0 warnings, 0 errors). No C# changed by the PR.

## Defect (AC6) - reproducible

AC6 (issue #821 acceptance criteria) and the issue's Affected Containers both
require: "The move-session flow accepts a three-digit number as the session to
move and moves exactly that session." Issue Assumption 3 explicitly keeps
move-session / handover target resolution IN SCOPE for this issue (only the
Desktop/Cockpit jump box is deferred).

The PR does not satisfy AC6:

1. PR changed files (git diff --name-only main...HEAD): only
   tools/cc_shared/director.py, tools/cc-devthrottle/src/session_ops.py,
   tools/cc_shared/tests/test_director.py, and the proof artifacts. No
   move-session or handover code is touched. (No move-session code exists in this
   repo at all.)

2. The move-session skill is global, at
   C:\Users\soren\.claude\skills\move-session\SKILL.md (outside this repo). Its
   source-identification step (Step 3) resolves the session to move ONLY by:
     - "this session" / "move me" -> $CC_SESSION_ID
     - "slot N" -> the session at POSITION N in the listing (its own 1..N
       positional numbering; the file states "the API does not number slots")
     - a name -> match against name
   The skill has NO reference to the #820 three-digit session number (the strings
   "820", "three-digit", and "by its number" do not appear). It does not call the
   shared resolver and was not changed by this PR.

Repro: with the feature merged, invoke the move-session flow with a three-digit
session number as the target (e.g. "/move-session 412" or "move 412"). The skill
either treats "412" as positional slot 412 (which never exists for a normal
handful of sessions) or as a name, and never resolves the #820 number. There is
no code path by which move-session resolves a session by its three-digit number.

Expected: move-session resolves the session whose #820 number is 412 and moves
exactly that session.
Actual: move-session has no #820-number resolution; "412" is not understood as a
session number. AC6 is unmet.

## How to resolve (for Developer Agent / Product Agent)

Either (a) bring move-session / handover target resolution into scope and teach it
to resolve the #820 three-digit number (the number is available on the Director's
GET /sessions DTO as `number`), or (b) have the Product Agent formally de-scope
AC6 / move-session from issue #821 by amending the acceptance criteria. As
written, AC6 is an acceptance criterion that the delivered work does not meet.

## Note (not a PR defect, environmental)

When a Director is Gateway-connected, GET /fleet/sessions relays the Gateway's
aggregated list. Against the user's currently-running Gateway (port 7878) that
relayed list carried number=null for every session, so number addressing would
not resolve in that path. This is attributable to the separately-running Gateway
binary's version, not to this PR; the PR's own standalone Director serves
fleet/sessions with Number populated (verified). Flagged for awareness only.
