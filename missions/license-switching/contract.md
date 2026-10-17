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

The key holder sends its `"connected"` and interval reports even when the machine holds no sign-in at
all (`Holdings` empty), so a machine that only approves sign-ins for others still has a known Director (8.3).

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

- **The Gateway never sends a machine to an account whose sign-in on that machine expires at or before
  the command's `NotAfterUtc`.** A sign-in that could die before the switch is allowed to start does not
  make the machine ready for that account. It is judged per machine: it never stops another machine that
  is ready from moving, and it never makes the account count as having no room (MISSION.md section 5).
  A null `SignInExpiresAtUtc` does not make a machine unready on its own; the Director still refuses
  an expired sign-in at 3.3 step 9.
- **The Cockpit shows an expiry verdict for every holding**: `expired` (at or before now), `expires
  soon` (within `LicenseContract.SignInExpiryWarning`, 3 days, of now), `fine` (later than that), or
  `not known` when `SignInExpiresAtUtc` is null. The Gateway folds the verdict and the Cockpit shows it
  as given (`CLAUDE.md` rule 7); the Cockpit does not compare times itself.

### 5.8 A switch that keeps failing the same way

A machine whose Director answers `failed` to the same switch again and again is not sent that switch every minute
for ever (the whole-phase review, finding one). The rule, exactly as built:

- **What counts.** A switch row that ended `failed` on an ANSWER of the machine's Director: the body's `Outcome`
  was `failed`, or the Director returned `BadRequest` or `Error` (`director-error`), or its answer could not be read.
  A switch that was never sent (`director-offline`) is not a failure of the machine and is never held.
- **A switch that is sent and never answered is not held either.** A Director that takes the command and does not
  answer leaves the row `unknown`; it is settled `failed` from the machine's next report, with NO reason word
  (5.5). Such a row is not an answered failure: it is not held, and it ends any run before it. It needs no hold,
  because it is bounded by its own expiry: the machine is decided afresh only after `NotAfterUtc` (10 minutes) and
  one more send, not every minute. The owner is not emailed about it, because a target exists and the machine is not
  held. If a Director that never answers turns out to be common, that is a new finding, not this rule.
- **In a row.** The machine's switch rows, newest first, whatever asked for them (the minute check or **Switch
  now**). The run is the rows that are the same failure: the same reason word, the same account switched from and
  the same account switched to. Anything else ends the run - a switch that worked, another reason, another from or
  to, a switch that was never sent.
- **The hold.** The first failure of a run holds nothing. The second holds the machine out of the AUTOMATIC decision
  for one minute from when that row ended; each further one twice as long - two minutes, four, eight ... up to one
  hour, and one hour from then on. The time the machine is held until is written on the failed row itself
  (`license_switch_log.HeldUntilUtc`), so a Gateway that restarts keeps it.
- **While it lasts.** A hold is in force while its time is ahead, its row is still the machine's latest switch row,
  and the machine still reports the account the failing switch was from as active. A held machine is left out of the
  minute check's decision exactly as a stale machine is: it is not sent a switch, and the account's other machines
  still are. At the hold's time the machine is switched again like any other.
- **Every machine held is the no-room case.** When a tired account has a target with room, and every machine that
  could be switched to it is held, nothing is switched and the Gateway treats it as "no account has room": the same
  event, the same row of kind `no-room` in the log, and the same one-email rule, so the owner is told ONCE for the
  event and not again while it lasts. The email and the row say what is true - that the switch keeps failing, on
  which machines, to which account, with which reason and until when each is held - not that no account had room.
  The event ends as the no-room event ends: when NO machine is left on the account (every one was switched, or
  moved by hand), when a fresh reading shows the account is no longer tired, or when its end time has passed and no
  fresh reading still shows a tired window. One machine switched while another is still held on the account does not
  end it: the outage goes on for the held one, and no second email is sent. A run of failures after the event has
  ended is a new event and a new email. An event that was opened because no account
  had room is not emailed about again when its cause becomes a switch that keeps failing, nor the other way round:
  it is one event for as long as nothing can be switched.
- **Switch now ignores the hold**, as it ignores a bar: a held machine is sent the switch at once. If that fails
  the same way it is one more failure of the run, and the hold grows.
- **The page.** The Gateway says it in its own sentences (`cockpit-api.md` sections 2, 3 and 4): on the machine,
  that it is held, why and until when; in the forecast, where the page shows what is next; and on the failed row of
  the log. The page compares no times.

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
| A machine is held after (5.8) | 2 failures in a row with the same reason, from and to |
| The hold (5.8) | 1 minute, doubling with each further failure, at most 1 hour |

The two rows for 5.8 are the Gateway's alone (`LicenseStore.FailureHold`); the Director knows nothing of a hold.

These live as named constants in `LicenseMessages.cs` (`LicenseContract.ReportInterval` and so on),
so the two sides cannot drift.

## 8. Phase 4 additions - AGREED; the Director side and the Gateway side are both built

