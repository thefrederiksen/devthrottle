# Worker I - notes: clients that ruled for themselves, and a name with a slash in it

Phase C. Findings **I5** (clients turn an unknown outcome into a definite outcome) and **I6** (a
repeat stop by a name containing a slash fails instead of answering). Nothing else was touched.

Branch `mission/stop-a-session`, worktree `C:\ReposFred\devthrottle-stop-a-session`. Nothing was
merged to main and no pull request was opened.

---

## What changed

| File | Finding | What it does now |
|---|---|---|
| `tools/cc_shared/gateway.py` | I5, I6 | `GatewayError` carries the HTTP `status` it was refused with, and `None` where there never was one. New `path_segment()` escapes one caller-typed value so it stays one segment of a path. |
| `tools/cc-devthrottle/src/session_ops.py` | I5, I6 | The stop announces a refusal as `Not stopped:` and everything else as `Outcome unknown:`, and it sends the target through `path_segment`. |
| `src/CcDirector.Avalonia/StopSessionDialog.axaml.cs` | I5 | The failure line no longer says the session was not stopped. |
| `packages/client-core/src/api/client.ts` | I5 | A successful answer with no headline no longer says the session was stopped. |
| `tools/cc_shared/tests/test_gateway.py` | both | The status really travels out of a real HTTP failure; `path_segment` really escapes. |
| `tools/cc-devthrottle/tests/test_session_stop.py` | both | The four new wording tests, and the composed path put through real HTTP routing. |
| `src/CcDirector.Avalonia.Tests/StopSessionDialogTests.cs` | I5 | The dialog is driven with the Gateway's own "it is not known whether the command was carried out" sentence. |
| `packages/client-core/src/api/stopSession.test.ts` | I5 | The malformed-body message is pinned as a hedge rather than a claim. |

**Where these commits are.** All of the code above was swept into commit `a6c4c28c` by another seat's
`git add -A` while I was working - the commit message is about documentation and says nothing about
I5 or I6. The Manager confirmed it and said to carry on rather than revert, so this document is the
only place the change has a description attached to it. Nothing was lost and nothing was mutated in
flight: `a6c4c28c` was checked afterwards and carries the fixed code, not a mutation.

---

## I5 - the wording each client uses, and why

**The rule applied everywhere: a client may say what IT knows, and may not turn that into a statement
about whether the session is running.**

### The command line - the status is carried through, and it buys the distinction

I took the option the brief offered. `GatewayError` now carries the HTTP status, so:

- **A 4xx keeps `Not stopped:`.** Every 4xx this route can answer - no reason, no tenant, an
  unaccepted key, no such route - is decided *before* the owning Director is asked, so nothing was
  carried out and saying so is reporting rather than guessing. That matches the branch the brief told
  me to leave alone: the refuse-before-the-call for a missing reason, which is the same fact arrived
  at without a round trip.
- **Everything else says `Outcome unknown:`**, and adds one line of its own: "This cannot say whether
  the session is still running. Run cc-devthrottle session list to see whether it is still there."
  Every 5xx, and every failure that never got a status at all (a refused connection, a read timeout,
  an answer that was not the JSON it was promised), lands here. The Gateway's own sentence is printed
  underneath, intact, exactly as before.
- **The unknown side is the default**, so a status nobody has thought about cannot inherit the wording
  that claims a verdict. There is a test for that specific direction.

The reason hint (`Re-run with --reason ...`) is now offered only on a refusal, and still only when the
server's sentence mentions a reason. A timeout sentence that happens to quote the caller's own reason
back no longer sends them off to re-type a flag that was never wrong.

**The cost of this choice, stated rather than buried.** The Gateway answers a Director-*reported*
failure - "the agent process 51884 did not exit after being signalled and then forced" - with the same
502 it gives a tunnel that dropped mid-command. Those two are not the same fact, and this client cannot
tell them apart, so a definite Director-side failure is now announced with the weaker prefix. The
Director's own definite sentence is printed directly underneath it, so nothing is lost to the reader,
but the prefix is vaguer than that case deserves. **That is a Gateway-side gap, not a client one**, and
I did not touch the Gateway: distinguishing them would mean a distinct status or a flag on the answer.
Reported here rather than fixed silently.

### The Director window - one wording, because it cannot learn any more than that

