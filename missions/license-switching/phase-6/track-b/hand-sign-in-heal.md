# Phase 6, track B: the Director heals a hand sign-in

## Why

On 16 October a machine was signed in by hand (`claude auth login` straight into its default Claude
config) to an account it already held as a spare. Its Director then reported that account twice - once
in use, once as a spare - and the Gateway refused every report from it (`an account held twice`). The
account the machine had been on before was overwritten and lost.

## What was built

**The key store heals it** (`tools/cc-devthrottle/src/license_store.py`, `_heal_hand_sign_in`). When the
account in the default folder is also held as a spare, the spare is stale (the active sign-in is the
newer one). The store moves the spare's folder, whole, into `<store>/hand-sign-in-backups/` as
`<account>-<UTC time>`, with `-2`, `-3` ... when that name is taken. Nothing is deleted. The backup is
outside `spares/`, so it is never listed, renewed or switched to again. The move is journaled like every
other store change, so a crash part way is finished by the next run, and each step names its own undo
(rename the folder back) so a heal that cannot finish puts the spare back. On macOS the key is a Keychain
item named after its folder's path, so the heal moves the item to the backup's name too, deleting the old
item only once the new one is proven to hold the same key. In the one case where the spare holds the
very same key as the default folder (a duplicate no store operation leaves behind), the duplicate is
dropped from the backup under the store's usual rule, so a key never ends up in two places.

**Where it runs, and why there.** Inside the store, holding the store's own lock, as the first thing
`held_accounts()`, `switch()` and `add()` do after finishing an interrupted operation. So:

- it cannot race a switch, a renewal or a sign-in's write: all of them hold the same lock, and a store
  read that finds the lock taken is refused whole (`switch-in-progress`), so the heal simply happens on
  the next round;
- the Director only runs the tool while it is the key holder (`LicenseKeyHolderLock`), so only one
  Director on the machine can ever trigger it;
- the command line and the Director agree, because the code lives once, in the store - and a switch
  never meets a held-twice account either (it used to be refused with "would overwrite").

**The report says it once.** `license list --json` and `license usage --json` now carry `healed`, the
heals that run made (an email, the folder moved from and to, the time; never a key). The Director reads
it in `CcDevthrottleLicenseCommandLine.ReadHandSwitches`, logs what was moved from where to where, and
the usage reporter sends a `changed` report at once carrying the new contract field
`LicenseUsageReport.SwitchedByHand` (account email and time only). The field is cleared once a report
carrying it has been sent; a send that fails keeps it for the next report; if another account became
active before the report went, the field is dropped rather than sending a report the Gateway would call
malformed.

