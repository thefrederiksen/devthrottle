# Answer - the review of pull request 4004 (the definition routes, the command line and the report)

The review is `review-routes.md` beside this file. All three findings are accepted and fixed, as the Tech Lead ruled
them, and the read-route logging noted in the review is added. Each fix has a test.

## Finding 1 - a successful publish or restore could be answered with a 500; a diff could be a 500

**Accepted. Fixed as ruled.**

- `Publish` and `Restore` now work out "what changes" BEFORE the store writes
  (`FactoryDefinitionEndpoints.ChangesAgainstPublished`). When the version published until now no longer passes the
  format check, the changes are listed against nothing and the answer carries a notice naming the version, the file
  and line of its first problem, and that problem: "Version 1, published until now, no longer passes the format
  check at seats/writer.yaml:5 ('colour' is not a key of a seat file. ...), so what changes is listed as if nothing
  had been published before." The write goes ahead. The content being published has just passed the whole check, so
  comparing it can no longer throw.
- `GET .../diff` reads both versions with the format check first and, when either does not parse, answers 422 with
  `outcome: "DoesNotParse"`, the version, and where: "Version 1 of the definition of devthrottle no longer passes
  the format check at seats/writer.yaml:5, so what changes cannot be shown. Compare other versions with from and to."
  It is never a 500.
- Tests, each over a definition the format rules refuse that is stored and published straight through the store, as
  one published before the rules tightened would be:
  `A_PUBLISH_OVER_A_VERSION_THAT_NO_LONGER_PARSES_SUCCEEDS_and_lists_what_changes_from_nothing_with_a_notice_saying_why`,
  `A_RESTORE_OVER_A_VERSION_THAT_NO_LONGER_PARSES_SUCCEEDS_with_the_same_notice`,
  `A_DIFF_FROM_OR_TO_A_VERSION_THAT_NO_LONGER_PARSES_IS_422_IN_WORDS_naming_the_version_and_where`.
  In the old order, `FactoryDefinitionChanges.Compare` read this same content after the store had published and its
  `ReadOrThrow` throws on it; these tests were not run against the old code to watch that happen.

## Finding 2 - the unknown-flag test proved nothing for save, publish and restore

**Accepted. Fixed.** `test_anUnknownFlag_Fails_AndCallsNothing` now passes every required option with the unknown flag
(`save --dir <folder> --expected-version 0`, `publish --version 1`, `restore --version 1`), so the unknown flag is
the only reason to refuse, and asserts exit 2, the words "No such option", the flag's name, and that the Gateway was
not called.

## Finding 3 - publishing the version that is already published was answered "was not published"

**Accepted. Fixed as ruled.** The store's own rulings now come before the check. A publish goes straight to the store
when the version does not exist, is already published, or is older than the published one; a restore does so when
the version does not exist, is the published one, or the caller's expected version is not the newest. Only a write
the store would carry out is checked. Tests:
`PUBLISHING_THE_VERSION_THAT_IS_ALREADY_PUBLISHED_SAYS_SO_even_when_the_account_changed_since` (409,
"version 1 is already the published definition of devthrottle"),
`PUBLISHING_A_VERSION_OLDER_THAN_THE_PUBLISHED_ONE_SAYS_SO_even_when_the_account_changed_since` (409,
`OlderThanPublished`), and `RESTORING_THE_PUBLISHED_VERSION_SAYS_SO_even_when_the_account_changed_since` (409,
`AlreadyPublished`). Each removes a fleet skill the published version names before asking, which is what made the
old order answer 422.

## Noted, not findings

- **Read-route logging: taken.** The four reads now log what they answered: the published and newest versions, the
  page asked for and how many versions it held, and whether one version was found.
- **The Python mission line after the help fix.** It is run again on the final commit; the result is in the report's
  table.
- **Elided timestamps in the report, and `ConnectedMachinesOf`.** No change: the transcript carries the full text,
  and decision 6 of the report records the second.

## Tests after the fixes

`FactoryDefinitionEndpointsTests` 42 and `FactoryDefinitionRoutesThroughThePipelineTests` 27: 69 passed, 0 failed.
`test_factory_definition_ops.py`: 38 passed. The mission's seven lines are run again after the rebase onto the merged
pull request 4003; their results are in the report.
