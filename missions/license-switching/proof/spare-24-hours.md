# The 24-hour spare check - 12 October 2026

The open item from `../phase-1-report.md`: does a spare that has sat unused for a day, with its access token long
expired, still renew and read with nobody there?

**Yes.** One command renewed it and read its usage.

## What was run

On the Windows machine, from the source of this pull request (Typer 0.16.1, Click 8.2.1), against an ISOLATED
store holding one spare for a test account ("account C") and an empty isolated default folder. The machine's real
Claude login was never read or touched. No sign-in and no browser approval was involved: this is the token renewal
`license usage --refresh` already does.

    cc-devthrottle license usage --refresh --json --default-config-dir <empty default folder> --store-dir <isolated store>

## What it shows

| | |
|---|---|
| The spare was last renewed | 21:34:11 UTC, 11 October (the store's own log) |
| Its access token expired | 05:34:11 UTC, 12 October |
| The check ran | 22:20:52 UTC, 12 October - 24 hours 46 minutes unused, 16 hours 46 minutes after the token expired |
| Renewed | yes: the store logged `key-refreshed` at 22:20:54 UTC; the access token and the refresh token both changed; the new access token ends 06:20:54 UTC, 13 October (8 hours) |
| Read | yes: `readError` is null and the usage figures came back with the new key |
| The sign-in's own end | unchanged by the renewal: 9 November, as `spare-refresh.md` measured |

`signedInAtUtc` is null because this spare was added with `license add` in phase 1, before a sign-in recorded its
own time; a spare added with `license sign-in` reports it.

Keys are shown only as the first 8 hex characters of their SHA-256. The account address and the folders are
replaced with labels.

## The output

```
STARTED 2026-10-12T22:20:52+00:00
BEFORE {"accessToken": "626f241f", "refreshToken": "d30e671f", "accessTokenExpiresAtUtc": "2026-10-12T05:34:11+00:00"}
FINISHED 2026-10-12T22:20:55+00:00
EXIT 0
OUTPUT
{
  "count": 1,
  "total": 1,
  "defaultConfigDir": "<empty default folder>",
  "storeDir": "<isolated store>",
  "recovered": null,
  "accounts": [
    {
      "accountEmail": "account C",
      "isActive": false,
      "plan": "max",
      "rateLimitTier": "default_claude_max_20x",
      "fiveHour": {
        "utilization": 0.0,
        "resetsAtUtc": null
      },
      "sevenDay": {
        "utilization": 99.0,
        "resetsAtUtc": "2026-10-13T12:00:00Z"
      },
      "extraUsage": {
        "isEnabled": false,
        "utilization": null
      },
      "readAtUtc": "2026-10-12T22:20:54Z",
      "readError": null,
      "readErrorDetail": null,
      "signedInAtUtc": null,
      "signInExpiresAtUtc": "2026-11-09T04:59:05Z",
      "folder": "<isolated store>\\spares\\<account C>"
    }
  ]
}

AFTER {"accessToken": "78156426", "refreshToken": "27ff60e5", "accessTokenExpiresAtUtc": "2026-10-13T06:20:54+00:00"}
LOG
{"time": "2026-10-11T20:27:27+00:00", "event": "add-finished", "account": "account C"}
{"time": "2026-10-11T21:34:11+00:00", "event": "key-refreshed", "account": "account C"}
{"time": "2026-10-12T22:20:54+00:00", "event": "key-refreshed", "account": "account C"}
```
