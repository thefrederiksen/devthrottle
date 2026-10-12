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

## Also on the same service, for the Licenses page

These two come in the same pull request and are stated here so the API need not rebuild them:

```csharp
/// The picture of one tenant the engine and the forecast both take: accounts with their freshest
/// reading, machines, bars in force and open "no account has room" events.
public LicenseSnapshot ReadSnapshot(CcDirector.Core.Tenancy.TenantId tenant, DateTime nowUtc);

/// The recent readings (two hours, kept in memory, rebuilt after a restart) LicenseForecaster needs.
public IReadOnlyList<LicenseAccountHistory> ReadHistory(CcDirector.Core.Tenancy.TenantId tenant, DateTime nowUtc);
```

So the forecast is `LicenseForecaster.Forecast(service.ReadSnapshot(tenant, now), service.ReadHistory(tenant, now))`.

## In the switch log

The minute check adds one new kind of row to `license_switch_log`, read with the existing
`LicenseStore.ListLog`: `Kind = "no-room"` (`LicenseLogKinds.NoRoom`) - no account had room, nothing
was switched. `FromAccountEmail` is the account that ran out, `Message` is the sentence to show
(it says whether the owner was emailed), and the row is open while `FinishedAtUtc` is null.