**The Gateway writes one log entry** (`LicenseStore.ApplyReport`, kind `switched-by-hand`), for example:
"machine-a was switched to account-b@example.com by hand; the old spare for account-b@example.com was
moved to a backup on the machine." When the machine's previous accepted report had another account
active and this report no longer holds it, the entry adds: "The account it was on before,
account-a@example.com, is no longer held on the machine: the sign-in by hand overwrote it." Nothing tries
to recover that account. The ingest refuses a report whose `SwitchedByHand` is not its active holding.
The Gateway edit stops at that log entry and that check; `LicenseDisplayFold` is untouched (the page
shows an unknown kind as "Note" with the entry's sentence until the fold names it).

The contract is updated: `missions/license-switching/contract.md` section 2.2 (the type) and new
section 2.6.

## The tests, in plain terms

Key store (`tools/cc-devthrottle/tests/test_license_store.py`; every test runs twice, on the credentials
file and on the macOS Keychain through a fake `security`):

- `test_heal_ActiveAlsoHeldAsSpare_SpareMovesWholeToABackupAndIsHeldOnce` - after a hand sign-in over a
  spare, the store lists the account once, as active; the spare folder is gone from `spares/`; the backup
  folder holds exactly the spare's files, byte for byte, and the spare's key; the store log records what
  moved from where to where and holds no key.
- `test_heal_NothingToHeal_TouchesNothing` - a normal machine is left exactly as it was.
- `test_heal_NeverDeletes_TwoHealsOfOneAccountKeepBothBackups` - two heals of one account, even in the
  same second, leave two backups with two different names, each holding its own key.
- `test_heal_LockTaken_DoesNothing_AndTheNextRoundHeals` - while another holder has the store lock, the
  read is refused as `switch-in-progress` and nothing changes; the next read, with the lock free, heals.
- `test_heal_SpareHeldTheSameKey_TheDuplicateIsRemovedSoTheKeyIsInOnePlace` - a duplicate key is not
  kept in the backup, so the key is in exactly one place.
- `test_heal_CrashAtEveryChangePoint_TheNextRunFinishesTheHeal` - a crash at every single change point
  of the heal is finished by the next run: one backup, the account held once, the heal reported, every
  sign-in in exactly one place.
- `test_exchange_ActiveAccountAlsoHeldAsSpare_TheStaleSpareIsHealedAwayFirstAndTheSwitchRuns` (was
  `..._IsRefusedRatherThanOverwritingASignin`) - a switch on a machine in that state now heals first and
  then runs; nothing is overwritten.

Command line (`tests/test_license_cli.py`, `tests/test_license_usage.py`):

- `test_list_Json_HandSignInOverASpare_IsHealedAndSaysSoOnce` - `license list --json` heals, lists the
  account once and names the heal in `healed`; the next list has an empty `healed`.
- `test_list_Text_HandSignInOverASpare_WarnsWhatWasMoved` - the text form says on standard error what
  was moved.
- `test_license_usage_Json_IsTheContractShapeAndHasNoToken` - updated: the usage envelope now has `healed`.

Director (`src/CcDirector.Core.UnitTests/Licenses`):

- `ReadHandSwitches_AHealedRow_IsReadAsEmailAndTimeOnly` - a healed row from the tool becomes an email
  and a UTC time.
- `ReadHandSwitches_NoHealedRows_IsEmpty` - an older tool that prints no `healed` healed nothing.
- `ReadHandSwitches_ARowNotInTheToolsShape_Throws` - a row with no time, no email or no folders is a
  broken answer, not a silent skip.
- `TickAsync_TheStoreHealedAHandSignIn_TheNextReportSaysSoOnceAndHoldsTheAccountOnce` - the next report
  goes at once, holds the account once and carries `SwitchedByHand`; the report after it does not.
- `TickAsync_ASendThatFails_KeepsTheHandSwitchForTheNextReport` - a dropped tunnel does not lose it.
- `TickAsync_TheMachineMovedOnBeforeTheReport_TheHandSwitchIsDroppedNotSentAsAMalformedReport`.

Gateway (`src/CcDirector.Gateway.UnitTests/Licenses/LicenseHandSwitchTests.cs`):

- `Malformed_TheReportFromBeforeTheHeal_HoldsTheAccountTwice_AndIsRefused` - the 16 October report shape
  is refused by the real `LicenseUsageIngest.Malformed`.
- `HandSignInHealed_TheReportHoldsTheAccountOnce_IsAccepted_AndTheGatewayWritesOneLogEntry` - the tool's
  JSON after a heal, read by the Director's real parsers and built into a report by the real reporter,
  passes the real `Malformed` check, is accepted by the real ingest, and the Gateway writes exactly one
  `switched-by-hand` entry naming the machine, the account and the overwritten previous account.
- `ApplyReport_HandSwitchWhereThePreviousAccountIsStillHeld_DoesNotCallItLost`.
- `ApplyReport_AReportWithoutTheField_WritesNoHandSwitchEntry`.
- `Malformed_AHandSwitchThatIsNotTheActiveHolding_IsRefused` - another account, or none.

Revert check: with the heal call removed from `_settle`, the heal tests fail (12 failed; only the two
"nothing to heal" runs pass), and pass again with it restored.

## The gate

Recorded in the pull request after the run on the committed change: the default gate, the parked gate
(the Gateway was touched), and the Python license tests, which ran 317 passed in a virtual environment
with the tool's own dependency floors (the machine's own Python has Click 8.1.8, below the tool's floor
of 8.2.1, which makes every command-line test fail before it starts).

## What the live proof should look for

On a machine whose Director has license switching on, after someone signs an account in by hand that
the machine holds as a spare: within 30 seconds the Director log shows "hand sign-in healed: account=...
moved from ... to ...", the store folder has a new `hand-sign-in-backups/<account>-<time>` folder, the
Licenses page log shows one "Note" entry "... was switched to ... by hand ...", and the Gateway log shows
the machine's reports ACCEPTED again (no more "an account held twice").
