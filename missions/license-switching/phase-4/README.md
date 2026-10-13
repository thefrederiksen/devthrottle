# Phase 4 - adding a spare by signing it in, and renewing sign-ins - live proof, 11 October 2026

`live_sign_in_proof.py` was run with the Windows machine as the target and with the Mac as the target. The
committed outputs are passing runs of the design in this pull request (see "Which code each output ran on"). The
script replaces account addresses with "account A" and "account B", and the test root, home folder, user, machine
and browser profile names with labels. It never prints the authorize code, and replaces every authorize URL (which
names the account in its login hint) with `<authorize URL>`.

| Output | Target (gains the sign-in) | Approval | Key kept in |
|---|---|---|---|
| `windows-2026-10-11.txt` | the Windows machine | the same machine, a Director browser profile | `.credentials.json` |
| `mac-2026-10-11.txt` | the Mac, over SSH, with `license sign-in --remote-approval` | the Windows machine, `license approve` | the Keychain |

## What each run shows

1. The isolated store starts empty.
2. A sign-in for account B is attempted while the browser profile is signed in to claude.ai as account A. The
   approval reads "Logged in as" on the consent page, sees account A, clicks nothing and refuses
   (`wrong-account`). The target keeps nothing.
3. A sign-in for account A adds it as a spare.
4. `license usage` reads the new spare with its own key, and reports `signedInAtUtc` (the moment the login
   finished) and `signInExpiresAtUtc` (from the new key).
5. Signing account A in again RENEWS the spare in place: same folder, a new sign-in time, a new end date.
6. The renewed spare reads; exactly one spare is held; the refused sign-in for account B left no folder behind.
7. Account A is switched in, so it is the ACTIVE account of the isolated default folder, and signing it in again
   renews the ACTIVE sign-in in place: the default folder, a new sign-in time, and it reads with `--active`.
8. The test root is deleted and checked: on Windows the folder is gone; on the Mac the Keychain item of the default
   folder and of every spare folder is gone too. The root is one the run created itself, exclusively, inside the
   parent folder given, marked with the run's id; cleanup deletes only a root carrying that marker.

## A sign-in is never moved

