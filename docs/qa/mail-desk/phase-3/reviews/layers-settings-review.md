# Review: pull request 4240, the four screening layers and the Mail Desk settings

Branch `mail-desk-p3-layers-settings` at `95b67e1cc`, into `mail-desk-phase-3`. Measured against sections 3 and 7 of
`docs/qa/mail-desk/phase-3/CONTRACT.md` as it stands on `origin/mail-desk-phase-3`.

## Findings

None. Within the scope below, I found no defect whose harm I can show.

What I checked and found to hold, so that the next seat knows these were looked at and not skipped:

1. **Layer order and first-match-wins.** `MailDeskScreening.DecideCoreAsync` returns from the fixed rules (in document
   order) before it reads the allow-list, the allow-list before the block list, and the block list before any model
   question. A rule, an allow match or a block match never reaches the model. Tests: `DecideAsync_MatchesRuleAllowAndBlock_TheRuleWins`,
   `DecideAsync_MatchesAllowAndBlock_TheAllowListWins`, `DecideAsync_FirstRuleInDocumentOrder_Wins`.
2. **A known sender skips the sender screen.** The person-or-spam question is inside `if (!message.KnownSender)` only.
3. **The sender screen.** Junk happens only on the `spam` label with a confidence at or above the spam threshold. An
   answer off the labels, `spam` below the threshold, and `spam` with no confidence all go to `mail-desk-agent`.
   `person`, at any confidence, goes on to the route question. Nothing in this layer decides `route`. Any other label
   throws, and it cannot occur: `MailDeskModel` returns `Label` only for a letter it offered.
4. **The route question** does the same with the route threshold. `unsure`, off the labels, below the threshold, and no
   confidence all go to `mail-desk-agent`. With no routes, nothing is asked and the reason is "no routes are defined".
5. **The spend limit.** `MailDeskSpendLedger.IsReached` (spent at or over the limit) is checked right before each of the
   two questions, and each answered question is charged before the next check. A call that would start at or over the
   limit is never made, including the second question about the same email. A limit of 0 stops every question. Rules
   and lists still decide while the limit is reached. The fake model's call log proves this in
   `DecideAsync_FirstQuestionCrossesTheLimit_TheSecondIsNeverAsked` and
   `DecideAsync_SeveralMessagesOnOneLedger_TheCallThatWouldStartAtTheLimitIsNeverMade`.
6. **Matching.** `MailDeskAddresses.Matches` compares a lower-cased, trimmed exact address, or for `*@domain` the text
   after the sender's last `@`, exactly. A subdomain does not match, and an empty sender matches nothing. No other
   wildcard can reach it: `TryPattern` already refuses every other shape when a rule is written. "Subject contains" is
   `string.Contains` with `OrdinalIgnoreCase`, with no pattern syntax, and an empty text never matches.
7. **No body text in any reason.** Every reason is built from a rule name, a pattern with its stored what or why, a
   route key, a label, a confidence or a threshold. The preview goes only to the model.
   `DecideAsync_EveryKindOfDecision_NeverPutsThePreviewInTheReason` covers every kind of decision.
8. **The 25-route cap** is in `MailDeskRulesValidator.Validate`, so a 26th route is refused when it is added
   (`AddRoute_TheTwentySixth_IsRefused400AndRecordsNothing`). A document that somehow held more throws at screening
   time instead of being cut short.
9. **Who may change a setting.** `POST /settings` uses `TryAuthor` with `channelChange: true`, the same rule as `mode`:
   a person or a session in no factory may change a setting. A factory's own session gets 403
   `factory_session_refused`, and the machine token or a Director gets 403 `not_an_author`. The author check, the
   why, the value type, the name and the range are all checked before `MailDeskSettingsStore.Set` writes anything, and
   the setting row and its `setting` change row are saved in one `SaveChanges`. The tests for each refusal assert that
   nothing was recorded.
10. **Ranges.** Thresholds run from 0.5 to 1.0 and the spend limit from 0 to 100, both ends included, on the Gateway
    and again in the command before any request. A stored value outside its range, or one that cannot be read, throws
    instead of falling back to the default.
11. **The command against `docs/axi-standard.md`.** Typer refuses an unknown flag. `--why` is required before any
    request, and an unknown name or a value out of range (not-a-number and infinity included) exits 2 before any
    request. `maildesk settings --json` prints the Gateway's answer unchanged, and `maildesk status --json` gains the
    `spend` block with no other change in shape. Neither command filters a list or prints a list, so the filter rule,
    the count-0 rule and the rule against cutting values short have nothing to apply to here.

Two things I noticed that are not findings, because I cannot show harm from either:

- A reason prints a confidence to at most four decimal places. A confidence within 0.00005 below a threshold (for
  example 0.949996) would read "spam at 0.95, under the threshold 0.95". The decision itself is correct; only the line
  looks odd, and it can happen only in a very narrow band.
- A setting is stored to six decimal places while the change row records the value as sent. A spend limit sent with
  more than six decimal places would be stored a millionth of a dollar away from what its record says. The
  command-line path can produce that only for nonsensical input.

## Scope

**Read:** the whole diff of the pull request against `origin/mail-desk-phase-3`: `MailDeskScreening.cs`,
`MailDeskSettings.cs`, the settings parts of `MailDeskEndpoints.cs` (including `TryAuthor`), `MailDeskAddresses.Matches`,
the `MailDeskRulesValidator` cap, the `SessionKeyGuard` change, the `GatewayHost` wiring of `MailDeskSpendOf` and the
store, `MailDeskEmailSection` (the setting history line), `cli.py`, and `maildesk_ops.py`. I also read how
`MailDeskModel` sets `Label` and `OffLabels`, and the names of every test in `MailDeskScreeningTests.cs` and
`MailDeskSettingsEndpointTests.cs`, along with the bodies of the ones cited above.

**Ran:** on Linux,
`dotnet test src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true --filter "Category=MailDesk|FullyQualifiedName~MailDesk"`:
454 passed, 0 failed, 0 skipped. In `tools/cc-devthrottle`, `pytest tests/test_maildesk_ops.py -q`: 122 passed.

**Not reached:** the full unit suites, the Gateway host tests, and the database tests on PostgreSQL. The settings read
in `MailDeskSettingsStore` (`List.Contains` in a query) was exercised only on the test database the unit suite uses.
I did not drive a running Gateway or the command against one. Outside this pull request by the Tech Lead's statement:
decision rows, rereads, memory, the spend stop time, and how `GET /settings` arrives at `stopped`.
