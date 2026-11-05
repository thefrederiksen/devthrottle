# Factory Inbox phase 1, Task B - review of the cc-devthrottle factory inbox commands

Review session afa54660, 4 November 2026. Branch `factory-inbox/cli` in worktree `D:\ReposFred\devthrottle-fi-cli`,
two commits on top of the merge base `31db15d92` (`c0093af40`, `110b46cec`). Nothing in the worktree was edited,
built or committed; every run was made on a copy taken with `git archive HEAD`.

## Scope

What I read:

- The whole diff `git diff origin/main...HEAD`: `tools/cc-devthrottle/src/factory_inbox_ops.py` (new, 439 lines),
  the eight commands added to `tools/cc-devthrottle/src/cli.py`, `tools/cc-devthrottle/tests/test_factory_inbox_ops.py`
  (new, 55 tests) and the "Factory inbox" section added to `docs/cli-reference.md`.
- The shared pieces the module leans on, as they are on this branch: `axi_cli.py`, `usage_errors.py`,
  `cc_shared/axi_output.py`, `cc_shared/gateway.py`.
- The standards: `MISSION.md`, `phase-1-plan.md` (the FINAL JSON contract), `docs/axi-standard.md`,
  `docs/CodingStyle.md`, `CLAUDE.md`.
- Task A's Gateway code on `origin/factory-inbox/store` (`e1d48573e`): `Api/FactoryInboxEndpoints.cs`,
  `Factory/Inbox/FactoryInboxStore.cs`, `Factory/Inbox/FactoryInboxFold.cs`. Task B codes against Task A's routes, so
  I compared what the commands send and read with what those routes take and answer.

What I ran, all out of tree in the scratchpad copy:

- The 55 new tests with `FORCE_COLOR=1`, as the continuous integration job runs them: 55 passed in 35 seconds
  (Python 3.11.6, typer 0.20.0, pytest 7.4.3).
- Two reproduction scripts against a loopback server answering in the final contract's shape, for findings 1 and 2.

What I could not reach:

- The commands against a real Gateway. Task A is not merged, so no Gateway on this machine has the routes. Every
  judgement about the Gateway's behaviour below comes from reading Task A's branch, not from running it.
- The rest of the cc-devthrottle suite and `.\scripts\test-local.ps1`. The change adds one module, one test file and
  eight command registrations; I ran the tests that cover them. The Developer's own gate run is not something I
  verified.
- Main has moved past the merge base by two commits (`c9e60babc`, `8a9a8b5fd`); neither touches
  `tools/cc-devthrottle` or `docs/cli-reference.md`.

## What holds

The parts that the plan and the AXI standard ask for and that I checked are in place: `--json` prints the Gateway's
answer unchanged and `--since` reaches the Gateway under `--json`; an unknown flag, an unknown field and `--fields`
with `--json` exit 2 before any call; empty lists say `count: 0`; an answer without the list it promised exits 1
rather than reading as none; ids and factory names are never cut; a change is reported only from what the Gateway
answered (`confirmed` on the bundle id, the decision label and the to-do id); the seat commands act as
`CC_SESSION_ID` and never name the factory; a flag a decision would ignore is refused; a factory name is
path-encoded. The bodies the commands post (`ids`, `merges`, `links`, `kind`, `session`, `why`) match the fields
Task A's `ReceiveBody`, `BundleBody` and `DecideBody` read, and the fields the commands read (`stateLabel`,
`noteLabel`, `decision.label`, `trail[].label`, `bundle.id`, `todo.id`, `lastMessage`, `byDecision`) match
`FactoryInboxFold` and the anonymous objects the endpoints return. The tests cover the failure cases as well as the
flow, and three of them prove a refusal made on this side never reaches the Gateway.

## Findings

### 1. `summary` tells the agent to run `--full`, and `summary` has no `--full`

`factory_inbox_ops.summary` renders its items through `_message_blocks(items, [...], False, "items")`, so any item
text over 80 characters is cut and the block ends with the shared hint line
`text cut at 80 characters in N rows - use --full`. The `summary` command registered in `cli.py` takes only
`--since` and `--json`. Reproduced out of tree with one item of 204 characters:

```
items[1]{id,state,decision,why,text}:
  m1,Decided,Noted,,This is a long message from the owner that goes well past eighty characters s... (204 chars)
text cut at 80 characters in 1 rows - use --full
help[1]:
  cc-devthrottle factory inbox thread warmforward

--- summary warmforward --full: exit 2
Error: No such option: --full
```

The harm: the output names a next step that does not exist. An agent that follows it pays a usage error and still
has no way to read the whole text from this command (AXI principle 9, "concrete next commands", and principle 3,
the hint must lead somewhere). A message to the Boss is the owner's instruction, and the summary is where the Boss
and the owner read what was decided about it; a text the summary can only show cut is the case the hint exists for.
No test catches it because `SUMMARY` in the test file has short item texts only. Either give `summary` a `--full`
option (the way `thread` has one), or render summary items whole and drop the hint; and add a test with a long
item text that asserts the hint names a flag the command accepts.

### 2. `thread` prints the number of rows it was given as `count:` and drops the Gateway's count of the whole thread

Task A's `GET /gateway/factory/{factory}/inbox` answers `count = total` (every message in the thread) and
`messages` = the newest `limit` of them, where `limit` defaults to `FactoryInboxStore.DefaultThread` (100) and goes up
to 500 through `?limit=`. `factory_inbox_ops.thread` never reads the answer's `count`: `_message_blocks` prints
`axi_output.format_count(len(records))`, and the command has no option to pass a limit. Reproduced out of tree with
an answer in Task A's shape, `count: 150` and 100 messages:

```
factory: warmforward
count: 100
messages[100]{id}:
...
--- thread --count 500: exit 2
Error: No such option: --count Did you mean --json?
```

The harm: on a factory whose thread is longer than 100 messages the plain output says `count: 100` while the
Gateway's own answer says 150, nothing says that 50 older messages exist, and no flag reaches them. The mission's
"messages to the boss should be obviously logged. So if they're missed, they can be found again" is exactly the case:
an agent asked whether an old message was ever handled reads a thread that silently ends at the newest 100 and
answers from that. The AXI standard's rule for this is `count: 100 of 150 total` (principle 5) with a `--count`
option (the convention `factory email list --count N` already uses, and `docs/cli-reference.md` names `--count`/`-n`
as the limit flag). The `--json` path is fine, since it prints the whole answer. The thread test
(`test_thread_prints_the_gateway_trail_and_whole_ids`) answers `count: 2` with two messages, so it cannot see the
mismatch; a test whose answer carries more in `count` than in `messages` would.

### 3. `receive` and `bundle --hand` add two rules of their own that Task A's routes do not have

The FINAL contract has `ids?` optional on both `POST .../receive` and `POST .../bundle`, and Task A's store reads a
null `ids` as "every message this session may file" and "every message this session holds". It also says, in
`FactoryInboxStore.Receive`: "Merges and links may name any message this session now holds, whether this call or an
earlier one took it." The commands narrow both:

- `_ids` refuses an empty list for `receive` and for `bundle --hand` ("name at least one message"), so the
  natural Secretary run, receive everything then hand everything, is two extra round trips: run `new`, copy every id
  into `receive`, copy them again into `bundle --hand`.
- `receive` refuses a `--merge` or `--link` whose ids are not all in this call's ids, with the sentence "both must be
  among the ids passed". A repeat of a message this session received in an earlier call (`receive m2 --merge m2:m1`
  after `receive m1`) is refused here, although the Gateway accepts it. The workaround, naming m1 again, works
  because a session may re-receive what it holds, but nothing tells the agent that.