`The session was not stopped:` became:

> `Outcome unknown - this cannot say whether the session is still running:`

It does **not** split refusals from lost replies the way the command line now does, and it cannot: the
client it is handed (`GatewayClient.StopSessionAsync`, via `ControlApiHost`) raises one exception type
for every failure and carries no status code, and `src/CcDirector.ControlApi` is another Worker's file
this phase. So one wording that claims nothing about the session covers both. That is the safe
direction to be wrong in - a refusal announced as unknown is weaker than it needs to be, while a lost
reply announced as "not stopped" is simply false.

One case in this window is genuinely definite and still reads as unknown: when the Director's own
services are not running, `MainWindow` throws before any request is made. The thrown sentence itself
says plainly that it has no connection to a Gateway and cannot stop a session, so the reader is not
misled - but the prefix above it is weaker than the facts allow. Left alone deliberately; making the
dialog tell that case apart means changing the injected delegate's contract, which reaches outside I5.

### The shared web client - the opposite claim, facing the other way

> `The session was stopped, but the answer came back without the words that describe it.`

became

> `The Gateway answered without the words that say what happened, so this cannot report whether it was
> stopped. Check the session list to see whether it is still there.`

A 2xx does not establish that anything was stopped: three verdicts arrive on that same status and only
one of them ended a process - `notOnFleet` stopped nothing at all and asked no machine. The field that
would have told them apart is the field that is missing, so the old message asserted the very fact
whose absence it was reporting. The new sentence is deliberately the same shape as the command line's
missing-headline sentence and `GatewayClient`'s, so one event is described one way on all three
surfaces.

### The Cockpit's retryable path - looked at, reported, not changed

`gatewayErrorMessage` was read in full (`packages/client-core/src/api/client.ts:584`).

- When the server sent a sentence, that sentence wins outright and the only addition is `Try again.`
  for a retryable failure. That is **advice, not an outcome** - it does not contradict a Gateway
  sentence that says the outcome is unknown. Nothing to change.
- When the server sent **no** sentence, it composes one from the status: for 0/502/503/504 that reads
  "DevThrottle could not stop the session - the machine running this session could not be reached
  (error 502)." That *is* an outcome claim, and for a lost reply it could be wrong. Two things keep it
  out of scope here: the stop route always sends a sentence on those statuses, so this branch is
  reachable only through a proxy or an edge rule answering with an empty body; and the function is
  shared by every action in both shells, so re-wording it is a product-wide change with nothing to do
  with the stop. **Reported, not changed** - the brief's instruction exactly.

---

## I6 - the encoding, and the audit of everywhere else in that module

`gateway.path_segment()` escapes with `quote(safe="")` - nothing is safe, the separator included - and
the stop's call is now `sessions/{path_segment(sid)}/stop`. It is deliberately **not** applied inside
`_request`: the separators *between* segments are part of the path, and escaping those would break
every call the tool makes.

**Every other place in `session_ops.py` that interpolates a target into a path was checked.** There are
twelve, and eleven of them are safe for one reason: the value is an identifier resolved from the
roster (`gateway.field(chosen, "sessionId", "SessionId")`) or read from this session's own environment
(`gateway.session_id()`, `_my_director()`), never a string the caller typed. `session stop` is the only
verb that deliberately sends an unresolved target, which is why it is the only one with this hole -
the same design decision that makes `notOnFleet` reachable at all.

One other caller-typed interpolation exists and is **reported rather than fixed**, because it is not in
the stop's call path:

- `tools/cc-devthrottle/src/session_ops.py:1173` - `f"machines/{target_machine}/sessions"`, where
  `target_machine` is whatever was typed after `--machine`. A machine name cannot contain a slash on
  Windows, so this is far less likely to bite than the session-name case; but `?` and `#` would acquire
  URL syntax there exactly as they did here, and the one-line fix is the same helper.

---

## The mutation table - each red as it actually printed

Every mutation was applied to the production file, the suite was run, and the file was restored from a
copy taken before the mutation. No test file was mutated.