Written by the phase 4 Developer; agreed by the phase 2 Tech Lead on 11 October 2026 (pull request 3875), with
the changes to 8.3 below. Phase 2 builds the Gateway side; the Director side is the second phase 4 pull request,
and what building it settled is marked "As built" in each part below. Until the Gateway offers
`RequestLicenseApproval`, a Director with no browser profile for an account answers `no-approver`.
The one change to an earlier section is in 8.3: the key holder reports even when it holds nothing (2.4). Words as in section 5 of
`MISSION.md`; a "sign-in" is one `claude auth login` and what it leaves behind.

What the first phase 4 pull request already gives, with no message change: `license sign-in` and `license renew`
on the command line, and `SignedInAtUtc` (2.2) is now filled by `license usage --json` for every sign-in made with
`license sign-in`, so the Director passes it through as it is. A sign-in was measured to live 27.1 to 29.9 days
(`phase-4/README.md`), and the ACTIVE account's dies too, so renewal covers every holding.

### 8.1 A failed renewal travels in the report

Three fields are added to `LicenseHolding` (2.2), filled by the Director and folded by the Gateway like the rest:

```csharp
public DateTime? RenewalFailedAtUtc { get; set; }  // the LAST renewal of this holding failed at this time; null when the last one worked, or none was tried
public string? RenewalError { get; set; }          // LicenseRenewalErrors code, else null
public string? RenewalErrorDetail { get; set; }    // one plain sentence the Director writes; never a page's or a process's text
```

`LicenseRenewalErrors`: `"login-failed"`, `"approval-failed"`, `"wrong-account"` (the approving browser profile is
signed in to claude.ai as someone else), `"browser-not-signed-in"`, `"needs-fresh-sign-in"` (the approving browser
profile is signed in, but the approval page asks for a recent sign-in first - added in phase 6), `"no-approver"` (no browser profile is set for
the account, here or on another machine), `"approver-offline"`, `"expired"` (no approval came back by `NotAfterUtc`,
8.3), `"store-refused"`. They are the reasons `license renew --json` already writes, without the `failed-` prefix,
plus the two only the relay can produce: `approver-offline` and `expired`.

**What the Gateway has built of 8.1, and what it has not (13 October 2026).** BUILT: the three fields are stored
from every accepted report and cleared by the next report that says the renewal worked, and the Licenses page is
given them folded (`renewalFailedAtUtc`, `renewalFailedText`, `renewalFailedTone` on a holding, `cockpit-api.md`
sections 2 and 6). ALSO BUILT: the owner email of the next paragraph (`LicenseRenewalEmailService`, run by the
Gateway's once-a-minute license check for every DevThrottle account). The Gateway keeps a reason word only when it
is one of `LicenseRenewalErrors`; any other word is stored as `not-known` (`LicenseStore.RenewalErrorNotKnown`), and
a failure reported with no word has none - so the email is keyed on `RenewalFailedAtUtc`, never on the word.

A holding with `RenewalFailedAtUtc` set and `SignInExpiresAtUtc` within `LicenseContract.SignInExpiryWarning`
(3 days) - or already past - makes the Gateway email the owner ONCE for that holding's failure streak (the same
email channel as "no account has room", `MISSION.md` section 4), and the Licenses page shows the error beside the
expiry verdict (5.7). A failed renewal is therefore always seen twice: on the page, and by email before the sign-in
dies. The rule, exactly as built:

- ONE EMAIL FOR EACH CHECK, NOT ONE FOR EACH SIGN-IN (the whole-phase review, finding two). Every holding of one
  DevThrottle account whose email is due in the same minute check goes into ONE email, one line each: the machine,
  the account, when the sign-in ends, and the machine's reason. A machine that fails to renew three sign-ins in one
  night is one email, not three. A holding that becomes due on a later check gets its own email, and the ones
  already emailed about are not sent again. Each holding still has its own row and its own streak, and every
  guarantee below holds for each row: every row in one email is marked from the same confirmation of the mail
  service, an email that fails is tried again as one on its wait, and a Gateway with no account to email records
  that on every row. One holding alone gets the email it always got.

- A FAILURE STREAK of one sign-in on one machine starts when the Gateway first sees that holding failing inside the
  window. IT ENDS ON SOMETHING PRESENT, NEVER ON SOMETHING MISSING. The Director keeps its failed renewals in memory
  only (`LicenseUsageReporter`), so a Director that has just started reports the same sign-in with `RenewalFailedAtUtc`
  null until its next try fails; a report that only lacks that time says nothing and ends nothing. The streak ends
  as renewed when the machine reports a NEWER sign-in for the account: its `SignInExpiresAtUtc` is later than the one
  the streak was opened for BY MORE THAN ONE HOUR (`LicenseContract.SignInEndTolerance`), or its `SignedInAtUtc` is
  later than the one reported then. The hour is the license tool's own: one sign-in's reported end is written again
  on every key refresh as "now + the seconds left", so it moves by seconds, and the tool allows one hour when it
  asks whether two ends are the same sign-in (`SIGN_IN_MATCH_TOLERANCE_SECONDS`). A renewal moves the end by weeks.
  The streak also ends when the machine no longer holds the sign-in. A failure after that is a new streak and a new
  email.
- An open row whose email has not gone out is tried again on its growing wait whether or not the machine's last
  report still carries the failure time. The email then says that an earlier report said the renewal had failed and
  that the last one does not say when or why.
