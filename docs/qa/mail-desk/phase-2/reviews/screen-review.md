# Mail Desk phase 2: review of `checks/screen.py`, its tests and its contract

Reviewed: branch `origin/mail-desk-p2-screen` at commit `44a9af182`, as the change against `origin/mail-desk-phase-2`
(eight files, 2,018 lines added). Reviewer seat opened by the phase 2 Tech Lead (session 113). Date: 18 October 2026.

## Verdict

**Not yet.** Two findings must be answered before this is called done, and one small one should be. The request
guard holds: I could not get any request past it other than a GET to graph.microsoft.com and the two moves. Observe
mode moves nothing. The cursor and the batches are right in every case I could construct, the budget is measured in
UTF-8 bytes of the final context, every failure path leaves the memory byte for byte unchanged, the sign-in lookup is
phase 0's rule exactly, the repository stays clean of real addresses, and the 110 tests pass in 1.6 seconds with no
machine dependence. What is wrong is the one thing the tests never looked at: **what the Gateway actually keeps when
the check fails is not what the check thinks it said.** The mission's requirement that a failed sign-in "names the fix
so it reaches the Open problems card and the 08:00 email" is not met as the code stands, and one realistic failure
produces a Python traceback instead of a sentence.

## Scope

**What I read.** The whole of `screen.py` (617 lines), `CONTRACT.md`, and all six test files, from the diff. Mission
sections 5.4 (with its phase 0 findings), 5.5, 5.6, 5.7 and decisions D18 to D21 on `origin/main`; phase 0's `README.md`;
the Developer's brief; phase 1's contract, section 5. On `origin/main`, the code the check is judged against:
`src/CcDirector.Gateway/Factory/Triggers/TriggerCheckContract.cs` (how the Gateway reads a check's report and builds a
failure reason), `src/CcDirector.ControlApi/Triggers/DefinitionCheckRunner.cs` (how the Director runs the check and
captures its output), `tools/cc-outlook/src/auth.py` and `cli.py` (the token file rule and how `-a` resolves an
account). The installed sources of MSAL 1.39.0 (`authority.py`, `application.py`) and Python 3.11's
`urllib.request.HTTPRedirectHandler` and `http.client`.

**What I ran.** The test command from the brief: 110 passed in 1.58 seconds. Four probes of my own through the test
rig that capture standard error on a failure and apply the Gateway's `OneLine` rule to it. One run of the real
`screen.py` with the real MSAL, a scratch home and the sign-in service unreachable. One probe sending message ids with
control characters through the guard and then through the real `urllib`. One run of `uv run --script` on a script that
declares `msal`, to see what uv itself writes to standard error. An address sweep of the diff. A check of the case of
the token file names on devlinux (names only, nothing opened, nothing printed).

**What I could not reach.** No real mailbox and no real token: that Graph answers 404 to a move of an already-moved id
(the premise behind `already-gone`) is the fake's behaviour, not proven here. Windows and macOS paths. Phase 1's code:
`origin/mail-desk-phase-1` holds only its contract, so whether the Gateway lowercases a channel's address is unknown.
The real Director on devlinux capturing uv's standard error: by construction it must (the Director starts `uv` as the
process and keeps that process's error output), but I did not run a Director.

## Findings

### Finding 1 (high): the fix never reaches the Gateway, because the Gateway keeps the first 200 characters of ALL of standard error, and the check's log lines come first

**What the Gateway does.** `TriggerCheckContract.Read` on `origin/main`, for a non-zero exit, takes the WHOLE of the
check's standard error, flattens every line into one with spaces (`OneLine`), cuts it at 200 characters
(`QuotedOutputLimit = 200`) and stores `exit code 1: <that>` as the run's reason. That reason is `LastReason` on the
trigger, which phase 1's contract (section 5) quotes verbatim as the channel's `signedIn.failing` label, and which the
Open problems card and the 08:00 email show. The Gateway does not look for `check failed:`, does not take the last
line, and does not take the first line. It takes the first 200 characters of everything.

