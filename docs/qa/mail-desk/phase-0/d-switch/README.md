# Mail Desk, phase 0, proof (d): where the account-level switch belongs

Mission: `docs/missions/mail-desk-2026-10-18/MISSION.md`, section 5.10 (issue #4213).

**Read from origin/main at commit `d091f90834c838797a8c215b142dbcf2fe06359b`** ("Mail Desk: the mission document
(#4212)"), using `git show origin/main:<file>` and `git grep <pattern> origin/main` only. Every `file:line` below is
from that commit. This proof reads code only: it touched no mailbox, no Gateway, and no product code.

## The answer in three lines

1. The factory agents switch is **already per account**. It is two switches: a whole-Gateway one in `config.json`
   (off on the hosted Gateway) and a per-account decision stored as one row in `tenant_settings`, written only by an
   administrator route guarded by `ADMIN_SERVICE_TOKEN`.
2. So "exactly like the factory agents switch" is possible as written. The hosted Gateway can turn it on for the
   owner's account with one administrator request, which writes one database row: **no deploy, no restart, no app
   setting change**.
3. Recommendation: `mailDesk.enabled` copies that design exactly - a new `tenant_settings` key, a `MailDeskSwitch`
   class, a `MailDeskGate` route group, and `/gateway/admin/mail-desk`. **No new table, so no migration pair.**

## 1. The factory agents switch, as built

| What | Answer | Where |
|---|---|---|
| Name, per account | `tenant_settings` key `factory_agents_switch` | `src/CcDirector.Gateway/Settings/TenantSettingKeys.cs:250-256`, listed in `All` at `:355` |
| Name, whole Gateway | `factoryAgents.enabled` in the Gateway's `config.json` | `src/CcDirector.Core/Configuration/FactoryAgentsConfig.cs:11-40` |
| What the stored value is | JSON: `enabled`, `actor`, `reason`, `recorded_at_utc` | `src/CcDirector.Gateway/Factory/FactoryAgentsSwitch.cs:81-86` |
| Where it is stored | `tenant_settings` table, one row per (account, key), through `TenantSettingsStore`; the store refuses any key not in `TenantSettingKeys.All` | `src/CcDirector.Gateway/Settings/TenantSettingsStore.cs:27-53`, `:221-227`; table made by `src/CcDirector.Gateway/Data/Migrations/20260722174132_AddTenantSettings.cs` and `src/CcDirector.Gateway.Migrations.Postgres/Migrations/20260722174151_AddTenantSettings.cs` |
| Default | Off for every account. Only a JSON `true` turns the machine switch on; no stored row means off for the account | `FactoryAgentsConfig.cs:15-16`, `:37-40`; `FactoryAgentsSwitch.cs:22-24`, `:47-51` |
| How the two combine | On = machine switch on, OR this account switched on. An account switched off does not override a machine switch that is on | `FactoryAgentsSwitch.cs:13-24`, `:47-51` |
| A stored value that cannot be read | Throws; never quietly "off" | `FactoryAgentsSwitch.cs:26-27`, `:54-61` |
| Who can change it, and how | Only an administrator, with `ADMIN_SERVICE_TOKEN` as a bearer token: `GET /gateway/admin/factory-agents?account=<id>` and `POST /gateway/admin/factory-agents {account, enabled, actor, reason}`. Every field is required, the account must exist on the Gateway, there is no wildcard, and the previous decision is written to the log | `src/CcDirector.Gateway/Api/AdminFactoryAgentsEndpoint.cs:12-33`, `:56-98`, `:124-146`, `:150-155`; `FactoryAgentsSwitch.cs:67-78` |
| Can the account switch itself on? | No. The account's settings page has no setter for this key; only the administrator route writes it | `TenantSettingKeys.cs:252-253`; `AdminFactoryAgentsEndpoint.cs:23-24` |
| The administrator token | Fails closed (503) when unset, and refuses when it equals `REPORT_SERVICE_TOKEN` | `src/CcDirector.Gateway/Api/AdminTrialEndpoint.cs:62`, `:213-245` |
| The administrator route and the device key | Exempted from the device-key check by exact path, like the other administrator routes | `src/CcDirector.Gateway/Util/AuthMiddleware.cs:230-232` |
| Read once at start | The machine switch is read once at construction; the account switch is read from the database on every request | `src/CcDirector.Gateway/GatewayHost.cs:232-239`, `:2035`, `:2475` |
| How a route reads it | Every factory route is mapped INTO a route group whose endpoint filter asks `FactoryAgentsGate.IsOnFor`, so a route added later is gated by construction | `src/CcDirector.Gateway/Api/FactoryAgentsGate.cs:10-41`, `:44-64`; wired at `GatewayHost.cs:5432-5439` |
| What a route answers when off | **404 with no body**, the same as an unmapped route, both for an account that is off and for a request bound to no account | `FactoryAgentsGate.cs:33-37`, `:52-61` |
| Routes that check it by hand instead of the group | Owner actions, factory talk, the factory manager call `FactoryAgentsGate.IsOnFor` inside the handler | `src/CcDirector.Gateway/Api/FactoryOwnerActionEndpoints.cs:152`, `src/CcDirector.Gateway/Api/FactoryTalkEndpoints.cs:198`, `src/CcDirector.Gateway/Api/FactoryManagerEndpoints.cs:189` |
| The switch question for the Cockpit | `GET /gateway/factory-agents/switch`, deliberately outside the gate, answers `{enabled, howToStart}` for the calling account through the same test the gate uses | `src/CcDirector.Gateway/Api/FactoryAgentsViewEndpoints.cs:63-93`; wired at `GatewayHost.cs:5435-5436` |
| The trigger and check report path | Both halves of the triggers - the owner's `/triggers` and the Director's `GET /directors/{directorId}/triggers` and `POST /directors/{directorId}/triggers/{id}/checks` - are mapped into the gate. So for an account that is off, the Director is handed no checks and a report is answered 404 | `GatewayHost.cs:6201-6219`; `src/CcDirector.Gateway/Api/TriggerEndpoints.cs:33-39`, `:51-52`, `:236-279`. The definition-check download is behind the same gate: `GatewayHost.cs:6221-6230` |
| The Director's side of a 404 | Read as "the Gateway serves no triggers": the Director runs nothing | `src/CcDirector.ControlApi/Triggers/ITriggerGateway.cs:10-12`; `src/CcDirector.ControlApi/GatewayClient.cs:1003-1011` |
| Timed starts (schedules) with a check | The cron engine asks the switch before a checked fire and records why when it is off | `GatewayHost.cs:2721-2723` |
| The roster's factory chip | Stamped only when the switch is on for that account | `GatewayHost.cs:4955-4959` |

### The tests that prove it

All in `src/CcDirector.Gateway.UnitTests/Factory/FactoryAgentsSwitchTests.cs` (a PARKED suite - it runs with
`-Parked`, or when `select-parked-tests.ps1` picks it):

- `:61` `IsOn_NothingRecordedAndMachineOff_IsOffForEveryAccount`
- `:70` `IsOn_OneAccountSwitchedOn_IsOnForThatAccountOnly`
- `:80` `IsOn_MachineSwitchOn_IsOnForEveryAccountEvenOneSwitchedOff`
- `:90` `Set_SwitchedOnThenOff_IsOffAndRecordsTheLatestDecision`
- `:106` `Set_NoActorOrNoReason_Throws`
- `:115` `Decision_StoredValueIsNotADecision_ThrowsRatherThanAnsweringOff`
- `:125` `TenantSettingKeys_FactoryAgentsSwitch_IsAStorableKey`
- `:205` `EveryFactoryRoute_AccountOff_Answers404AsIfUnmapped` - maps the real routes into the gate exactly as
  `GatewayHost` does (`:134-180`), names more than twenty of them (including the Director's check report at `:218`) so
  an empty list cannot pass, and asserts 404 with an empty body for an account that is off and for no account.
- `:249` `FactoryRoutes_AccountOn_AreServed`
- `:262` `SwitchQuestion_AnswersForTheCallingAccount`
- `:281` `AdminSet_NoServiceToken_IsRefusedAndRecordsNothing`
- `:299` `AdminSet_MissingField_IsRefused` (five cases)
- `:313` `AdminSet_AccountTheGatewayDoesNotHave_IsRefused`
- `:321` `AdminSet_KnownAccount_SwitchesThatAccountOnlyAndReadsBack`
- `:346` `AdminPath_IsExemptFromTheDeviceKeyGateLikeTheOtherAdminRoutes`

Other test files that use the switch: `Factory/FactoryOwnerActionEndpointTests.cs`, `Factory/Talk/FactoryTalkEndpointTests.cs`,
`FactoryActivityRecordTests.cs`, `Running/CronEngineCheckedFireTests.cs`,
`Factory/Definition/DefinedByDefinitionRoutesThroughThePipelineTests.cs` (all under `src/CcDirector.Gateway.UnitTests/`).

The feature arrived in pull request #3296 (commit `9a824d312`, "the Factory Agents switch, per account").

## 2. Per account or whole Gateway?

**Per account, with a whole-Gateway override.** On the hosted Gateway the machine switch is off and each account is
switched on by an administrator one at a time (`FactoryAgentsSwitch.cs:17-20`). That is the shape section 5.10
needs: on for the owner's account, off for every other account.

A second, identical precedent exists: the administrator turn-log switch (`AdminTurnLogEndpoint`, exempted at
`AuthMiddleware.cs:225-229`), also one account's boolean behind the same token. A contrasting pattern -
`factory_email_enabled` (`TenantSettingKeys.cs:298`, set through `FactoryEmailEndpoints` at `GatewayHost.cs:6195-6199`) -
is a setting the account turns on for itself. That is the wrong pattern for Mail Desk: the owner's grant (D16) holds
only while no other account can have the feature on, so another account must not be able to switch itself on.

**Turning it on for one account on the hosted Gateway, without a deploy:** one authorised request,
`POST /gateway/admin/mail-desk {account, enabled: true, actor, reason}`, with `ADMIN_SERVICE_TOKEN` as the bearer
token. It writes one `tenant_settings` row and takes effect on the next request, because the account switch is read
from the database on every call. The token value lives in cc-secrets (entry `admin-service-token`) and the Gateway
address in cc-secrets (entry `gateway-admin-url`) (`docs/deploy/hosted-gateway-ci.md:84-96`). The account
identifier comes from `GET /gateway/admin/accounts?email=...` (`AdminFactoryAgentsEndpoint.cs:19`). The switch route
itself must be deployed first, so this happens in phase 6, after the deploy - never as a separate deploy.

## 3. Recommendation for phase 1

Copy the factory agents switch, with one difference: **no machine switch.** A whole-Gateway "on for everyone" for a
feature that reads a mailbox has no user today, and leaving it out means no `config.json` edit can ever turn Mail
Desk on for every account of the hosted Gateway. (If the Tech Lead wants self-hosted parity, add it later as a
`mailDesk` block in `config.json`, exactly as `FactoryAgentsConfig` does; it is one small class.)

**Files to add or change (all on the Gateway; no Director change, so no desktop release):**

| File | What |
|---|---|
| `src/CcDirector.Gateway/Settings/TenantSettingKeys.cs` | New key `MailDeskSwitch = "mail_desk_switch"`, added to `All`. Doc comment: written only by the administrator route |
| `src/CcDirector.Gateway/MailDesk/MailDeskSwitch.cs` (new) | Same shape as `FactoryAgentsSwitch`: `IsOn(tenant)`, `Decision(tenant)`, `Set(tenant, enabled, actor, reason, now)`, an unreadable row throws |
| `src/CcDirector.Gateway/Api/MailDeskGate.cs` (new) | Same shape as `FactoryAgentsGate`: `Group(...)` with an endpoint filter answering 404 with no body, and `IsOnFor(...)` |
| `src/CcDirector.Gateway/Api/AdminMailDeskEndpoint.cs` (new) | `GET` and `POST /gateway/admin/mail-desk`, calling `AdminTrialEndpoint.ServiceTokenDenial` (never a copy), every field required, account must exist |
| `src/CcDirector.Gateway/Util/AuthMiddleware.cs` | Exempt `AdminMailDeskEndpoint.Path` from the device-key check, beside `AdminFactoryAgentsEndpoint.Path` (`:230-232`) |
| `src/CcDirector.Gateway/GatewayHost.cs` | Construct the switch beside `FactoryAgentsSwitch` (`:2475`), map every Mail Desk route into `MailDeskGate.Group(...)`, map the admin route beside `:5555` |
| The report hand-off (phase 3 owns its body; phase 1 owns the check) | A Mail Desk check reports on the SAME route as every trigger, `POST /directors/{directorId}/triggers/{id}/checks` (`TriggerEndpoints.cs:246`). The factory gate in front of it is ON for the owner's account (its factories run in production), so it cannot be the Mail Desk refusal. The refusal must be in the report handling itself: when the reported trigger is a Mail Desk screening check (or the report carries the `screened` outcome) and `MailDeskSwitch.IsOn(tenant)` is false, refuse without recording a decision, without keeping the check's memory (`TriggerService.KeepState`), and without starting anything |
| Optional: a switch question | `GET /gateway/mail-desk/switch` outside the gate, as `FactoryAgentsViewEndpoints.MapSwitch` does, only if a surface needs to ask. Section 5.11 puts the Cockpit page out of scope, so it can wait |

**No new table, so no migration pair.** The switch is one more key in `tenant_settings`, which exists in both
databases already. The Mail Desk tables of section 5.6 still need their own pair (SQLite in
`src/CcDirector.Gateway/Data/Migrations/` and PostgreSQL in `src/CcDirector.Gateway.Migrations.Postgres/Migrations/`),
but that is phase 1's store work, not the switch.

**Tests (a new `src/CcDirector.Gateway.UnitTests/MailDesk/MailDeskSwitchTests.cs`, one for one with the factory ones):**

- `IsOn_NothingRecorded_IsOffForEveryAccount`
- `IsOn_OneAccountSwitchedOn_IsOnForThatAccountOnly`
- `Set_SwitchedOnThenOff_IsOffAndRecordsTheLatestDecision`, `Set_NoActorOrNoReason_Throws`
- `Decision_StoredValueIsNotADecision_ThrowsRatherThanAnsweringOff`
- `TenantSettingKeys_MailDeskSwitch_IsAStorableKey`
- `EveryMailDeskRoute_AccountOff_Answers404AsIfUnmapped` - maps the real routes as `GatewayHost` does and NAMES each
  route it expects, so a route lost from the mapping cannot pass on an empty list
- `MailDeskRoutes_AccountOn_AreServed`
- `MailDeskRoutes_FactoryAgentsOnButMailDeskOff_Answer404` - the case that matters for D16: the owner's factories on,
  Mail Desk off
- `CheckReport_MailDeskOff_IsRefusedAndRecordsNothing` - no decision row, no kept memory, no start
- `AdminSet_NoServiceToken_IsRefusedAndRecordsNothing`, `AdminSet_MissingField_IsRefused`,
  `AdminSet_AccountTheGatewayDoesNotHave_IsRefused`, `AdminSet_KnownAccount_SwitchesThatAccountOnlyAndReadsBack`
- `AdminPath_IsExemptFromTheDeviceKeyGate`
- A guard that the account's own settings page cannot write `mail_desk_switch`

These sit in the parked `Gateway.UnitTests` suite; the default local run picks them up through
`select-parked-tests.ps1` when the changed files declare the types they name.

## What this proof did NOT settle

- **Whether the owner's account is already switched on for factory agents on the hosted Gateway.** It must be, since
  the Website factory's `website-new-mail` trigger runs in production (mission 5.2), but I did not ask the hosted
  Gateway (forbidden in this phase). The Mail Desk check needs BOTH switches on for the owner's account: the factory
  one for the trigger routes, the Mail Desk one for the screening.
- **How the report handler knows a check is a Mail Desk check** (the trigger's factory is `mail-desk`, or a
  `screened` outcome, or a flag on the definition). That depends on phase 3's design of the `screened` outcome; phase 1
  should pick one and test it.
- **What the refusal answers on the wire.** The factory gate answers 404, but a Mail Desk report reaches a route that
  is served for the owner's account, so a 404 there would read to the Director as "no such trigger". A clearer refusal
  (for example 409 `mail_desk_off`) may be better; the Director's handling of each status was not traced.
- **Who will send the administrator request.** No command-line tool or website screen calls
  `/gateway/admin/factory-agents` on origin/main of either repository (`devthrottle` at `d091f9083`,
  `devthrottle_internal` at `e50e72f3`), so today it is a hand-written request with the token from cc-secrets. Phase 6
  should write the exact command into its runbook.
- **Two out-of-date comments found on the way** (not Mail Desk work): `FactoryAgentsConfig.cs:17` says the Gateway
  "does not map the factory surface at all" while off, and `GatewayClient.cs:1006` says it "does not map the route";
  since #3296 the routes are mapped and answer 404 through the gate. The Director behaves the same either way.

## How to check this again

```
git fetch origin
git grep -n "FactoryAgentsSwitch\|FactoryAgentsGate\|AdminFactoryAgentsEndpoint" origin/main -- src
git show origin/main:src/CcDirector.Gateway/Factory/FactoryAgentsSwitch.cs
git show origin/main:src/CcDirector.Gateway.UnitTests/Factory/FactoryAgentsSwitchTests.cs
```

Line numbers drift as main moves; the class and test names do not.