- The streak is ONE ROW of the switch log, of kind `renewal-failed` (`LicenseRenewalFailureLog`; no table and no
  column is added). The row is what makes the email go out once: it is stored, so a Gateway that restarts in the
  middle of a streak does not email again, and a row recorded as emailed is never changed back. THE ONE EXCEPTION:
  a Gateway that stops between the mail service's confirmation and the row being marked emails once more after it
  restarts. While a Gateway keeps running, an email the mail service confirmed is remembered in memory until its
  row is marked, so a database that refuses the mark costs no second email. For an email about several sign-ins
  the confirmation is remembered for EVERY one of its rows before the first is marked, so a database that refuses
  one mark costs no second email for any of them. The same holds for the "no account has room" email.
- What became of the email is the row's reason word, the same three the "no account has room" row uses:
  `owner-emailed`, `owner-email-failed` (tried again, waiting one minute, then two, four ... up to one hour, or as
  long as the mail service asked; a mail sender that throws counts as a failed send with the same wait),
  `no-owner-account` (nobody to email; not tried again in that streak).
- The email says how old its facts are: when the renewal last failed (by the machine's clock) and when the machine
  last reported, and that it is written from that report. It says the Gateway's own sentence for the reason. The
  machine's own sentence (`RenewalErrorDetail`) is in the email only when the Gateway knows no reason, on one line,
  with every control, format and separator character removed, and cut to 200 characters.
- The email check runs beside the switch pass of the minute check, not behind it: a Director that does not answer
  its switch delays nobody's renewal email.
- A holding whose `SignInExpiresAtUtc` is not known is not emailed about: the Gateway cannot say the sign-in is
  about to end. A holding failing with more than three days left is not emailed about yet; it is when the three
  days are reached and the renewal is still failing.
- A machine whose last report is stale is still emailed about: the last thing it said is that its renewal failed
  and its sign-in was about to end, and silence since then is not good news.

### 8.2 The Director renews on a schedule

The key holder (2.4) runs a renewal once a day and once at start, only while `licenseSwitching.enabled` is on. A
renewal is due when the sign-in ends within five days of now (`LicenseContract.RenewAhead`, matching
`license_signin.RENEW_AHEAD`). Each row that changes - renewed, or failed - makes a report with
`Trigger = "changed"` (2.3). A renewal never interrupts a switch: both take the key store's lock, and a renewal that
finds it taken is tried at the next run.

**As built.** A run is two kinds of call, not one `license renew --json`:

1. `license renew --check --json` signs nothing in and exits 0. It answers one row per holding: `due` (with the
   browser profile set for it on this machine, or none), `not-due`, `unknown-expiry`, or `failed` / `store-refused`
   for a stored sign-in it could not read.
2. For each `due` row, one of three things, decided from that row before anything is started:
   - a Gateway that relays an approval is connected: `license sign-in <account> --ask-when-no-browser --json`. The
     tool approves in the profile set on this machine when there is one, and otherwise prints the authorize URL
     and waits for the code, which the Director gets through 8.3. One call, and the tool chooses the route, so the
     owner's local setting always wins;
   - no Gateway relays, and the row names a profile here: `license sign-in <account> --json`, approved here;
   - no Gateway relays, and the row names no profile: **nothing is started.** The Director records `no-approver`
     for the holding and reports it. This is every Director's state until phase 2 is deployed, and afterwards
     whenever the tunnel is down when the renewal runs.

A sign-in the Gateway asks for (8.4) is decided the same way. The account may not be held yet, so there is no row
to read; when no Gateway relays, the Director first asks `license get-browser <account> --json`, which answers the
profile or none and exits 0.

The reason is that every cc-* tool that exits with a failure files an error report. `license renew --json` exits 1
when any renewal failed, and a sign-in tried locally first would fail by design for every account whose profile is
on another machine, so the plain design would have filed a report a day for each such account. Asking first makes
no call that is expected to fail. (The review of pull request 3916 found the one case the first version of this
still got wrong - no relay and no profile - and it is the third branch above.)

The renewer wakes every 10 minutes and does nothing until a day has passed since the last run. A run in which the
tool gave no answer at all does not count as the day's run and is tried again at the next wake. A failure recorded
for a holding is cleared when a later run renews it, or finds it no longer due (the owner signed it in by hand).

### 8.3 Approval on another machine

The browser profile signed in to claude.ai as an account may live on a machine other than the one that needs the
sign-in. Which machine and profile approve which account is the OWNER's setting, kept by the Gateway per tenant and
set on the Licenses page: `LicenseApprover { AccountEmail, MachineName, OsUser, BrowserProfile }`. A machine with a
local profile set by `license set-browser` approves its own sign-ins and never asks the Gateway.

**Nothing waits on a hub call.** `DirectorHub` runs one invocation at a time per client, so a hub call that waited
minutes for another machine would hold up `PushLicenseUsage` and every session push from the target behind it. The
request returns at once; the answer comes back as a command, the pattern section 3 already uses.

**1. Hub method, target Director to Gateway, returns at once:**

```csharp
// DirectorHub - returns as soon as the Gateway has accepted the request and sent license-approve (or answered it)
public void RequestLicenseApproval(LicenseApprovalRequest request)

public sealed class LicenseApprovalRequest
{
    public string SignInId { get; set; } = "";        // 32 lower-case hex, minted by the target Director; logged and correlated
    public string AccountEmail { get; set; } = "";
    public string AuthorizeUrl { get; set; } = "";    // what `claude auth login` printed; carries a PKCE challenge, never the verifier
    public DateTime NotAfterUtc { get; set; }         // the target's login gives up after this
}
```

**2. Command `license-approve`, Gateway to the approving machine's Director.** The Gateway finds the account's
`LicenseApprover` and sends this to the Director that last reported for that machine (2.4), with the target
Director's Secret Handoff public key (already sent in `Hello` as `SecretTransferPublicKey`):

```csharp
public sealed class LicenseApproveRequest
{
    public string SignInId { get; set; } = "";
    public string AccountEmail { get; set; } = "";
    public string AuthorizeUrl { get; set; } = "";
    public string BrowserProfile { get; set; } = "";
    public string SealToRecipient { get; set; } = "";  // the TARGET Director's Secret Handoff public key, as sent in its Hello
    public DateTime NotAfterUtc { get; set; }
}
```

The approving Director runs `license approve --email <AccountEmail> --browser <BrowserProfile> <AuthorizeUrl>`. That
command refuses, as `wrong-account` and before any browser opens, when the account the URL itself names (its
`login_hint`) is not `AccountEmail`: the consent page shows only who the browser is signed in as, and a code for
another account would replace a renewal's held sign-in before anything could read it back. The Director then
seals its answer to `SealToRecipient` with the Secret Handoff envelope before it leaves the process, and returns
`LicenseApprovalResult` as the command's result.

**3. Command `license-approval-result`, Gateway to the target Director**, payload `LicenseApprovalResult`, keyed by
`SignInId`. The Gateway sends it when the approving Director answers, or AT ONCE with `refused` /
`approver-offline` when the approving machine has no Director connected, or no `LicenseApprover` is set
(`refused` / `no-approver`).

```csharp
public sealed class LicenseApprovalResult
{
    public string SignInId { get; set; } = "";
    public string Outcome { get; set; } = "";          // "approved" | "refused"
    public string? ReasonCode { get; set; }            // a LicenseRenewalErrors code when refused
    public string? Message { get; set; }               // one plain sentence
    public string? SealedApproval { get; set; }        // when approved: the authorize answer sealed to SealToRecipient
}
```

The target Director opens `SealedApproval` with its private key and types it into the waiting login. A target that
has received no `license-approval-result` for its `SignInId` by `NotAfterUtc` gives up, and the attempt is `failed`
/ `expired`. Giving up means exactly what `LicenseStore.abandon_sign_in` does: the waiting login is ended; a NEW
spare's folder that received no sign-in is removed; **a renewal's folder is never touched** - it is the folder that
holds the account (its spare folder, or the default folder for the active account) and still holds the sign-in it
had, because nothing is written until the code is typed in. A result that arrives after that, or for a
`SignInId` the target is not waiting on, is logged by `SignInId` and dropped.

**The Gateway relays, and never keeps.** `AuthorizeUrl` and `SealedApproval` are held in memory only for the relay.
They are never written to a store, the Licenses log, or a log line; the Gateway logs only `SignInId`, the account,
the machines and the outcome. A Gateway test proves no stored table has a column for either.

**Finding the approving machine's Director.** That machine may hold no sign-ins at all. So section 2.4 is amended:
**the key holder sends its `"connected"` and interval reports even when it holds nothing (`Holdings` empty).** Every
machine with a key holder then has a known Director, and a machine with none is `approver-offline`. No new
mechanism.

**Rule 6.1 has one stated exception for this**: `SealedApproval` is the only field that carries anything derived
from a sign-in. It is sealed to one machine's key, the Gateway cannot open it, and it is useless alone - it completes
only the login that holds the matching verifier, on the target, within minutes. `LicenseMessagesCarryNoKeyTests`
names it as its one allowed exception, by name. (No other new field contains "Key", "Token", "Secret", "Credential"
or "Code": the target's public key travels as `SealToRecipient`.)

Agreed with the Tech Lead: the sealing is done by a small `cc-secrets seal` / `open` pair (a value on standard input,
sealed to a recipient), not by a second copy of the envelope code in the Director. One place seals.

**As built.**

- The approving Director runs `cc-secrets seal --to <SealToRecipient> --purpose license-approval --id <SignInId>`
  with the answer on standard input; the target runs `cc-secrets open --purpose license-approval --id <SignInId>`
  with `SealedApproval` on standard input. The answer is never a command-line argument on either machine. The
  purpose and the `SignInId` are bound into the envelope, so an envelope made for one sign-in does not open as
  another's, and a value envelope never opens as a Secret Handoff transfer or the other way round.
- **What the seal gives and what it does not.** It keeps the answer from the Gateway and from anything else on the
  path. It does not prove who sealed it: anyone holding the target's public key can seal a value to it. That is
  enough here because a wrong answer completes nothing - the waiting login accepts only the answer that matches
  the verifier it holds, and the tool then reads back which account the new sign-in is for before keeping it.
- `cc-secrets open` refuses inside a session and refuses to write to a terminal: it is for the Director, which
  types the answer straight into the waiting login. The answer is in no log line, no stored result and no message.
- The Director validates `SignInId` (32 lower-case hex) and `SealToRecipient` (32 bytes, base64) before either
  reaches a command line.
- The target asks for an approval only when the Gateway it is connected to offers `RequestLicenseApproval` and
  `cc-secrets` is there to open the answer. Otherwise only a profile on this machine can approve: with one set the
  sign-in runs here, and with none the attempt is `failed` / `no-approver` and no login is started at all (8.2).
- **The Director's time limits fit inside the Gateway's waits** (8.5; a test holds them to it). The approving
  Director lets `license approve` run for 2 minutes 30 seconds, inside the Gateway's 3 minute wait for
  `license-approve`, so an approval - which can be used once - is never made for a Gateway that has stopped
  listening. The target waits for an approval for 3 minutes from the moment it starts the sign-in, or until the
  request's `NotAfterUtc` when that is sooner, and that time is the `NotAfterUtc` it sends in
  `LicenseApprovalRequest`. With 90 seconds for the login to finish, the whole sign-in is answered inside the
  Gateway's 5 minute wait for `license-sign-in`.
- A sign-in the Director gives up on is ended by closing the tool's standard input. The tool treats that as the
  caller calling it off, not as its own failure, so an approval that never came files no error report.

**As built, the Gateway's side (`LicenseApprovalRelay`).**

- **A request that is not taken fails the hub call, and the Gateway keeps nothing for it.** `RequestLicenseApproval`
  throws for a `SignInId` that is not 32 lower-case hex, an `AccountEmail` that is not an email of at most 320
  characters, an `AuthorizeUrl` that is missing or longer than 4,096 characters, and a request made while the
  account already has 8 approvals in flight. The Director turns a failed call into the end of its sign-in. No
  entry, no task and no command exists for a refused request, so a Director that asks in a loop costs the Gateway
  nothing for each request past the limit, and the limit is each DevThrottle account's own.
- **A request that is taken is always answered with `license-approval-result`**, also when the relay itself fails:
  the answer is then `refused` / `approval-failed` with a sentence that names only the kind of failure.
- **The Gateway waits for `license-approve` until the sooner of 3 minutes and the request's `NotAfterUtc`.** A wait
  that ended at the deadline is answered `refused` / `expired`; a request that arrives at or after its deadline is
  answered `expired` and no machine is asked.
- **Nothing of a request is logged before it is checked**, and a refusal quotes none of it: the log names the
  sign-in's identifier, the account (once it is known to be an email), the machines and the outcome.

### 8.4 Starting a sign-in from the Cockpit

**Command `license-sign-in`, Gateway to the target Director**, for "Add a spare" and "Renew now" on the Licenses page:

```csharp
public sealed class LicenseSignInRequest
{
    public string SignInId { get; set; } = "";        // 32 lower-case hex, Gateway-minted
    public string AccountEmail { get; set; } = "";
    public string Reason { get; set; } = "";          // "manual" | "renewal" | "auto-spare" | "auto-renewal"
    public DateTime NotAfterUtc { get; set; }         // the Director refuses to START after this
}

public sealed class LicenseSignInResult
{
    public string SignInId { get; set; } = "";
    public string Outcome { get; set; } = "";          // "added" | "renewed" | "failed"
    public string? ReasonCode { get; set; }            // a LicenseRenewalErrors code, or "not-key-holder", when failed
    public string? Message { get; set; }
    public DateTime? SignedInAtUtc { get; set; }
    public DateTime? SignInExpiresAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }        // as built: when the Director finished it; the 24 hours run from here
    public bool Replayed { get; set; }                 // as built: true when this is the stored answer to a repeated SignInId
}
```

The Director answers `not-key-holder` unless it is the key holder (2.4), runs the sign-in (approving locally when it
has a local profile for the account, else through 8.3), and sends a `"changed"` report after it. Results are kept by
`SignInId` for 24 hours and replayed like a switch (3.6).

**As built.** `FinishedAtUtc` and `Replayed` are the two additions above, the same pair a switch result carries.
A request that arrives after its `NotAfterUtc` is answered `failed` / `expired` and starts nothing. Sign-ins on one
Director run one at a time: a second waits for the first, and one whose deadline passed while it waited is answered
`expired`. The three commands (`license-sign-in`, `license-approve`, `license-approval-result`)
address no session, as `license-switch` does.

**As built, the Gateway's side: a sign-in with no answer (`LicenseSignInService.SettleUnansweredAsync`).** The
Gateway writes the sign-in's row before it sends, as it does for a switch (5.5). A row whose answer never came - the
5 minute wait ended, the tunnel dropped, or the Gateway restarted - is left alone until no Director could still be
running it: `NotAfterUtc` plus the 5 minute wait, 15 minutes after it was asked. Then, once a minute:

1. The Gateway sends `license-sign-in` again with THE SAME `SignInId` and the same `NotAfterUtc` to the Director
   that last reported for the machine, and waits 60 seconds. This is the replay of this section and 3.6: a Director
   that ran it returns its stored result (`Replayed = true`), and that ends the row.
   - **Asking again starts nothing only while the Director's clock is within 5 minutes of the Gateway's**, because
     the Director judges `NotAfterUtc` by its own clock and the Gateway asks 5 minutes after it. So a machine whose
     measured clock difference (5.2) is 4 minutes or more, either way, is NOT asked; it goes to step 2.
   - **An answer with `Replayed = false` ends the row only when no command for it had gone out before.** A Director
     keeps a result for 24 hours (`SignInResultRetention`); after that it answers `failed` / `expired` for a
     sign-in it did run. For a row whose command had gone out, that answer is not how the sign-in ended, and step 2
     decides.
   - **A wait that ends with no answer settles nothing in that pass**, not from a report either: the Director may
     be running the sign-in at that moment. The next minute tries again.
2. Only when step 1 gave nothing that says how it ended (no Director connected, a machine that was not asked, a
   dropped tunnel, an answer that is not this sign-in's result, or an answer that is not a replay of a command that
   had gone out) is the row settled from the machine's report - and only from a report the Gateway accepted AFTER
   those 15 minutes, never from an earlier one. A new spare is `added` when that report shows the holding. A
   renewal is `renewed` when the holding's `SignedInAtUtc` in that report DIFFERS from the value the machine had
   reported before the sign-in was asked for, which the Gateway keeps on the row. Two values of the machine's own
   clock are compared; the machine's clock is never compared with the Gateway's, because they may be minutes apart
   (5.2). Until such a report arrives the row stays open.
3. A row whose machine has sent no report for 24 hours is ended `failed`, with no reason word and a sentence saying
   the machine has not been seen for a day and how the sign-in ended is not known. Without this a machine that
   never comes back would keep a sign-in for that machine and account refused as running for good.

`SignInId` and `SwitchId` are exactly 32 lower-case hexadecimal digits and nothing after them: the pattern ends at the
end of the text, so an identifier followed by a line break is refused.

### 8.5 Constants added to section 7

| Constant | Value |
|---|---|
| Renewal due when the sign-in ends within (`RenewAhead`) | 5 days |
| Director renewal run | once a day, and at start |
| `license-sign-in` command timeout (Gateway) | 5 minutes |
| `license-approve` command timeout (Gateway) | 3 minutes |
| `license-approval-result` command timeout (Gateway) | 60 seconds |
| A sign-in's `NotAfterUtc` (the Director refuses to START after it) | requested + 10 minutes |
| The approving Director lets `license approve` run (`ApproveToolTimeout`) | 2 minutes 30 seconds |
| The target waits for an approval, from the start of the sign-in (`SignInApprovalWait`) | 3 minutes |
| The target lets the login finish once the approval is typed in (`SignInFinishAllowance`) | 90 seconds |
| The Director's renewer wakes (a run happens once a day) | every 10 minutes |
| A stored `LicenseSignInResult` is kept | 24 hours |
| The seal's purpose word (`LicenseContract.ApprovalSealPurpose`) | `license-approval` |

## 9. Phase 6 - the license alarm (one mechanism for every track)

A problem with licenses is LOUD and STAYS until it is fixed. There is exactly one mechanism for that, on the Gateway:
`Licenses.LicenseAlarmService`. Tracks A, B and C raise their alarms through it and never build another.

### 9.1 What an alarm is

An alarm names a **kind** (a short stable word the raising check chooses: `not-ready-at-80`), the **machine** (absent
for an alarm about no one machine), the **account** (absent for an alarm about no one account), **what is wrong** in one
plain-English sentence and **the one thing to do** in one plain-English sentence. It has an opened time and, once
closed, a closed time. Its identity is tenant + kind + machine + account. Rows live in `license_alarms`, one row for
each time a problem was open.

### 9.2 How to raise one, and how it clears

Declare the kind once, as a constant beside the check:

```csharp
static readonly LicenseAlarmKind NotReady = new("not-ready-at-80", "Not ready to switch");
```

The title is what a phone's locked screen shows: a few plain words, no full stop, never an email address (the
constructor refuses one).

On every run of the check, pass EVERY alarm of that kind that is true now - an empty list when none is:

```csharp
await alarms.RaiseAsync(tenant, NotReady, new[]
{
    new LicenseAlarmCondition(machine.MachineId, machine.MachineName, "alice@example.com",
        "machine-a holds no spare that could take it off alice@example.com.",
        "Sign machine-a in to a second account."),
}, ct);
```

- **Not open yet:** it opens. One row in `license_alarms`, one `alarm-opened` row in the Licenses log, one push to the
  owner's phones and one owner email.
- **Already open:** it stays open and nothing is sent. Its two sentences are brought up to date.
- **Open, but not in the list:** it closes by itself and writes an `alarm-closed` row in the Licenses log.
- **Raised again after it closed:** a NEW alarm, with its own push and its own email.
- A check speaks only for its own kind: it never closes another kind's alarm.

Raising is idempotent, so a check that runs every minute simply raises what it sees every minute. The service is
`GatewayHost.LicenseAlarms`.

### 9.3 What the words must look like

- Both sentences are plain English, one line each, ending in a full stop, at most 400 characters. A sentence that is
  not refused with `ArgumentException`.
- **What is wrong** names the machine and the account where they matter: "machine-a holds no spare that could take it
  off alice@example.com." It may contain the account email: the banner and the email are read over the owner's own
  signed-in connection or in the owner's own mailbox.
- **The one thing to do** is one action the owner can take: "Sign machine-a in to a second account."
- The banner's line is the two joined with a space, so write each as a whole sentence. When several alarms are open the
  banner adds "N license problems need you." above them.
- The push carries only "<machine>: <kind title>. Open Licenses to see what to do." - never the sentences, because a
  push shows on a locked screen.

### 9.4 The email and the push

- **Email:** through `LicenseOwnerMailer`, the same channel as the "no account has room" email. Every alarm whose email
  is due in one check of one tenant goes in ONE email. Recorded as emailed only when the mail service confirmed it; a
  failed send is tried again on the Gateway's once-a-minute license check (one minute, two, four, up to an hour, or
  longer when the mail service asks) for as long as the alarm is open. No email when it closes.
