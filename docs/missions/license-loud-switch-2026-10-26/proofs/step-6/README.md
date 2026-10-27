# Step 6 proof: sessions stopped on the limit carry on after a switch

Mission "Licenses - Loud When Stranded", step 6. It was built only after the investigation of 26 October (kept in the
private record) proved what happened. Nine sessions on one machine stopped on the weekly limit. One of them, which
never signed in again, resumed on a single plain prompt once the machine's active sign-in had changed.

## What it does

After a VERIFIED license switch, the Director sends ONE plain `continue` to each of its Claude Code sessions whose
last turn ended on a usage-limit reply.

- **Verified** comes from the switch's own result. The outcome is `switched`, the read-back names the new account,
  AND a usage read with the new sign-in answered (`VerifiedByUsageRead`). A replayed answer to the same switch id is
  not a new switch. The check is `LimitStoppedSessionResumer.IsVerifiedSwitch`. It is hung on the `license-switch`
  verb (`LicenseSwitchExecutor.SwitchAndResumeAsync`), and it runs after the switch's answer, which does not wait
  for it.
- **The limit is read from the transcript**, never from the screen (`UsageLimitTranscript`). The last user or
  assistant record outside a side chain must be an assistant record with `isApiErrorMessage: true` and
  `apiError: "usage_limit_reached"`. Any other last record is not a stop, including a prompt typed after the limit
  reply.
- **Once.** When a session's `continue` is sent, the record id of its limit reply is stored (`LimitResumeStore`, in
  the license folder every Director on the machine shares). So a later switch, or a Director restart, sends nothing
  more for that reply. A send that was refused is not stored, so a later switch may try again.
- **Skipped, with the reason logged:**
  - a session running another agent;
  - a session that has named no transcript;
  - an unreadable transcript (logged loudly; nothing is guessed from the screen);
  - a session given any input after the limit reply (`Session.LastInputAtUtc`; the Director's own Escape to the
    limit dialog is not input);
  - any session the guarded send refuses: not waiting for a prompt, other input in flight, the owner's unsent text in
    the composer, or the usage-limit dialog still on the screen.
- Every session's verdict is one log line, `[LimitStoppedSessionResumer] switchId=..., session=...: RESUMED - ...`
  or `skipped - <why>`, followed by a count.

## The tests (all pass; output in this folder)

`new-tests-core.txt`: 25 tests in `src/CcDirector.Core.UnitTests/Licenses/`.

`UsageLimitTranscriptTests` (10) runs the recogniser on transcript tails shaped like the real 26 October records,
with every identifier replaced (`LimitTranscripts.cs`):
- a limit reply that ends the turn;
- a normal last turn;
- a prompt after the limit reply;
- another API error;
- a side-chain record after the limit reply;
- a half-written last line;
- a last record longer than the first part of the file read;
- a missing file, no conversation record, and a broken line, which are all unreadable.

`LimitStoppedSessionResumerTests` (15):
- a verified switch sends exactly one `continue` to each limited session and none to a normal one;
- a switch not verified by the usage read, a failed one, an already-active one and a replayed answer send nothing;
- a second verified switch, and a restart (a new store over the same folder), send nothing more;
- input after the limit reply means nothing is sent, while input only before it still lets the `continue` go;
- Codex and Gemini sessions are untouched;
- an unreadable transcript is skipped while the other sessions are resumed;
- a session with no transcript is skipped;
- a refused send is not recorded;
- the store's pruning.

`executor-tests.txt`: 12 tests in `src/CcDirector.Avalonia.Tests/Licenses/LicenseSwitchExecutorTests.cs`, 4 of them
new. After a verified switch the verb starts the resume once, with its result. It does not start it again for the
same switch id, after a refusal, or after an unreadable payload.

`revert-proofs.txt` shows that the tests see the rules:
- With both the recogniser's `usage_limit_reached` and the verified-switch gate broken, 11 of the 25 fail.
- With only the gate broken (`IsVerifiedSwitch` returns true), exactly the four tests for a switch that should not
  resume fail.

Both files were restored and the project was rebuilt afterwards.

`test-local.txt` is the gate at `101db5e2e`. Every suite completed except `cc-director-setup-engine.Tests`. Its ten
`MacAppNameAndPlaceTests` failures are red on main too (issue #4484). The coverage-gap line named the parked
`CcDirector.Gateway.Tests`, so that suite was run with `-Gateway`, and it was green.

## What is NOT proven here (owed to the Delivery Lead's QA)

**That a running Claude Code process picks up a sign-in written by the Director's switch.** The investigation saw a
running session resume after `/login`, in another session, rewrote the machine's sign-in. The Director's switch
rewrites the same default config folder through `cc-devthrottle license switch`. That path was not observed with a
running session.

Showing it in isolation needs two things: a Claude Code process signed in to an isolated config folder that holds
two real accounts, and a test Director switching between them. Every way to get a second real sign-in into an
isolated folder signs in one of the owner's real accounts, which the brief forbids. So it was not run, and nothing
here stands in for it.

**Sessions on another Director on the same machine.** The resume runs in the Director that carried out the switch
(the key holder), over its own sessions. If a machine runs a second Director, that Director's sessions move to the
new sign-in too, but the Director is not told, so its limit-stopped sessions are not prompted. On 26 October all the
stopped sessions were on one Director.
