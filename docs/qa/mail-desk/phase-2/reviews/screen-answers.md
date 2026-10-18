# Mail Desk phase 2: answers to the review of `checks/screen.py`

The review is `screen-review.md` beside this file (a Reviewer running a different agent from the Developer, against commit `44a9af182`). These answers are
the Developer's, made on branch `mail-desk-p2-screen-fix`, cut from `mail-desk-p2-screen` at `200156d4d`. That commit
came after the reviewed one and before the review was read. Every answer below is proved by the commit that adds this
file and by the tests it names.

Tests: `uv run --no-project --with pytest --with "msal>=1.28" python -m pytest docs/qa/mail-desk/phase-2/tests -v`
gives **208 passed** (110 at the review). Run against `screen.py` as it stood at `200156d4d`, the same suite gives
**92 failed, 116 passed**. Every one of the 92 is a test added or changed for a finding or an observation below, so
each fails without its fix.

| Item | Answer |
|---|---|
| Finding 1 (high) | Accepted |
| Finding 2 (medium) | Accepted |
| Finding 3 (low) | Accepted |
| Observation 1 | Accepted: changed |
| Observation 2 | Declined here: the Tech Lead's decision. Documented. |
| Observation 3 | Accepted: documented. No code change. |
| Observation 4 | Accepted: documented for phase 6. No code change. |
| Observation 5 | Accepted: changed |
| Observation 6 | Noted. No change. |

## Finding 1: the fix never reaches the Gateway

**Accepted.** The review is right: the Gateway keeps `exit code 1: ` plus the first 200 characters of ALL of standard
error, flattened, and uv's line can come first. I read `TriggerCheckContract.Read` and `OneLine` on `origin/main`, and
`DefinitionCheckRunner.CheckLine`, which runs `uv run --script` with no quiet flag.

**What `200156d4d` already covered.** It buffered the run's log so that the failure's sentence is line 1, and it put
the search path on line 2. Judged by the Gateway's rule with that commit's code, the missing and the expired sign-in,
and the Graph failures, already fit. It did not cover:

- the account unknown to cc-outlook: its sentence was about 190 characters, so the fix was cut;
- more than one sign-in: about 180 characters;
- the sign-in service out of reach: a traceback (Finding 2);
- a failed move: its sentence quoted the whole Graph address, so a real 152-character id pushed the fix out of the
  window and put the id into the reason;
- two channels: a Graph failure did not say which mailbox failed;
- a machine name with a domain, which used up the window.

**What changed** (`screen.py`):

- Every sentence is at most `SENTENCE_LIMIT` = 200 - 35 = 165 characters, leaving room for a uv line of up to 34.
- The machine is named without its domain (`machine()`).
- A Graph failure names the mailbox and the step (`while reading the Inbox`, `while moving a message to archive`). The
  address it called goes on line 2, with any message id replaced by `{id}`. The reader's `graph()` makes every Graph
  sentence in one place.
- The account unknown to cc-outlook: the sentence names the first step only (`first run cc-outlook accounts add
  <address> -c <client id>`), and line 2 names the next one. I read cc-outlook's `accounts_add`: `--client-id` is
  required, so it cannot be left out, and both commands plus the client id do not fit in 165 characters. Once the
  account is added, the next run's failure is the ordinary "not signed in", naming `cc-outlook -a <nickname> auth`.
  Each run names the one next step, whole.
- More than one sign-in: shortened to `<address> is signed in more than once on <machine>: keep one and sign out of
  the others with cc-outlook`.
- The memory over its limit now ends with its fix: `give this factory fewer channels`.

**What I measured, not assumed:** uv 0.12.23 on devlinux writes **nothing** when the script's environment is already
built, which is the usual run. It writes `Installed 10 packages in 95ms` (29 characters) when it builds the
environment from a warm cache. It writes 85 characters on a machine whose package cache is empty. The rule covers the
first two. In the third case a long sentence can be cut on that first run only: the failure repeats five minutes later
with uv silent. `CONTRACT.md` section 1.1 says so.