- **Push:** through the Web Push subscriptions of the tenant (`Push.LicenseAlarmPushNotifier`), once when the alarm
  opens, never retried. A tap opens the Cockpit's Licenses page (`/licenses`), because the phone has no Licenses screen
  of its own.

### 9.5 Where it shows

`GET /gateway/licenses/alarms` (cockpit-api.md section 8) answers the open alarms with the banner's finished words. The
Cockpit's frame and the phone's shell render them as a red banner on every page, verbatim, and draw nothing when no
alarm is open. **There is no route that raises or clears an alarm** - only Gateway code calls `RaiseAsync`.

### 9.6 The readiness alarm, `not-ready-at-80` (Track A)

Raised by the Gateway's readiness check (`Licenses.LicenseReadinessService`, once a minute, after the switch check and
the settling of sign-ins with no answer). See section 11.

## 10. Phase 6 additions - standing approvers (Track C)

The types are in `src/CcDirector.Gateway.Contracts/LicenseApproverMessages.cs`. A Director whose machine offers its
browser profiles as approvers checks each profile daily by reading a real approval page (it never presses Authorize,
never signs in and never types anything), and reports what it found over its own hub connection. The owner-only routes
stay owner-only; no session key gains anything.

### 10.1 Hub methods (Director to Gateway)