**What the check writes.** Before any failure, `run()` has already written `screen.py: N channel(s): <address>
(outlook, observe), ...` to standard error, and in live mode the filing and Sent-folder lines too. The failure sentence
comes after them, and the fix is the LAST thing in that sentence. For a missing sign-in the sentence itself is over 200
characters because it quotes the full search path.

**Reproduction** (test rig, `OneLine` applied as the Gateway's code does; the proof file is the Reviewer's probe, not
committed). What the Gateway would store, verbatim:

- Two channels, the second not signed in:
  `exit code 1: screen.py: 2 channel(s): email@devthrottle.com (outlook, observe), second@example.org (outlook, observe) check failed: second@example.org is not signed in on testmachine (looked for /tmp/pytest-of-sor...`
  **The fix is absent.**
- One channel, not signed in:
  `exit code 1: screen.py: 1 channel(s): email@devthrottle.com (outlook, observe) check failed: email@devthrottle.com is not signed in on testmachine (looked for /tmp/pytest-of-soren/pytest-55/test_probe_sign_in_miss...`
  **The fix is absent.**
- One channel, sign-in expired (the shortest sign-in failure there is):
  `exit code 1: screen.py: 1 channel(s): email@devthrottle.com (outlook, observe) check failed: the sign-in for email@devthrottle.com on testmachine has expired and could not be renewed (invalid_grant): run cc-outloo...`
  **Cut in the middle of the fix.**
- Live mode, one move done, the Sent folder refreshed, then the Inbox read answers HTTP 500:
  `exit code 1: screen.py: 1 channel(s): email@devthrottle.com (outlook, live) screen.py: email@devthrottle.com: filing 688663154ab283c8 archive moved screen.py: email@devthrottle.com: the Sent folder was read again:...`
  **The failure sentence is absent entirely.** The owner reads a log of what succeeded and nothing about what failed.

It is worse on a real Director: the process the Director runs is `uv run --script checks/screen.py` with no quiet
flag (`DefinitionCheckRunner.cs` line 334), and uv writes its own line to standard error first. On devlinux with uv
0.12.23 and a warm cache that line is `Installed 10 packages in 95ms` (29 characters), so the check has about 158
characters left after `exit code 1: ` and uv's line, before it has written anything itself. Its own channel line then
takes 64 of them for one channel and about 115 for two.

**Harm.** Mission 5.4: "A missing or expired sign-in is a failed check whose reason names the fix ... so it reaches the
Open problems card and the 08:00 email." It does not reach them. Phase 1's `signedIn.failing` label will show a
truncated path or a list of channels. The contract's section 1 ("The reason is on standard error, starting `check
failed: `") and section 7 ("On failure the last line is `check failed: <reason>`") describe the raw stream, which no
reader of the product ever sees. The tests pass because every one of them asserts on the raw standard error, not on
what the Gateway keeps: the proof covers the wrong reader.

**What the contract must say for phase 3**, whatever the Developer does about the shape of the stream: the Gateway
quotes the first 200 characters of standard error, flattened, after uv's own output; so the fix has to be inside that
window, and phase 3 cannot assume the reason starts with `check failed:`.

### Finding 2 (medium): a realistic failure is a Python traceback, not a sentence; the entry point catches only the two exceptions the check itself raises

**Mechanism.** `main()` catches `CheckFailed` and `RefusedRequest` only. Anything else escapes to the interpreter: exit
code 1, a traceback on standard error, no `check failed:` line. The memory IS left unchanged (the write comes after
`run()`), so the mission's "memory not kept" holds; only the reason is lost.

**The realistic path.** `msal.PublicClientApplication(...)` performs a network call in its constructor
(`Authority.__init__` calls `tenant_discovery`, a GET of the OpenID configuration at login.microsoftonline.com, every
time it is constructed; MSAL 1.39.0 source). When that call cannot be made, MSAL raises `requests.exceptions.ConnectionError`
(a `ProxyError` here), which nothing in `sign_in()` or `main()` catches. The same is true inside `acquire_token_silent`
when the cached token has expired and the renewal cannot reach the sign-in service: MSAL's
`_acquire_token_silent_from_cache_and_possibly_refresh_it` re-raises the transport exception when there is no cached
token to fall back on. So a Director whose machine is offline, or cannot reach login.microsoftonline.com, fails every
five minutes with a traceback as its reason. The tests cannot see this: the rig replaces `PublicClientApplication`
entirely.

**Reproduction.** The real `screen.py`, the real MSAL, a scratch home holding a sign-in file, the proxy variables
pointed at a closed local port so the sign-in service is unreachable:

```
exit=1
stdout: 0 bytes
stderr: 89 lines, beginning
  screen.py: 1 channel(s): email@devthrottle.com (outlook, observe)
  Traceback (most recent call last):
    File "/home/soren/.cache/uv/archive-v0/.../urllib3/connection.py", line 239, in _new_conn
  ...
  requests.exceptions.ProxyError: HTTPSConnectionPool(host='login.microsoftonline.com', port=443): Max retries exceeded with url: /common/v2.0/.well-known/openid-configuration ...
