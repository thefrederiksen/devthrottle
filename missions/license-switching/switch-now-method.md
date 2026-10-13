# Switch now - the one method the Cockpit API calls

Written first by the Developer wiring the Gateway's once-a-minute check, so the Developer building the
Licenses API can build against it in parallel. The signature and the result type below are exact. If
either has to change before the pull request merges, this file changes in the same commit and the Tech
Lead is told.

## Where it is

- Class: `CcDirector.Gateway.Licenses.LicenseSwitchService` (`src/CcDirector.Gateway/Licenses/LicenseSwitchService.cs`).
- The one instance: `GatewayHost.LicenseSwitches` (an `internal` property beside `LicenseStore`,
  `LicenseUsage` and `LicenseReads`).

## The method

```csharp
namespace CcDirector.Gateway.Licenses;

public sealed class LicenseSwitchService
{
    /// Switch now: move every machine on sourceAccountEmail that can be sent a switch to
    /// targetAccountEmail, or to the best target when targetAccountEmail is null or blank.
    public Task<LicenseSwitchNowResult> SwitchNowAsync(
        CcDirector.Core.Tenancy.TenantId tenant,
        string sourceAccountEmail,
        string? targetAccountEmail,
        CancellationToken ct);
}
```

- `tenant`: the caller's own tenant, resolved by the route from the request (never from the body).
- `sourceAccountEmail`: the account to move machines off. It is an ACCOUNT, not a machine, because
  usage is counted per account (`MISSION.md` decision a). Any case, any surrounding spaces.
  **Blank or null throws `ArgumentException`** - the route answers 400 before calling.
- `targetAccountEmail`: null or blank means "choose for me", which is exactly the automatic choice.
- `ct`: the request's token. If the caller goes away mid-send, the method throws
  `OperationCanceledException`; the switch log rows already written stay open and the next minute
  check settles them. Nothing is lost and nothing runs twice (the Director answers a repeated switch
  identifier from its store).

## What it does

1. Builds the same picture the minute check builds and runs `LicenseDecisionEngine.DecideManual`.
2. Refused: returns at once with the refusal. Nothing is written and nothing is sent.
3. Accepted: for every machine the engine named, writes the pending switch log row FIRST, then sends
   `license-switch` with reason `manual` through the SAME code path the minute check uses, to every
   machine at once.
4. **Returns when every machine has answered or its 60-second wait has ended** - so normally in a
   second or two, and never much longer than 60 seconds. Each switch in the result carries where its
   log row stood at that moment.

The route should assert that `tenant` IS the tenant in scope before it calls. The method reads and writes rows for
the tenant it is given and the send looks the Director up in the tenant in scope; the two are never compared here, so
a route that passed one inside the other would leave rows `pending` with nothing sent (review of pull request 3923,
note 10).

It must be called inside the caller's tenant scope, which a request already is: the send goes through
`GatewayHost.SendCommandAsync`, which drops a command sent with no tenant scope.

## The result

```csharp
namespace CcDirector.Gateway.Licenses;

/// The answer to Switch now. Either Accepted is true and Switches has one entry per machine that
/// was sent a switch, or Accepted is false and RefusalCode and RefusalMessage say why nothing was sent.
public sealed record LicenseSwitchNowResult(
    bool Accepted,
    string? RefusalCode,
    string? RefusalMessage,
    string? TargetAccountEmail,
    IReadOnlyList<LicenseSwitchNowMachine> Switches,
    IReadOnlyList<LicenseSkippedMachine> Skipped);

/// One machine's switch, as its switch log row stood when SwitchNowAsync returned.
public sealed record LicenseSwitchNowMachine(
    string SwitchId,
    string MachineName,
    string OsUser,
    string State,
    string? ReasonCode,
    string? Message);
```

| Field | Meaning |
|---|---|
| `Accepted` | True when at least one switch was written and sent. |
| `RefusalCode` | Null when accepted. Otherwise one of `LicenseManualRefusals`: `no-machine-to-switch`, `target-is-source`, `target-not-held`, `target-sign-in-expiring`, `target-stale`, `target-full`, `no-target`. |
| `RefusalMessage` | Null when accepted. Otherwise one plain sentence, ready to show as given. |
| `TargetAccountEmail` | The account the machines were sent to (the named one, or the one chosen), lower-cased. Null when refused. |
| `Switches` | One per machine sent a switch. Empty when refused. |
| `Skipped` | Machines on the source account that were NOT sent a switch, each with a plain `Why` (stale, or a switch already open). Can be non-empty whether accepted or refused. `LicenseSkippedMachine` is the existing record in `LicenseDecisionModel.cs`. |
| `SwitchId` | The switch log row's identifier (32 lower-case hexadecimal characters). Find the row later with `LicenseStore.FindSwitch(tenant, switchId)`. |
| `State` | A `LicenseLogStates` word: `switched`, `already-active`, `failed`, `pending` (the Director is offline; the minute check sends it again until the switch expires) or `unknown` (sent, no answer in 60 seconds; the minute check settles it). |
| `ReasonCode` | Why it is `pending` or `failed`: a `LicenseSwitchReasonCodes` word from the Director, or the Gateway's own `director-offline` / `director-error`. Null otherwise. |
| `Message` | One plain sentence for the row, the Director's or the Gateway's. May be null. |

A route can map it directly: refused is a 409 with `RefusalCode` and `RefusalMessage`; accepted is a
200 with the switches. The Cockpit shows every word as given (`CLAUDE.md` rule 7) - it never works
out what a state means.

## The picture and the history are NOT on this service

An earlier version of this file offered `ReadSnapshot` and `ReadHistory` here. They were removed before
the pull request: the snapshot and the history have one home each, shared by both pull requests
(commit `9596645a5`, cherry-picked into this branch unchanged).

- The engine's picture: `GatewayHost.LicenseSnapshots` - `LicenseSnapshotSource.Read(tenant, nowUtc, openExhaustions)`
  and `ReadPicture(...)`. The minute check passes its open no-room events in; the Licenses page and the
  forecast pass an empty list.
- The recent readings: `GatewayHost.LicenseHistory` - `LicenseUsageHistory.Read(tenant, nowUtc)`.

One thing the page should know about that picture. It marks a machine as mid-switch
(`HasOpenSwitch`) whenever the log holds a `pending` or `unknown` row for it, which is right to show.
The minute check and Switch now are a little narrower when they DECIDE: a row that is `pending` with
reason `director-offline` was never sent, so it does not hold its machine back from a new decision -
Switch now on that account is accepted, ends the waiting row as `failed`, and writes a new one.

## In the switch log

The minute check adds one new kind of row to `license_switch_log`, read with the existing
`LicenseStore.ListLog`: `Kind = "no-room"` (`LicenseLogKinds.NoRoom`) - no account had room, nothing
was switched. `FromAccountEmail` is the account that ran out, `Message` is the sentence to show
(it says whether the owner was emailed), and the row is open while `FinishedAtUtc` is null.