**Contract.** New section 1.1, "What the Gateway keeps of a failure: the reason window". It covers the Gateway's rule,
that phase 3 must not assume or strip a prefix, uv's measured output, the 165-character rule and what the rule does not
cover. New section 1.2 lists every failure sentence. Section 6's table is updated.

**Proof.** `test_what_the_gateway_keeps.py`:

- `test_Failure_TheReasonTheGatewayKeeps_HoldsTheWholeSentenceAfterUvsLine` covers 12 failure kinds × one and two
  channels × observe and live, 48 cases. The kinds are: missing, unknown to cc-outlook, more than one, expired, sign-in
  service out of reach, Graph HTTP error, Graph out of reach, a page without values, not JSON, an unusable message,
  unknown provider, memory too big.
  - Each case applies the Gateway's rule (`gateway_reason` in `conftest.py`: flatten as `OneLine` does, cut at 200,
    after the stand-in uv line) and asserts that the kept reason starts with `exit code 1: <uv line> <the whole
    sentence>`.
  - Each case also asserts that the sentence holds its fix or its cause, that it is at most 165 characters, that no
    traceback is in the window, and that the memory is unchanged byte for byte.
  - With two channels, the second fails, so the first has run in full. In live mode with two channels, the test
    asserts that the healthy channel's move really ran before the failure.
  - The machine is `DESKTOP-ABCD123.corp.example.com`, the longest name Windows gives, plus a domain.
- `test_MoveFailing_WithARealLengthGraphId_KeepsTheWholeSentence` covers a 152-character id.
- `test_TheRuleOnTheWindow_IsTheGatewaysOwn` and `test_SentenceLimit_LeavesRoomForUvsLineInsideTheGatewaysWindow` cover
  the helper and the arithmetic.

With only the window assertion kept, 12 of the 48 cases fail at `200156d4d`: more than one, unknown to cc-outlook, and
the sign-in service out of reach, in all four combinations. With every assertion, all 48 fail there. The
real-length-id test fails there too.

## Finding 2: a realistic failure is a traceback

**Accepted.** Every failure is now one sentence that names what failed, and the memory is left unchanged:

- **The sign-in service.** The MSAL constructor and `acquire_token_silent` are both network calls, and both now sit
  inside one `try`.
  - `OSError`, which covers every `requests` exception (`RequestException` is an `IOError`), becomes `could not reach
    the Microsoft sign-in service from <machine> for <address>: <error kind>`.
  - `ValueError` and `RuntimeError`, which MSAL raises when the authority's configuration answers with an error
    (`authority.py`, `tenant_discovery`), become `the Microsoft sign-in service failed on <machine> for <address>:
    <message>`.
  - The whole error is on line 2, so the cause is not hidden. The test rig's guard forbids `import requests` in the
    check, so I relied on the class hierarchy rather than import it.
- **The memory.** `read_memory` now also validates:
  - `keys`: an object of `{id: text, reportedUtc: text}`;
  - `sentCache`: `{refreshedUtc: text, hashes: [text]}`;
  - `cursor.idsAtNewest` entries: text.

  A file that cannot be read or is not UTF-8 is a sentence too.
- **Graph's messages.** `read_message` refuses a message whose id, time, sender, name, subject or preview is not text,
  instead of failing later in `clean_text`. A page that is not an object is a sentence.
- **Anything unforeseen.** `main()`, the entry point, catches everything else. Line 1 is `screen.py stopped on an
  error it does not handle: <kind>: <message>`, line 2 is `in <function>, screen.py line <n>`, and the whole traceback
  comes last, after the run's log. The memory is not written.

**Proof.** `test_no_traceback.py`, 25 tests:

- the sign-in service out of reach, in setup and in renewal, for `ConnectionError`, `ProxyError`, `Timeout` and
  `OSError`. These are the real `requests` classes, the same kind of exception the review's offline run raised;
