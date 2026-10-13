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
   name ("the-windows-machine (owner)"). Every sentence in these answers calls a machine by its label. Show `label`;
   never build a name from the other fields.

**Decided here (the Tech Lead's rulings on the review of this pull request):**

- **Any session key can read these three routes.** That means every agent session of the DevThrottle account can
  read all of its Claude account emails, its machine names, their operating system users and the switch log. That
  is accepted: an agent has to be able to say which account a machine is on and why it was switched. No session key
  can switch anything (section 6).
- **The log is one row per machine, and the page lists it that way.** One switch of an account that is on three
  machines is three rows, each with its own state, reason and timings, because each machine can end differently.
  The rows carry no identifier that groups them and the page does not group them.
- **The renewal date of a subscription is a date a person sets, and no report can carry it.** Goal 1's "renewal
  date" is the date the SUBSCRIPTION is billed again. No Director can read that from a sign-in. Its two fields are
  on every account now, with their final names - `renewsOn` and `renewsText` - and until the follow-up (section 6)
  adds the route that sets the date, `renewsOn` is null and `renewsText` is "Renewal date not set" for every
  account. It is never a guess. `signInExpiry` is a DIFFERENT date - when a machine must be signed in to the account
  again - and the page must never label it "renewal".
- **These field names are final.** Phase 3 builds to them. The follow-up fills in values; it renames nothing.

Who may call them: the Cockpit and the phone (a device key), a machine's own credential, and a session key (an
agent) may call the three reads - they are in `SessionKeyGuard`'s allow list. Switch now is refused to a session
key (section 6).

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
| `readAtUtc`, `readAgeText` | When the numbers were read and the sentence saying how old they are and on which machine ("Read 7 minutes ago on the-mac", or "Never read"). |
| `readError`, `readErrorText` | The word and the sentence when the last read failed; both null when it worked. |
| `extraUsage` | `state` (`on`, `off`, `not-known`), `text`, and `utilization` when the provider gave one. |
| `renewsOn`, `renewsText` | The date the subscription is billed again, as `yyyy-MM-dd` (a date with no time and no zone), and its sentence: "Renews 17 Oct", or "Renewal date not set". `renewsOn` is null when no date is set - and none is set for any account until the follow-up (section 6) adds the route that sets it, so today `renewsOn` is always null and `renewsText` is always "Renewal date not set". Show `renewsText`. |
| `signInExpiry` | The sign-in for this account that expires FIRST, across every machine holding it: `expiresAtUtc`, `machine` (the machine it is on, rule 9), `verdict` (the expiry verdict below), `text` ("Sign-in on the-windows-machine expires in 2 days 5 hours", "Sign-in on the-mac expired 3 hours ago") and `tone`. When no machine holding the account gave an expiry: `expiresAtUtc` and `machine` are null, `verdict` is `not known` and `text` is "Sign-in expiry not known". When no machine holds the account: the same, with "Not signed in on any machine". This is the sign-in expiry, not the subscription's renewal date (section 1). |
| `activeOnMachines`, `spareOnMachines` | The machines working on it, and the machines holding it as a spare - each one the four fields of rule 9. |
| `canSwitchNow` | The Switch now control is offered for this account. |
| `switchNowText` | What Switch now would do ("Moves the-mac and the-windows-machine to account-b@example.com."), or the sentence saying why it is not offered. Always a sentence. |
| `switchNowTargetAccountEmail` | The target Switch now takes when none is named; null when it would take none. |
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

A sign-in a machine holds (`LicenseMachineHoldingDto`): `accountEmail`, `isActive`, `signedInAtUtc`, `signedInText`
("Signed in 21 days ago", or "Sign-in date not known"), `signInExpiresAtUtc`, `expiryVerdict`, `expiryText`,
`expiryTone`, `renewalFailedAtUtc`, `renewalFailedText`, `renewalFailedTone`, `isReadingStale`, `readError`,
`readErrorText`.