| Method | Payload | Answer |
|---|---|---|
| `GetLicenseApproverAssignments()` | none | `{ rows: [{ accountEmail, browserProfile, source }] }` - the records that name the caller's machine and operating system user |
| `ReportLicenseApproverChecks(report)` | `{ offersApprovers, rows: [{ browserProfile, browserProfileName, accountEmail, shownAccount, state, checkedAtUtc, detail }] }` | `{ rows: [{ browserProfile, accountEmail, outcome, reason }] }`, one per row, in order |

Both are refused before Hello and are listed in the Hello answer only by a Gateway that keeps registrations. The
tenant and the Director are the connection's binding. The machine and operating system user are the Licenses read
model's record of which machine that Director last reported for - never anything in the payload. A Director that
reports for no machine of the tenant gets no assignments and every row of its report is refused
`not-the-reporting-director`.

`browserProfile` is everywhere the profile's IDENTIFIER on the Director (for example `work-chrome`), never its display
name: in the approver record, the Cockpit's `PUT`, the assignment and check rows, and the `license-approve` request. It
is stable across a rename and compared exactly, with no "either spelling" matching. A record set by display name
before this rule never matches a check, so it reads `not-checked` and says so, and the owner sets it again. A check
row also carries `browserProfileName`, the display name (at most 64 characters, the shape of a profile name, or null);
the Gateway keeps it on the record as the last name seen and says it in its sentences.

