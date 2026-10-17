# License Switching - the Gateway routes the Licenses page reads

What the Gateway answers the Licenses page of the Cockpit with (phase 3 builds from this file). The types are in
`src/CcDirector.Gateway.Contracts/LicenseCockpitDtos.cs`, the routes in `src/CcDirector.Gateway/Api/LicenseEndpoints.cs`,
and every verdict is folded in one place, `src/CcDirector.Gateway/Licenses/LicenseDisplayFold.cs`.

Every example below is a real answer: it was produced by running the route handlers over a store filled with the
reports of two machines ("the-windows-machine" and "the-mac") and three accounts, with the Gateway's clock at
2026-10-11 03:12:00 UTC. Nothing was typed by hand.

## 1. Rules that hold for every route

1. **The caller's own account, always.** The account is taken from the request's credential (the Cockpit's device
   key, a machine's own credential, or a session key). No route takes an account in the path, the query or a body.
   A request with no account bound is answered `403` with `{ "code": "no_account", "error": "..." }`.
2. **The page is dumb (`CLAUDE.md` rule 7).** Every judgement arrives finished: a status word with its label, its
   tone and a sentence; a window's level; a sign-in's expiry verdict; whether Switch now is offered and the sentence
   saying what it would do or why it is not offered. **The page never compares a percentage with 97 and never
   compares a time with now.** If the page needs a judgement that is not in the answer, that is a gap to fix in
   `LicenseDisplayFold`, not a branch to write in a view.
3. **Words and their labels travel together.** `status` is a fixed word for a test or a style hook; `statusText` is
   what is shown. Show the text. Colour comes from the `...Tone` word, one of `good`, `warning`, `bad`, `neutral`.
4. **Times.** Every time is Coordinated Universal Time in ISO 8601 with a `Z`. Beside every time that matters there
   is a sentence that already says how far away it is ("Resets in 1 hour 48 minutes"), true at `asOfUtc`. The page
   may show the time itself in the viewer's own time zone; it does not work out the distance.
5. **`asOfUtc`** is the Gateway's clock when the answer was folded. Reading the routes again every 30 to 60 seconds
   keeps the sentences true; the Directors report every 2 minutes.
6. **An account whose machines have never reported** is not an error: `200`, `isEmpty: true`, empty lists, and
   `emptyText` holds the sentence to show (section 5).
7. **Never a sign-in key.** The Gateway holds none. These answers carry emails, plan words, numbers, times, words
   and sentences, and a test (`LicenseMessagesCarryNoKeyTests`) holds every member NAME of these types to that.
   What that test does not and cannot show: two values are free text a Director wrote and the Gateway passes on
   unchanged - the detail of a read error whose word this build does not know (`readErrorText`), and a log row's
   `message`. The contract obliges the Director never to put anything from a sign-in in them, and the Gateway caps
   their length; nothing on the Gateway's side reads their content.
8. **JSON** property names are camel case. A list is never null; a missing value is `null`.
9. **A machine is a machine name AND an operating system user.** Two users of one computer are two machines here,
   each with its own sign-ins. Wherever a list names a machine it is the same four fields - `machineId`,
   `machineName`, `osUser`, `label` - and `machineId` is the `machineId` of its row in the overview's `machines`.
   `label` is what to call it: the machine name, with the user in brackets only when another machine has the same
   name ("the-windows-machine (owner)"). Every sentence the Gateway's display fold writes calls a machine by its
   label, on all three routes, and a log row carries the same four fields. Show `label`; never build a name from the
   other fields.

   The decision engine's own sentences follow the same rule: `switchTargets[].unavailableText`, the reason a machine
   is left out inside `switchNowText` and inside a forecast machine's `text`, a refusal of Switch now, and the owner
   email all name a machine by its label ("account-c@example.com has room;
   the-windows-machine (second) holds no sign-in for it.").

**Decided here (the Tech Lead's rulings on the review of this pull request):**

- **Any session key can read these three routes.** That means every agent session of the DevThrottle account can
  read all of its Claude account emails, its machine names, their operating system users and the switch log. That
  is accepted: an agent has to be able to say which account a machine is on and why it was switched. No session key
  can switch anything or set a renewal date (section 6).
- **The log is one row per machine, and the page lists it that way.** One switch of an account that is on three
  machines is three rows, each with its own state, reason and timings, because each machine can end differently.
  The rows carry no identifier that groups them and the page does not group them.
- **The renewal date of a subscription is a date a person sets, and no report can carry it.** Goal 1's "renewal
  date" is the date the SUBSCRIPTION is billed again. No Director can read that from a sign-in. Its two fields are
  on every account - `renewsOn` and `renewsText` - and the owner sets the date with the route of section 6. Until a
  date is set for an account, its `renewsOn` is null and its `renewsText` is "Renewal date not set". It is never a
  guess. `signInExpiry` is a DIFFERENT date - when a machine must be signed in to the account again - and the page
  must never label it "renewal".
- **"Renewal" is two different things in these answers, and the page must keep them apart.** `renewsOn` and
  `renewsText`, on an ACCOUNT, are the SUBSCRIPTION's billing renewal: the day the provider charges for it again.
  `renewalFailedAtUtc`, `renewalFailedText` and `renewalFailedTone`, on a HOLDING, are a machine renewing its
  SIGN-IN: its stored sign-in being exchanged for a fresh one before it expires. A failed sign-in renewal says
  nothing about the subscription, and it is shown on the holding beside that sign-in's expiry - never beside
  "Renews 17 Oct" as if the two were one thing.
- **These field names are final.** Phase 3 builds to them. Nothing later renames them.

Who may call them: the Cockpit and the phone (a device key), a machine's own credential, and a session key (an
agent) may call the three reads - they are in `SessionKeyGuard`'s allow list. The two writes are for a signed-in
person only: the owner's own phone or browser, and nobody else (section 6).

## 2. `GET /gateway/licenses` - the accounts and the machines

`LicenseOverviewResponse`. Accounts are in account email order, machines in machine name order.

### An account (`LicenseAccountDto`)

| Field | Meaning |
|---|---|
| `accountEmail` | The Claude account. |
| `plan`, `planText`, `rateLimitTier` | The plan word as the sign-in names it, the same word ready to show ("Max", or "Not known"), and the rate limit tier. |
| `status`, `statusText`, `statusTone`, `statusDetail` | The account's ONE status (the table below), its label, its tone, and one or two sentences saying what it means now. |
| `isInUse` | A machine is working on it. |
| `fiveHour`, `sevenDay` | The two usage windows: `utilization` (0 to 100, or null), `utilizationText` ("96%", rounded down, or "Not known"), `level`, `resetsAtUtc`, `resetsText`. |
| `isStale` | The numbers are too old to trust, or there are none. Show them muted. |
| `readAtUtc`, `readAgeText` | When the numbers were read and the sentence saying how old they are and on which machine ("Read 7 minutes ago on the-mac", or "Never read on any machine"). |
| `readError`, `readErrorText` | The word and the sentence when the last read failed; both null when it worked. |
| `extraUsage` | `state` (`on`, `off`, `not-known`), `text` ("Paid extra usage is on (12% used)", "Paid extra usage is off", "Paid extra usage not known"), and `utilization` when the provider gave one. |
| `renewsOn`, `renewsText` | The date the subscription is billed again, as `yyyy-MM-dd` (a date with no time and no zone), and its sentence: "Renews 17 Oct", or "Renewal date not set". `renewsOn` is null when no date is set (section 6 has the route that sets it and every sentence). Show `renewsText`. |
| `barredUntilUtc`, `barredText` | Set while the account is barred from the AUTOMATIC choice of a target, because it triggered a switch and its window has not reset: until when, and the sentence to show beside the account ("Left out of the automatic choice for 2 hours, until its 5-hour window resets: it triggered a switch. It can still be chosen by hand."). Both null when it is not barred. A barred account can still be chosen by hand - its row in another account's `switchTargets` stays `canChoose: true`. |
| `signInExpiry` | The sign-in for this account that expires FIRST, across every machine holding it: `expiresAtUtc`, `machine` (the machine it is on, rule 9), `verdict` (the expiry verdict below), `text` ("Sign-in on the-windows-machine expires in 2 days 5 hours", "Sign-in on the-mac expired 3 hours ago") and `tone`. When no machine holding the account gave an expiry: `expiresAtUtc` and `machine` are null, `verdict` is `not known` and `text` is "Sign-in expiry not known". When no machine holds the account: the same, with "Not signed in on any machine". This is the sign-in expiry, not the subscription's renewal date (section 1). |
| `activeOnMachines`, `spareOnMachines` | The machines working on it, and the machines holding it as a spare - each one the four fields of rule 9. |
| `canSwitchNow` | The Switch now control is offered for this account. |
| `switchNowText` | What Switch now with no target would do - the automatic moves, each machine to the best account with room it is ready for, which can be two accounts ("Moves the-mac and the-windows-machine to account-b@example.com.", "Moves machine-a to account-b@example.com; machine-b and machine-c to account-c@example.com, the best account with room they are ready for.") - or the sentence saying why it is not offered. Always a sentence. |
| `switchNowTargetAccountEmail` | The Gateway's choice, the best account with room, when a Switch now with no target would be accepted; null when it would not. The page pre-selects it and, when it is the one picked, sends NO target (section 6), so the moves are the ones `switchNowText` describes. |
| `switchTargets` | Every other account as a target: `accountEmail`, `canChoose`, `unavailableText` (the sentence when it cannot be chosen), `isAutomaticChoice`. Empty when no machine on this account can be switched. |

**The status of an account.** Exactly one, the first that applies. What the owner must act on outranks what the
Gateway handles by itself:

| Order | `status` | `statusText` | Tone | When |
|---|---|---|---|---|
| 1 | `not-held` | Not on any machine | neutral | No machine holds a sign-in for it any more. |
| 2 | `sign-in-needed` | Sign-in needed | bad | On some machine its sign-in has expired (at or before now), or the provider refused it (`unauthorized`, `refresh-failed`). It comes before `stale` because it is why the numbers go stale. |
| 3 | `stale` | Stale | warning | Its freshest reading is older than 6 minutes (read where it is active) or 30 minutes (read as a spare), or it was never read. Nothing can be said about its use. |
| 4 | `at-limit` | At limit | bad | A fresh reading shows a window at or over 97% whose reset is still ahead. |
| 5 | `sign-in-expires-soon` | Sign-in expires soon | warning | A sign-in for it expires within 3 days. |
| 6 | `in-use` | In use | good | A machine is working on it. |
| 7 | `has-room` | Has room | good | Not in use, and both windows are under 97%. |
| 8 | `use-not-known` | Use not known | warning | The reading is fresh but does not say what its use is now: a window is missing, or a window reset since the reading. |

**The level of a window** (`level`), the first that applies: `not-known` (no number), `stale` (the account's
reading is too old to trust - the number is still given, and it says nothing about the window now), `reset-since`
(the window has reset since the reading, so the number is from before the reset), `at-limit` (97% or more, reset
still ahead), `normal` (under 97%). **The level alone is enough to colour a bar.** A stale account's windows are
`stale`, never `at-limit` and never `normal`, so the page never has to weigh a window's level against its account's
status.

**Switch now on the page.** Offer the control when `canSwitchNow` is true and show `switchNowText` beside it; when it
is false show `switchNowText` in its place. If the page lets the owner pick a target, list `switchTargets`: an entry
with `canChoose: false` is shown disabled with its `unavailableText`. `canSwitchNow` can be true while
`switchNowTargetAccountEmail` is null: no target is chosen automatically (for example the only one is barred until
its window resets), and one can still be chosen by hand; `switchNowText` says so.

### A machine (`LicenseMachineDto`)

| Field | Meaning |
|---|---|
| `machineId` | Stable for this machine and operating system user; use it as the list item's identity. |
| `machineName`, `osUser`, `label` | The machine, the operating system user whose Claude sign-ins these are, and what to call it (rule 9). |
| `activeAccountEmail` | The account it is on; null when it is signed in to none. |
| `status`, `statusText`, `statusTone` | `not-reporting` (Not reporting, warning), `switching` (Switching, neutral), `signed-out` (Signed out, warning), `reporting` (Reporting, good) - the first that applies. |
| `isStale` | Its last report is older than 6 minutes. It is not sent a switch until it reports again. |
| `lastReportAtUtc`, `lastReportText` | When the Gateway took its last report, and "Last report 1 minute ago". |
| `active` | Its active sign-in (the shape below); null when signed in to none. |
| `spares` | The spares it holds, in account email order. |
| `switchInProgress` | Null, or `switchId`, `fromAccountEmail`, `toAccountEmail`, `text` - the switch it is in the middle of and one sentence saying where it stands. The two emails are "" when only the machine's own report says a switch is running. |
| `heldUntilUtc`, `heldText` | **Both null unless the machine is held out of the automatic switch** because its switch keeps failing the same way (contract section 5.8). Then: the time it is held until, and the Gateway's sentence saying so, why and until when. The page draws `heldText` in the schedule, under the machine's status, exactly as given; never compare `heldUntilUtc` with now. Switch now is still offered for its account, and still sends the machine a switch at once. |

**A machine that is held** (`heldUntilUtc`, `heldText`). The sentence is "Held out of the automatic switch for
{how long}, until {yyyy-MM-dd HH:mm UTC}: its switch from {account} to {account} has failed more than once in a row
({the reason}). Switch now still sends it at once." The reason in brackets is the Gateway's own sentence for the
machine's reason word, or "the machine's reason word: {word}" when this Gateway has no sentence for it. Both fields
are null again as soon as the hold has passed, a later switch of the machine is recorded, or the machine reports
another account as active. The example below shows no machine held, so both are null there.

A sign-in a machine holds (`LicenseMachineHoldingDto`): `accountEmail`, `isActive`, `roleText` ("Active" or "Spare":
show it; `isActive` is the same fact for a test or a style hook), `signedInAtUtc`, `signedInText`
("Signed in 21 days ago", or "Sign-in date not known"), `signInExpiresAtUtc`, `expiryVerdict`, `expiryText`,
`expiryTone`, `renewalFailedAtUtc`, `renewalFailedText`, `renewalFailedTone`, `isReadingStale`, `readError`,
`readErrorText`. A read error that leaves the last numbers standing ("The machine could not reach the provider.")
ends "The numbers shown are the last ones read." only when some machine has read the account; when none ever has, it
ends "No usage has been read for this account yet.", because the card has no numbers to point at.

**A failed renewal of the sign-in** (`renewalFailedAtUtc`, `renewalFailedText`, `renewalFailedTone`) - the machine
renewing its SIGN-IN, not the subscription's billing renewal (section 1): when the machine last tried to
renew this sign-in and failed, the sentence for it, and its tone. **All three are null when the last renewal worked
or is not known.** The machine's Director says it in each usage report, and the next report that says the renewal
worked clears all three. When `renewalFailedText` is not null, show it on the holding in its tone; when it is null,
show nothing. Section 6 has the sentences and the tone rule.

**The expiry verdict** (`expiryVerdict`, the words of the contract, section 5.7): `expired` (at or before now, tone
`bad`), `expires soon` (within 3 days, `warning`), `fine` (`good`), `not known` (the machine did not say, `neutral`).
`expiryText` is the sentence: "Sign-in expires in 9 days", "Sign-in expired 2 hours ago", "Sign-in expiry not known".

### Example

```json
{
  "asOfUtc": "2026-10-11T03:12:00Z",
  "isEmpty": false,
  "emptyText": null,
  "accounts": [
    {
      "accountEmail": "account-a@example.com",
      "plan": "max",
      "planText": "Max",
      "rateLimitTier": "default_claude_max_20x",
      "status": "in-use",
      "statusText": "In use",
      "statusTone": "good",
      "statusDetail": "In use on the-mac and the-windows-machine.",
      "isInUse": true,
      "fiveHour": {
        "utilization": 96,
        "utilizationText": "96%",
        "level": "normal",
        "resetsAtUtc": "2026-10-11T05:00:00Z",
        "resetsText": "Resets in 1 hour 48 minutes"
      },
      "sevenDay": {
        "utilization": 61,
        "utilizationText": "61%",
        "level": "normal",
        "resetsAtUtc": "2026-10-15T09:00:00Z",
        "resetsText": "Resets in 4 days 5 hours"
      },
      "isStale": false,
      "readAtUtc": "2026-10-11T03:11:58Z",
      "readAgeText": "Read less than a minute ago on the-windows-machine",
      "readError": null,
      "readErrorText": null,
      "extraUsage": {
        "state": "off",
        "text": "Paid extra usage is off",
        "utilization": null
      },
      "renewsOn": null,
      "renewsText": "Renewal date not set",
      "signInExpiry": {
        "expiresAtUtc": "2026-10-20T03:12:00Z",
        "machine": {
          "machineId": "f26a799d85a244129a27ba10b8cb3427",
          "machineName": "the-windows-machine",
          "osUser": "owner",
          "label": "the-windows-machine"
        },
        "verdict": "fine",
        "text": "Sign-in on the-windows-machine expires in 9 days",
        "tone": "good"
      },
      "activeOnMachines": [
        {
          "machineId": "94ebcb15a4bf40438dca738dd31189d9",
          "machineName": "the-mac",
          "osUser": "owner",
          "label": "the-mac"
        },
        {
          "machineId": "f26a799d85a244129a27ba10b8cb3427",
          "machineName": "the-windows-machine",
          "osUser": "owner",
          "label": "the-windows-machine"
        }
      ],
      "spareOnMachines": [],
      "canSwitchNow": true,
      "switchNowText": "Moves the-mac and the-windows-machine to account-b@example.com.",
      "switchNowTargetAccountEmail": "account-b@example.com",
      "switchTargets": [
        {
          "accountEmail": "account-b@example.com",
          "canChoose": true,
          "unavailableText": null,
          "isAutomaticChoice": true
        },
        {
          "accountEmail": "account-c@example.com",
          "canChoose": false,
          "unavailableText": "account-c@example.com has no room: it is at 99% of its 5-hour window.",
          "isAutomaticChoice": false
        }
      ],
      "barredUntilUtc": null,
      "barredText": null,
      "approver": null,
      "approverText": "No machine is set to approve sign-ins for this account. A machine that has its own browser profile for it approves there.",
      "approverTone": "neutral"
    },
    {
      "accountEmail": "account-b@example.com",
      "plan": "max",
      "planText": "Max",
      "rateLimitTier": "default_claude_max_20x",
      "status": "sign-in-expires-soon",
      "statusText": "Sign-in expires soon",
      "statusTone": "warning",
      "statusDetail": "The sign-in on the-windows-machine expires in 2 days 5 hours.",
      "isInUse": false,
      "fiveHour": {
        "utilization": 4,
        "utilizationText": "4%",
        "level": "normal",
        "resetsAtUtc": "2026-10-11T05:00:00Z",
        "resetsText": "Resets in 1 hour 48 minutes"
      },
      "sevenDay": {
        "utilization": 22,
        "utilizationText": "22%",
        "level": "normal",
        "resetsAtUtc": "2026-10-15T09:00:00Z",
        "resetsText": "Resets in 4 days 5 hours"
      },
      "isStale": false,
      "readAtUtc": "2026-10-11T03:05:00Z",
      "readAgeText": "Read 7 minutes ago on the-mac",
      "readError": null,
      "readErrorText": null,
      "extraUsage": {
        "state": "off",
        "text": "Paid extra usage is off",
        "utilization": null
      },
      "renewsOn": null,
      "renewsText": "Renewal date not set",
      "signInExpiry": {
        "expiresAtUtc": "2026-10-13T08:12:00Z",
        "machine": {
          "machineId": "f26a799d85a244129a27ba10b8cb3427",
          "machineName": "the-windows-machine",
          "osUser": "owner",
          "label": "the-windows-machine"
        },
        "verdict": "expires soon",
        "text": "Sign-in on the-windows-machine expires in 2 days 5 hours",
        "tone": "warning"
      },
      "activeOnMachines": [],
      "spareOnMachines": [
        {
          "machineId": "94ebcb15a4bf40438dca738dd31189d9",
          "machineName": "the-mac",
          "osUser": "owner",
          "label": "the-mac"
        },
        {
          "machineId": "f26a799d85a244129a27ba10b8cb3427",
          "machineName": "the-windows-machine",
          "osUser": "owner",
          "label": "the-windows-machine"
        }
      ],
      "canSwitchNow": false,
      "switchNowText": "No machine on account-b@example.com can be switched now: none is on it.",
      "switchNowTargetAccountEmail": null,
      "switchTargets": [],
      "barredUntilUtc": null,
      "barredText": null,
      "approver": null,
      "approverText": "No machine is set to approve sign-ins for this account. A machine that has its own browser profile for it approves there.",
      "approverTone": "neutral"
    },
    {
      "accountEmail": "account-c@example.com",
      "plan": "max",
      "planText": "Max",
      "rateLimitTier": "default_claude_max_20x",
      "status": "at-limit",
      "statusText": "At limit",
      "statusTone": "bad",
      "statusDetail": "At 99% of its 5-hour window, which resets in 1 hour 48 minutes.",
      "isInUse": false,
      "fiveHour": {
        "utilization": 99,
        "utilizationText": "99%",
        "level": "at-limit",
        "resetsAtUtc": "2026-10-11T05:00:00Z",
        "resetsText": "Resets in 1 hour 48 minutes"
      },
      "sevenDay": {
        "utilization": 40,
        "utilizationText": "40%",
        "level": "normal",
        "resetsAtUtc": "2026-10-15T09:00:00Z",
        "resetsText": "Resets in 4 days 5 hours"
      },
      "isStale": false,
      "readAtUtc": "2026-10-11T03:03:00Z",
      "readAgeText": "Read 9 minutes ago on the-windows-machine",
      "readError": null,
      "readErrorText": null,
      "extraUsage": {
        "state": "off",
        "text": "Paid extra usage is off",
        "utilization": null
      },
      "renewsOn": null,
      "renewsText": "Renewal date not set",
      "signInExpiry": {
        "expiresAtUtc": "2026-11-09T03:12:00Z",
        "machine": {
          "machineId": "f26a799d85a244129a27ba10b8cb3427",
          "machineName": "the-windows-machine",
          "osUser": "owner",
          "label": "the-windows-machine"
        },
        "verdict": "fine",
        "text": "Sign-in on the-windows-machine expires in 29 days",
        "tone": "good"
      },
      "activeOnMachines": [],
      "spareOnMachines": [
        {
          "machineId": "f26a799d85a244129a27ba10b8cb3427",
          "machineName": "the-windows-machine",
          "osUser": "owner",
          "label": "the-windows-machine"
        }
      ],
      "canSwitchNow": false,
      "switchNowText": "No machine on account-c@example.com can be switched now: none is on it.",
      "switchNowTargetAccountEmail": null,
      "switchTargets": [],
      "barredUntilUtc": null,
      "barredText": null,
      "approver": null,
      "approverText": "No machine is set to approve sign-ins for this account. A machine that has its own browser profile for it approves there.",
      "approverTone": "neutral"
    }
  ],
  "machines": [
    {
      "machineId": "94ebcb15a4bf40438dca738dd31189d9",
      "machineName": "the-mac",
      "osUser": "owner",
      "label": "the-mac",
      "activeAccountEmail": "account-a@example.com",
      "status": "reporting",
      "statusText": "Reporting",
      "statusTone": "good",
      "isStale": false,
      "lastReportAtUtc": "2026-10-11T03:11:00Z",
      "lastReportText": "Last report 1 minute ago",
      "active": {
        "accountEmail": "account-a@example.com",
        "isActive": true,
        "roleText": "Active",
        "signedInAtUtc": "2026-09-21T03:12:00Z",
        "signedInText": "Signed in 20 days ago",
        "signInExpiresAtUtc": "2026-10-21T03:12:00Z",
        "expiryVerdict": "fine",
        "expiryText": "Sign-in expires in 10 days",
        "expiryTone": "good",
        "renewalFailedAtUtc": null,
        "renewalFailedText": null,
        "renewalFailedTone": null,
        "isReadingStale": false,
        "readError": null,
        "readErrorText": null,
        "canRenewNow": false,
        "renewNowText": "The Director on the-mac is not connected, so a sign-in cannot be started there."
      },
      "spares": [
        {
          "accountEmail": "account-b@example.com",
          "isActive": false,
          "roleText": "Spare",
          "signedInAtUtc": "2026-10-06T03:12:00Z",
          "signedInText": "Signed in 5 days ago",
          "signInExpiresAtUtc": "2026-11-05T03:12:00Z",
          "expiryVerdict": "fine",
          "expiryText": "Sign-in expires in 25 days",
          "expiryTone": "good",
          "renewalFailedAtUtc": null,
          "renewalFailedText": null,
          "renewalFailedTone": null,
          "isReadingStale": false,
          "readError": null,
          "readErrorText": null,
          "canRenewNow": false,
          "renewNowText": "The Director on the-mac is not connected, so a sign-in cannot be started there."
        }
      ],
      "switchInProgress": null,
      "heldUntilUtc": null,
      "heldText": null,
      "canAddSpare": false,
      "addSpareText": "The Director on the-mac is not connected, so a sign-in cannot be started there.",
      "signInsInProgress": [],
      "directorId": "dir-mac",
      "canListBrowserProfiles": false,
      "browserProfilesText": "the-mac's Director is not connected, so its browser profiles cannot be listed."
    },
    {
      "machineId": "f26a799d85a244129a27ba10b8cb3427",
      "machineName": "the-windows-machine",
      "osUser": "owner",
      "label": "the-windows-machine",
      "activeAccountEmail": "account-a@example.com",
      "status": "reporting",
      "statusText": "Reporting",
      "statusTone": "good",
      "isStale": false,
      "lastReportAtUtc": "2026-10-11T03:12:00Z",
      "lastReportText": "Last report less than a minute ago",
      "active": {
        "accountEmail": "account-a@example.com",
        "isActive": true,
        "roleText": "Active",
        "signedInAtUtc": "2026-09-20T03:12:00Z",
        "signedInText": "Signed in 21 days ago",
        "signInExpiresAtUtc": "2026-10-20T03:12:00Z",
        "expiryVerdict": "fine",
        "expiryText": "Sign-in expires in 9 days",
        "expiryTone": "good",
        "renewalFailedAtUtc": null,
        "renewalFailedText": null,
        "renewalFailedTone": null,
        "isReadingStale": false,
        "readError": null,
        "readErrorText": null,
        "canRenewNow": false,
        "renewNowText": "The Director on the-windows-machine is not connected, so a sign-in cannot be started there."
      },
      "spares": [
        {
          "accountEmail": "account-b@example.com",
          "isActive": false,
          "roleText": "Spare",
          "signedInAtUtc": "2026-09-13T03:12:00Z",
          "signedInText": "Signed in 28 days ago",
          "signInExpiresAtUtc": "2026-10-13T08:12:00Z",
          "expiryVerdict": "expires soon",
          "expiryText": "Sign-in expires in 2 days 5 hours",
          "expiryTone": "warning",
          "renewalFailedAtUtc": null,
          "renewalFailedText": null,
          "renewalFailedTone": null,
          "isReadingStale": false,
          "readError": null,
          "readErrorText": null,
          "canRenewNow": false,
          "renewNowText": "The Director on the-windows-machine is not connected, so a sign-in cannot be started there."
        },
        {
          "accountEmail": "account-c@example.com",
          "isActive": false,
          "roleText": "Spare",
          "signedInAtUtc": "2026-10-10T03:12:00Z",
          "signedInText": "Signed in 1 day ago",
          "signInExpiresAtUtc": "2026-11-09T03:12:00Z",
          "expiryVerdict": "fine",
          "expiryText": "Sign-in expires in 29 days",
          "expiryTone": "good",
          "renewalFailedAtUtc": null,
          "renewalFailedText": null,
          "renewalFailedTone": null,
          "isReadingStale": false,
          "readError": null,
          "readErrorText": null,
          "canRenewNow": false,
          "renewNowText": "The Director on the-windows-machine is not connected, so a sign-in cannot be started there."
        }
      ],
      "switchInProgress": null,
      "heldUntilUtc": null,
      "heldText": null,
      "canAddSpare": false,
      "addSpareText": "The Director on the-windows-machine is not connected, so a sign-in cannot be started there.",
      "signInsInProgress": [],
      "directorId": "dir-windows",
      "canListBrowserProfiles": false,
      "browserProfilesText": "the-windows-machine's Director is not connected, so its browser profiles cannot be listed."
    }
  ]
}
```

An account that is barred from the automatic choice carries the bar in its last two fields - the rest of the account
is as above, and it can still be chosen by hand:

```json
"barredUntilUtc": "2026-10-11T05:12:00Z",
"barredText": "Left out of the automatic choice for 2 hours, until its 5-hour window resets: it triggered a switch. It can still be chosen by hand."
```

## 3. `GET /gateway/licenses/forecast` - the forecast and the schedule

`LicenseForecastResponse`. The rate of use comes from the readings of the last two hours, which the Gateway keeps in
memory: after a Gateway restart the rate is back within two reports (about four minutes), and until then the
sentences say there are not enough readings. A target is always "on today's numbers": the forecast does not project
the other accounts forward. A window rising so slowly that it would need longer than a week to reach 97% - longer
than any window lasts - is not expected to reach it, whatever its reset time.

| Field | Meaning |
|---|---|
| `nextSwitch` | Null when no switch is expected. Otherwise the soonest one: `fromAccountEmail`, `expectedAtUtc`, `window` (`five-hour` or `seven-day`), `windowText` ("5-hour window" or "weekly limit"), `targetAccountEmail` (the account the Gateway would switch to next - named whenever an account has room, even while no machine is ready for it; null only when no account has room), `machines` (the machines that would actually move - a machine not ready for any account with room is not listed - each the four fields of rule 9). |
| `nextSwitchText` | The sentence for it, or "No switch is expected.". When nothing would be switched it gives the real reason: no account has room (with each account's reason), an account has room and no machine is ready for it ("account-b@example.com has room; machine-a holds no sign-in for it."), or no machine on the account can be sent a switch. When some machines would move and one would not, the sentence names the ones that move and ends with each one that would not and why ("machine-d would not move: it holds no sign-in for account-b@example.com."). When every machine that could be switched is held because its switch keeps failing (contract section 5.8), the sentence ends "The automatic switch keeps failing and is held: it is tried again in {how long}, at {yyyy-MM-dd HH:mm UTC}. Switch now still sends it at once." - and so does that account's own `nextSwitchText`. |
| `accounts[]` | Per account: `isInUse`, `isStale`, the two windows, `expectedAtLimitUtc` and `expectedText` (when it is expected to reach 97% on either window), `nextSwitchTargetAccountEmail` and `nextSwitchText` (where its machines go at its next switch). |
| `accounts[].fiveHour`, `.sevenDay` | The window in the overview's own words - `utilization`, `utilizationText`, `level`, `resetsAtUtc`, `resetsText`, exactly as section 2 gives them for the same window - and then the forecast: `percentPerHour` (null when there is no rate), `rateText` ("28.5% an hour", "Not rising", "No rate yet"), `expectedAtLimitUtc`, `expectedText`. The schedule colours a window by `level` and shows `resetsText`; it never compares `utilization` with 97 or `resetsAtUtc` with now, and it does not need the overview to do either. |
| `machines[]` | Per machine: `machineId`, `machineName`, `osUser`, `label` (rule 9), `activeAccountEmail`, `nextAccountEmail` (the account it moves to next; null when there is none), `expectedSwitchAtUtc` (null when no switch is expected yet), and `text`, which says it all: "Next: account-b@example.com, in about 2 minutes.", "Next: account-b@example.com, when account-a@example.com reaches its limit. No switch is expected yet.", "Would not be switched now: its last report is too old.", "Signed in to no account, so there is nothing to switch." **For a machine that is held** `text` is "Next: {account}, not before {yyyy-MM-dd HH:mm UTC}: the automatic switch is held for this machine.", and `expectedSwitchAtUtc` is never earlier than the time it is held until. Why and for how long is the machine's own `heldText` of section 2, which the page draws in the same row; the two sentences do not repeat each other. |

### Example

```json
{
  "asOfUtc": "2026-10-11T03:12:00Z",
  "isEmpty": false,
  "emptyText": null,
  "nextSwitch": {
    "fromAccountEmail": "account-a@example.com",
    "expectedAtUtc": "2026-10-11T03:14:04.1052631Z",
    "window": "five-hour",
    "windowText": "5-hour window",
    "targetAccountEmail": "account-b@example.com",
    "machines": [
      {
        "machineId": "94ebcb15a4bf40438dca738dd31189d9",
        "machineName": "the-mac",
        "osUser": "owner",
        "label": "the-mac"
      },
      {
        "machineId": "f26a799d85a244129a27ba10b8cb3427",
        "machineName": "the-windows-machine",
        "osUser": "owner",
        "label": "the-windows-machine"
      }
    ]
  },
  "nextSwitchText": "account-a@example.com is expected to reach 97% of its 5-hour window in about 2 minutes; its machines then move to account-b@example.com, the account the Gateway would choose today.",
  "accounts": [
    {
      "accountEmail": "account-a@example.com",
      "isInUse": true,
      "isStale": false,
      "fiveHour": {
        "utilization": 96,
        "utilizationText": "96%",
        "level": "normal",
        "resetsAtUtc": "2026-10-11T05:00:00Z",
        "resetsText": "Resets in 1 hour 48 minutes",
        "percentPerHour": 28.547579298831383,
        "rateText": "28.5% an hour",
        "expectedAtLimitUtc": "2026-10-11T03:14:04.1052631Z",
        "expectedText": "Expected to reach 97% in about 2 minutes."
      },
      "sevenDay": {
        "utilization": 61,
        "utilizationText": "61%",
        "level": "normal",
        "resetsAtUtc": "2026-10-15T09:00:00Z",
        "resetsText": "Resets in 4 days 5 hours",
        "percentPerHour": 3.005008347245409,
        "rateText": "3% an hour",
        "expectedAtLimitUtc": "2026-10-11T15:10:46Z",
        "expectedText": "Expected to reach 97% in about 11 hours 58 minutes."
      },
      "expectedAtLimitUtc": "2026-10-11T03:14:04.1052631Z",
      "expectedText": "Expected to reach 97% of its 5-hour window in about 2 minutes.",
      "nextSwitchTargetAccountEmail": "account-b@example.com",
      "nextSwitchText": "Next: account-b@example.com, in about 2 minutes."
    },
    {
      "accountEmail": "account-b@example.com",
      "isInUse": false,
      "isStale": false,
      "fiveHour": {
        "utilization": 4,
        "utilizationText": "4%",
        "level": "normal",
        "resetsAtUtc": "2026-10-11T05:00:00Z",
        "resetsText": "Resets in 1 hour 48 minutes",
        "percentPerHour": null,
        "rateText": "No rate yet",
        "expectedAtLimitUtc": null,
        "expectedText": "No forecast yet: there are not enough readings to show a rate."
      },
      "sevenDay": {
        "utilization": 22,
        "utilizationText": "22%",
        "level": "normal",
        "resetsAtUtc": "2026-10-15T09:00:00Z",
        "resetsText": "Resets in 4 days 5 hours",
        "percentPerHour": null,
        "rateText": "No rate yet",
        "expectedAtLimitUtc": null,
        "expectedText": "No forecast yet: there are not enough readings to show a rate."
      },
      "expectedAtLimitUtc": null,
      "expectedText": "Not expected to reach its limit.",
      "nextSwitchTargetAccountEmail": null,
      "nextSwitchText": "Not in use, so there is nothing to switch."
    },
    {
      "accountEmail": "account-c@example.com",
      "isInUse": false,
      "isStale": false,
      "fiveHour": {
        "utilization": 99,
        "utilizationText": "99%",
        "level": "at-limit",
        "resetsAtUtc": "2026-10-11T05:00:00Z",
        "resetsText": "Resets in 1 hour 48 minutes",
        "percentPerHour": null,
        "rateText": "No rate yet",
        "expectedAtLimitUtc": "2026-10-11T03:12:00Z",
        "expectedText": "Has reached 97%."
      },
      "sevenDay": {
        "utilization": 40,
        "utilizationText": "40%",
        "level": "normal",
        "resetsAtUtc": "2026-10-15T09:00:00Z",
        "resetsText": "Resets in 4 days 5 hours",
        "percentPerHour": null,
        "rateText": "No rate yet",
        "expectedAtLimitUtc": null,
        "expectedText": "No forecast yet: there are not enough readings to show a rate."
      },
      "expectedAtLimitUtc": "2026-10-11T03:12:00Z",
      "expectedText": "Has reached 97% of its 5-hour window.",
      "nextSwitchTargetAccountEmail": null,
      "nextSwitchText": "Not in use, so there is nothing to switch."
    }
  ],
  "machines": [
    {
      "machineId": "94ebcb15a4bf40438dca738dd31189d9",
      "machineName": "the-mac",
      "osUser": "owner",
      "label": "the-mac",
      "activeAccountEmail": "account-a@example.com",
      "nextAccountEmail": "account-b@example.com",
      "expectedSwitchAtUtc": "2026-10-11T03:14:04.1052631Z",
      "text": "Next: account-b@example.com, in about 2 minutes."
    },
    {
      "machineId": "f26a799d85a244129a27ba10b8cb3427",
      "machineName": "the-windows-machine",
      "osUser": "owner",
      "label": "the-windows-machine",
      "activeAccountEmail": "account-a@example.com",
      "nextAccountEmail": "account-b@example.com",
      "expectedSwitchAtUtc": "2026-10-11T03:14:04.1052631Z",
      "text": "Next: account-b@example.com, in about 2 minutes."
    }
  ]
}
```

## 4. `GET /gateway/licenses/log` - the switch log

`LicenseLogResponse`: newest first, one page at a time.

- `?limit=` is a whole number from 1 to 200; 50 when it is left out.
- `?cursor=` is the `nextCursor` of the page before, passed back exactly as it was given. The page never reads it.
- `hasMore` says older rows exist; `nextCursor` is then the cursor of the next (older) page, and null otherwise.
- A walk through the pages is held to the rows dated at or before the moment the walk began, so a row dated later
  never appears in it. Read the first page again (no cursor) to see new rows. One rare case is not excluded: a row
  is dated when the Gateway RECEIVED the report that caused it, and saved a moment later. A report received just
  before the first page was read and saved just after it puts one more row inside the walk, and the next page then
  repeats the last row of the page before. Nothing is ever lost. The page should key its list on `entryId`, which
  makes a repeated row harmless.
- A limit or a cursor that cannot be read is answered `400` with `{ "code": "invalid_limit" | "invalid_cursor", "error": "..." }`.

### A row (`LicenseLogEntryDto`)

| Field | Meaning |
|---|---|
| `entryId` | The row's identity, for the page's list. |
| `kind`, `kindText` | `switch` (Switch), `spare-added` (Spare added), `spare-removed` (Spare removed), `no-room` (Nothing was switched), `renewal-failed` (Renewal failing), `approval` (Approval). **A kind a later build writes is shown as "Note"** - the row is still listed, and its `summary` is its message. Never branch on a kind you do not know; show `kindText` and `summary`. |
| `atUtc` | When the Gateway decided the switch or saw the change. |
| `machineId`, `machineName`, `osUser`, `label` | The machine the row is about, as the four fields of rule 9: `machineId` is the `machineId` of its row in the overview's `machines`, and `label` is what the row's `summary` calls it. One switch of an account is one row per machine, and the page lists one row per machine (section 1). On a row about no machine (a `no-room` row) `machineId` is null and the other three are "". A machine that is no longer listed in the overview still has its identifier and its label here. |
| `fromAccountEmail`, `toAccountEmail` | A switch's from and to. On `spare-added` and `renewal-failed` only `to` is set (on `renewal-failed` it is the account whose sign-in could not be renewed), on `spare-removed` and `no-room` only `from`. |
| `why`, `whyText` | Why a switch was asked for: `threshold` (Threshold reached) or `manual` (By hand). Null and "" on other rows. |
| `state`, `stateText`, `stateTone`, `isOpen` | Where the row stands, in the table below. `isOpen` is true while it has not reached its end. |
| `reasonCode`, `reasonText` | When a switch is waiting or failed: the reason word and its sentence. On a `no-room` or `renewal-failed` row: what became of the owner email - `owner-emailed` ("The owner was emailed."), `owner-email-failed` ("The owner has not been emailed yet; it is tried again."), `no-owner-account` ("This Gateway has no DevThrottle account to email.") - and null before the first try. Null otherwise. |
| `message` | The sentence the machine or the Gateway wrote on the row; may be null. |
| `summary` | The row in one sentence. Always set. |
| `sendCount`, `lastSentAtUtc` | How many times the command was sent, and when last. |
| `startedOnMachineAtUtc`, `finishedOnMachineAtUtc`, `exchangeMilliseconds`, `finishedAtUtc` | The timings: the machine's own start and finish, the exchange step alone, and when the row reached its end by the Gateway's clock. Null when not known. |
| `heldUntilUtc` | On a failed switch row that held its machine out of the automatic switch (contract section 5.8): the time it was held until. Null on every other row. It is a record of the moment, not a state: it stays on the row after the hold has passed. The row's `summary` says it; the page needs nothing else. |
| `timingText` | "Took 2.0 seconds on the machine (the exchange itself 41 milliseconds)"; null when there is nothing to say. |
| `verifiedByUsageRead`, `verifiedText` | After the switch the new sign-in answered a usage read; null when the machine gave no answer. `verifiedText` is the sentence, and it is set only on a row whose switch was made (`switched` or `already-active`): "The new sign-in answered a usage read." or "The new sign-in did not answer a usage read." On every other row it is null and the page shows nothing - a switch that was refused or failed carries `verifiedByUsageRead: false` and has nothing to confirm. Show `verifiedText`; never put words to the boolean. |

| `state` | `stateText` | Tone | Open |
|---|---|---|---|
| `pending` | Waiting to be sent | neutral | yes |
| `pending` with reason `director-offline` | Waiting for the machine | warning | yes |
| `unknown` | Sent, no answer yet | warning | yes |
| `switched` | Switched | good | no |
| `already-active` | Already on the target | good | no |
| `failed` | Failed | bad | no |
| `recorded` | Recorded | neutral | no |
| `open` on a `renewal-failed` row | Failing | bad | yes |
| `ended` on a `renewal-failed` row | Over | neutral | no |
| `approved` on an `approval` row | Approved | good | no |
| `refused` on an `approval` row | Refused | bad | no |
| any other word | the word itself | neutral | no |

The `summary` of a switch row says "is switching" only while it is `pending` or `unknown`. A switch row in a state
a later build writes is not said to be under way: its `summary` is its message, or a sentence naming the state.

A `no-room` row is open while its `finishedAtUtc` is null (nothing on the account can be switched yet), whatever
its state word. **It is the row of one event in which a tired account's machines could not be switched, for either
of two reasons, and its `message` and `summary` say which**: no account had room, or - since the whole-phase review -
every machine that could be switched is held because its switch keeps failing (contract section 5.8). It stays open
until no machine is left on the account or the account is no longer tired - one machine switched while another is
still held on the account does not end it. That is why
its `kindText` is "Nothing was switched" and no longer "No account had room": the word `no-room` is the stored kind
and is kept, the label must be true of both.

A failed switch row whose machine was held by it says so at the end of its `summary`: "It was not the first time in
a row that this switch failed this way, so the automatic switch left {the machine} out until {yyyy-MM-dd HH:mm
UTC}." The first failure of a run holds nothing and has no such sentence.

An `approval` row is one approval the Gateway relayed between machines (contract section 8.3), final when
written: the daily renewal's, which no person started, as well as one for a sign-in started from the Cockpit. Its
machine fields are the machine that ASKED, `toAccountEmail` the account, `state` `approved` or `refused`, and on a
refusal `reasonCode` the contract's word with its `reasonText`. Its `message` and `summary` are the Gateway's own
sentence naming the machine set to approve - "the-pc approved the sign-in for account-a@example.com on the-mac.",
"the-mac approved its own sign-in for account-a@example.com.", "The sign-in for account-a@example.com on the-mac was
not approved by the-pc: the approving browser needs a fresh claude.ai sign-in on the approval page." It never carries
the address the sign-in was approved at, the sealed approval, or the approving machine's own words. A request from a
Director that has never reported for the account writes no row: there is no machine to put it against.

A `renewal-failed` row is one FAILURE STREAK of one sign-in on one machine (contract section 8.1): written when a
machine's renewal of a sign-in is failing and that sign-in ends within three days, and open until the machine
reports a NEWER sign-in for that account (one that ends more than an hour later, or was made later) or no longer
holds the sign-in. A
report that only stops saying the renewal failed ends nothing: a Director that has just started has forgotten its
failed renewals. It is open while its `finishedAtUtc` is null. Its `summary` is a finished sentence that says what
is wrong, how old the facts are, and what became of the one owner email of that streak. Every time in it is a
moment, never a distance, so it stays true however long the row is open:

- "Renewing the sign-in for account-a@example.com on the-mac is failing, and the sign-in ends at 2026-10-13 03:12
  UTC (the last try failed at 2026-10-11 03:07 UTC by the machine's clock; the-mac last reported at 2026-10-11 03:12
  UTC); the owner was emailed."
- "... UTC); the owner has not been emailed yet because {why}."
- "... UTC); no owner email was sent because {why}."
- "Renewing the sign-in for account-a@example.com on the-mac had been failing; the-mac now reports a newer sign-in."
- "Renewing the sign-in for account-a@example.com on the-mac had been failing; the-mac no longer holds that sign-in."

`startedOnMachineAtUtc` and `timingText` are always `null` on this row. `sendCount` is 1 and `lastSentAtUtc` is set
once the owner was emailed. The page shows the row like any other; the
holding itself still carries `renewalFailedText` (section 2), which is where the reason is said.

### Example (`?limit=4`)

```json
{
  "asOfUtc": "2026-10-11T03:12:00Z",
  "isEmpty": false,
  "emptyText": null,
  "entries": [
    {
      "entryId": "e01929159dd747939b37143680054049",
      "kind": "spare-added",
      "kindText": "Spare added",
      "atUtc": "2026-10-11T03:12:00Z",
      "machineId": "f26a799d85a244129a27ba10b8cb3427",
      "machineName": "the-windows-machine",
      "osUser": "owner",
      "label": "the-windows-machine",
      "fromAccountEmail": "",
      "toAccountEmail": "account-c@example.com",
      "why": null,
      "whyText": "",
      "state": "recorded",
      "stateText": "Recorded",
      "stateTone": "neutral",
      "isOpen": false,
      "reasonCode": null,
      "reasonText": null,
      "message": "The machine's report shows a new spare for account-c@example.com.",
      "summary": "The machine's report shows a new spare for account-c@example.com.",
      "sendCount": 0,
      "lastSentAtUtc": null,
      "startedOnMachineAtUtc": null,
      "finishedOnMachineAtUtc": null,
      "exchangeMilliseconds": null,
      "finishedAtUtc": "2026-10-11T03:12:00Z",
      "timingText": null,
      "verifiedByUsageRead": null,
      "verifiedText": null
    },
    {
      "entryId": "fddfab36f39e4bc7a09166bd7a519a89",
      "kind": "switch",
      "kindText": "Switch",
      "atUtc": "2026-10-11T01:12:00Z",
      "machineId": "f26a799d85a244129a27ba10b8cb3427",
      "machineName": "the-windows-machine",
      "osUser": "owner",
      "label": "the-windows-machine",
      "fromAccountEmail": "account-a@example.com",
      "toAccountEmail": "account-c@example.com",
      "why": "manual",
      "whyText": "By hand",
      "state": "failed",
      "stateText": "Failed",
      "stateTone": "bad",
      "isOpen": false,
      "reasonCode": "spare-not-held",
      "reasonText": "The machine holds no spare for the target.",
      "message": "This machine holds no spare for account-c@example.com.",
      "summary": "the-windows-machine was not switched from account-a@example.com to account-c@example.com.",
      "sendCount": 1,
      "lastSentAtUtc": "2026-10-11T01:12:01Z",
      "startedOnMachineAtUtc": "2026-10-11T01:12:01Z",
      "finishedOnMachineAtUtc": "2026-10-11T01:12:01Z",
      "exchangeMilliseconds": 0,
      "finishedAtUtc": "2026-10-11T01:12:01Z",
      "timingText": null,
      "verifiedByUsageRead": false,
      "verifiedText": null
    },
    {
      "entryId": "0920e92e743c40c69a1217aff69a433f",
      "kind": "spare-removed",
      "kindText": "Spare removed",
      "atUtc": "2026-10-10T23:12:00Z",
      "machineId": "f26a799d85a244129a27ba10b8cb3427",
      "machineName": "the-windows-machine",
      "osUser": "owner",
      "label": "the-windows-machine",
      "fromAccountEmail": "account-c@example.com",
      "toAccountEmail": "",
      "why": null,
      "whyText": "",
      "state": "recorded",
      "stateText": "Recorded",
      "stateTone": "neutral",
      "isOpen": false,
      "reasonCode": null,
      "reasonText": null,
      "message": "The machine's report no longer shows the active sign-in for account-c@example.com.",
      "summary": "The machine's report no longer shows the active sign-in for account-c@example.com.",
      "sendCount": 0,
      "lastSentAtUtc": null,
      "startedOnMachineAtUtc": null,
      "finishedOnMachineAtUtc": null,
      "exchangeMilliseconds": null,
      "finishedAtUtc": "2026-10-10T23:12:00Z",
      "timingText": null,
      "verifiedByUsageRead": null,
      "verifiedText": null
    },
    {
      "entryId": "492e609fc02f4331863e231a92a43e5e",
      "kind": "switch",
      "kindText": "Switch",
      "atUtc": "2026-10-10T22:12:00Z",
      "machineId": "f26a799d85a244129a27ba10b8cb3427",
      "machineName": "the-windows-machine",
      "osUser": "owner",
      "label": "the-windows-machine",
      "fromAccountEmail": "account-c@example.com",
      "toAccountEmail": "account-a@example.com",
      "why": "threshold",
      "whyText": "Threshold reached",
      "state": "switched",
      "stateText": "Switched",
      "stateTone": "good",
      "isOpen": false,
      "reasonCode": null,
      "reasonText": null,
      "message": "Switched from account-c@example.com to account-a@example.com.",
      "summary": "the-windows-machine switched from account-c@example.com to account-a@example.com.",
      "sendCount": 1,
      "lastSentAtUtc": "2026-10-10T22:12:01Z",
      "startedOnMachineAtUtc": "2026-10-10T22:12:01Z",
      "finishedOnMachineAtUtc": "2026-10-10T22:12:03Z",
      "exchangeMilliseconds": 41,
      "finishedAtUtc": "2026-10-10T22:12:03Z",
      "timingText": "Took 2.0 seconds on the machine (the exchange itself 41 milliseconds)",
      "verifiedByUsageRead": true,
      "verifiedText": "The new sign-in answered a usage read."
    }
  ],
  "hasMore": true,
  "nextCursor": "639272851200000000.4"
}
```

## 5. An account whose machines have never reported

`GET /gateway/licenses` (the forecast and the log answer the same way, each with its own lists):

```json
{
  "asOfUtc": "2026-10-11T03:12:00Z",
  "isEmpty": true,
  "emptyText": "No machine has reported its Claude accounts yet. They appear here once a Director with license switching turned on has connected.",
  "accounts": [],
  "machines": []
}
```

Show `emptyText` in place of the tables. `isEmpty` is false as soon as one machine has reported, even a machine that
holds no sign-in at all: that machine is then listed as "Signed out".

A refusal, here `GET /gateway/licenses/log?limit=0`:

```json
{
  "code": "invalid_limit",
  "error": "limit is a whole number from 1 to 200."
}
```

## 6. The two writes - the renewal date and Switch now - and a failed renewal

### `PUT /gateway/licenses/accounts/renewal` - the date a subscription is billed again

The one thing a person sets on the Licenses page. The body names one Claude account of the caller's own DevThrottle
account and gives the date, or null to clear it:

```json
{ "accountEmail": "account-a@example.com", "renewsOn": "2026-10-17" }
```

`renewsOn` is a date written `yyyy-MM-dd` and nothing else: no time, no zone, no other spelling. **It must be
given.** Only an explicit `"renewsOn": null` clears the date; a body that leaves the field out - a misspelled name,
most likely - is refused and the stored date is left as it was. The account email is matched whatever its capitals. The answer is what was stored, with its sentence already folded - the page can
show `renewsText` at once, and the next `GET /gateway/licenses` carries the same two values on that account:

```json
{"accountEmail":"account-a@example.com","renewsOn":"2026-10-17","renewsText":"Renews 17 Oct"}
```

Cleared (`"renewsOn": null`):

```json
{"accountEmail":"account-a@example.com","renewsOn":null,"renewsText":"Renewal date not set"}
```

The sentences of `renewsText`, all folded on the Gateway by its own date in Coordinated Universal Time:

| The stored date | `renewsText` |
|---|---|
| none | "Renewal date not set" |
| today | "Renews today, 11 Oct" - the date is given in every sentence, because "today" is the Gateway's date in Coordinated Universal Time and can be a day off for the viewer around midnight |
| later this year | "Renews 17 Oct" |
| in another year | "Renews 1 Jan 2027" |
| already past | "Renewal date 10 Oct has passed" (or "Renewal date 31 Dec 2025 has passed") |

A date that has passed is never moved forward by a month or a year: nobody told the Gateway how often the
subscription is billed, so it says the date has passed and the owner sets the next one.

What is refused, each with `code` and a sentence in `error` (rule 6):

| Status | `code` | When |
|---|---|---|
| 400 | `invalid_body` | The body was not sent as `application/json`, is not a JSON object of this shape, or does not name `renewsOn`. |
| 400 | `account_required` | `accountEmail` is missing or blank. |
| 400 | `invalid_account` | `accountEmail` is longer than 320 characters, or holds a character that is not text to be read: a control character, a line or paragraph separator, or a format character. |
| 400 | `invalid_date` | `renewsOn` is text that is not a real date written `yyyy-MM-dd` ("17 Oct", "2026-02-30", a date with a time). |
| 404 | `unknown_account` | No machine of this DevThrottle account holds that Claude account. The answer is the same whether the account exists in another DevThrottle account or nowhere. |
| 403 | `owner_only` | The caller is not the owner's own signed-in phone or browser ("Who may call the writes", below). |
| 403 | `no_account` | The caller's credential belongs to no DevThrottle account. |

```json
{"code":"invalid_date","error":"renewsOn is a date written yyyy-MM-dd, or null to clear it."}
```

Nothing is stored by a refused call. A date is kept through every later usage report, because no report carries it.

### A failed renewal of a sign-in

No route: the Gateway stores what each usage report says of a holding's last renewal (contract section 8.1) and the
overview fills the three fields on that holding. When the sign-in also ends within three days the Gateway emails
the owner once for that failure streak and writes a `renewal-failed` row in the switch log (section 4); the page
does nothing for that. Sign-ins that become due in the same minute check go out in ONE email, one line each, and
each still has its own row; the `summary` of each then ends "the owner was emailed, in one email about {N}
sign-ins". A holding whose renewal failed three hours ago, with one day of
sign-in left:

```json
"renewalFailedAtUtc": "2026-10-11T00:12:00Z",
"renewalFailedText": "Renewing this sign-in failed 3 hours ago: no browser is set to approve this account.",
"renewalFailedTone": "bad"
```

- `renewalFailedText` is "Renewing this sign-in failed {how long ago}: {why}." There is one "why" for each reason
  word of the contract: the sign-in did not start, or ended without a sign-in; the approval in the browser did not
  complete; the approving browser is signed in as another account; the approving browser is not signed in; no browser
  is set to approve this account; the machine that approves this account was not connected; no approval came back in
  time; the machine's key store would not take the new sign-in.
- A reason word this Gateway does not know is never shown or stored as given (it is kept only as "a reason was given"). The sentence is then "Renewing this sign-in failed 5
  minutes ago." followed by the Director's own sentence when it sent one.
- The time is the machine's clock. When it is ahead of the Gateway's, no age is said: "Renewing this sign-in failed:
  no approval came back in time."
- `renewalFailedTone` is `bad` when that sign-in's `expiryVerdict` is `expired` or `expires soon` - it is dead or
  about to be and the machine could not renew it, so the owner must sign in - and `warning` otherwise, including when
  the expiry is not known.

### `POST /gateway/licenses/switch-now` - move every machine on one account to another, now

Everything the page needs to DRAW the control is in section 2 (`canSwitchNow`, `switchNowText`,
`switchNowTargetAccountEmail`, `switchTargets`). This route performs it. It is by ACCOUNT, because usage is counted
per account: every machine on the account that can be sent a switch AND is ready for the target moves (MISSION.md section 5:
it holds a sign-in for the target that was not refused and does not expire before the switch could finish). A machine
that is not ready stays where it is and is listed in `skipped` with its reason. The target is optional - null, blank or
absent lets the Gateway choose, exactly as an automatic switch would, each machine to the best account with room it is
ready for:

```json
{ "accountEmail": "account-a@example.com", "targetAccountEmail": null }
```

The account is one of the caller's own DevThrottle account - which account that is comes from the credential, never
from the body. Either email is matched whatever its capitals.

**It answers when every machine has answered or its 60-second wait has ended** - normally in a second or two, never
much longer than a minute. The page should show that it is working until then.

**Accepted is `200`**, whatever each machine then did - a machine that refused, or one whose Director is not
connected, is still an accepted call, and each machine's own state says how it stands:

```json
{"targetAccountEmail":"account-b@example.com","text":"The switch of machine-of-one to account-b@example.com has not finished; the switch log shows how it ends.","switches":[{"switchId":"f28f268f315842558629a946f91050c3","entryId":"e6fd8eedf7b04915bed55866cd4eac04","machineName":"machine-of-one","osUser":"owner","label":"machine-of-one","state":"pending","stateText":"Waiting for the machine","stateTone":"warning","summary":"machine-of-one is switching from account-a@example.com to account-b@example.com."}],"skipped":[]}
```

| Field | What it is |
|---|---|
| `targetAccountEmail` | The account the machines were sent to: the one named, or the one chosen. Null when a call with no target sent the machines to more than one account; each machine's `summary` names its own. |
| `text` | The whole outcome in one sentence, ready to show: "the-windows-machine is now on account-b@example.com.", "The switch of the-mac to account-b@example.com has not finished; the switch log shows how it ends.", "The switch of the-mac to account-b@example.com failed; the switch log says why." - one part for each kind that happened, for each account the machines were sent to ("the-windows-machine is now on account-b@example.com. the-mac is now on account-c@example.com."), then each machine left out with its reason. |
| `switches[]` | One per machine sent a switch, as its row of the switch log stood when the Gateway answered: `switchId` (the switch itself), `entryId` (that row's `entryId` in section 4, so the page can find it in the log), `machineName`, `osUser`, `label` (rule 9), and `state`, `stateText`, `stateTone` and `summary` - the same four a log row carries (section 4). |
| `skipped[]` | The machines on the account that were NOT sent a switch: `machineName`, `osUser`, `label`, and `why`, a finished sentence ("the-linux-machine is not switched: its last report is too old."). |

A switch that is `pending` or `unknown` in the answer is finished later by the Gateway's once-a-minute check; the
page reads how it ended from the switch log, by `entryId`. It never sends the same Switch now again to find out.

**Refused is `409`** with the refusal word in `code` and a finished sentence in `error` - the same sentence
`switchNowText` or `switchTargets[].unavailableText` already shows for that account, so the page shows nothing new.
Nothing was written and nothing was sent:

```json
{"code":"no-machine-to-switch","error":"No machine on account-a@example.com can be switched now: every machine on it is stale or already mid-switch. machine-of-one is not switched: a switch sent to it has not finished."}
```

| Status | `code` | When |
|---|---|---|
| 409 | `no-machine-to-switch` | No machine is on the account (also the answer for an account of another DevThrottle account, or one that exists nowhere), or every machine on it is stale or has a switch that has not finished. |
| 409 | `target-is-source` | The target named is the account the machines are already on. |
| 409 | `target-not-held` | The target has room and no machine to be switched is ready for it; at least one holds no usable sign-in for it. The sentence names every machine and its reason: "account-b@example.com has room; machine-a holds no sign-in for it." Also the answer with no target named, when an account has room and no machine is ready for any account with room. |
| 409 | `target-sign-in-expiring` | The same, when every machine's sign-in for the target expires before a switch could finish. |
| 409 | `target-stale` | The latest reading of the target is too old to show it has room. |
| 409 | `target-full` | The target has no room. |
| 409 | `no-target` | No target was named and no other account has room, judged on each account alone. The sentence gives each account's reason: "No other account has room for the machines on account-a@example.com: for account-b@example.com, it is at 98% of its weekly limit." |
| 400 | `invalid_body` | The body was not sent as `application/json`, or is not a JSON object of this shape. |
| 400 | `account_required` | `accountEmail` is missing or blank. |
| 400 | `invalid_account` | `accountEmail` or `targetAccountEmail` is longer than 320 characters, or holds a control character, a line or paragraph separator, or a format character. |
| 403 | `owner_only` | The caller is not the owner's own signed-in phone or browser. |
| 403 | `no_account` | The caller's credential belongs to no DevThrottle account. |

**The page offers Switch now exactly where this route accepts it.** `canSwitchNow` is asked of the same picture the
route decides on. One case is worth knowing: a machine whose switch is only waiting for its Director to connect
(`pending`, shown as "Waiting for the machine") was never sent anything, so it CAN be switched now - the waiting
switch is ended as `failed` and a new one is written. A machine whose switch was sent and has not been answered
cannot, and `switchNowText` says so for that machine.

**Asking the same thing twice writes nothing.** While a machine's switch is waiting for its Director, a Switch now
with the same account and the same target (named, or chosen by the Gateway) is answered `200` with that waiting
switch - the same `switchId` and `entryId` - and no new row is written. Only a Switch now to ANOTHER target replaces
it. The page should still disable the control while a call is out.

**When `switchNowTargetAccountEmail` is null, the page never sends the call without a target.** That is the case
"No other account has room ... A target can still be chosen by hand.": `canSwitchNow` is true only because a target
can be NAMED - a barred account, most often. A call with no `targetAccountEmail` would be refused `409` `no-target`.
So the page's default action exists only when `switchNowTargetAccountEmail` is not null; otherwise the owner picks
one of the `switchTargets` whose `canChoose` is true, and the page sends that one.

**When `switchNowTargetAccountEmail` is the one picked, the page sends no target.** Naming it would move only the
machines ready for it and leave the rest on an account at its limit, while the automatic switch would send them to
the next best account they are ready for. Sending none makes exactly the automatic moves, which `switchNowText` and
the schedule both describe. A target picked by hand is sent as named.

### Who may call the writes

**The two writes are for a signed-in person only: the owner's own phone or browser.** Setting the date a
subscription renews, and moving every machine of the owner's subscription to another one, are the owner's own
actions, from the Cockpit. Everything else that can READ the license routes is refused the writes with `403`:

| Caller | Answer |
|---|---|
| The owner's signed-in browser or phone | allowed |
| A session key (an agent), raised or not | `403` `session_key_out_of_scope` - `SessionKeyGuard` lists neither route |
| A Director's own device key | `403` `owner_only` |
| The shared machine token of a self-hosted Gateway | `403` `owner_only` |
| No credential | `401` |

```json
{"code":"owner_only","error":"only the owner on their own signed-in phone or browser may set the renewal date of a subscription; this request was made with a device (workstation) credential"}
```

Each write names itself in its sentence. The same caller asking for Switch now is answered:

```json
{"code":"owner_only","error":"only the owner on their own signed-in phone or browser may switch the machines of a subscription to another one; this request was made with a device (workstation) credential"}
```

**A self-hosted Cockpit opened with the machine token reads the page and is refused both writes.** On a
self-hosted Gateway the login page can store the shared machine token as the browser's credential. With it the page
reads all three routes - and is told `canSwitchNow: true`, because that is about the machines, not about who is
looking - and both writes answer `403` `owner_only`. That is not an error of the page: the page shows the sentence in
`error` as it is given, and the owner signs in as a person to use the writes.

The caller is judged before the body is read, and a body must be sent as `application/json`.

It is the one rule the Fleet Manager's owner-only routes use (`FleetManagerOwnerDevice.Require`). Tests hold every
row of the table over real HTTP (`LicenseHostTests`, `LicenseWritesSelfHostedTests`) and the guard's half in
`LicenseGuardTests`.
**Both are refused to a session key.** Moving every machine of the owner's subscription to another one, and setting
the date a subscription renews, are the owner's own actions, from the Cockpit. `SessionKeyGuard` lists neither route;
a test holds Switch now to that today (`LicenseGuardTests`), and the follow-up adds the same for the renewal route.

## 7. Sign-ins - who approves an account, Add a spare, Renew now

The Gateway side of the contract's section 8. The types are in
`src/CcDirector.Gateway.Contracts/LicenseSignInCockpitDtos.cs`, the routes in
`src/CcDirector.Gateway/Api/LicenseSignInEndpoints.cs`, and every verdict is folded in
`src/CcDirector.Gateway/Licenses/LicenseSignInFold.cs`. The rules of section 1 hold for all of it. The examples are
real answers, produced the same way as the others: "the-windows-machine" is on account A and holds a spare for B,
"the-mac" is on account C, and the clock starts at 2026-10-11 03:12:00 UTC.

Nothing here adds a table or a column. The approvers are one per-account setting; a sign-in is a row of the switch
log of its own kind.

**The Gateway relays and never keeps.** No route in this section takes or returns the address a sign-in is approved
at, an approval, or anything else of a sign-in. Those pass between two Directors through the Gateway's memory and
are never stored and never logged.

### Who may call

| Route | The owner's own phone or browser | A session key, raised by the owner or not | A Director's own key | The shared machine token of a self-hosted Gateway |
|---|---|---|---|---|
| `GET /gateway/licenses/approvers` | yes | yes | yes | yes |
| `PUT /gateway/licenses/approvers` | yes | `403` `session_key_out_of_scope` | `403` `owner_only` | `403` `owner_only` |
| `DELETE /gateway/licenses/approvers` | yes | `403` `session_key_out_of_scope` | `403` `owner_only` | `403` `owner_only` |
| `POST /gateway/licenses/sign-ins/add-spare` | yes | `403` `session_key_out_of_scope` | `403` `owner_only` | `403` `owner_only` |
| `POST /gateway/licenses/sign-ins/renew` | yes | `403` `session_key_out_of_scope` | `403` `owner_only` | `403` `owner_only` |

Naming the machine and browser profile that approve a sign-in, and starting a sign-in, are the owner's own actions:
the four writes are for a signed-in person only, the same one rule as the two writes of section 6. A session key is
stopped by `SessionKeyGuard`, which lists only the read - and raising a session does not add these four. Every other
credential that is not the owner's own device is refused by the route itself, before the body is read.

Each column is held over real HTTP, by a test that sends that credential to all four writes and the read:
a session key and a Director's own key (`LicenseSignInHostedTests.TheApproverRoutes_AreTheOwnersToWrite_AndASessionKeyMayOnlyRead`),
a session key the owner raised through the raise route (`...TheFourWrites_AskedWithASessionKeyTheOwnerRaised_AreStillRefused_AndNothingIsStoredOrSent`),
and the shared machine token on a self-hosted Gateway (`LicenseSignInWritesSelfHostedTests`).

Every refusal is `{ "code": "...", "error": "..." }`, where `error` is a finished sentence to show as it is.

The three `owner_only` sentences, each as the Gateway answered a Director's own key over real HTTP (the proof run of
the page, `phase-3/qa-sign-ins/qa-sign-ins-run-output.txt`, part 5). Setting an approver:

```json
{
  "code": "owner_only",
  "error": "only the owner on their own signed-in phone or browser may set which machine approves an account's sign-ins; this request was made with a device (workstation) credential"
}
```

Removing one:

```json
{
  "code": "owner_only",
  "error": "only the owner on their own signed-in phone or browser may remove the machine that approves an account's sign-ins; this request was made with a device (workstation) credential"
}
```

Starting a sign-in, by Add a spare or by Renew now:

```json
{
  "code": "owner_only",
  "error": "only the owner on their own signed-in phone or browser may start a sign-in on a machine; this request was made with a device (workstation) credential"
}
```

### 7.1 `GET /gateway/licenses/approvers` - who approves each account

One approver per Claude account at most: the machine (its name and operating system user) and the browser profile
on it that is signed in to claude.ai as that account. With none set:

```json
{
  "asOfUtc": "2026-10-11T03:12:00Z",
  "isEmpty": true,
  "emptyText": "No machine is set to approve sign-ins for any account. A machine that has its own browser profile for an account approves its sign-ins there.",
  "approvers": []
}
```

With two set - one whose Director is connected and whose last check found it can approve, one on a machine that
has never reported to this account:

```json
{
  "asOfUtc": "2026-10-11T03:12:00Z",
  "isEmpty": false,
  "emptyText": null,
  "approvers": [
    {
      "accountEmail": "account-a@example.com",
      "machineName": "the-mac",
      "osUser": "owner",
      "browserProfile": "work",
      "machineId": "527062dd720c4d28a144183033cf93c1",
      "label": "the-mac",
      "canApproveNow": true,
      "status": "ready",
      "statusText": "Ready",
      "statusTone": "good",
      "text": "Sign-ins for account-a@example.com are approved on the-mac, in the browser profile work.",
      "browserProfileName": null,
      "source": "registered",
      "state": "can-approve",
      "checkedAtUtc": "2026-10-11T00:12:00Z",
      "healthText": "The approver for account-a@example.com (browser profile \"work\" on the-mac) can approve sign-ins; it was last checked 2026-10-11 00:12 UTC.",
      "whatToDo": null
    },
    {
      "accountEmail": "account-b@example.com",
      "machineName": "the-laptop",
      "osUser": "owner",
      "browserProfile": "personal",
      "machineId": null,
      "label": "the-laptop",
      "canApproveNow": false,
      "status": "never-reported",
      "statusText": "Not reporting yet",
      "statusTone": "warning",
      "text": "Sign-ins for account-b@example.com are approved on the-laptop, in the browser profile personal. No Director on the-laptop has reported to this account, so no sign-in can be approved there yet.",
      "browserProfileName": null,
      "source": "owner",
      "state": "not-checked",
      "checkedAtUtc": null,
      "healthText": "The approver for account-b@example.com (browser profile \"personal\" on the-laptop) has never been checked.",
      "whatToDo": "Make sure the Director on the-laptop is running with license switching on; it checks its approvers once a day."
    }
  ]
}
```

An approver (`LicenseApproverDto`):

| Field | What it is |
|---|---|
| `accountEmail` | The Claude account whose sign-ins it approves. |
| `machineName`, `osUser` | The approving machine, as the owner set it, or as the machine itself spells it once it has reported. |
| `browserProfile` | The browser profile on that machine: its IDENTIFIER on the Director, for example `work-chrome`, never its display name. |
| `browserProfileName` | The profile's display name as its last check saw it, for example "Work Chrome"; `null` until a check has named it. The sentences say this name, else the identifier. |
| `machineId` | The machine's row in the overview; `null` while no such machine has reported to this account. |
| `label` | What to call the machine (section 1 rule 9). |
| `canApproveNow` | A sign-in for this account can be approved there now: its Director is connected AND its health is `can-approve`. |
| `status`, `statusText`, `statusTone` | `ready` "Ready" / `not-connected` "Not connected" / `never-reported` "Not reporting yet" / `cannot-approve` "Cannot approve" or "Not checked". A machine that cannot be reached is said first; then the profile's health. |
| `text` | The whole of it in one or two sentences. Show this. For `cannot-approve` it is `healthText` and `whatToDo` together. |
| `source` | `owner` (set on this page) or `registered` (registered by the Director of the machine it names, from a check that proved the profile can approve; contract section 10). |
| `state` | The record's health now: `can-approve`, `signed-out`, `needs-fresh-sign-in`, `wrong-account`, `check-failed`, or `not-checked` when it was never checked or its last check is more than 26 hours old. |
| `checkedAtUtc` | When it was last checked; `null` when it never was. |
| `healthText` | The finished sentence of its health, naming the account, the machine and the profile. Show as given. |
| `whatToDo` | The one thing to do so it can approve; `null` when it can. |

A machine that only approves needs no sign-in of its own: it is listed in the overview as "Signed out" as soon as
its Director reports, and `status` is `ready` while that Director is connected and its approver profile's last
check, within 26 hours, found it can approve. An approver the owner has just set has never been checked, so it is
`cannot-approve` "Not checked" until that machine's Director checks it.

The `healthText` and `whatToDo` sentences, for account `alice@example.com`, profile `work`, machine `machine-a`:

| `state` | `healthText` | `whatToDo` |
|---|---|---|
| `can-approve` | The approver for alice@example.com (browser profile "work" on machine-a) can approve sign-ins; it was last checked 2026-10-11 01:12 UTC. | `null` |
| `signed-out` | The approver for alice@example.com (browser profile "work" on machine-a) is not signed in to claude.ai. | Open that browser on machine-a and sign in to claude.ai as alice@example.com once. |
| `needs-fresh-sign-in` | The approver for alice@example.com (browser profile "work" on machine-a) needs a fresh claude.ai sign-in on the approval page. | Open that browser on machine-a and sign in to claude.ai once. |
| `wrong-account` | The approver for alice@example.com (browser profile "work" on machine-a) is signed in to claude.ai as bob@example.com, not as alice@example.com. | Open that browser on machine-a, sign out of claude.ai and sign in as alice@example.com once. |
| `check-failed` | The approver for alice@example.com (browser profile "work" on machine-a) could not be checked: (the Director's detail). | Make sure the browser profile "work" opens on machine-a; the check runs again within a day. |
| `not-checked` | The approver for alice@example.com (browser profile "work" on machine-a) has never been checked. / ... was last checked 2026-10-10 01:11 UTC, more than 26 hours ago. | Make sure the Director on machine-a is running with license switching on; it checks its approvers once a day. |

### 7.2 `PUT /gateway/licenses/approvers` - set the approver of one account

Body (`LicenseSetApproverRequest`): `{ "accountEmail": "...", "machineName": "...", "osUser": "...",
"browserProfile": "..." }`. `browserProfile` is the browser profile's identifier (the Director's slug, for example "work-chrome", as `GET /directors/{id}/browsers` lists it in `id`), never its display name. Setting an account that has an approver replaces it. The machine does not have to have
reported yet. Answered `200` with the approver as stored:

```json
{
  "accountEmail": "account-a@example.com",
  "machineName": "the-mac",
  "osUser": "owner",
  "browserProfile": "work",
  "machineId": "527062dd720c4d28a144183033cf93c1",
  "label": "the-mac",
  "canApproveNow": false,
  "status": "cannot-approve",
  "statusText": "Not checked",
  "statusTone": "warning",
  "text": "The approver for account-a@example.com (browser profile \"work\" on the-mac) has never been checked. Make sure the Director on the-mac is running with license switching on; it checks its approvers once a day.",
  "browserProfileName": null,
  "source": "owner",
  "state": "not-checked",
  "checkedAtUtc": null,
  "healthText": "The approver for account-a@example.com (browser profile \"work\" on the-mac) has never been checked.",
  "whatToDo": "Make sure the Director on the-mac is running with license switching on; it checks its approvers once a day."
}
```

Setting the very machine, user and profile that a check already covered keeps that check, so it stays `ready`; any
other choice starts `not-checked`. Setting an account clears the record of any registration the owner removed for it.

Refused `400` with `invalid_account`, `invalid_machine`, `invalid_browser_profile` or `bad_request_body`, and `409`
with `too_many_approvers` (64 for one DevThrottle account). `machineName` and `osUser` are names of at most 128
characters, and they and `accountEmail` are refused when they hold a control character, a line or paragraph
separator, or a format character - a name with a line break in it is `invalid_machine`, and nothing is stored:

```json
{
  "code": "invalid_browser_profile",
  "error": "browserProfile is the name of a browser profile on the approving machine: letters, digits, spaces, dots, underscores and hyphens, at most 64 characters."
}
```

### 7.3 `DELETE /gateway/licenses/approvers?accountEmail=...` - remove it

Answered `200` whether or not there was one; `removed` and `text` say which
(`LicenseApproverRemovedResponse`). Refused `400` `invalid_account` when the query names no account. Removing a
`registered` approver keeps a record of the removal, so that machine's Director does not register the same profile
for the account again; it lasts until the owner sets an approver for that account (contract section 10.5).

```json
{
  "accountEmail": "account-b@example.com",
  "removed": true,
  "text": "No machine is set to approve sign-ins for account-b@example.com any more."
}
```

### 7.4 `POST /gateway/licenses/sign-ins/add-spare` and `POST /gateway/licenses/sign-ins/renew`

Body (`LicenseStartSignInRequest`): `{ "accountEmail": "...", "machineId": "..." }`, where `machineId` is the
`machineId` of the machine's row in the overview. **Add a spare** signs the machine in to an account it does not
hold; **Renew now** signs it in again to an account it holds.

**The answer does not wait for the sign-in.** A sign-in takes minutes. The route answers `202` as soon as the
sign-in's row of the log is written and its command is on its way to the machine's Director
(`LicenseSignInStartedResponse`):

```json
{
  "signInId": "501d61535066417d966ef172151b00bc",
  "accountEmail": "account-a@example.com",
  "machine": {
    "machineId": "2f9cea24fe1b44f4951e48ed1748f105",
    "machineName": "the-windows-machine",
    "osUser": "owner",
    "label": "the-windows-machine"
  },
  "text": "Asked the Director on the-windows-machine to renew the sign-in for account-a@example.com. It can take a few minutes; the outcome appears in the log.",
  "entry": {
    "entryId": "1e080484ba78433982ea385e8ffa04f4",
    "kind": "sign-in",
    "kindText": "Sign-in",
    "atUtc": "2026-10-11T03:14:00Z",
    "machineId": "2f9cea24fe1b44f4951e48ed1748f105",
    "machineName": "the-windows-machine",
    "osUser": "owner",
    "label": "the-windows-machine",
    "fromAccountEmail": "",
    "toAccountEmail": "account-a@example.com",
    "why": "renewal",
    "whyText": "Renew now",
    "state": "pending",
    "stateText": "Signing in",
    "stateTone": "neutral",
    "isOpen": true,
    "reasonCode": null,
    "reasonText": null,
    "message": "Asked the Director on the-windows-machine to renew the sign-in for account-a@example.com.",
    "summary": "the-windows-machine is renewing its sign-in for account-a@example.com.",
    "sendCount": 0,
    "lastSentAtUtc": null,
    "startedOnMachineAtUtc": null,
    "finishedOnMachineAtUtc": null,
    "exchangeMilliseconds": null,
    "finishedAtUtc": null,
    "timingText": null,
    "verifiedByUsageRead": null,
    "verifiedText": null
  }
}
```

`entry` is the sign-in's row exactly as `GET /gateway/licenses/log` answers it, so the page can show it at once.
The outcome arrives on that same row (same `entryId`): read the log again.

Refused, and nothing is written or sent:

| Status | `code` | When |
|---|---|---|
| `400` | `invalid_account` | `accountEmail` is not an email. |
| `400` | `invalid_machine` | `machineId` is not a machine identifier. |
| `400` | `bad_request_body` | The body is missing or is not JSON. |
| `404` | `unknown_machine` | No machine with that identifier has reported to the caller's account. |
| `409` | `already_held` | Add a spare for an account the machine holds. |
| `409` | `not_held` | Renew now for an account the machine does not hold. |
| `409` | `sign_in_running` | A sign-in for that machine and account has not ended. |
| `409` | `director_offline` | The machine's Director is not connected. |

```json
{
  "code": "sign_in_running",
  "error": "A sign-in for account-a@example.com on the-windows-machine is already running."
}
```

```json
{
  "code": "director_offline",
  "error": "The Director on the-mac is not connected, so a sign-in cannot be started there."
}
```

```json
{
  "code": "already_held",
  "error": "the-windows-machine already holds a sign-in for account-a@example.com. Use Renew now to renew it."
}
```

```json
{
  "code": "unknown_machine",
  "error": "No machine with that identifier has reported to this account."
}
```

### 7.5 A sign-in in the log

`GET /gateway/licenses/log` (section 4) answers sign-ins among the switches, as rows whose `kind` is `sign-in`.
The names are those of every other row. What differs:

| Field | For a sign-in |
|---|---|
| `kindText` | "Sign-in". |
| `toAccountEmail` | The account signed in to. `fromAccountEmail` is empty. |
| `why`, `whyText` | `manual` "Add a spare", or `renewal` "Renew now". |
| `state`, `stateText`, `stateTone` | `pending` "Signing in" (neutral), `unknown` "Sent, no answer yet" (warning), `added` "Signed in" (good), `renewed` "Renewed" (good), `failed` "Failed" (bad). |
| `isOpen` | True for `pending` and `unknown`: the sign-in has not ended. |
| `reasonCode`, `reasonText` | When failed: the word and its sentence (`no-approver`, `approver-offline`, `approval-failed`, `wrong-account`, `browser-not-signed-in`, `login-failed`, `expired`, `store-refused`, `not-key-holder`, `director-offline`, `director-error`). |
| `message` | The Director's own sentence once it has answered; before that, what was asked. |
| `summary` | One sentence saying what happened. Show this. |
| `finishedOnMachineAtUtc` | When the Director says it finished. |
| `startedOnMachineAtUtc`, `exchangeMilliseconds`, `timingText`, `verifiedByUsageRead`, `verifiedText` | Always `null`: they belong to a switch. |

A row that is sent and gets no answer is `unknown`, and it stays open until the Gateway KNOWS how it ended. Nothing is
tried until no Director could still be running it (15 minutes after it was asked). Then, once a minute:

1. **The Gateway asks the machine's Director again, with the same sign-in identifier.** A Director that ran the
   sign-in answers with the result it kept - `added`, `renewed`, or `failed` with its reason and its own sentence -
   and the row ends with that answer; `replayed` is true and `sendCount` is one higher. Three things keep this step
   from saying or starting something wrong:
   - **A machine whose clock is 4 minutes or more from the Gateway's is not asked.** The Director judges the
     sign-in's deadline by its own clock. Asking again starts nothing only while that clock is within 5 minutes of
     the Gateway's; a Director further behind would see a deadline still ahead and start the sign-in a quarter of
     an hour after the owner clicked. The Gateway measures each machine's clock difference from its reports, and a
     machine at 4 minutes or more goes straight to step 2.
   - **An answer that is not a replay does not end a row whose command had gone out before.** A Director keeps a
     result for 24 hours. After that it answers `failed` / `expired` ("nothing was started") for a sign-in it did
     run, so that answer is not taken as how the sign-in ended: step 2 decides. Only for a row whose command never
     went out at all (the Gateway stopped before it sent) does that answer end the row.
   - **When the Director gives no answer in time, nothing is settled that minute** - not from a report either: it
     may be running the sign-in at that moment.
2. **Only when step 1 gave nothing that says how it ended** - the Director is not connected, was not asked, or its
   answer did not settle it - the row is settled from the machine's usage report, and only from a report the
   Gateway accepted AFTER those 15 minutes. A report from before that says nothing about how the sign-in ended.
   Then: a new spare is `added` when the report shows the machine holds the account, and `failed` when it does not;
   a renewal is `renewed` when the report shows a sign-in time other than the one the machine had reported before
   the renewal was asked for, and `failed` when it is the same. `message` and `summary` begin "Settled from the
   machine's report:" so the page says how it was known.
3. **A machine that has sent no report for 24 hours ends the row `failed`**, with no reason word and a sentence that
   begins "Ended without an answer:" and says the machine has not been seen for a day, so how the sign-in ended is
   not known. "Add a spare" or "Renew now" for that machine and account is then offered again.

**So a row can stay `unknown` for up to a day**: a machine that was switched off in the middle of a sign-in is shown
as "Sent, no answer yet" until it comes back or a day has passed. That is the truth - the Gateway does not know -
and the page shows it as it is. While the row is open, "Add a spare" or "Renew now" for that machine and account is
refused `sign_in_running`; the overview says so before the owner tries (7.6).

Three sign-ins, the newest first - one running, one that signed in, one that failed:

```json
{
  "asOfUtc": "2026-10-11T03:14:00Z",
  "isEmpty": false,
  "emptyText": null,
  "entries": [
    {
      "entryId": "1e080484ba78433982ea385e8ffa04f4",
      "kind": "sign-in",
      "kindText": "Sign-in",
      "atUtc": "2026-10-11T03:14:00Z",
      "machineId": "2f9cea24fe1b44f4951e48ed1748f105",
      "machineName": "the-windows-machine",
      "osUser": "owner",
      "label": "the-windows-machine",
      "fromAccountEmail": "",
      "toAccountEmail": "account-a@example.com",
      "why": "renewal",
      "whyText": "Renew now",
      "state": "pending",
      "stateText": "Signing in",
      "stateTone": "neutral",
      "isOpen": true,
      "reasonCode": null,
      "reasonText": null,
      "message": "Asked the Director on the-windows-machine to renew the sign-in for account-a@example.com.",
      "summary": "the-windows-machine is renewing its sign-in for account-a@example.com.",
      "sendCount": 0,
      "lastSentAtUtc": null,
      "startedOnMachineAtUtc": null,
      "finishedOnMachineAtUtc": null,
      "exchangeMilliseconds": null,
      "finishedAtUtc": null,
      "timingText": null,
      "verifiedByUsageRead": null,
      "verifiedText": null
    },
    {
      "entryId": "4cf0cc4873ce48f19fd29486681980fc",
      "kind": "sign-in",
      "kindText": "Sign-in",
      "atUtc": "2026-10-11T03:13:00Z",
      "machineId": "2f9cea24fe1b44f4951e48ed1748f105",
      "machineName": "the-windows-machine",
      "osUser": "owner",
      "label": "the-windows-machine",
      "fromAccountEmail": "",
      "toAccountEmail": "account-c@example.com",
      "why": "manual",
      "whyText": "Add a spare",
      "state": "added",
      "stateText": "Signed in",
      "stateTone": "good",
      "isOpen": false,
      "reasonCode": null,
      "reasonText": null,
      "message": "Signed in to account-c@example.com and stored as a spare.",
      "summary": "the-windows-machine was signed in to account-c@example.com and holds it as a spare.",
      "sendCount": 1,
      "lastSentAtUtc": "2026-10-11T03:13:00Z",
      "startedOnMachineAtUtc": null,
      "finishedOnMachineAtUtc": "2026-10-11T03:13:50Z",
      "exchangeMilliseconds": null,
      "finishedAtUtc": "2026-10-11T03:13:00Z",
      "timingText": null,
      "verifiedByUsageRead": null,
      "verifiedText": null
    },
    {
      "entryId": "843e95a632854de7a66b40391b14d647",
      "kind": "sign-in",
      "kindText": "Sign-in",
      "atUtc": "2026-10-11T03:12:00Z",
      "machineId": "2f9cea24fe1b44f4951e48ed1748f105",
      "machineName": "the-windows-machine",
      "osUser": "owner",
      "label": "the-windows-machine",
      "fromAccountEmail": "",
      "toAccountEmail": "account-b@example.com",
      "why": "renewal",
      "whyText": "Renew now",
      "state": "failed",
      "stateText": "Failed",
      "stateTone": "bad",
      "isOpen": false,
      "reasonCode": "approver-offline",
      "reasonText": "The machine set to approve this account had no Director connected.",
      "message": "The machine that approves sign-ins for account-b@example.com had no Director connected.",
      "summary": "The sign-in for account-b@example.com on the-windows-machine was not renewed.",
      "sendCount": 1,
      "lastSentAtUtc": "2026-10-11T03:12:00Z",
      "startedOnMachineAtUtc": null,
      "finishedOnMachineAtUtc": "2026-10-11T03:12:04Z",
      "exchangeMilliseconds": null,
      "finishedAtUtc": "2026-10-11T03:12:00Z",
      "timingText": null,
      "verifiedByUsageRead": null,
      "verifiedText": null
    }
  ],
  "hasMore": false,
  "nextCursor": null
}
```

### 7.6 What the overview says of sign-ins

`GET /gateway/licenses` (section 2) carries these as well. They are appended to the three shapes; nothing is renamed.

On an account (`LicenseAccountDto`):

| Field | What it is |
|---|---|
| `approver` | The account's approver, the same shape as in 7.1; `null` when none is set. |
| `approverText` | The sentence to show, whether or not one is set. |
| `approverTone` | `good`, `warning`, or `neutral` when none is set. |

On a machine (`LicenseMachineDto`):

| Field | What it is |
|---|---|
| `canAddSpare` | Add a spare is offered for this machine. |
| `addSpareText` | What it does, or the sentence saying why it is not offered - the same sentence the route refuses with. |
| `signInsInProgress` | The sign-ins on this machine that have not ended: `signInId`, `accountEmail`, `text`. |
| `directorId` | The Director that sent the machine's last accepted report. The approver form asks it for the machine's browser profiles (`GET /directors/{id}/browsers`). |
| `canListBrowserProfiles` | That Director is connected, so its browser profiles can be listed now. |
| `browserProfilesText` | What listing them does, or the sentence saying why they cannot be listed ("the-mac's Director is not connected, so its browser profiles cannot be listed."). |

On each sign-in a machine holds (`LicenseMachineHoldingDto`, in `active` and `spares`):

| Field | What it is |
|---|---|
| `canRenewNow` | Renew now is offered for this sign-in. |
| `renewNowText` | What it does, or the sentence saying why it is not offered - the same sentence the route refuses with. |

Which account a new spare is for is the owner's choice; the page offers a free choice of account email and the route
refuses `already_held` for one the machine holds. Only these fields of the answer are shown here, taken while the
renewal of 7.4 was running and the Director on "the-mac" was not connected:

```json
{
  "accounts": [
    {
      "accountEmail": "account-a@example.com",
      "approver": {
        "accountEmail": "account-a@example.com",
        "machineName": "the-mac",
        "osUser": "owner",
        "browserProfile": "work",
        "machineId": "527062dd720c4d28a144183033cf93c1",
        "label": "the-mac",
        "canApproveNow": false,
        "status": "not-connected",
        "statusText": "Not connected",
        "statusTone": "warning",
        "text": "Sign-ins for account-a@example.com are approved on the-mac, in the browser profile work. The Director on the-mac is not connected, so no sign-in can be approved there now.",
        "browserProfileName": null,
        "source": "registered",
        "state": "can-approve",
        "checkedAtUtc": "2026-10-11T00:12:00Z",
        "healthText": "The approver for account-a@example.com (browser profile \"work\" on the-mac) can approve sign-ins; it was last checked 2026-10-11 00:12 UTC.",
        "whatToDo": null
      },
      "approverText": "Sign-ins for account-a@example.com are approved on the-mac, in the browser profile work. The Director on the-mac is not connected, so no sign-in can be approved there now.",
      "approverTone": "warning"
    },
    {
      "accountEmail": "account-b@example.com",
      "approver": {
        "accountEmail": "account-b@example.com",
        "machineName": "the-laptop",
        "osUser": "owner",
        "browserProfile": "personal",
        "machineId": null,
        "label": "the-laptop",
        "canApproveNow": false,
        "status": "never-reported",
        "statusText": "Not reporting yet",
        "statusTone": "warning",
        "text": "Sign-ins for account-b@example.com are approved on the-laptop, in the browser profile personal. No Director on the-laptop has reported to this account, so no sign-in can be approved there yet.",
        "browserProfileName": null,
        "source": "owner",
        "state": "not-checked",
        "checkedAtUtc": null,
        "healthText": "The approver for account-b@example.com (browser profile \"personal\" on the-laptop) has never been checked.",
        "whatToDo": "Make sure the Director on the-laptop is running with license switching on; it checks its approvers once a day."
      },
      "approverText": "Sign-ins for account-b@example.com are approved on the-laptop, in the browser profile personal. No Director on the-laptop has reported to this account, so no sign-in can be approved there yet.",
      "approverTone": "warning"
    },
    {
      "accountEmail": "account-c@example.com",
      "approver": null,
      "approverText": "No machine is set to approve sign-ins for this account. A machine that has its own browser profile for it approves there.",
      "approverTone": "neutral"
    }
  ],
  "machines": [
    {
      "machineName": "the-mac",
      "canAddSpare": false,
      "addSpareText": "The Director on the-mac is not connected, so a sign-in cannot be started there.",
      "signInsInProgress": [],
      "active": {
        "accountEmail": "account-c@example.com",
        "canRenewNow": false,
        "renewNowText": "The Director on the-mac is not connected, so a sign-in cannot be started there."
      },
      "spares": []
    },
    {
      "machineName": "the-windows-machine",
      "canAddSpare": true,
      "addSpareText": "Signs the-windows-machine in to another account, to be held there as a spare.",
      "signInsInProgress": [
        {
          "signInId": "501d61535066417d966ef172151b00bc",
          "accountEmail": "account-a@example.com",
          "text": "the-windows-machine is renewing its sign-in for account-a@example.com."
        }
      ],
      "active": {
        "accountEmail": "account-a@example.com",
        "canRenewNow": false,
        "renewNowText": "A sign-in for account-a@example.com on the-windows-machine is already running."
      },
      "spares": [
        {
          "accountEmail": "account-b@example.com",
          "canRenewNow": true,
          "renewNowText": "Signs the-windows-machine in to account-b@example.com again, replacing the sign-in it holds."
        }
      ]
    }
  ]
}
```

### 7.7 What the page does not see

When a machine has no browser profile of its own for an account, its Director asks the Gateway to have the sign-in
approved on the approver set in 7.2 (the hub method `RequestLicenseApproval`). The Gateway passes the request to the
approving machine's Director and passes the answer back; nothing of it is stored and no route shows it. The page sees
only its result: the sign-in's row ends `added` or `renewed`, or `failed` with `no-approver` (none is set for the
account), `approver-offline` (the approving machine has no Director connected) or the reason the approving machine
gave.

The owner's email for a renewal that keeps failing (contract section 8.1) is not part of this section; it follows in
its own pull request.

## 8. `GET /gateway/licenses/alarms` - the license alarms open now (phase 6)

The red banner on every Cockpit page and on the phone. The shell shows `summary` when it is not null, then every
alarm's `text` in order, then `linkText` as the link to the Licenses page - and nothing at all when `count` is 0. Every
word is the Gateway's (`LicenseAlarmFold`); the client decides nothing. A read only: a session key may read it, and no
route raises or clears an alarm (contract section 9).

No alarm open:

```
{ "asOfUtc": "2026-10-17T08:00:00Z", "count": 0, "summary": null, "linkText": null, "alarms": [] }
```

Two open:

```
{
  "asOfUtc": "2026-10-17T08:00:00Z",
  "count": 2,
  "summary": "2 license problems need you.",
  "linkText": "Open Licenses",
  "alarms": [
    {
      "alarmId": "0123456789abcdef0123456789abcdef",
      "kind": "not-ready-at-80",
      "machineName": "machine-a",
      "accountEmail": "alice@example.com",
      "text": "machine-a holds no spare that could take it off alice@example.com. Sign machine-a in to a second account.",
      "openedAtUtc": "2026-10-17T07:58:00Z"
    },
    {
      "alarmId": "fedcba9876543210fedcba9876543210",
      "kind": "stopped-reporting",
      "machineName": "machine-b",
      "accountEmail": null,
      "text": "machine-b has not reported for 20 minutes. Check that the Director on machine-b is running.",
      "openedAtUtc": "2026-10-17T07:59:00Z"
    }
  ]
}
```

`machineName` is null for an alarm about no one machine, and `accountEmail` for an alarm about no one account.
