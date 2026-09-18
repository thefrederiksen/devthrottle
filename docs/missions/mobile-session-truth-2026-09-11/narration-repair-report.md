# Wingman periodic-sweep narration repair evidence

Mission: `4962cc51-2b35-4c26-abe8-81b199477fb3`

Branch: `fix/wingman-terminal-narration-1991`

Pull request: [#2813](https://github.com/thefrederiksen/devthrottle/pull/2813)

## Pushed slice

- `6f47321fb` - initial terminal-failure narration implementation.
- `ccaf8c4f3` - production-path regression only (`VoiceSweepBudgetTests.cs`).
- `3392a6f3b` - periodic-sweep repair only (`GatewayHost.cs` and `WingmanVoiceService.cs`).
- The branch and `origin/fix/wingman-terminal-narration-1991` both point to `3392a6f3b`.

## Defect and repair

The periodic sweep used a bare cached-audio check before source selection. A missed Working transition could
therefore leave the previous turn's clip indefinitely, even after a later user message. The sweep also passed
the synchronous provider-budget callback into generation, and generation used that callback's presence as a
reason not to read the live terminal. A session without cached audio could consequently reach source selection
without the screen evidence needed to classify its current terminal failure.

The repair removes the bare cached-audio bypass. The sweep now captures the stored conversation and, only for
the later-user-message shape, awaits the owning Director's live terminal before generation. It holds the tenant
scope across that read and isolates a preparation failure to the affected session. Generation requires this
prepared input whenever the budget callback is supplied, so every no-cost arm and the provider commit point
still execute synchronously before generation's first await. A later user message immediately removes the
superseded ready clip; a positively classified terminal failure becomes the new narration source. The now-async
timer sweep admits only one pass at a time, preserving the global generation budget across timer overlap.

## Mutation proof

Guard-only commit `ccaf8c4f3` was pushed directly on top of production commit `6f47321fb`, with the production
repair still uncommitted. [Hosted run 34664743385](https://github.com/thefrederiksen/devthrottle/actions/runs/34664743385)
failed exactly as expected in the Windows .NET job:

- Gateway suite inventory: 2,459 total; 2,411 passed; 1 failed; 47 skipped.
- Sole failure: `VoiceSweepBudgetTests.Cached_audio_cannot_hide_a_terminal_failure_after_a_later_user_message`.
- Failure location: `VoiceSweepBudgetTests.cs:211`.
- Failure: `Assert.Contains() Failure: Filter not matched in collection`.
- The captured command collection contained `set-resolved-role` and `set-display-state`, but no `screen-grid`.
  This positively demonstrates that the old cached-audio guard prevented the real sweep from reaching the live
  terminal read. It does not, by itself, prove the downstream stale-clip behavior after successful selection;
  that is covered by the green form of the same production-path test.

## Repair proof

After `3392a6f3b` was committed and pushed, the exact host-bound regression was run locally through the repository
gate:

```powershell
.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~VoiceSweepBudgetTests.Cached_audio_cannot_hide_a_terminal_failure_after_a_later_user_message" -ExpectTests 1 -Configuration Release
```

- Result-file outcome: `Completed`.
- Inventory: 1 total; 1 passed; 0 failed; 0 skipped.
- Duration reported by the test runner: 236 ms.
- This run used the shared working tree, which still contains unrelated unstaged roster work. It proves the exact
  production path is green in that working tree, but it does not prove commit isolation. The hosted run below is
  the clean-checkout proof.

The focused Release gate for the direct Wingman service, narration-source, and terminal-classifier tests also
completed with 116 total, 116 passed, 0 failed, and 0 skipped. The host-bound test project built with zero warnings
and zero errors.

[Hosted run 34667658171](https://github.com/thefrederiksen/devthrottle/actions/runs/34667658171) checked out final
commit `3392a6f3b` and completed successfully:

- The named production-path regression is present in the log as passed in 38 ms.
- Gateway suite inventory: 2,459 total; 2,412 passed; 0 failed; 47 skipped.
- Gateway unit-test inventory: 4,251 total; 4,243 passed; 0 failed; 8 skipped.
- Core test inventory: 4,413 total; 4,405 passed; 0 failed; 8 skipped.
- Installer inventories: 25 of 25 passed and 541 of 541 passed.
- The Windows .NET job, web job, and Python tool-contract job all completed successfully.
- Because this was a clean hosted checkout of the pushed head, it excludes every unrelated unstaged roster and
  mission file from the proof.

## Local gate limitation

The mission's earlier broad local gate did not produce a usable green verdict. Its failures were in the
environment-bound PostgreSQL/live-proof, ambient hosted-state, and authentication/hosted-image-publish surfaces;
the hosted-image publish child exited while its parent remained live. Those failures do not exercise this
periodic-sweep repair and are not presented as evidence for or against it. At the Architect's direction, the
full local gate was not rerun after the machine-wide Gateway lock released. This report therefore makes no
claim that a complete local gate passed. The accepted broad clean-checkout evidence is hosted run 34667658171;
the accepted direct local evidence is limited to the explicitly inventoried focused runs above.

## Preserved work

No roster-card or pre-existing mission file was staged or committed as part of this repair. Those unrelated
working-tree changes remain preserved for a fresh Manager after the reviewed Wingman merge.
