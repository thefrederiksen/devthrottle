import type {
  LicenseAccount,
  LicenseForecast,
  LicenseHolding,
  LicenseLogPage,
  LicenseOverview,
  LicenseRenewalResult,
  LicenseSwitchNowResult,
} from "@devthrottle/client-core/licenses/licensesClient";

// THE GATEWAY'S OWN ANSWERS, NOT HAND-WRITTEN ONES. Each constant below is the real example of its route from
// missions/license-switching/cockpit-api.md, which the Gateway produced by running its route handlers over a
// store filled with the reports of its machines and accounts. A test that draws these draws what the Gateway
// sends. This file is written by missions/license-switching/phase-3/copy_fixtures.py and is not edited by hand:
// fixtures.test.ts fails when a constant here differs from its example in the document. Run the script again.

/** GET /gateway/licenses: three accounts (in use, sign-in expires soon, at limit) on two machines. */
export const OVERVIEW: LicenseOverview = {
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
          "unavailableText": "the-mac holds no usable spare for account-c@example.com.",
          "isAutomaticChoice": false
        }
      ],
      "barredUntilUtc": null,
      "barredText": null
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
      "barredText": null
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
      "barredText": null
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
};

/** The last two fields of an account that is barred from the automatic choice of a target. */
export const BARRED_FIELDS: Pick<LicenseAccount, "barredUntilUtc" | "barredText"> = {
  "barredUntilUtc": "2026-10-11T05:12:00Z",
  "barredText": "Left out of the automatic choice for 2 hours, until its 5-hour window resets: it triggered a switch. It can still be chosen by hand."
};

/** GET /gateway/licenses/forecast for the same picture. */
export const FORECAST: LicenseForecast = {
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
};

/** GET /gateway/licenses/log?limit=4: a spare added, a switch that FAILED, a spare removed, a switch that worked; older rows exist. */
export const LOG_PAGE: LicenseLogPage = {
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
};

/** GET /gateway/licenses for an account whose machines have never reported. */
export const EMPTY_OVERVIEW: LicenseOverview = {
  "asOfUtc": "2026-10-11T03:12:00Z",
  "isEmpty": true,
  "emptyText": "No machine has reported its Claude accounts yet. They appear here once a Director with license switching turned on has connected.",
  "accounts": [],
  "machines": []
};

/** PUT /gateway/licenses/accounts/renewal, a date stored. */
export const RENEWAL_SET: LicenseRenewalResult = {
  "accountEmail": "account-a@example.com",
  "renewsOn": "2026-10-17",
  "renewsText": "Renews 17 Oct"
};

/** PUT /gateway/licenses/accounts/renewal, the date cleared. */
export const RENEWAL_CLEARED: LicenseRenewalResult = {
  "accountEmail": "account-a@example.com",
  "renewsOn": null,
  "renewsText": "Renewal date not set"
};

/** The three fields of a holding whose last sign-in renewal failed. */
export const RENEWAL_FAILED_FIELDS: Pick<LicenseHolding, "renewalFailedAtUtc" | "renewalFailedText" | "renewalFailedTone"> = {
  "renewalFailedAtUtc": "2026-10-11T00:12:00Z",
  "renewalFailedText": "Renewing this sign-in failed 3 hours ago: no browser is set to approve this account.",
  "renewalFailedTone": "bad"
};

/** POST /gateway/licenses/switch-now, accepted: one machine, whose switch waits for its Director. */
export const SWITCH_NOW_ACCEPTED: LicenseSwitchNowResult = {
  "targetAccountEmail": "account-b@example.com",
  "text": "The switch of machine-of-one to account-b@example.com has not finished; the switch log shows how it ends.",
  "switches": [
    {
      "switchId": "f28f268f315842558629a946f91050c3",
      "entryId": "e6fd8eedf7b04915bed55866cd4eac04",
      "machineName": "machine-of-one",
      "osUser": "owner",
      "label": "machine-of-one",
      "state": "pending",
      "stateText": "Waiting for the machine",
      "stateTone": "warning",
      "summary": "machine-of-one is switching from account-a@example.com to account-b@example.com."
    }
  ],
  "skipped": []
};

/** POST /gateway/licenses/switch-now, refused 409. */
export const SWITCH_NOW_REFUSED: { code: string; error: string } = {
  "code": "no-machine-to-switch",
  "error": "No machine on account-a@example.com can be switched now: every machine on it is stale or already mid-switch. machine-of-one is not switched: a switch sent to it has not finished."
};

/** The renewal date, asked by a caller that is not the owner's own signed-in phone or browser: 403. */
export const RENEWAL_OWNER_ONLY: { code: string; error: string } = {
  "code": "owner_only",
  "error": "only the owner on their own signed-in phone or browser may set the renewal date of a subscription; this request was made with a device (workstation) credential"
};

/** Switch now, asked by the same caller: 403, with its own sentence. */
export const SWITCH_NOW_OWNER_ONLY: { code: string; error: string } = {
  "code": "owner_only",
  "error": "only the owner on their own signed-in phone or browser may switch the machines of a subscription to another one; this request was made with a device (workstation) credential"
};

/** A fresh copy of a fixture, so a test may change it without changing it for the next test. */
export function copyOf<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T;
}
