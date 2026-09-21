# Proof - the append-only factory activity record

Website Business Factory, product track. Developer task: the factory activity record on the Gateway,
behind a switch that is off by default, and the `cc-devthrottle factory` commands business tools call.

## What was built

- **The switch.** `factoryAgents.enabled` in the Gateway's `config.json`, default off. Only a JSON
  `true` turns it on. Read once when the Gateway starts (`FactoryAgentsConfig`), with an explicit
  `factoryAgentsEnabled` constructor override for tests. While it is off, the routes are not mapped
  and answer 404.
- **The table** `factory_activity`, with a SQLite migration and its PostgreSQL twin. The Gateway mints
  the key; rows are tenant-scoped. "What happened" is capped at 500 characters.
- **The store** `FactoryActivityRecord` has `Append` and `Query` and nothing else. No update, no
  delete, no retention sweep. A correction is a new row whose `CorrectsId` names the old one, and it
  must name a row in the same account.
- **The routes** `POST` and `GET /gateway/factory/activity`. A session key may call both. When the
  caller names no actor, the Gateway stamps `session:<id>` from the session key, and fills in the row's
  session the same way.
- **The commands** `cc-devthrottle factory record` and `cc-devthrottle factory activity`, documented
  in `docs/cli-reference.md`.

## What each test proves

Store tests, `src/CcDirector.Gateway.UnitTests/FactoryActivityRecordTests.cs` (22 tests):