The harm is smaller than findings 1 and 2: every case has a workaround. But the module's own first rule is "Nothing
here decides anything", and these are rules the product does not have, stated to the agent as if it did. The
Gateway already refuses, in words, a merge or link naming a message the session does not hold
(`merge_or_link_not_held`), a merge into itself, a merge chain, and an empty receive with merges attached, so the
client checks that duplicate them protect nothing. Suggested: let `receive` and `bundle --hand` take no ids (post
`ids` absent) and keep only the checks the Gateway cannot make before the call (the pair shapes, a repeated id); let
a merge or link name any id and let the Gateway rule on it. If the Developer keeps the client rules on purpose, the
sentence should say they are this command's convenience, and `docs/cli-reference.md` should stop presenting
"must be among the ids received" as the product's rule.

## Not findings, noted for the record

- The tests answer from a loopback HTTP server on a port the operating system assigns. That is the style the plan
  asked for and the style the sibling suites use (`test_factory_email_ops.py`); it is not a real wait on the
  machine, so the test rule in `CLAUDE.md` does not condemn it. All 55 passed on this machine in one run.
- The plain-English rule: the new text uses "id" and "JSON" as the rest of `docs/cli-reference.md` does. I did not
  count that against the change.
- Task A's `GET /gateway/factory/{factory}/inbox` is newest first; the command prints the order it was given, which
  is right under rule 7 (the Gateway owns the ordering). Worth one phrase in the docs once finding 2 is settled.

## Answers

The Developer, 5 November 2026. All three findings accepted and fixed in the commit that adds this file. Tests:
`tools/cc-devthrottle/tests/test_factory_inbox_ops.py`, 66 (was 55); the whole cc-devthrottle suite rerun.

### 1. Accepted - `summary` now takes `--full`

`summary` gains `--full`, the way `thread` has it, so the hint names a flag the command takes. The same rule was
applied to the two other commands that cut text without having the flag: `receive` and `bundle --hand` now always
show text whole, as `bundle` already did, so no command prints a hint it cannot honour. Tests:
`test_summary_cut_text_hint_names_full_and_full_shows_it_whole` (a 228-character item: the hint is printed, and
`summary --full` exits 0 with the whole text and no hint) and
`test_receive_and_hand_show_text_whole_because_they_have_no_full_option`.

### 2. Accepted - `thread` prints the Gateway's count and takes `--count`

`thread` reads the answer's `count` and prints `count: 100 of 150 total`. When the thread holds more than is
listed it adds `newest 100 of 150 shown - use --count up to 500` and offers `thread <factory> --count 150` as the
first next step. Past the Gateway's most (500) it says so and points at `summary --since`, which has no cap.
`--count N` / `-n N` (1 to 500, matching `FactoryInboxStore.MaxThread`) is sent as `?limit=` and applies to
`--json` too. An answer whose `count` is missing, not a number, or smaller than the messages it carries exits 1
instead of being read as the whole thread. The docs now say the thread is newest first. Tests:
`test_thread_longer_than_listed_says_how_many_and_offers_count`, `test_thread_count_reaches_the_gateway_in_json_too`,
`test_thread_over_the_most_one_read_lists_points_at_summary`,
`test_thread_count_out_of_range_is_a_usage_error_before_any_call` and
`test_thread_answer_whose_count_cannot_cover_its_messages_is_an_error`.

### 3. Accepted - the client-only rules are gone

`receive` and `bundle --hand` take no ids, and then send no `ids` field, so the Gateway takes every message the
session may file or holds. A merge or link may name any id; which are allowed is the Gateway's rule, and its
refusal is printed in its words. The only checks left before the call are the shape of each pair and a repeated
id. The docs no longer present "must be among the ids received" as the product's rule. Tests:
`test_receive_with_no_ids_sends_no_ids_so_the_gateway_takes_every_message`,
`test_receive_merge_naming_a_message_held_from_an_earlier_call_reaches_the_gateway`,
`test_receive_merge_the_gateway_refuses_is_reported_in_its_words`,
`test_bundle_hand_with_no_ids_sends_no_ids_so_the_gateway_hands_every_held_message`, and
`test_receive_refuses_a_malformed_merge_or_link_before_any_call` (now only malformed pairs and a repeated id).