- the service answering an error;
- nine memory shapes the check did not write;
- a memory that is not UTF-8;
- four message fields of the wrong kind;
- a page that is a list;
- an unforeseen `KeyError`.

Each asserts one sentence first, no traceback in what the Gateway keeps, and the memory unchanged. All fail at
`200156d4d`.

**What this does not cover:** I did not repeat the review's real run against the real MSAL with the proxy pointed at a
closed port. The rig replaces `PublicClientApplication`, and the tests raise the exception classes that MSAL's
transport raises. The claim that MSAL raises them from those two calls rests on the review's run and on MSAL 1.39.0's
source, not on a run of mine.

## Finding 3: control characters pass the guard

**Accepted.** `check_request` now refuses any address that holds a space or a control character (code points up to
0x20, and 0x7F) **before** `urlsplit` sees it. Python's `urlsplit` silently drops tab, carriage return and line feed.
Because it is the guard that refuses, `file_queue` reports that one item as `refused` and files the rest, as the
contract promises. `transport()` no longer needs to know about `http.client.InvalidURL`.

**Proof.** `test_request_guard.py`:

- `test_Guard_AnIdHoldingAControlCharacter_IsRefusedByTheGuardItself` covers line feed, carriage return, tab, space,
  null, unit separator and delete, in a POST and a GET, with the fake Graph's log empty.
- `test_FilingQueue_AnIdWithALineFeed_IsRefusedForThatItemOnly`: the bad id is refused, the good one is moved, the run
  succeeds, and the queue is emptied.

Both fail at `200156d4d`.

## Observation 1: address case

**Accepted, and changed.** I read cc-outlook's source on `origin/main` (`tools/cc-outlook/src/auth.py`):

- `save_profile` keeps the account under the address as typed: `profiles['profiles'][email]`.
- `get_msal_token_path(email)` names the file from it, with case.
- `resolve_account` matches the address key exactly, with case: `if account_name in profiles.get('profiles', {})`.
- Only nicknames are matched without case: `account_name.lower()`. They are stored in lower case.
- MSAL's `get_accounts(username=...)` compares without case (MSAL 1.39.0 `application.py`).

**Decision:** the check keeps reading the channel's address in lower case. The Gateway's row may hold it either way,
and two channels differing only in case are still one mailbox.

- It finds the sign-in file **without regard to case**, matching every `*_msal.json` in the token folders by its
  lower-cased name. Two files that differ only in case are "more than once".
- When the fix must name the address rather than a nickname, it names it **as cc-outlook holds it**, so `-a` finds it.
- `CONTRACT.md` 3.2 and section 6 say so.

**Proof.** `test_sign_in_and_failures.py`:

- `test_SignIn_ATokenFileNamedWithCapitals_IsFoundForTheLowerCaseChannel` (fails at `200156d4d`);
- `test_SignIn_TwoFilesDifferingOnlyInCase_AreMoreThanOne` (fails there);
- `test_SignIn_AnAccountRegisteredWithCapitals_IsNamedAsCcOutlookHoldsIt` (fails there);
- `test_SignIn_ANicknameLeadingToAnAccountRegisteredWithCapitals_IsUsed`;
- `test_CcOutlook_MatchesTheAddressWithCaseAndNamesTheFileAsTyped`, which reads the three source lines the premise
  rests on, so a change in cc-outlook turns it red.

On Windows and macOS, whose file systems ignore case by default, this changes nothing. On Linux it is the difference
between found and not found.

## Observation 2: one channel's failure stops every channel

**Declined here, as a change I make on my own.** The review says so itself: it is a design decision. One check has
one outcome and one memory, and writing the memory for some channels while failing the run would break the rule that
a failed run leaves the memory untouched. Another design is possible: a failing channel reported inside a successful
run's context, its cursor not moved, with the Gateway raising it as a problem. That changes phase 1's and phase 3's
contracts, so it is the Tech Lead's call before phase 6 runs two channels.

