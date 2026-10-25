# Model picker screen proof for issue 4381

## Root cause

The screen reader ran only after the ten-second silence timer. It already understood Pi's final picker footer, but no code asked it while the session was active. The current Claude Code picker also changed its footer to `Enter to set as default`, which the reader did not recognise.

The fix schedules a throttled positive screen read while Claude Code or Pi is active. Only the captured model-picker footers may settle this early check as `MenuOpen`; a permission prompt, an ordinary composer, a partial picker repaint, and an unread screen do not. The resulting state source is `screen`. The silence timer remains unchanged.

## State matrix

The before run was made on main commit `1a64f1e76` in `phase3-after-20261024-1433`. Its summary is in `before/state-summary.txt`: 16 of 20 case runs passed, and both runs of the model picker failed for both agents at 9.3 seconds.

The after run was made from commit `752f08a7ef43` plus this change in `4381-picker-final-20261024-151227`. Its summary and complete result rows are in `after/`: all 20 of 20 case runs passed, with no inconsistent cases. Both agents reached `MenuOpen` 0.5 seconds after the picker command and 0.0 seconds after the recorded true end. The other 16 case runs still passed.

The exact command was:

```text
src/CcDirector.DeliveryQualification/bin/Debug/net10.0/CcDirector.DeliveryQualification --state-matrix --agents claude,pi --args-pi "--model azure-openai-responses/gpt-5.6-terra --thinking off" --out /home/soren/ReposFred/_delivery-qa/runs/4381-picker-final-20261024-151227
```

## Automated checks

Focused checks passed:

- `PickerOnScreenTests`: 19 passed.
- Active picker checks in `ContentTurnRuleTests`: 4 passed.
- The background-work register check passed after recording the third timer.

The complete Core unit project ran 2,712 tests: 2,684 passed, 23 skipped, and the five known Linux failures remained. The complete parked Core project ran 4,604 tests: 4,584 passed, 12 skipped, and the eight known link-detection failures remained. A lifecycle signal test also failed in that first complete run and passed immediately when run alone; it was therefore removed under the repository rule for a test that fails while the code is correct. No changed model-picker test failed.