| Test | What it proves |
|---|---|
| `Store_exposes_no_mutating_method_besides_Append` | Reads the store type's public methods and settable properties. The only methods are `Append` and `Query`, and nothing can be set. Adding an update, delete or purge fails this test. |
| `Append_then_Query_returns_the_row_with_server_stamps` | A written row reads back with every field, and the Gateway stamps both times. |
| `A_correction_is_a_new_row_pointing_at_the_old_one_and_the_old_one_is_unchanged` | A correction is a second row that points at the first. The first row, serialized before and after, is identical. |
| `A_correction_of_a_row_that_does_not_exist_is_refused_and_writes_nothing` | A correction cannot point at nothing. |
| `A_refused_outcome_lists_the_allowed_words_and_writes_nothing` | An unknown outcome is refused with all eleven allowed words, and a query afterwards finds the same count as before. |
| `Every_listed_outcome_is_accepted` | All eleven words are accepted (the design's ten plus `skipped`, added by review finding F1), and there are exactly eleven. |
| `A_sentence_over_500_characters_is_refused_and_one_of_exactly_500_is_kept` | The cap sits exactly at 500. |
| `A_missing_required_field_is_refused` (3 cases) | Factory, factory agent and "what happened" are required. |
| `A_named_actor_wins_over_the_calling_session`, `A_row_with_no_actor_and_no_caller_is_refused` | Who acted is never blank. |
| `Query_filters_by_factory_agent_outcome_and_time_window` | Each filter works, and the window includes its start and excludes its end. |
| `Query_orders_newest_first_by_default_and_oldest_first_on_request_and_pages` | Both orders work, and pages report whether more rows match. |
| `Query_refuses_an_unknown_outcome_filter_and_a_bad_page` | Bad filters are refused rather than silently matching nothing. |
| `Another_tenants_query_does_not_see_the_row_and_cannot_correct_it` | **Tenant isolation.** Account beta sees none of account alpha's rows, and cannot correct one by its id. |
| `The_switch_is_on_only_for_a_boolean_true` (6 cases) | The switch turns on only for `"enabled": true`. A missing key, `false`, the string `"true"` or a malformed block all leave it off. |

Route tests on a real booted Gateway, `src/CcDirector.Gateway.Tests/FactoryActivityRouteTests.cs` (3 tests):

| Test | What it proves |
|---|---|
| `Switch_on_a_session_key_appends_a_row_stamped_with_its_session_and_reads_it_back` | The session-key guard lets a session key append and read. The Gateway stamps that session as the actor and as the row's session. |
| `Switch_on_a_refused_outcome_is_a_400_listing_the_allowed_words_and_writes_nothing` | A bad outcome is an HTTP 400 that lists every allowed word, and a read afterwards finds the same count as before. |
| `Switch_off_both_routes_are_404` | With the switch off, `POST` and `GET` both answer 404. The same paths answer 201 and 200 in the tests above, so the 404 comes from the switch and not from a wrong path. |

Command tests, `tools/cc-devthrottle/tests/test_factory_ops.py` (12 tests). Each one answers from a
**real local HTTP server**, so the real transport and its error handling run end to end. Nothing
between the command and the socket is replaced.

| Test | What it proves |
|---|---|
| `test_record_Written_ExitsZeroAndPrintsTheId` | Exit 0 and the id on the first line, and only when the Gateway returned an id. Also checks the exact request body and that the session's own key is sent. |
| `test_record_GatewayRefusesTheRow_ExitsNonZeroWithItsSentence` | A 400 exits non-zero and shows the Gateway's own sentence. |
| `test_record_SwitchOff_ExitsNonZeroAndSaysTheFeatureIsOff` | A 404 exits non-zero and says factory agents are switched off, naming the config key. |
| `test_record_GatewayFails_ExitsNonZero` | A 500 exits non-zero. |
| `test_record_SuccessWithNoId_IsNotReportedAsWritten` | A success answer with no id is still "not recorded". |
| `test_record_GatewayUnreachable_ExitsNonZero` | A refused connection exits non-zero. |
| `test_record_NoSessionKey_ExitsNonZero` | No session key means non-zero, and no request is sent. |
| `test_activity_*` (3 tests), `test_record_Json_*`, `test_actions_ListTheFactoryVerbs` | The read command, its filters and its failures, the JSON form, and the action catalogue. |

Existing guards that this change had to satisfy, and that caught real gaps while it was built:
- the migration guards in Gateway.UnitTests, which require a PostgreSQL twin for every SQLite
  migration and pin the newest migration by name (updated to name this one; the chain test's schema
  change count goes from 24 to 29, which is this table plus its four indexes);
- the command-line census tests, which require every top-level group to be accounted for, a help
  summary of at most 80 characters, and every new action to be listed.

## Gate results

See `gate-default.txt`, `gate-parked.txt` and `gate-python.txt` beside this file.

Re-run after review finding F1 added `skipped` (2026-09-21, at the commit that adds it):
- `scripts/test-local.ps1` default run: every suite passed except four tests, none in code this change
  touches. Two are the Launcher tests that read this machine's live restart signal
  (`An_unarmed_launcher_declares_no_restart_signal...`, `Describing_the_launcher_asks_the_signal...`).
  Two come from running with TEMP on drive D because drive C is full:
  `DirectorSupervisorTests.DefaultConstructor_ResolvesRealLocalAppDataPath` expects the temporary
  folder under `AppData\Local`, and `ReclaimRefusalTests.Reclaim_APathThatIsNotCanonicalAfterResolution_IsRefused`
  needs Windows short names, which drive D does not create.
- Gateway.UnitTests in full: 6998 passed, 8 skipped, 0 failed.
- tools/cc-devthrottle, same scratch virtual environment recipe as `gate-python.txt`: 3483 passed,
  3 skipped (`test_factory_ops.py` 12 passed); the shared contract and output tests 136 passed.

## What this proof does not cover

- **Core.Tests was not run.** It is parked and takes 11 to 33 minutes, longer than the 10-minute
  foreground limit a session has. This change adds one new file to Core (`FactoryAgentsConfig.cs`),
  and that file's parsing is tested in Gateway.UnitTests.
- **The switch on the hosted Gateway.** The switch is read from `config.json`, the same place as the
  rest of the Gateway's settings. Nothing here checks how the hosted container gets that file. Turning
  the record on in production is a separate, deliberate step.
- **The Cockpit.** The record has no screen yet, and the Gateway does not yet tell the Cockpit whether
  the area is on. Both belong to later work on this mission.