`claude auth login` runs straight into the folder the account belongs in - its spare folder for a new spare, the
folder already holding it for a renewal (the default folder for the active account) - and Claude Code's own single
write is the only write. There is no staging folder and no copy step, so there is no moment, however short, when
one sign-in is in two places (the review's first finding). The key store's lock is held only around that write,
after checking the folder still holds what it held when the login started; a switch in between stops the sign-in
with nothing written. Tests assert exactly-one-place in the state immediately after a simulated crash on either
side of the write, for a new spare, a spare renewal and an active renewal, with no recovery run first.

The active folder always holds a sign-in: Claude Code replaces the old key with the new one. A running session keeps
working - its old sign-in stays valid at the provider until its own end - and reads the new key before its next
call. Not provable in isolated folders: a real session that was already part way through renewing the old key at
that moment. Claude Code's own lock orders its writes, so whichever write lands last is in place; if that is the
session's, the old sign-in is still valid and the next daily renewal runs again.

## Which code each output ran on

- `windows-2026-10-11.txt`: the final code.
- `mac-2026-10-11.txt`: the final design, from the run before one last change to the browser step (focus
  emulation while approving, below). That change touches only the approving browser, which is on Windows in both
  runs.

Runs of the no-move design: Windows passed 5 of 5 complete runs; the Mac passed 1 of 2, the other losing one
Authorize click. After that, every approval was stopped by claude.ai itself (below), so the focus change has two
Windows runs and no Mac run behind it.

## Setup (not in the output files)

- **Isolated folders only.** An empty default config folder and a store under a fresh test root, on both
  machines. The real default Claude login was never read or touched.
- **The code under test came from this checkout.** On Windows, run from `tools/cc-devthrottle/src` in a virtual
  environment at the tool's floor versions (Typer 0.16.1, Click 8.2.1). On the Mac, the same source copied over and
  run with the installed tool's own Python. The Mac's Keychain was unlocked over SSH first (MISSION.md trap 3).
- **The browser profile is one of the Director's own** (`cc-devthrottle browser list`), started by the command when
  it was stopped and stopped again afterwards. No browser was launched by hand.

## Findings

- **claude.ai asked for a fresh login after about 30 automated approvals in one day.** From then on the authorize
  link led the profile to "Log in - Continue with your Claude.ai account to authenticate connections", although the
  same profile was still signed in to claude.ai itself. The tool reported it as it should (`browser-not-signed-in`,
  nothing approved). Signing a profile in is the owner's job, so the live runs stopped there. For unattended renewal
  this matters: a renewal can meet this login wall, and getting past it without a person needs the email-link login
  the prototype proved (`request_link.py` with the mailbox reader). That is not in this pull request.
- **A sign-in lives a little less than 30 days, and not always the same.** The sign-ins made today reported end
  dates 27.1 to 29.9 days after they were made. So nothing assumes 30 days: renewal is due when the end date the
  key itself reports is within five days (`RENEW_AHEAD`), and that date is what is reported.
- **Browser races, each seen live:**
  - a stopped profile left browser-harness's daemon behind, and the next approval failed with "Access is denied" on
    the daemon's port file - every approval now ends its own daemon (`browser-harness --reload`) first;
  - the Authorize button can render before the "Logged in as" line - the page is now judged only once both are
    there;
  - the Authorize click was sometimes lost and the consent page stayed up. The button is now scrolled into view
    and its position read just before each click, it is clicked again (at most three times) while it still shows,
    and focus is emulated while approving, because the tab is driven in the background and a page can ignore input
    it thinks has no focus. Even after the scrolling and the re-clicks, one click in eight was still lost on the
    Mac run; whether focus emulation cures it is not yet proven (see above). When it happens, the error now names
    the clicks made, the page's visibility and focus, its address and its first words.
- **The Mac's shell is zsh, which aborts on a file pattern that matches nothing**, so the first cleanup failed on a
  run that left no spare. The cleanup now lists folders with `find`.

## What the first pull request did not do

- **The Director did not run `license renew` on a schedule** and did not report a failed renewal; **two machines
  were joined by hand** (`license sign-in --remote-approval` and `license approve`, joined over SSH in this proof);
  **there was no Cockpit button.** The messages for all three are agreed in `../contract.md` section 8. The second
  pull request, below, builds the Director's part of the first two.
- **A claude.ai login wall is not passed automatically** (the finding above). The approval refuses
  (`browser-not-signed-in`) and says to sign the profile in by hand.

# The second pull request - the Director renews, reports a failure, and does its half of a two-machine sign-in

12 October 2026. No live sign-in was made for this pull request: the owner stopped live runs after claude.ai asked
for a fresh login (the first finding above). What it has instead is `director-proof/`, described below, and it
says plainly what that does not reach.

## What the Director now does

- **Renews once a day and at start**, while it is the key holder and license switching is on. It asks the tool
  what is due (`license renew --check`), then signs each due account in again in place.
- **Reports a failed renewal** in the usage report it already sends: when, one of eight reason words, and one
  sentence the Director writes itself. The next renewal that works, or finds the account no longer due, clears it.
- **Signs an account in when asked** (`license-sign-in`), with the approval on this machine when a browser profile
  is set for the account here, and otherwise by asking the Gateway for one (`RequestLicenseApproval`) and waiting
  for `license-approval-result`.
- **Approves for another machine when asked** (`license-approve`): it runs `license approve` in its own browser
  profile and seals the answer to the asking machine's key before the answer leaves the process.

Two small tool changes carry this, both in `tools/`: `cc-secrets seal` / `open` (a value sealed to one machine),
and in `cc-devthrottle license` the `renew --check` and `sign-in --ask-when-no-browser` options, `get-browser`,
and JSON answers for a failure. Section 8 of the contract says why the renewal asks first.

## The proof: `director-proof/`

`director-proof-2026-10-13.txt` is the output of one run, unedited. Two Directors' license parts run in one
process on one machine - the target, and the approving machine - and every step goes through the code the product
runs. Run it with:

    dotnet run --project missions/license-switching/phase-4/director-proof/DirectorProof.csproj -- --root <a folder that does not exist yet> --python <python.exe> --repo <this checkout>

| Real in the proof | Stood in for |
|---|---|
| `LicenseSwitching` twice: the key holder lock, the reporter, the renewer, the sign-in coordinator, the approver, the stored results | The Gateway: an in-process relay that passes the three messages as JSON text and keeps what it carried, so the proof can read it |
| `cc-devthrottle license` from this checkout, as a process, for the target: `renew --check`, `sign-in --ask-when-no-browser`, `usage`, the store and its lock | `claude`: `fake_claude.py` prints an authorize URL naming the account, reads one line, and writes a made-up sign-in only for the one right answer. It contacts nothing |
| `cc-secrets seal` and `open` from this checkout, as processes, with a separate key folder for each machine | The approving machine's browser: its `license approve` answers a fixed made-up answer, or refuses with `wrong-account` |
| The Director's log of the run, which the proof then reads | The clock moving a day forward, and the ten-minute deadline arriving |

What the run shows, in its own steps: both Directors report, the approving one with no holdings; a sign-in with no
profile here is approved on the other machine and held as a spare; the same `SignInId` again runs nothing; while
no Gateway relays, a due account with no profile here is recorded `no-approver` and NO sign-in is started, for the
daily renewal and for a sign-in that was asked for; a refused renewal is reported with its reason and leaves the
held sign-in as it was; the next day's renewal renews it in place and clears the failure; a sign-in nobody answers
ends at its deadline with nothing left behind; the answer is in no log line, no stored result and nothing the
relay carried; and no call in the run filed a tool error report.

That last one is a count, not a remark: step 8 counts the report files in the tool's error outbox (0), then starts
on purpose a sign-in the Director never makes - no profile, told not to ask - and counts again (1), so the count is
shown to see a report when there is one.

Two findings changed the design. An earlier run of this proof, before `renew --check`, sent three scrubbed error
reports to the hosted Gateway, because that design made calls that were expected to fail; the proof now points the
tool's error reports at a closed local address so a run can never send one. And the review of this pull request
found the case that still filed one a day - no relay, no profile - which is step 4 of the proof now.

## What the second pull request does not prove, and does not do

- **Nothing here ran against a real Gateway.** `GatewayLicenseApprovalRelay` (the hub call) and the three command
  executors are covered by unit tests against a hand-built command, not by a Gateway sending one. The Gateway side
  (the relay, the owner's approver setting, the email) is phase 2's and is not built.
- **`CcSecretsLicenseApprovalSeal` - the Director starting `cc-secrets` as a process - never started the real
  tool.** Its unit tests give it a stand-in process and check the arguments, that the answer travels on standard
  input, and what it refuses. The proof starts the real `seal` and `open` with the same arguments, but through its
  own small runner.
- **No real `claude auth login`, no real browser approval, and no Mac** were part of this pull request. The first
  pull request's live runs cover those for the tool; the Keychain path of a Director-run sign-in on the Mac is
  unproven.
- **A claude.ai login wall is still not passed automatically.** A renewal that meets one is reported as
  `browser-not-signed-in`, on the page and by email once phase 2 sends it.
- **There is no Cockpit button** (phase 3).