**A failed renewal** (`renewalFailedAtUtc`, `renewalFailedText`, `renewalFailedTone`): when the machine last tried to
renew this sign-in and failed, the sentence for it, and its tone. **All three are null when the last renewal worked
or is not known.** The Gateway does not yet store what a report says about a renewal, so today all three are null on
every holding; the follow-up (section 6) fills them in. When `renewalFailedText` is not null, show it on the holding
in its tone; when it is null, show nothing.

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
          "machineId": "c0910007404f4ab9a4f725c028c0569b",
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
          "machineId": "2fef72637c9d41a58d77623fed457e8a",
          "machineName": "the-mac",
          "osUser": "owner",
          "label": "the-mac"
        },
        {
          "machineId": "c0910007404f4ab9a4f725c028c0569b",
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
          "unavailableText": "the-mac holds no usable spare for account-c@example.com.",
          "isAutomaticChoice": false
        }
      ]
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
          "machineId": "c0910007404f4ab9a4f725c028c0569b",
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
          "machineId": "2fef72637c9d41a58d77623fed457e8a",
          "machineName": "the-mac",
          "osUser": "owner",
          "label": "the-mac"
        },
        {
          "machineId": "c0910007404f4ab9a4f725c028c0569b",
          "machineName": "the-windows-machine",
          "osUser": "owner",
          "label": "the-windows-machine"
        }
      ],
      "canSwitchNow": false,
      "switchNowText": "No machine on account-b@example.com can be switched now: none is on it.",
      "switchNowTargetAccountEmail": null,
      "switchTargets": []
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
          "machineId": "c0910007404f4ab9a4f725c028c0569b",
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
          "machineId": "c0910007404f4ab9a4f725c028c0569b",
          "machineName": "the-windows-machine",
          "osUser": "owner",
          "label": "the-windows-machine"
        }
      ],
      "canSwitchNow": false,
      "switchNowText": "No machine on account-c@example.com can be switched now: none is on it.",
      "switchNowTargetAccountEmail": null,
      "switchTargets": []
    }
  ],
  "machines": [
    {
      "machineId": "2fef72637c9d41a58d77623fed457e8a",
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
        "readErrorText": null
      },
      "spares": [
        {
          "accountEmail": "account-b@example.com",
          "isActive": false,
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
          "readErrorText": null
        }
      ],
      "switchInProgress": null
    },
    {
      "machineId": "c0910007404f4ab9a4f725c028c0569b",
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
        "readErrorText": null
      },
      "spares": [
        {
          "accountEmail": "account-b@example.com",
          "isActive": false,
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
          "readErrorText": null
        },
        {
          "accountEmail": "account-c@example.com",
          "isActive": false,
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
          "readErrorText": null
        }
      ],
      "switchInProgress": null
    }
  ]
}
```

## 3. `GET /gateway/licenses/forecast` - the forecast and the schedule

`LicenseForecastResponse`. The rate of use comes from the readings of the last two hours, which the Gateway keeps in
memory: after a Gateway restart the rate is back within two reports (about four minutes), and until then the
sentences say there are not enough readings. A target is always "on today's numbers": the forecast does not project
the other accounts forward. A window rising so slowly that it would need longer than a week to reach 97% - longer
than any window lasts - is not expected to reach it, whatever its reset time.

| Field | Meaning |
|---|---|
| `nextSwitch` | Null when no switch is expected. Otherwise the soonest one: `fromAccountEmail`, `expectedAtUtc`, `window` (`five-hour` or `seven-day`), `windowText` ("5-hour window" or "weekly limit"), `targetAccountEmail` (null when nothing would be switched), `machines` (the machines that would be switched, each the four fields of rule 9). |
| `nextSwitchText` | The sentence for it, or "No switch is expected.". When there is no target it gives the reason: no account has room, or no machine on the account can be sent a switch. |
| `accounts[]` | Per account: `isInUse`, `isStale`, the two windows, `expectedAtLimitUtc` and `expectedText` (when it is expected to reach 97% on either window), `nextSwitchTargetAccountEmail` and `nextSwitchText` (where its machines go at its next switch). |
| `accounts[].fiveHour`, `.sevenDay` | The window in the overview's own words - `utilization`, `utilizationText`, `level`, `resetsAtUtc`, `resetsText`, exactly as section 2 gives them for the same window - and then the forecast: `percentPerHour` (null when there is no rate), `rateText` ("28.5% an hour", "Not rising", "No rate yet"), `expectedAtLimitUtc`, `expectedText`. The schedule colours a window by `level` and shows `resetsText`; it never compares `utilization` with 97 or `resetsAtUtc` with now, and it does not need the overview to do either. |
| `machines[]` | Per machine: `machineId`, `machineName`, `osUser`, `label` (rule 9), `activeAccountEmail`, `nextAccountEmail` (the account it moves to next; null when there is none), `expectedSwitchAtUtc` (null when no switch is expected yet), and `text`, which says it all: "Next: account-b@example.com, in about 2 minutes.", "Next: account-b@example.com, when account-a@example.com reaches its limit. No switch is expected yet.", "Would not be switched now: its last report is too old.", "Signed in to no account, so there is nothing to switch." |

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
        "machineId": "2fef72637c9d41a58d77623fed457e8a",
        "machineName": "the-mac",
        "osUser": "owner",
        "label": "the-mac"
      },
      {
        "machineId": "c0910007404f4ab9a4f725c028c0569b",
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
      "machineId": "2fef72637c9d41a58d77623fed457e8a",
      "machineName": "the-mac",
      "osUser": "owner",
      "label": "the-mac",
      "activeAccountEmail": "account-a@example.com",
      "nextAccountEmail": "account-b@example.com",
      "expectedSwitchAtUtc": "2026-10-11T03:14:04.1052631Z",
      "text": "Next: account-b@example.com, in about 2 minutes."
    },
    {
      "machineId": "c0910007404f4ab9a4f725c028c0569b",
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
| `kind`, `kindText` | `switch` (Switch), `spare-added` (Spare added), `spare-removed` (Spare removed), `no-room` (No account had room). **A kind a later build writes is shown as "Note"** - the row is still listed, and its `summary` is its message. Never branch on a kind you do not know; show `kindText` and `summary`. |
| `atUtc` | When the Gateway decided the switch or saw the change. |
| `machineName`, `osUser` | The machine the row is about. One switch of an account is one row per machine, and the page lists one row per machine (section 1). "" on a row about no machine (a `no-room` row). |
| `fromAccountEmail`, `toAccountEmail` | A switch's from and to. On `spare-added` only `to` is set, on `spare-removed` and `no-room` only `from`. |
| `why`, `whyText` | Why a switch was asked for: `threshold` (Threshold reached) or `manual` (By hand). Null and "" on other rows. |
| `state`, `stateText`, `stateTone`, `isOpen` | Where the row stands, in the table below. `isOpen` is true while it has not reached its end. |
| `reasonCode`, `reasonText` | When a switch is waiting or failed: the reason word and its sentence. Null otherwise. |
| `message` | The sentence the machine or the Gateway wrote on the row; may be null. |
| `summary` | The row in one sentence. Always set. |
| `sendCount`, `lastSentAtUtc` | How many times the command was sent, and when last. |
| `startedOnMachineAtUtc`, `finishedOnMachineAtUtc`, `exchangeMilliseconds`, `finishedAtUtc` | The timings: the machine's own start and finish, the exchange step alone, and when the row reached its end by the Gateway's clock. Null when not known. |
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
| any other word | the word itself | neutral | no |

The `summary` of a switch row says "is switching" only while it is `pending` or `unknown`. A switch row in a state
a later build writes is not said to be under way: its `summary` is its message, or a sentence naming the state.

A `no-room` row is open while its `finishedAtUtc` is null (the account is still out of room), whatever its state word.

### Example (`?limit=4`)

```json
{
  "asOfUtc": "2026-10-11T03:12:00Z",
  "isEmpty": false,
  "emptyText": null,
  "entries": [
    {
      "entryId": "fd6f2c59dd514884b4a1d8139670469a",
      "kind": "spare-added",
      "kindText": "Spare added",
      "atUtc": "2026-10-11T03:12:00Z",
      "machineName": "the-windows-machine",
      "osUser": "owner",
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
      "entryId": "83019e32f08e4a1486d3ac48c3ef6c14",
      "kind": "switch",
      "kindText": "Switch",
      "atUtc": "2026-10-11T01:12:00Z",
      "machineName": "the-windows-machine",
      "osUser": "owner",
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
      "entryId": "e3dc6d1ca06d4cb7ae3bf05c3dc517b6",
      "kind": "spare-removed",
      "kindText": "Spare removed",
      "atUtc": "2026-10-10T23:12:00Z",
      "machineName": "the-windows-machine",
      "osUser": "owner",
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
      "entryId": "4cbca57571e74da6b8b1552b73db78b3",
      "kind": "switch",
      "kindText": "Switch",
      "atUtc": "2026-10-10T22:12:00Z",
      "machineName": "the-windows-machine",
      "osUser": "owner",
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

## 6. The follow-up pull request - Switch now, the renewal date, a failed renewal

This section is final: the names and routes below are what the follow-up builds, and phase 3 can be laid out for
them now. The follow-up is one pull request, "Licenses API: Switch now, renewal date, failed renewal", opened after
this one and the pull request that wires the Gateway's once-a-minute check have both merged. It adds one migration,
for both database providers. Nothing in sections 1 to 5 is renamed by it.

### Switch now

Everything the page needs to DRAW the control is in section 2 already (`canSwitchNow`, `switchNowText`,
`switchNowTargetAccountEmail`, `switchTargets`). The route that performs it calls one method,
`LicenseSwitchService.SwitchNowAsync`, which belongs to the pull request that wires the once-a-minute check
(`missions/license-switching/switch-now-method.md` on `origin/license-switching-gw-tick` has its exact signature).

- `POST /gateway/licenses/switch-now`, body `{ "accountEmail": "...", "targetAccountEmail": "..." | null }` - by
  ACCOUNT, because usage is counted per account and every machine on it moves; the target is optional.
- Accepted: `200` with the target and one entry per machine sent a switch (its switch identifier, its state word and
  its sentence, folded like a log row), and the machines left out with the reason for each.
- Refused: `409` with the refusal word and the same sentence `switchTargets[].unavailableText` or `switchNowText`
  already shows, so the page shows nothing new.
- **One case the follow-up must close.** The page takes `canSwitchNow` from the picture in which a machine is mid-switch
  whenever its log row is `pending` or `unknown`. The Switch now method is a little narrower when it decides: a row that
  is `pending` because the machine's Director is offline was never sent, so it does not hold the machine back. Until the
  follow-up, the page therefore does NOT offer Switch now for an account whose only machine is waiting for an offline
  Director, although the route would accept it. The follow-up makes the fold ask the same narrowed picture, so the page
  offers exactly what the route accepts. The page needs no change for it.

### The renewal date

- `PUT /gateway/licenses/accounts/renewal`, body `{ "accountEmail": "...", "renewsOn": "yyyy-MM-dd" | null }` - sets
  or clears the date for one account of the caller's own DevThrottle account. The Gateway stores it in a new column.
- The overview then answers `renewsOn` with that date and `renewsText` with "Renews 17 Oct"; with no date set they
  stay null and "Renewal date not set", as they are today.

### A failed renewal

- The Gateway stores what each usage report says of a holding's last renewal (`RenewalFailedAtUtc` and `RenewalError`
  of the contract, section 8.1), and the overview then fills `renewalFailedAtUtc`, `renewalFailedText` and
  `renewalFailedTone` on that holding. No route is added for it.

### Who may call the two writes

**Both are refused to a session key.** Moving every machine of the owner's subscription to another one, and setting
the date a subscription renews, are the owner's own actions, from the Cockpit. `SessionKeyGuard` lists neither route;
a test holds Switch now to that today (`LicenseGuardTests`), and the follow-up adds the same for the renewal route.