### 10.2 Words

- State (`LicenseApproverStates`): `can-approve`, `signed-out`, `needs-fresh-sign-in`, `wrong-account`,
  `check-failed`, and the Gateway's own `not-checked` (never reported by a Director).
- Source (`LicenseApproverSources`): `owner`, `registered`. A record stored before sources existed reads `owner`.
- Outcome (`LicenseApproverCheckOutcomes`): `registered`, `health-updated`, `nothing-to-store`, `refused`.
- Refusal (`LicenseApproverRefusals`): `not-the-reporting-director`, `invalid-row`, `account-not-held`,
  `not-offered`, `owner-set`, `owner-removed`, `healthy-incumbent`, `record-names-another-approver`,
  `too-many-approvers`.

### 10.3 The rule

A report that is missing, has no list of rows, or has more than 32 rows fails the call and nothing of it is kept.
An empty list is taken and answered with an empty list: a machine with no profiles to report may send one. Each row is
then decided under the approver store's lock, against the records as the earlier rows of the same report left them:

1. A row with a field missing, too long or of the wrong shape is `invalid-row`: the profile must be a profile name;
   the display name, when given, a profile name of at most 64 characters; the accounts emails; the state a reportable word; the check no more than 5 minutes ahead of the Gateway's clock and
   no older than 26 hours; the detail at most 200 characters with no control character; and a `can-approve` row must
   name its account and show no other.
