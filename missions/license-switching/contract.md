# License Switching - the Director and Gateway contract

The messages the Director (phase 1) and the Gateway (phase 2) exchange for license switching. Both
phases build against this file; a change to it is a change to both, and goes through the Tech Lead.

Words follow `MISSION.md` section 5: an **account** is one Claude subscription, identified by its
email; a **sign-in key** is what Claude Code keeps after signing in; the **active** key is the one in
the machine's default Claude config; a **spare** is a key for another account kept in the Director's
key store. In this file, **holding** means "one sign-in key this machine has, active or spare" - the
word is chosen so that no field name in the contract contains "key" or "token" (rule 5.1).

## 1. The channel this rides on - nothing new is invented

All of it uses the one tunnel that already exists. The Director dials the Gateway; the Gateway never
dials the Director.

| What | Where it is today | How license switching uses it |
|---|---|---|
| The hub the Director dials | `src/CcDirector.Gateway/Streaming/DirectorHub.cs` (`DirectorHub : Hub`) | One new public hub method, `PushLicenseUsage` (section 2). |
| The Director's client | `src/CcDirector.ControlApi/GatewayStreamClient.cs` | Calls `PushLicenseUsage` through its existing `SendAsync`/sequence pattern, the way `PushRepoSnapshot` is sent. |
| Binding a connection to a Director and a tenant | `DirectorHub.Hello(DirectorStreamHello)`; tenant from `ResolveConnectionTenant()`, never from a payload | Unchanged. The report's Director id and tenant come from the binding. |
| "Does the Gateway have this method?" | `GatewayCapabilities.HubMethods`, answered by `Hello`; `GatewayStreamClient.GatewaySupports(name)` | The Director calls `PushLicenseUsage` only when `GatewaySupports("PushLicenseUsage")`. `HubMethods` is built by reflection, so the new method appears in it with no other edit. |
| A command down to a Director | Hub client call `"Command"` with `DirectorCommand { CommandId, Verb, SessionId, PayloadJson }`, answered by `DirectorCommandResult { CommandId, Status, BodyJson, Error }` (`src/CcDirector.Gateway.Contracts/DirectorCommandMessages.cs`) | One new verb, `license-switch` (section 3). `SessionId` is `""`: it addresses no session. |
| Sending it from the Gateway | `DirectorCommandRouter.TrySendAsync(...)` (`src/CcDirector.Gateway/Api/DirectorCommandRouter.cs`) over `GatewayHost.SendCommandAsync` | Used as is, with an explicit timeout (section 3.4). Null means "not connected"; `Timeout` and `TunnelDropped` are made by the Gateway, never by the Director. |
| Running it on the Director | An `ISessionCommandArea` (`src/CcDirector.ControlApi/SessionCommandArea.cs`) listed in `SessionCommandExecutor.Areas` (`src/CcDirector.ControlApi/SessionCommandExecutor.cs`) | A new area class, `LicenseSwitchExecutor`, in `src/CcDirector.ControlApi/`, added to `Areas` with a one-line comment, owning the one verb. `SecretTransferExecutor.cs` is the model: a refusal is a successful answer carrying a reason. |

**Serialization.**
- The hub method's arguments go over SignalR with the MessagePack protocol (`AddMessagePackProtocol()`
  on both sides), exactly like `PushRepoSnapshot`. Plain classes with public get/set properties, no
  attributes, the way every type in `CcDirector.Gateway.Contracts` is written.
- The command payload and the result body are JSON strings inside `PayloadJson` and `BodyJson`, written
  with `JsonSerializerDefaults.Web` (camel case property names) - the options `DirectorCommandRouter`
  and `SessionCommandExecutor.Serialize/Deserialize` already use.
- Every time is a UTC `DateTime` (ISO 8601 with `Z` in JSON). Every percentage is a `double` from 0 to
  100, as the provider's usage answer gives it. Fixed vocabularies (outcome, reason code, plan) are
  strings, not enums, so a JSON body never carries a bare number and an older side reading a newer
  value sees a word it can log.

### 1.1 What the Director already has, and how this relates to it

**`src/CcDirector.Core/Claude/ClaudeAccountStore.cs`** (with `ClaudeAccount.cs` beside it) is the
Director's existing account list: `accounts.json` under `CcStorage.ToolConfig("director")`, filled by
the Accounts dialog (`src/CcDirector.Avalonia/AccountsDialog.axaml.cs`) and loaded at start-up in
`App.axaml.cs`. Two parts of it are reused and one part must not be:

- **Reused:** `ParseCredentialsJson` already reads `subscriptionType` and `rateLimitTier` from a
  `.credentials.json`; those two words are the report's `Plan` and `RateLimitTier` (section 2.2).
  `GetDefaultCredentialsPath` is the Windows and Linux active key's location.
- **Must not be reused as the key store:** each `ClaudeAccount` holds its own `AccessToken` and
  `RefreshToken`, and `CaptureFromCredentials` / `RefreshActiveTokenFromCredentials` write the ACTIVE
  sign-in's tokens into `accounts.json` beside the live `.credentials.json`. That is two places holding
  one sign-in - exactly the copy `MISSION.md` trap 2 forbids (a refresh in one may cancel the other).
  The license key store (phase 1) keeps each sign-in in exactly one place, and the report's holdings
  come from that store, never from `accounts.json`. Whether phase 1 retires `ClaudeAccountStore` and
  the Accounts dialog or makes them stop copying the active sign-in is phase 1's call, recorded in its
  pull request; this contract only requires that after phase 1 no second copy of a sign-in is written
  anywhere.

**The retired usage poll.** Until 22 September 2026 the Director read the provider's usage endpoint
once a minute for every stored account and raised an event nothing subscribed to; it was deleted, and
`src/CcDirector.Core.UnitTests/Background/NoUnreadUsagePollTests.cs` now fails if any Director source
names `api/oauth/usage`. That rule was "a call whose result nothing reads is deleted". **The usage
report replaces that retired poll for a reader that now exists: the Gateway's decision engine and the
Licenses page.** Phase 1 therefore changes that test rather than deleting it: it allows the endpoint in
exactly one file - the license usage reader - and asserts, as a presence, that the reader's result
reaches `PushLicenseUsage`, so an unread poll cannot come back under cover of this one. The new reader
is also cheaper than the old one: the active account every 2 minutes and each spare at most every 10,
against every account every minute.

**Where the types live.** One new file, `src/CcDirector.Gateway.Contracts/LicenseMessages.cs`, holding
every class and constant named below. Both sides reference that project already.

## 2. The usage report (Director to Gateway)

### 2.1 Hub method

```csharp
// DirectorHub
public void PushLicenseUsage(long sequence, LicenseUsageReport report)
```

Shaped like `PushRepoSnapshot`: `RequireBoundDirector()` first (a report before `Hello` is refused),
the tenant is `RequireBoundTenant()`, the work runs inside `EnterBoundTenantScope()`, and a report is
accepted only from the Director's **active connection** with a `sequence` higher than the last one
accepted for that Director (the same gate `PushedRepositoryStore.ApplySnapshot` applies). A refused
report is logged and dropped; it is not an error to the Director.

### 2.2 Types

```csharp
public sealed class LicenseUsageReport
{
    public string MachineName { get; set; } = "";      // which machine sent it (same value the Director puts in Hello)
    public string OsUser { get; set; } = "";           // the operating system user whose Claude config this is (same as Hello.User)
    public DateTime SentAtUtc { get; set; }            // when the Director built this report
    public string Trigger { get; set; } = "";          // LicenseReportTriggers: "interval" | "changed" | "after-switch" | "connected"
    public string? SwitchInProgressId { get; set; }    // the SwitchId being executed right now, else null
    public List<LicenseHolding> Holdings { get; set; } = new();  // EVERY account this machine holds, active first
}

public sealed class LicenseHolding
{
    public string AccountEmail { get; set; } = "";     // from the holding's oauthAccount block; lower-cased
    public bool IsActive { get; set; }                 // true for exactly one holding when the machine is signed in
    public string? Plan { get; set; }                  // "max" | "pro" | ... as the sign-in names it; null when not known
    public string? RateLimitTier { get; set; }         // as the sign-in names it; null when not known
    public LicenseWindow? FiveHour { get; set; }       // null when the read failed or the answer carried no such window
    public LicenseWindow? SevenDay { get; set; }       // same
    public LicenseExtraUsage? ExtraUsage { get; set; } // null when not known
    public DateTime? ReadAtUtc { get; set; }           // when THESE numbers were read from the provider; null if never read
    public string? ReadError { get; set; }             // LicenseReadErrors code when the last read failed, else null
    public string? ReadErrorDetail { get; set; }       // one plain sentence, never containing anything from the sign-in
    public DateTime? SignedInAtUtc { get; set; }       // when this sign-in was made (the sign-in that created it); null when not known
    public DateTime? SignInExpiresAtUtc { get; set; }  // when the Director expects this sign-in to stop working; null when not known
}

public sealed class LicenseWindow
{
    public double Utilization { get; set; }            // 0..100, verbatim from the provider
    public DateTime? ResetsAtUtc { get; set; }         // null when the provider gave none
}

public sealed class LicenseExtraUsage
{
    public bool? IsEnabled { get; set; }               // the paid extra usage is switched on for this account
    public double? Utilization { get; set; }           // 0..100 of the monthly extra-usage limit, when given
}
```

