# Answers to the review of pull request 3854 (the contract types)

Review: `missions/license-switching/reviews/3854-contract-types-review.md`. Proof:
`missions/license-switching/proof/license-messages-contract.md`, section "Second round".

## Finding 1 - the guard does not scan every License type: accepted and fixed

The scan no longer filters by kind. It takes every type in the contracts namespace whose name, or the
name of a type it is nested in, starts with `License`, so a class, struct, record, abstract class,
static class or interface is scanned alike. It inspects static and instance properties of any visibility,
and instance fields (static fields are the vocabulary words and numbers, such as
`LicenseSwitchReasonCodes.NotKeyHolder`, which are never sent as a member of a message).

The scan also checks itself: `Offenders_EveryDeclarationForm_IsCaught` hands it one probe of each form
and asserts every forbidden name is reported, and `LicenseTypes_FoundByTheScan_IncludeStaticTypes`
asserts the static classes are in the scan.

Proven by mutation on the real file: a `readonly struct` with `AccessToken` (the form that passed all
10 tests before) and a static `RefreshToken` property on `LicenseContract` turned the test red with
both names; restored, it is green again. Recorded in the proof.

## Finding 2 - the product name in code comments: declined

The rule in `CLAUDE.md` is headed "NO Claude / Claude Code / Anthropic / AI attribution", and its
purpose, stated there, is to stop the repository advertising the assistant that wrote the code. The
two comments do not attribute anything: they name the product whose subscriptions this feature
switches - the thing being managed, exactly as `MISSION.md` and `contract.md` name it throughout, and as
existing types such as `ClaudeAccountStore` already do. Removing the name would make the comments
vaguer about what they describe without serving the rule's purpose. Commits and the pull request
carry no attribution.

## Finding 3 - short forms in the prose: accepted and fixed

In `LicenseMessages.cs`: the time-zone short form is gone from the member comments; one sentence in the
file header says every time is in Coordinated Universal Time, which the "Utc" ending of a name says. The
wire-format short form is replaced by "carried as text in `PayloadJson`"; "hex" is "hexadecimal digits";
"id" is "identifier". The proof's "switch id" is "switch identifier". Identifiers (`SentAtUtc`,
`PayloadJson`, `Guid`) are unchanged.
