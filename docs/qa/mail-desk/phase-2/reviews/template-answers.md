# Mail Desk phase 2 - the Developer's answers to the review of the template and its draft offer

The review is `template-review.md` beside this file (verdict: accepted, no blocking finding). It reviewed commit
7814e6917. The answers below are in the commit after c70bd9bef on branch `mail-desk-p2-template`. Date: 18 October 2026.

| Note | Answer | What changed |
|---|---|---|
| A. A template the owner imported himself is offered once more | **Accepted.** One draft the owner never asked for is a small harm, and the fix makes the rule simpler. | `VersionHoldingThisTemplate` now compares every version in the history, whoever wrote it, not only the versions the Gateway wrote. Test: `Offer_TemplateImportedByAPersonThenEdited_IsNotOfferedAgain` - the reviewer's reproduction, now answering `AlreadyOffered` with version 1 and storing nothing. |
| B. A lost race is reported as a defect | **Accepted.** The Tech Lead ruled that the offer will also run at Gateway start for every account whose switch is on, so two offers at once become a real case, not a theory. | A `Stale` answer from the store no longer becomes `Refused`. The offer reads again: if the winner stored this template, the answer is `AlreadyOffered`. If the winner stored something else, the answer is a new outcome, `LostRace`: nothing stored, and the next offer decides again. Both are logged as a race resolved, not as a defect. `Refused` is left for the check's and the store's other refusals, which are defects. Tests: `Offer_AnotherOfferStoresTheTemplateFirst_AnswersAlreadyOfferedNotRefused` and `Offer_OwnerSavesDuringTheOffer_LostRaceAndStoresNothing`. The race is made deterministic through the injected clock, which the save reads just before it writes - no thread, no sleep, no real clock. |
| C. The offer is unwired | **Accepted as open work, no change.** The Tech Lead ruled that the wiring waits for phase 1's `MailDeskSwitch`. | None. What remains: build the offer in `GatewayHost` with `MailDeskSwitch.IsOn`; call `Offer(tenant)` in `POST /gateway/admin/mail-desk` once the switch is set on; and, per the Tech Lead's decision, call it at Gateway start for each account whose switch is on. |

The review also noted that the brief left out `decisions --channel <address>`. That was already fixed in c70bd9bef,
together with the four `remove` commands and the `rules history` and `channel list` flags from section 6 of the phase 1
contract.

## Tests after these answers

`cd src/CcDirector.Gateway.UnitTests; DOTNET_USE_POLLING_FILE_WATCHER=1 ~/.dotnet/dotnet test --filter Category=MailDesk`:
26 tests, 22 pass, 4 fail. The 4 are the same ones the review verified, and each fails only because `checks/screen.py`
is not yet in the phase branch. When it lands, this branch is rebased and the 23 + 3 = 26 are confirmed to pass.