Read error codes (`LicenseReadErrors`): `"unauthorized"` (the provider refused the sign-in - it needs a
new sign-in), `"refresh-failed"` (the stored sign-in could not be renewed), `"rate-limited"`,
`"network"`, `"unexpected-answer"`, `"store-unreadable"`. When a read fails, the holding keeps the
**previous** numbers and their old `ReadAtUtc` and sets `ReadError`; the Gateway sees from `ReadAtUtc`
how old the numbers are (section 5.2). It never sees a zero standing in for "unknown".

The account email comes from the `oauthAccount` block the Director keeps with each holding (the active
one in the default config's `.claude.json`; a spare in its own folder). `Plan` and `RateLimitTier` come
from the sign-in's own `subscriptionType` and `rateLimitTier` fields, read with the existing
`ClaudeAccountStore.ParseCredentialsJson` (section 1.1) - those two words are copied, and nothing else
from that file is.

**How long a sign-in lives.** A sign-in dies about 30 days after it was made, however often it is
refreshed: a refresh renews the short-lived access, not the sign-in itself. So each holding carries
`SignedInAtUtc` - when the `claude auth login` that created it ran - and `SignInExpiresAtUtc` - when the
Director expects it to stop working. **The Director fills both; the Gateway never computes them**, so
the Gateway holds no rule about how long a sign-in lasts, and a change to that lifetime is a Director
change alone. Either is null when the Director does not know it (for example a sign-in made by hand
before the Director first saw it); a null expiry is never read as "fine" or as "expired" (section 5.7).

### 2.3 How often

- **Interval:** every **2 minutes** while the tunnel is connected (`Trigger = "interval"`).
- **The active holding is read from the provider for every report.** Spare holdings are read at most
  every **10 minutes**; in between, a report carries a spare's last numbers with their own
  `ReadAtUtc`. (A spare is also kept fresh between reads - that is phase 1's work and not part of the
  wire.)
- **On change, at once** (`Trigger = "changed"`): a holding added or removed, the active account
  changed by anything other than a Gateway command (for example `claude auth login` by hand), or a
  holding's `ReadError` appearing or clearing.
- **Right after every `license-switch`**, succeeded or not, after the readback (`"after-switch"`).
- **On every new connection**, after `Hello` (`"connected"`), so a restarted Gateway has the facts
  within seconds rather than two minutes.

The report is always **complete**: every holding the machine has, every time. It is a snapshot, never
a change list.

### 2.4 One Director per machine reports

Several Directors can run on one machine as one operating system user (the installed app and the
development slots), and they share one default Claude config. Exactly one of them is the **key
holder** for that machine and user: the Director that holds an exclusive lock on a lock file in the key
store's folder, opened with no sharing and kept open for the life of the process. Only the key holder
sends `PushLicenseUsage` and only it executes `license-switch`; any other Director answers
`license-switch` with `"not-key-holder"`. When the holder exits, the next Director to take the lock
sends a `"connected"` report at once.

The Gateway's machine is `(tenant, MachineName, OsUser)`, compared without regard to case. The
Gateway records which Director id sent the last accepted report for a machine and sends that machine's
`license-switch` to that Director only.

### 2.5 Example

Shown as JSON for reading; on the wire it is MessagePack with these property names.

```json
{
  "MachineName": "the-windows-machine",
  "OsUser": "owner",
  "SentAtUtc": "2026-10-11T03:12:00Z",
  "Trigger": "interval",
  "SwitchInProgressId": null,
  "Holdings": [
    {
      "AccountEmail": "account-a@example.com",
      "IsActive": true,
      "Plan": "max",
      "RateLimitTier": "default_claude_max_20x",
      "FiveHour": { "Utilization": 96.0, "ResetsAtUtc": "2026-10-11T05:00:00Z" },
      "SevenDay": { "Utilization": 61.0, "ResetsAtUtc": "2026-10-15T09:00:00Z" },
      "ExtraUsage": { "IsEnabled": false, "Utilization": null },
      "ReadAtUtc": "2026-10-11T03:11:58Z",
      "ReadError": null,
      "ReadErrorDetail": null,
      "SignedInAtUtc": "2026-09-20T14:05:00Z",
      "SignInExpiresAtUtc": "2026-10-20T14:05:00Z"
    },
    {
      "AccountEmail": "account-b@example.com",
      "IsActive": false,
      "Plan": "max",
      "RateLimitTier": "default_claude_max_20x",
      "FiveHour": { "Utilization": 4.0, "ResetsAtUtc": "2026-10-11T07:40:00Z" },
      "SevenDay": { "Utilization": 22.0, "ResetsAtUtc": "2026-10-16T18:00:00Z" },
      "ExtraUsage": { "IsEnabled": false, "Utilization": null },
      "ReadAtUtc": "2026-10-11T03:04:10Z",
      "ReadError": null,
      "ReadErrorDetail": null,
      "SignedInAtUtc": "2026-09-13T08:30:00Z",
      "SignInExpiresAtUtc": "2026-10-13T08:30:00Z"
    }
  ]
}
```

## 3. The switch command (Gateway to Director)

### 3.1 Verb and envelope

`DirectorCommand { CommandId = <new per send, minted by TrySendAsync>, Verb = "license-switch", SessionId = "", PayloadJson = <LicenseSwitchRequest as JSON> }`.

`CommandId` stays what it is today: one per send, for the log. **Idempotency is carried by
`SwitchId` in the payload**, because one switch may be sent more than once (section 5) and
`TrySendAsync` mints a fresh `CommandId` for every send.

### 3.2 Payload

```csharp
public sealed class LicenseSwitchRequest
{
    public string SwitchId { get; set; } = "";              // Gateway-minted, 32 lower-case hex (Guid "N"); the switch log row's id
    public string TargetAccountEmail { get; set; } = "";    // the spare to make active
    public string ExpectedActiveAccountEmail { get; set; } = ""; // what the Gateway believes is active now
    public string Reason { get; set; } = "";                // LicenseSwitchReasons: "threshold" | "manual"
    public DateTime RequestedAtUtc { get; set; }            // when the Gateway decided
    public DateTime NotAfterUtc { get; set; }               // the Director refuses to START this switch after this time
}
```

`NotAfterUtc` is `RequestedAtUtc` plus **10 minutes**. The Director checks `SwitchId` has the stated
shape before anything else (it is logged and used as a file name, like the transfer id in
`SecretTransferExecutor.TransferIdShape`).

```json
{
  "switchId": "6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40",
  "targetAccountEmail": "account-b@example.com",
  "expectedActiveAccountEmail": "account-a@example.com",
  "reason": "threshold",
  "requestedAtUtc": "2026-10-11T03:12:30Z",
  "notAfterUtc": "2026-10-11T03:22:30Z"
}
```

### 3.3 What the Director does, in order

1. Payload unreadable or `SwitchId` the wrong shape: `DirectorCommandStatus.BadRequest`, no body.
   This is the only non-`Ok` status the Director returns on purpose; a crash becomes `Error` through
   the existing boundary in `GatewayStreamClient`.
2. `SwitchId` already finished on this machine: return the stored result with `Replayed = true`. Do
   nothing else (section 3.6).
3. Not the key holder: outcome `failed`, reason `not-key-holder`.
4. Another switch running: if it has the **same** `SwitchId`, wait for it and return its result with
   `Replayed = true`; otherwise outcome `failed`, reason `switch-in-progress`.
5. Past `NotAfterUtc` by this machine's clock: `failed`, `expired`.
6. Active account is already `TargetAccountEmail`: outcome `already-active`. Nothing changes.
7. Active account is not `ExpectedActiveAccountEmail`: `failed`, `active-account-changed`. The
   Gateway decided on facts that are no longer true; the after-switch report tells it the real state.
8. No spare for `TargetAccountEmail`: `failed`, `spare-not-held`.
9. The spare's last read said `"unauthorized"` or `"refresh-failed"`, or its `SignInExpiresAtUtc` is at
   or before now by this machine's clock: `failed`, `spare-not-usable`.
10. **Exchange** active and spare, rewriting `oauthAccount` with the key (`MISSION.md` trap 1), as one
    step that either completes or is undone. Failure that was undone: `failed`,
    `exchange-failed-restored`. Failure that could not be undone: `failed`, `exchange-failed-unknown`
    - the Director logs it loudly and the after-switch report says what is really there.
11. **Read back** the new active account from the default config (`oauthAccount`), and read its usage
    once with the new key. Read-back not equal to the target: `failed`, `verify-failed`. The usage read
    failing does not undo the switch; it sets `VerifiedByUsageRead = false`.
12. Outcome `switched`. Store the result under `SwitchId`, then send the after-switch report.

### 3.4 Result

Returned as `DirectorCommandResult.Success(<LicenseSwitchResult as JSON>)` for every outcome in
3.3 steps 2 to 12 - a refusal is a successful answer, as in `SecretTransferExecutor`. The Gateway sends
with `timeout: TimeSpan.FromSeconds(60)` and the machine's name.

```csharp
public sealed class LicenseSwitchResult
{
    public string SwitchId { get; set; } = "";
    public string Outcome { get; set; } = "";                // LicenseSwitchOutcomes: "switched" | "already-active" | "failed"
    public string? ReasonCode { get; set; }                  // LicenseSwitchReasonCodes when Outcome is "failed", else null
    public string? Message { get; set; }                     // one plain sentence for the switch log
    public string PreviousActiveAccountEmail { get; set; } = ""; // read before the exchange ("" when none)
    public string ActiveAccountEmailAfter { get; set; } = "";    // READ BACK after the exchange (or now, on a refusal)
    public bool VerifiedByUsageRead { get; set; }            // the new active key answered a usage read
    public DateTime StartedAtUtc { get; set; }               // when the Director began this switch
    public DateTime FinishedAtUtc { get; set; }
    public int ExchangeMilliseconds { get; set; }            // the exchange step alone (0 when not reached)
    public bool Replayed { get; set; }                       // this answer is a stored one for a repeated SwitchId
}
```

Reason codes (`LicenseSwitchReasonCodes`): `not-key-holder`, `switch-in-progress`, `expired`,
`active-account-changed`, `spare-not-held`, `spare-not-usable`, `exchange-failed-restored`,
`exchange-failed-unknown`, `verify-failed`.

```json
{
  "switchId": "6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40",
  "outcome": "switched",
  "reasonCode": null,
  "message": "Switched from account-a@example.com to account-b@example.com.",
  "previousActiveAccountEmail": "account-a@example.com",
  "activeAccountEmailAfter": "account-b@example.com",
  "verifiedByUsageRead": true,
  "startedAtUtc": "2026-10-11T03:12:31Z",
  "finishedAtUtc": "2026-10-11T03:12:33Z",
  "exchangeMilliseconds": 41,
  "replayed": false
}
```

A refusal:

```json
{
  "switchId": "0d9e8c7b6a5f4e3d2c1b0a9f8e7d6c5b",
  "outcome": "failed",
  "reasonCode": "spare-not-held",
  "message": "This machine holds no spare for account-c@example.com.",
  "previousActiveAccountEmail": "account-a@example.com",
  "activeAccountEmailAfter": "account-a@example.com",
  "verifiedByUsageRead": false,
  "startedAtUtc": "2026-10-11T03:12:31Z",
  "finishedAtUtc": "2026-10-11T03:12:31Z",
  "exchangeMilliseconds": 0,
  "replayed": false
}
```

### 3.5 What the Gateway records from a result

`DirectorCommandRouter.TrySendAsync` returns one of four things, and the switch log row ends as:

| Returned | Log row |
|---|---|
| null (not connected) | stays `pending`, reason `director-offline`; retried (section 5.1) |
| `Status = Timeout` or `TunnelDropped` | `unknown`; settled by a resend with the same `SwitchId` or by the next report (section 5.5) |
| `Status = BadRequest` or `Error` | `failed`, reason `director-error`, with `Error` as the message |
| `Status = Ok`, body read | the body's `Outcome` and `ReasonCode`; `already-active` counts as succeeded |

The machine's active account in the Gateway's store changes **only** from `ActiveAccountEmailAfter` or
a later report, never from what the Gateway asked for.

### 3.6 Idempotency

The Director keeps every finished `LicenseSwitchResult` by `SwitchId` for **24 hours**, on disk in the
key store's folder (so a Director restart can still answer), and in memory. A repeated `SwitchId`:

- finished: the stored result, `Replayed = true`, nothing done again;
- running: wait for it, then the same;
- unknown to the Director: treated as new.

The Gateway reuses a `SwitchId` only for **the same decision on the same machine**: a resend after
"offline", "timeout" or "tunnel dropped", or after its own restart. A new decision (a new tick that
picks a target afresh, or a new **Switch now**) gets a new `SwitchId`.

## 4. Adding and removing a spare - the report carries it

There is **no separate message**. When a spare is added or removed, the Director sends a full report
at once (`Trigger = "changed"`), and the Gateway learns of the change by comparing it with the last
report from that machine; the Gateway writes "spare added" or "spare removed" into its log from that
comparison.

Why: the report is already a complete snapshot of what the machine holds, sent on change and every two
minutes. A separate event could be lost, arrive out of order with a report, or disagree with it, and
the Gateway would then need rules for which to believe. With one complete message there is one answer,
and a missed message heals itself at the next report. The same rule covers a sign-in done by hand on
the machine: the report shows it, nothing else needs to.

(How a spare is added from the Cockpit is phase 4, and may add its own command; it does not change
this: the holdings the Gateway believes are whatever the last accepted report says.)

## 5. Disagreement and failure

### 5.1 The Director is offline when a command is due

`TrySendAsync` returns null. Nothing was sent. The log row stays `pending` with `director-offline`,
and the decision loop (once a minute) resends **the same `SwitchId`** while the decision still stands
and `NotAfterUtc` has not passed. After `NotAfterUtc` the row ends `failed`, `director-offline`, and
the next tick decides afresh with a new `SwitchId`. Other machines on the same tired account are
switched independently; one offline machine does not hold the others back.

### 5.2 A stale report

The Gateway measures staleness per holding, from `ReadAtUtc` against its own clock:

- an **active** holding's numbers are stale when older than **6 minutes** (three missed reports);
- a **spare** holding's numbers are stale when older than **30 minutes**;
- a **machine** is stale when its last accepted report is older than **6 minutes**.

Account numbers are taken from the freshest reading of that account from any machine. What staleness
does:

- An account whose freshest reading is stale is **not chosen as a target** - the Gateway cannot show
  it is under 97%.
- An account in use whose freshest reading is stale does **not** trigger a switch on old numbers. The
  Licenses page shows it as stale.
- A stale machine is not sent a switch; the account's other machines still are.

Clock skew: the Gateway compares `ReadAtUtc` with its own clock. When a report's `SentAtUtc` is more
than 2 minutes away from the Gateway's receive time, the Gateway logs the skew once per machine and
judges that report's ages from its receive time minus (`SentAtUtc` - `ReadAtUtc`) instead.

### 5.3 A command to a machine that no longer holds that spare

The Director answers `failed`, `spare-not-held`, and sends a fresh report. The Gateway's store already
lags; it takes the report's holdings and the next tick picks a target from them. The Gateway never
sends a spare it has not seen in that machine's **latest** accepted report.

### 5.4 A switch already in progress

On the Director: one switch at a time per machine and user (it holds the key-store lock); a second
`SwitchId` is refused with `switch-in-progress`, the same one waits (3.3 step 4). On the Gateway: a
machine with a log row in `pending` or `unknown` is not sent another `SwitchId` until that row ends, and
a report whose `SwitchInProgressId` is set is never taken as grounds for a new decision on that
machine.

### 5.5 The Gateway restarts mid-switch

The Gateway writes the log row (state `pending`, with `SwitchId`, machine, from, to, reason) **before**
it sends. After a restart it finds rows in `pending` or `unknown`:

- before `NotAfterUtc`: it resends with the same `SwitchId` once the machine is connected and has
  reported. A finished switch answers from the Director's store (`Replayed = true`); one that never
  arrived runs now.
- after `NotAfterUtc`: it resends once anyway. The Director returns the stored result if it ran it;
  otherwise it answers `expired` without doing anything. Either answer settles the row.
- If the machine does not come back, the row is settled by its next report: the row ends `switched`
  when the report shows the target active, otherwise `failed`, with the message "settled from the
  machine's report" so the log says how it was known.

The Director's answer to a repeated `SwitchId` is what makes this safe: no switch runs twice.

### 5.6 The two sides disagree about the active account

The report wins. The Gateway's idea of "machine X is on account A" is only ever the latest accepted
report or a switch result's read-back. `ExpectedActiveAccountEmail` in the command is there so a
Director never acts on the Gateway's out-of-date picture (3.3 step 7).

### 5.7 A spare whose sign-in is about to expire

- **The Gateway never chooses a target whose spare's `SignInExpiresAtUtc` is at or before the
  command's `NotAfterUtc`.** A sign-in that could die before the switch is allowed to start is not a
  spare. A null `SignInExpiresAtUtc` does not exclude a target on its own; the Director still refuses
  an expired sign-in at 3.3 step 9.
- **The Cockpit shows an expiry verdict for every holding**: `expired` (at or before now), `expires
  soon` (within `LicenseContract.SignInExpiryWarning`, 3 days, of now), `fine` (later than that), or
  `not known` when `SignInExpiresAtUtc` is null. The Gateway folds the verdict and the Cockpit shows it
  as given (`CLAUDE.md` rule 7); the Cockpit does not compare times itself.

## 6. Rules the contract enforces

1. **The Gateway never receives or stores a sign-in key.** Nothing in this contract carries an access
   token, a refresh token, an authorize code, or the contents of `.credentials.json` or a Keychain
   item: only emails, the two plan words, percentages, times, outcome words and reason codes.
   `ReadErrorDetail` and `Message` are sentences the Director writes; they never quote a provider
   answer, a file, or a process's output. A test fails if any property (static or instance) or instance
   field of any `License*` type in `LicenseMessages.cs` - whatever its declaration form - is named with
   "Token", "Secret", "Credential", "Key" or "Code" (other than `ReasonCode`). There is no contracts
   test project, so the test is `LicenseMessagesCarryNoKeyTests` in the Director's unit tests.
2. **A switch exchanges, never copies.** `license-switch` moves the active key to the spare's place
   and the spare into the active place, with their `oauthAccount` blocks. After every outcome, each
   sign-in exists in exactly one place. No message asks a Director to duplicate a key, and none moves
   a key off a machine - moving a key between machines is the Secret Handoff path, not this one.
3. **No keystroke but Escape reaches a usage-limit dialog.** `license-switch` touches the key store
   and the config only; it types nothing into any session. No message in this contract asks the
   Director to send input to a session. If a session shows Claude's usage-limit dialog, the Director
   sends only Escape, and logs it, on its own - never because of a Gateway command.
4. **Tenant scoping.** A report's tenant is the connection's bound tenant, never a payload field.
   The Gateway sends `license-switch` only to a Director bound in the same tenant as the account and
   machine it is acting on, and the decision loop runs inside `ITenantPass.ForEachTenantAsync` so
   `GatewayHost.SendCommandAsync` has a tenant scope (otherwise it drops the command and logs a
   Gateway bug). Accounts, machines and the switch log are stored per tenant; an account email seen in
   two tenants is two separate accounts.
5. **Inert alone.** A Director with this work and a Gateway without it: `GatewaySupports` is false,
   nothing is sent. A Gateway with it and a Director without it: no report ever arrives from that
   machine, so it is never sent a switch. Neither half needs the other to be safe on main.

## 7. Constants, in one place

| Constant | Value |
|---|---|
| Report interval | 2 minutes |
| Spare provider read, at most every | 10 minutes |
| Active holding stale after | 6 minutes |
| Spare holding stale after | 30 minutes |
| Machine stale after | 6 minutes since its last accepted report |
| `license-switch` command timeout (Gateway) | 60 seconds |
| `NotAfterUtc` | `RequestedAtUtc` + 10 minutes |
| Director keeps switch results for | 24 hours |
| Clock skew logged above | 2 minutes |
| A sign-in "expires soon" within (`SignInExpiryWarning`) | 3 days |

These live as named constants in `LicenseMessages.cs` (`LicenseContract.ReportInterval` and so on),
so the two sides cannot drift.