| # | Mutation | Suite | Result |
|---|---|---|---|
| 1 | Put the raw path back: `f"sessions/{sid}/stop"` | `tests/test_session_stop.py` | **2 failed, 30 passed** |
| 2 | Put the prefix back: `_failure_prefix` always returns `Not stopped:` | `tests/test_session_stop.py` | **4 failed, 28 passed** |
| 3 | `path_segment` returns `str(value)`, and the status is dropped from `GatewayError` | `cc_shared/tests/test_gateway.py` | **2 failed, 52 passed** |
| 4 | Put the desktop prefix back: `"The session was not stopped:"` | `StopSessionDialogTests` | **1 failed, 13 passed** |
| 5 | Put the web claim back: `"The session was stopped, but..."` | `stopSession.test.ts` | **2 failed, 10 passed** |

**Mutation 1** - the slash-only name goes through real routing and comes back 404, which is the
reported symptom:

```
FAILED tests/test_session_stop.py::test_a_target_named_with_a_slash_in_it_still_reaches_the_stop_route[Mission/Worker-I]
E           AssertionError: Not stopped: no route matches POST /sessions/Mission/Worker-I/stop
E           assert 1 == 0
E            +  where 1 = <Result SystemExit(1)>.exit_code
```

and the fleet-shaped name does not even survive being made into a URL:

```
FAILED tests/test_session_stop.py::test_a_target_named_with_a_slash_in_it_still_reaches_the_stop_route[Stop a session / Worker I]
E           AssertionError:
E           assert 1 == 0
E            +  where 1 = <Result InvalidURL("URL can't contain control characters. '/sessions/Stop a session / Worker I/stop' (found at least ' ')")>.exit_code
```

**Mutation 2** - the finding itself, in the first line:

```
E       AssertionError: assert 'Outcome unknown' in 'Not stopped: Cannot reach the Gateway at http://gateway.invalid: [Errno 11001] getaddrinfo failed. Every fleet comman...s cannot say whether the session is still running. Run cc-devthrottle session list to see whether it is still there.\n'
E       AssertionError: assert 'Outcome unknown' in 'Not stopped: the agent process 51884 did not exit after being signalled and then forced, so the session is still runn...s cannot say whether the session is still running. Run cc-devthrottle session list to see whether it is still there.\n'
E       AssertionError: assert 'Not stopped' not in 'Not stopped...ill there.\n'
E       AssertionError: assert 'Outcome unknown' in 'Not stopped: something nobody has written a case for\nThis cannot say whether the session is still running. Run cc-devthrottle session list to see whether it is still there.\n'
FAILED tests/test_session_stop.py::test_an_unreachable_gateway_is_non_zero_and_says_so
FAILED tests/test_session_stop.py::test_a_process_that_would_not_die_is_non_zero_and_says_so
FAILED tests/test_session_stop.py::test_a_timeout_is_not_reported_as_a_session_that_is_still_running
FAILED tests/test_session_stop.py::test_a_status_this_client_has_never_seen_lands_on_the_unknown_side
```

**Mutation 3**:

```
E           AssertionError: assert None == 400
E       AssertionError: assert 'Mission / Worker' == 'Mission%20%2F%20Worker'
FAILED tests/test_gateway.py::test_an_http_failure_carries_the_status_it_was_refused_with
FAILED tests/test_gateway.py::test_path_segment_escapes_the_separator_so_a_name_stays_one_segment
```

**Mutation 4**:

```
  Failed CcDirector.Avalonia.Tests.StopSessionDialogTests.WhenTheGatewaySaysItDoesNotKnow_TheWindowDoesNotSayThatItDoes [1 s]
  Error Message:
   Assert.DoesNotContain() Failure: Sub-string found
                     v (pos 12)
String: "The session was not stopped:\r\n\r\nThe Direc"...
Found:  "was not stopped"
```

**Mutation 5**:

```
FAIL src/api/stopSession.test.ts > stopSession carries the Gateway's own sentence out of a refusal > never claims the session was stopped when the answer does not say so
AssertionError: expected 'The session was stopped, but the answ...' not to match /the session was stopped/i
FAIL src/api/stopSession.test.ts > ... > fails loudly when a 200 carries no headline, rather than showing an empty answer
AssertionError: expected 'The session was stopped, but the answ...' to contain 'without the words that say what happe...'
```

---

## What the new tests actually subject to routing - and what they do not

The brief offered two ways to prove I6 and asked me to say plainly which I did. **I reached real HTTP
routing from the Python suite, and I did not open a Gateway file.**

