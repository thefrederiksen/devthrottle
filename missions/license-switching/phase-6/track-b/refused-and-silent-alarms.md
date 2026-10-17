# Phase 6, Track B - a refused or silent machine is a license alarm

**Why.** Pull request 4113 made a refused report and a silent machine visible on the Licenses page and in the
Licenses log. But the page only helps someone who is looking at it. On 16 October nobody was looking, and a machine
was refused for hours while another was silent for ten. The owner should be told, once, and the alarm should stay up
until the problem is gone.

**What this piece does.** It adds two kinds of license alarm (contract section 9) that name the machine and the one
thing to do: `report-refused` and `machine-silent`. Each alarm is true exactly while its Licenses log row is open.

## What was built

- **`Licenses.LicenseMachineHealthAlarm`**, a new file beside `LicenseApproverAlarm`, using the same pattern. It
  declares the two kinds:
  - `report-refused`, "Report refused"
  - `machine-silent`, "Machine stopped reporting"

  On each raise it reads the open health rows (`LicenseStore.ListOpenMachineHealth`) and the machines of the read
  model. It then passes every open row of each kind to `LicenseAlarmService.RaiseAsync`. When no row is open, the list
  is empty, and that closes whatever was open.
- **The rows are the state.** The ingest opens and ends a refusal, and the minute check opens and ends a silence. The
  alarm is raised with what is open now. No second place has to remember to clear it, so the hooks from 4113 that I
  had suggested for the alarm ("raised here when a row was written") are not needed. Their comments now say that the
  row is the alarm's state.
- **Wired into the once-a-minute license check** (`GatewayHost.RunLicenseSwitchPassAsync`). It runs after the switch
  check, which writes the silence rows, and after the readiness check, and it raises for every tenant in that
  tenant's own scope. A failure in one tenant is logged and does not stop the other tenants.
- **The words.**
  - Refused, what is wrong: "machine-a is sending reports the Gateway refuses: it holds alice@example.com twice (once
    in use and once as a spare)." This is the refusal's own reason, which `LicenseStore.RefusalReason` reads back from
    the row's message. The message is now built from two named constants, so the reader and the writer cannot drift
    apart.
  - Refused, one thing to do: "Put right what the Licenses page says is wrong in the report from machine-a; the alarm
    closes by itself when a report from it is accepted."
  - When the reason would take the sentence past the 400 characters an alarm holds (an account email can be 320
    characters), what is wrong becomes "machine-a is sending reports the Gateway refuses; the Licenses page says
    why." The raise never throws over a long reason.
  - Silent, what is wrong: "machine-a has sent no report since 2026-10-11 03:12 UTC, so it is not switched: its
    Director may have stopped."
  - Silent, one thing to do: "Start the Director on machine-a, or check that machine-a is on and connected."
  - The machine is named by `LicenseSignInFold.Label`, the label every other license sentence uses.
- **One alarm for each machine, about no single account.** The alarm's machine is the machine's row in the overview.
  For a refused machine whose reports were never accepted, it is the stand-in row that the Licenses page lists it
  under (`LicenseDisplayFold.RefusedMachineId`). That stand-in identifier is stable, so the alarm and the page name
  the same machine, and a refusal whose reason changes keeps its one alarm.
- **Contract section 9.7** records the two kinds and their sentences.

## The tests

`src/CcDirector.Gateway.UnitTests/Licenses/LicenseMachineHealthAlarmTests.cs`. Every test runs the code as
production does:
- reports go through the real ingest into the real store, on a throwaway SQLite file;
- the silence rows are written by the real minute check (`LicenseSwitchService.TickAsync`);
- the alarms are raised through the real alarm service, with a fake mail service and a fake push.

- `ARefusedReport_OpensOneAlarm_NamingTheMachineAndTheReason_AndAnAcceptedReportClosesIt`: a refused report opens one
  alarm with the exact sentences, one push and one email. Thirty more refusals over an hour, raised every minute,
  leave it as one alarm with no second push or email. An accepted report closes it.
- `ARefusalThatChangesItsReason_KeepsTheOneAlarm_AndSaysTheNewReason`: a machine refused for a new reason keeps its
  one alarm, and the sentence now gives the new reason.
- `AMachineNeverAccepted_ButRefused_IsAlarmedUnderTheRowThePageListsItUnder`: the alarm's machine identifier is the
  one that the real overview fold gives the stand-in row.
- `AMachineSilentPastTheDeadLimit_OpensOneAlarm_AndReportingAgainClosesIt`: no alarm exactly at the 10-minute limit,
  one alarm past it with the exact sentences, and reporting again closes it.
- `NothingOpen_RaisesNothing_AndClosesNoOtherKind`: a raise with nothing open leaves another kind's alarm open.
- `OneAccountsRefusedMachine_RaisesNoAlarmInAnotherAccount`: one account's refusal raises nothing in a second account.
- `AReasonTooLongForAnAlarm_SendsTheOwnerToThePage_AndNeverBreaksTheRaise`: a reason built around a 300-character
  email falls back to the page sentence, and both sentences stay within the 400-character limit.

**Not covered by a test:** the one line in `GatewayHost.RunLicenseSwitchPassAsync` that calls `TickEveryTenantAsync`.
The approver alarm's identical line is not covered either. The live proof below is what covers it.

## The gate

GATE_LINES_GO_HERE

## What the live proof should look for (after a deploy; this piece deploys nothing)

- A machine whose report is refused shows the red license banner within a minute, with "Report refused" as its
  title, the machine's name and the reason. The owner receives one email and one push.
- The banner clears within a minute of that machine's next accepted report, and the Licenses log has an
  `alarm-closed` row.
- A Director that is stopped for more than 10 minutes raises "Machine stopped reporting" for its machine, and starting
  it again clears the alarm.