2. A row with no account stores nothing.
3. A row for an account whose record names exactly the caller's machine, user and that profile updates that record's
   health, whoever made the record.
4. Any other row that is not `can-approve` stores nothing when the account has no record, and is refused
   `record-names-another-approver` when it has one.
5. A `can-approve` row registers the profile as the account's approver, marked `registered`, unless: the report said
   the machine does not offer (`not-offered`); no machine of the tenant holds the account (`account-not-held`); the
   owner set the account's approver (`owner-set`); the owner removed this same registration (`owner-removed`, until the
   owner sets an approver for that account again); a registered incumbent can approve and was checked within 26 hours
   (`healthy-incumbent`); or the tenant already keeps 64 approvers (`too-many-approvers`).

There is no separate tenant guard: a Director reports licenses only with license switching switched on for it, so
rule "the reporting Director" and rule "an account the tenant holds" already bind a registration to a machine where
the owner switched automation on, and the machine's offer setting is off by default.

### 10.4 The not-checked rule

A record whose last check is older than 26 hours (`LicenseApproverContract.NotCheckedAfter`), or that was never
checked, reads `not-checked`. An absence is never read as healthy.

### 10.5 The owner's removal

The owner removing a registered record (`DELETE /gateway/licenses/approvers`) keeps a removal record of its account,
machine, user and profile (the tenant setting `license_approver_removals`, at most 64, oldest dropped first). The owner
setting an approver for that account clears that account's removal records.