`tests/test_session_stop.py` now stands up a loopback server that matches the stop route the way a
route template does - **by segments**: `/sessions/<one segment>/stop` is answered 200 with a
`notOnFleet` body, and anything else is answered 404. The whole command line runs against it through
the real `gateway.post_json`, with nothing stubbed but the roster fetch. A target carrying its own
separator misses that route for the same reason it missed the real one.

A second test gives that rig its teeth, because a server that answered anything at all would have let
the first test pass: it sends the **raw** path the client used to compose and asserts a 404 with
`status == 404`, then sends the **escaped** form of the same name and asserts a 200 with verdict
`notOnFleet`. That is the encoded-routes / raw-does-not pair the brief asked for, done at the
transport rather than inside the Gateway.

**It is not the Gateway.** It has no authentication, no tenant, no fold, and its 404 is its own. What
it proves is that the client hands over a path that survives being routed. The inspection separately
put the same two paths through the real endpoint and got 404 raw against 200 encoded; nothing here
re-proves that, and I did not ask for a Gateway-side test because this one reaches real routing.

---

## The suites, run after the change and not before it

| Run | Command | Result |
|---|---|---|
| Command-line suite, full, from its own tool directory | `python -m pytest tests/ -q` in `tools/cc-devthrottle` | **273 passed, 2 failed** |
| Command-line stop file | `python -m pytest tests/test_session_stop.py -q` | **32 passed** |
| Shared client suite | `python -m pytest tests/test_gateway.py -q` in `tools/cc_shared` | **54 passed** |
| Shared web client | `npm test --workspace @devthrottle/client-core` | **1042 passed**, 98 files |
| Type checking, all four workspaces | `npm run typecheck` | client-core, cc-assistant, cockpit, mobile - **exit 0** |
| Director window suite, to completion | `dotnet test src/CcDirector.Avalonia.Tests` | **421 passed, 0 failed, 0 skipped** |

**The two command-line failures are the pre-existing ones, and I confirmed that myself rather than
inheriting the claim.** They are `test_email_cli.py::test_owner_has_no_recipient_option` and
`test_spawn_ops.py::test_type_option_is_removed`, and both fail inside the installed command-line
dependencies while rendering help:

```
Parameter.make_metavar() missing 1 required positional argument: 'ctx'
TyperArgument.make_metavar() takes 1 positional argument but 2 were given
```

Neither file is touched by this work and neither failure mentions the stop.

**On `FORCE_COLOR`.** The brief said not to set it because the inspection saw spurious wrapping
failures with it. I ran the suite both ways to find out where this now stands, because the continuous
integration job sets it deliberately: **the same two failures, and only those two, in both runs** -
273 passed / 2 failed with it and without it, and 32 passed in the stop file with it. The four
wrapping failures the inspection saw in that file do not reproduce on this host.

---

## What these tests still do NOT cover, named honestly

- **The routing server is not the Gateway** (see above). Nothing here proves ASP.NET routes the two
  paths the way this rig does, or that the real route answers `notOnFleet` for an escaped name.
- **The statuses handed to the client-line tests are constructed by hand.** That the *real* client puts
  the *real* status on the error is proved next door in `cc_shared/tests/test_gateway.py` over a
  loopback server; that the *Gateway* answers a missing reason with 400 and a Director timeout with
  504 is the Gateway's own tests, not mine. If the Gateway changed which status it uses for a refusal,
  these tests stay green and the wording would be wrong.
- **Nothing here tells a Director-reported failure from a dropped tunnel**, because the Gateway gives
  both 502. The tests do not pretend to.
- **The desktop test drives the dialog's own logic, not pixels**, and nothing here proves the window
  renders that text where a person can read it. That gap is the same one Worker E recorded.
- **No live fleet stop was performed**, on any surface, and no browser or desktop interaction was
  driven. Nothing in this work was observed against a running Gateway.
- **The Cockpit and phone shells were not opened**, by instruction - another Worker is in both. If a
  shell asserts on the old malformed-body sentence from `client.ts`, that will show up in their suite
  and not in mine.

---

## What I did not touch

`apps/` (either shell), `src/CcDirector.Gateway`, `src/CcDirector.ControlApi`,
`src/CcDirector.Gateway.Contracts`. No verdict word was added anywhere, and no client branches on one.