memory after: byte for byte what was written before
```

What the Gateway would store: `exit code 1: screen.py: 1 channel(s): email@devthrottle.com (outlook, observe) Traceback (most recent call last): File "/home/soren/.cache/uv/archive-v0/NFRLWjh4MxXBH5Yu/lib/python3.11/site-packages/...`.

**Other members of the same class**, each a traceback for the same reason: a `keys` entry without `reportedUtc` or
`id`, or `keys` or `sentCache` that is not an object (`read_memory` validates `cursor` and `filingQueue` but not these
two); a memory file that cannot be read (permissions, not UTF-8); and Finding 3 below.

**Harm.** Mission 5.5 point 6 and the contract's section 1 promise "the reason on standard error". For the one failure
an unattended machine is most likely to have, the reason is a file path inside uv's cache. The owner cannot act on it.

### Finding 3 (low): a message id holding a carriage return, line feed or tab passes the guard; the standard library, not the guard, stops it, and the whole run fails instead of that one item being refused

**Mechanism.** `check_request` splits the address with `urllib.parse.urlsplit`, which since Python 3.9.5 silently REMOVES
tab, carriage return and line feed from the whole address before parsing. The two patterns then see a clean path and
accept it. The guard's own test (`abc/def`, `abc.def`, `%2F`, `..`) never sends a control character.

**Reproduction** (in-process, real `urllib`, a closed local port so nothing is reached):

```
guard PASSED id 'abc\ndef'
guard PASSED id 'abc\tdef'
guard PASSED id 'abc\rdef'
transport raised http.client InvalidURL - URL can't contain control characters. '/v1.0/me/messages/abc\ndef/move' (found at least '\n')
```