It is documented in `CONTRACT.md` section 6, last bullet. The two-channel cases in `test_what_the_gateway_keeps.py`
show that the failing channel is the one named, so the owner knows which mailbox to fix.

## Observation 3: mail older than the cursor is never reported

**Accepted: documented.** This comes with the receive-time cursor the mission chose, not from a defect.
`CONTRACT.md` 3.5 now says that a message entering the Inbox with an older receive time (moved back, restored,
delivered late) is never reported, and that phase 3 must not promise otherwise. No code change.

## Observation 4: `already-gone` rests on the fake's 404

**Accepted: documented.** `CONTRACT.md` 3.4 now marks this as not yet observed on a real mailbox, for phase 6 to see
once, and says why it would be harmless if the real Graph differs. No code change: neither the review nor I can reach
a real mailbox here.

## Observation 5: a refused reason may quote a Graph id

**Accepted, and changed.** A refused POST no longer quotes its path: `refused: a POST that is not the move of one
message`. A failed move's sentence no longer holds the address it called, and the detail line shows the message id as
`{id}` (`shown_url`). So no Graph id reaches the context, the reason or the log through a failure.

**Proof.**

- `test_request_guard.py::test_FilingQueue_ARefusedMove_DoesNotQuoteTheGraphId` asserts the id is in neither the
  context nor standard error.
- `test_sign_in_and_failures.py::test_Graph_AMoveAnswering500_IsAFailedCheck` asserts the id is on no line.
- `test_MoveFailing_WithARealLengthGraphId_KeepsTheWholeSentence` asserts the same for a 152-character id.

## Observation 6: "roughly 20 a run"

**Noted. No change.** The review agrees with the contract.

## Re-review of d69dd70c8: observations A, B and C

The re-review is the last section of `screen-review.md` beside this file. Its verdict is **accepted**. The answers are
made in the commit that adds this section. The suite gives **210 passed**.

### A. The sentence limit is a test assertion, not a rule in the code

**Declined, with the reason.** The re-review measured the longest sentence for this mission's two mailboxes at 162
characters on devlinux, inside the 165. A longer address on a long-named machine goes over only on a run where uv also
writes, which happens once after the environment is built, and the next run's reason is whole. Enforcing the limit in
the code could only cut or reword a sentence. Cutting is what the Gateway already does, and rewording to fit is a
fallback that hides the real text. `CONTRACT.md` 1.1 says what the rule does not cover. If a mailbox with a long
address joins, the fix is to shorten that sentence, and the window tests will show which one.

### B. An unreadable profiles.json was reported as the sign-in service out of reach

**Accepted, and changed.** The re-review is right. The "holds no account" failure was raised inside the `try` that
catches `OSError`, and building its fix reads `profiles.json`. Two changes:

- **Moved.** The "holds no account" failure is now raised after that `try`, so nothing but MSAL's own calls can be
  read as the sign-in service out of reach.
- **Named.** `outlook_fix` no longer lets an unreadable account list pass unnamed. It fails with `<address> is not
  signed in on <machine>, and cc-outlook's account list cannot be read: <error kind>`, with the file and the whole
  error on line 2. Before, the same fault on the no-sign-in path ended up in `main()`'s catch-all. Guessing past the
  fault would have named the wrong fix (`accounts add`), so the check does not.

`CONTRACT.md` 1.2 lists the new sentence.

**Proof.** `test_sign_in_and_failures.py::test_SignIn_AnAccountListThatCannotBeRead_IsSaidAsItIs_NeverAsTheSignInService`
covers both paths: a cache that holds no account, and no sign-in file at all. Both cases fail on `d69dd70c8`.

### C. The case-insensitive match is sound

**Noted. No change.** The collision the re-review names (`a.b@c.com` and `a@b.c.com` share a file name) is
cc-outlook's own file-name rule. Where it bites, MSAL's account check by username still refuses the wrong mailbox's
cache ("holds no account").