### 10.6 The approver alarm

An approver that cannot approve is a license alarm (section 9) of kind `approver-cannot-approve`, titled "Approver
cannot approve sign-ins", one for each such approver, about its account alone (the alarm's machine is always "no one
machine"; the sentences name the machine, and there is one approver per account, so the alarm never changes identity
when that machine first reports). What is wrong and the one thing to do are the approvers row's `healthText` and `whatToDo`. It is raised
every time approver health is evaluated: after each check report a Director sends (on a task of its own, so the hub
call never waits for a push or an email), and on the Gateway's once-a-minute license check, so an approver nobody
checks alarms once its 26 hours pass. Each raise passes every approver of the tenant that cannot approve now, so one
that recovers or is removed closes by itself. An account with no approver at all raises nothing here; whether an
account is ready is Track A's readiness, which asks `LicenseApproverReadiness.WhyNotReady`. Raises for one tenant run
one at a time, each reading the records after the previous one wrote. An approver whose sentence is longer than an alarm
takes (400 characters) costs only its own alarm, logged with its account. A raise with nothing to say writes no log
line.

## 11. Phase 6 - readiness on every minute, and the automatic sign-in (Track A, piece A3)

**What is judged.** For every machine on an account: the account it would switch to next
(`LicenseDecisionEngine.NextTarget`), and whether the machine is ready for it (`LicenseDecisionEngine.Readiness`, the
one definition of ready). "No other account has room" is its own state (`no-room`), never a readiness gap. The pure fold
is `Licenses.LicenseReadinessFold`; the Licenses page and the minute check call the same `Judge` on the same facts.

**What is done about a machine that is not ready**, in this order:

1. A sign-in for that machine and account is open (`pending` or `unknown`): wait.
2. The account's approver cannot approve - `LicenseApproverReadiness.WhyNotReady` (section 10, Track C's one check)
   says so: start nothing. The reason shown is that sentence.
3. The machine's Director is not connected: start nothing. It is judged again every minute, so the sign-in starts on
   the first minute the Director is back. No row is written while it is offline.
4. The last sign-in for that machine and account ended `added` or `renewed` less than 10 minutes ago: wait for the
   machine's next report, rather than sign it in twice.
5. The last sign-ins failed: wait 2, 5, 15 or 30 minutes after the last failed one, by how many failed in a row, then
   hourly. The count is read from the sign-in rows of the Licenses log (the last day of them), newest first, up to the
   first that did not fail - so it survives a Gateway restart and needs no store of its own.
6. Otherwise: start a sign-in now through `LicenseSignInService.Start` (section 8.4), with the reason `auto-spare` when
   the machine holds no sign-in for the account, or `auto-renewal` when the sign-in it holds was refused or expires
   too soon. Each is refused exactly as "Add a spare" or "Renew now" is; in particular a second one while one is
   running for that machine and account is refused `sign_in_running`, and the check relies on that.

**The two new reason words** travel in `LicenseSignInRequest.Reason` and are kept on the sign-in row. A Director only
logs the word. The Licenses log shows them as "Automatic: a new spare" and "Automatic: a renewal"; the row's first
sentence begins "Started automatically:".

**The alarm.** On each check the readiness check raises kind `not-ready-at-80` (title "Not ready to switch") with one
condition per machine that is not ready while the account it is on stands, on a fresh reading (section 5.2), at or
above 80% of either window (a window whose reset time has passed does not count). The condition's machine is the
machine, its account is the account in use, and its words are:

- what is wrong: "machine-a is not ready to switch off account-a@example.com, which is at 85% of its weekly limit: it
  holds no sign-in for account-b@example.com, the account it would switch to next."
- the one thing to do: the approver's sentence when the approver cannot approve; "Start the Director on machine-a."
  when its Director is not connected; otherwise "Nothing to do yet: DevThrottle is signing it in; if this stays, sign
  machine-a in to account-b@example.com from the Licenses page."

It closes by itself (section 9.2) when the machine becomes ready, when the account falls back under 80%, or when its
reading is no longer fresh.

**Only where automation is switched on.** A tenant none of whose machines has reported is left alone: nothing read,
nothing started, no alarm raised. A Director reports licenses only with license switching switched on for it (the
13 October ruling keeps that to the owner's own tenant), the same rule as section 10.3.