`http.client` raises `InvalidURL` in `putrequest`, before any connection is opened, so **nothing reaches the network**
and the promise "the Mail Desk never sends" is kept. But `InvalidURL` is neither `URLError` nor `OSError`, so
`transport()` does not catch it, and it is not `RefusedRequest`, so `file_queue` does not turn it into a `refused`
result: it escapes as a traceback (Finding 2's class) and the WHOLE run fails, every five minutes, with the item still
queued, until the Gateway edits the memory.

**Harm.** The contract's section 5 says an id "holding anything but letters, digits, `-`, `_` and `=`" is refused by
the guard before the network. For these three characters that is not what happens. The precondition is a key-table id
the check did not write (Graph ids never hold control characters), so this needs a defect elsewhere to bite; but when
it does, it stalls all screening rather than one item. Low.

## Where I looked and found nothing

- **The guard, everything else.** Methods other than exactly `GET` and `POST` (including `post`); every POST path but
  the move; `beta`; another user's path; a second mailbox's path; a port, user information, a trailing dot or a
  different case in the host (the host is compared as an exact string, so all are refused); `http`; a fragment; `..`,
  `%` and `\` anywhere in the path; ids with `/`, `.`, `;`, `?`, `#` or a non-ASCII letter; a query on a move; a body that
  is not exactly one key `destinationId` with `archive` or `junkemail`, including a JSON string, a list, a different
  case, or an extra key; a GET with a body or outside `/v1.0/`. All refused before `transport()`. The Graph error body
  is never printed. `urlopen` appears once, inside `transport()`, which is called once, from `graph_request()`, after
  `check_request()`.
- **Redirects.** Python's `HTTPRedirectHandler` turns a 301, 302 or 303 answer to a POST into a GET with the body
  dropped, and raises `HTTPError` (a failed check) for a 307 or 308 to a POST. So no redirect can turn a request into a
  write. The Authorization header does follow a redirect to another host, but only one that graph.microsoft.com itself
  issues over verified TLS; that is Microsoft's server, not something the memory or a message can cause.
- **MSAL's own traffic** goes to login.microsoftonline.com for `https://graph.microsoft.com/Mail.ReadWrite` only
  (asserted by a test). It cannot send mail.
- **Observe mode.** `read_memory` accepts only the exact strings `observe` and `live`; filing runs only in the `elif`
  branch that `observe` cannot reach; the queue is written back only in that branch; `count` excludes the queue. No
  move can happen when the mode is not exactly `live`.
- **Cursor and batches.** Candidates across channels are sorted by receive time, then channel order, then page order;
  the cursor per channel is the newest reported time plus every id reported at exactly that time, merged with the
  held ids when the time did not move; `inbox_since` filters `ge` the time and drops the held ids. I could not
  construct a skip or a repeat: a cut inside one second, a failure after a move, a first run with more than 200 or
  older than 30 days, new mail arriving during a backlog, two channels sharing the ten, are all covered by the tests and
  by my reading. The budget is `utf8_size(build_context(sections))` on the context as it will be written, with `more`
  preset to `false` (the longer value) so the final string can only shrink; the Gateway measures the same thing
  (`UTF8.GetByteCount` of the parsed `context` string, `MaxContextBytes = 8192`). The output cap is checked in UTF-8
  bytes, which is never fewer than the UTF-16 units the Director cuts at.
- **Failure keeps memory unchanged.** The only write of `CC_CHECK_STATE_FILE` is after `run()` returns. Every
  `CheckFailed`, every `RefusedRequest` and every escaped exception leaves it untouched; the tests compare bytes.
- **Sign-in.** Home from `pwd.getpwuid(os.getuid()).pw_dir` on Linux and macOS; the base folder is the Director's
  per-user folder; the glob is `instances/*/config/outlook/tokens/<name>_msal.json` with cc-outlook's own naming rule
  (checked against `auth.py` line 355 by a test); none and more than one both fail, naming every path, never picking
  one; no fallback to `HOME` or `~/.cc-director` (a test plants both and proves neither is found). The renewed cache is
  written to the same file and made 0600; the token appears in no output, no log and no memory (tests assert it).
- **More than one channel, not Outlook-only.** `run()` names nothing of Graph, MSAL or cc-outlook (a test reads the
  source); readers are a table; an unknown provider fails before any sign-in or network call; keys name their channel.
- **The repository.** Every address in the diff is `email@devthrottle.com`, an `example.com` or `example.org` address,
  or a fake (`token-for-...`, `someone@graph.microsoft.com` as user information in an address test). The one client id
  is cc-outlook's own, already in the repository.
- **The tests themselves.** No sleep, no port, no real clock (`utc_now` is replaced), no real host name, no real home,
  no real network: nothing on the machine decides a verdict.

## Observations (no change required by this review; the Tech Lead decides)

1. **Address case.** The contract (3.2) requires the address "lower case, exactly as registered in cc-outlook". Those
   two can conflict: cc-outlook keys its profile and names its token file by the address AS TYPED (`save_profile`,
   `get_msal_token_path`), and `resolve_account` matches the address key exactly, case-sensitively (only the nickname
   is matched without regard to case). A channel registered in cc-outlook with a capital letter can therefore never be
   found by a lowercase address, and the fix `cc-outlook -a <address> auth` would fail too. On devlinux today both token
   file names are entirely lower case, so the two mailboxes in this mission are not affected. Phase 1's code is not on
   its branch yet, so I could not see whether the Gateway lowercases.
2. **One channel's failure stops every channel** (contract 6, last bullet). This follows from one check having one
   outcome and one memory, and it is documented; but it means the owner's second mailbox losing its sign-in stops the
   screening of email@devthrottle.com until someone acts. Worth a conscious decision before phase 6 runs two channels.
3. **A message that enters the Inbox with a receive time older than the cursor** (moved back from Junk, undeleted, or
   delivered late by the server) is never reported. This is inherent in the receive-time cursor the mission chose
   (the phase 0 pattern), not a defect of this change; phase 3 should know it when it reasons about "every email".
4. **`already-gone` rests on Graph answering 404 for a moved message's old id.** The fake Graph does; I could not
   confirm the real one does. If the real Graph kept the old id readable, a move retried after a failure would move the
   message a second time (a no-op into the same folder) rather than report `already-gone`. Harmless either way for
   Archive and Junk; worth one real observation in phase 6.
5. **The refused reason may quote a Graph id.** When an id holds a `/`, the move path does not match the pattern and
   the refusal text includes the whole path, id and all, in the context. The id came from the Gateway's own memory, so
   nothing new is disclosed; it only bends "the Gateway never handles a Graph id".
6. **The contract's "roughly 20 a run" correction** (section 2) is right and useful: the 10-message stop binds for
   both measured sizes. The test that proves it builds messages of exactly 446 and 680 bytes in the check's own format.

## For the record

- Test command and result: `uv run --no-project --with pytest --with "msal>=1.28" python -m pytest docs/qa/mail-desk/phase-2/tests -v`,
  110 passed in 1.58 seconds, on devlinux, Python 3.11.17, MSAL 1.39.0.
- The reason probes and the offline run are reproducible from the descriptions above with the test rig's `world`
  fixture; nothing of mine is committed.

---

## Re-review of d69dd70c8

Re-reviewed: branch `origin/mail-desk-p2-screen-fix` at commit `d69dd70c8`, against `44a9af182` (the reviewed commit)
and as the whole change against `origin/mail-desk-phase-2`. The answers are `screen-answers.md` beside this file.
Date: 18 October 2026.

### Verdict

**Accepted.** The harm in each of the three findings is gone, the tests now judge every failure by what the Gateway
keeps rather than by the raw stream, and the fixes introduced nothing that breaks a promise. Three small observations
on the new code are below; none needs a change before this is called done.

### Scope of the re-review

**What I read.** The new `screen.py` in full (769 lines), the whole diff of the contract and of every test file, the two
new test files, and the answers. On `origin/main`: cc-outlook's `auth.py` for the three premises the new nickname
lookup rests on (`PROFILES_FILE` is `<config>/outlook/profiles.json`, beside the tokens; `derive_nickname` is the label
before the top-level domain; `_build_nickname_map` lets the last account win). In MSAL 1.39.0: `get_accounts` compares
the username in lower case, which the case-insensitive file match relies on and the fake does not exercise.

**What I ran.**

- The suite at `d69dd70c8`: **208 passed in 5.4 seconds**.
- The same suite against the check as it stood at `200156d4d` (the pre-fix code copied in, then restored): **92 failed,
  116 passed**, as the answers claim. The new tests bite.
- My four reason probes from the first review, against the new code. What the Gateway would keep is now, for the two
  channel case: `exit code 1: second@example.org is not signed in on testmachine: first run cc-outlook accounts add
  second@example.org -c f575e3f4-8f62-4bfb-8f30-f776e55b51bd then run cc-outlook -a second@example.org auth; looked ...`
  (the probe registers no cc-outlook account, so the first step is shown; with one registered it names the nickname).
  The live-mode Graph failure keeps `exit code 1: Microsoft Graph answered HTTP 500 for email@devthrottle.com while
  reading the Inbox HTTP 500 to GET ...`: the failure sentence is first and whole.
- The real `screen.py` with the real MSAL and the sign-in service unreachable (the first review's offline run): exit 1,
  three lines of standard error, the first `could not reach the Microsoft sign-in service from devlinux for
  email@devthrottle.com: ProxyError`, the whole error on line 2, the run's log on line 3, the memory byte for byte
  unchanged. No traceback.
- uv 0.12.23 on a script whose environment is already built: it writes nothing to standard error, as the contract's
  section 1.1 now says. The first run after a change writes `Installed 10 packages in 95ms`.
- On devlinux, the fix's new input exists where it looks: one `profiles.json` under `instances/*/config/outlook/`.

**What I could not reach.** As before: no real mailbox or token, no Windows or macOS, no Director run, no phase 1 code.
The Gateway's `OneLine` is reproduced in Python in the test rig (`gateway_reason`), not run as C#; the reproduction
matches the C# line for line, and a sample test pins its behaviour.

### Finding 1 (high): answered, harm gone

The run's log is held in memory and written after the failure, so the sentence is line 1. Every sentence the check can
produce is listed in the contract (1.2), and the test `test_what_the_gateway_keeps.py` builds each of 12 failure kinds
with one and two channels in observe and live mode (48 cases), applies the Gateway's own rule after a stand-in uv
line, and asserts the kept reason starts with `exit code 1: <uv line> <the whole sentence>`, that the sentence is at
most 165 characters, that no traceback is in the window, and that the memory is unchanged. That is the right reader.
The failed-move sentence names the mailbox and the step, so a 152-character id can no longer push the fix out, and the
id is on no line (asserted). The machine is named without its domain. The contract's new section 1.1 tells phase 3
what the Gateway actually keeps, and that it must not assume or strip a prefix.

The one thing the rule does not do is enforce itself: `SENTENCE_LIMIT` is asserted by the tests on the addresses they
use (21 and 18 characters) and is not applied in the code. See observation A.

### Finding 2 (medium): answered, harm gone

The MSAL constructor, `get_accounts` and `acquire_token_silent` sit in one `try`. `OSError` covers every `requests`
exception (`RequestException` is an `IOError`), and `ValueError` and `RuntimeError` cover what `tenant_discovery`
raises on an error answer. `read_memory` now validates `keys`, `sentCache` and the cursor's ids, and a memory that
cannot be read or is not UTF-8 is a sentence. The catch-all in `main()` turns anything else into a sentence first, the
place it stopped second, the run's log, and the whole traceback last, and returns 1 before the memory write.
`test_no_traceback.py` raises the real `requests` classes from both MSAL calls and covers nine memory shapes, a
non-UTF-8 file, four message fields of the wrong kind, a page that is a list, and an unforeseen `KeyError`. My
offline run against the real MSAL confirms the realistic case end to end.

**On the catch-all in `main()`.** It is at the entry point, which is where the repository rule allows a catch
(rule 4), and it hides nothing: exit code 1, the reason named by kind and message, and the full traceback still in the
Director's record of the run (the Director keeps 16 KB of standard error). It does not catch `KeyboardInterrupt` or
`SystemExit`, which is right. The memory write and the output print are outside it, so a failure there is still an
exit 1 with the memory file as it was. I found no path through it that reports success.

### Finding 3 (low): answered, harm gone

`check_request` refuses any address holding a character at or below 0x20 or equal to 0x7F **before** `urlsplit`, so
the guard, not `http.client`, does the refusing, and `file_queue` turns it into a `refused` result for that one item.
The test sends seven such characters in a POST and a GET with the fake's log empty, and the per-item test moves the
good item while refusing the bad one in the same run. `transport()` no longer needs to know about `InvalidURL`.

### Observations 1 to 6 from the first review

- **Observation 1 (address case): accepted and changed.** The sign-in file is matched by its lower-cased name over
  `*_msal.json` in every instance; two files differing only in case are "more than once"; when the fix must name the
  address it names it as cc-outlook holds it, so `-a` finds it. The premises are read from cc-outlook's source by a
  test. MSAL's `get_accounts` matching the username without regard to case, which the fake does not exercise, I
  confirmed in MSAL 1.39.0's source. See observation B for one edge of the new match.
- **Observation 2 (one channel's failure stops every channel): declined by the Developer as not theirs to decide,
  documented.** That is the right answer for a Developer; the two-channel tests now show the failing mailbox is the one
  named. It remains the Tech Lead's call before phase 6 runs two channels.
- **Observations 3 and 4: documented in the contract (3.5 and 3.4).** Right.
- **Observation 5 (a refused reason quoting a Graph id): changed.** The refusal no longer quotes the path, and
  `shown_url` replaces the id with `{id}` on the detail line; asserted for a 152-character id.

### What the fix changed beyond the findings

The contract now uses phase 1's words: filing items are `{"messageKey", "decision": "archive" | "junk"}`, message
fields are `messageKey`, `receivedUtc`, `sender`, `senderName`, the channel row's `id` is copied into its section, and
the `nickname` field is gone from the channel row (the check reads cc-outlook's own account list instead). Each reader
maps a decision to its folder (`OUTLOOK_FOLDERS`); the guard still allows only `archive` and `junkemail` as Graph
destinations, and `file_queue` checks the decision before the lookup, so an unknown decision is `refused`, never a
`KeyError`. Phase 3 is not built yet, so nothing breaks; the renaming is an improvement, since phase 3 will read the
same names in two places.

### Observations on the new code (no change required)

- **A. The sentence limit is a test assertion, not a rule in the code.** The only sentence whose length depends on the
  address twice is the "account in no cc-outlook list" one: `<address> is not signed in on <machine>: first run
  cc-outlook accounts add <address> -c <36-character client id>`. With the longer of the two in-scope addresses (29
  characters, measured from its token file name on devlinux, not printed here) and the machine name `devlinux`, it is
  162 characters, inside the 165. With a 15-character machine name it would be 169, which still fits the 187 characters
  available when uv is silent, which the contract shows is the usual run. So the two mailboxes in this mission are
  inside the window in every case; an address of about 30 characters or more on a long-named machine is the first that
  is not, and only on a run where uv also writes. The contract's 1.1 says as much.
- **B. One misattributed sentence is possible inside the new `try`.** `not_signed_in()` is raised from inside the block
  that catches `OSError` (the "cache holds no account" path), and it calls `outlook_fix()`, which reads
  `profiles.json`. `outlook_fix` catches `ValueError`, `AttributeError`, `IndexError` and `TypeError` but not `OSError`,
  so a `profiles.json` that exists and cannot be read (a permissions fault) would be reported as `could not reach the
  Microsoft sign-in service ...: PermissionError`. Exit 1 and the untouched memory still hold; only the sentence is
  wrong, and the whole error is on line 2. It needs a file the same user owns to be unreadable, so I did not weigh it.
- **C. The case-insensitive match is sound.** It compares lower-cased names only, never opens a file it did not match,
  and on a case-insensitive file system (Windows, macOS) it changes nothing. The one collision it inherits is
  cc-outlook's own: the file-name rule maps both `@` and `.` to `_`, so `a.b@c.com` and `a@b.c.com` would share a file
  in cc-outlook itself. Not new, not this check's to fix.

### For the record

- Test command and result at `d69dd70c8`: `uv run --no-project --with pytest --with "msal>=1.28" python -m pytest
  docs/qa/mail-desk/phase-2/tests -v`, 208 passed in 5.42 seconds, on devlinux, Python 3.11.17, MSAL 1.39.0.
- The same suite against the pre-fix check (`200156d4d`): 92 failed, 116 passed. The worktree was restored to
  `d69dd70c8` afterwards and is clean apart from this appended section.
