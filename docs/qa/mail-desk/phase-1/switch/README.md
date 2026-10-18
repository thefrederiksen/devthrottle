# Mail Desk phase 1: the switch, the check-report refusal, and the command that turns it on

Contract: `docs/qa/mail-desk/phase-1/CONTRACT.md`, section 1 (rows 1 to 4). Built from phase 0 (d)'s recommendation.

## What was built

| Piece | Where |
|---|---|
| The per-account switch, a `tenant_settings` key `mail_desk_switch`; no machine switch | `src/CcDirector.Gateway/MailDesk/MailDeskSwitch.cs`, `Settings/TenantSettingKeys.cs` |
| The route group answering 404 with no body when off | `src/CcDirector.Gateway/Api/MailDeskGate.cs` |
| The administrator route `GET`/`POST /gateway/admin/mail-desk`, exempt from the device-key check | `src/CcDirector.Gateway/Api/AdminMailDeskEndpoint.cs`, `Util/AuthMiddleware.cs`, wired in `GatewayHost.cs` |
| What a Mail Desk start is: a definition trigger of `MailDeskTemplate.Factory` with check `MailDeskTemplate.CheckPath` | `src/CcDirector.Gateway/MailDesk/MailDeskStart.cs` |
| The refusal: 409 `{"error":"mail_desk_off","detail":...}`, nothing recorded | `TriggerService.ReportCheckAsync`, `Api/TriggerEndpoints.cs` |
| The command | `scripts/mail-desk-switch.ps1` |

## The tests

`src/CcDirector.Gateway.UnitTests/MailDesk/MailDeskSwitchTests.cs` (in the parked `Gateway.UnitTests` suite). Run:

```
DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~MailDeskSwitchTests"
```

`CheckReport_MailDeskOff_IsRefusedAndRecordsNothing` and `CheckReportRoute_MailDeskOff_Answers409MailDeskOff` were
checked by switching the refusal off in the code: both failed, and the other 32 passed.

After review: the timed-start refusal tests are in `Factory/Triggers/DueCheckServiceTests.cs`, and
`MailDeskSwitchSet_IsCalledOnlyByTheAdministratorRoute` reads the compiled Gateway for every caller of
`MailDeskSwitch.Set`. Both were checked the same way: with the timed-start refusal switched off and a second caller
added to `GatewayHost` under another variable name, exactly those two tests failed.

## The command, run against a Gateway started on this machine

A self-host Gateway built from this branch (`CcDirector.Gateway.dll --port 7996`), with a scratch root and home and a
test administrator token made for this run - never the hosted Gateway. One account, `email@devthrottle.com`, was
written into its scratch database, because a self-host Gateway records no email for an account. The token is removed
from the transcript below; it does not appear in the transcript or in the Gateway's own output. Every `-TokenSupplied` line ran with `ADMIN_SERVICE_TOKEN` set to that test token, except the two that say
otherwise on the line. Each `-Reason` of more than one word is quoted, as it must be when run.

```

$ pwsh -NoProfile -NonInteractive -File scripts/mail-desk-switch.ps1 -Account email@devthrottle.com -Reason test -Gateway http://127.0.0.1:7996
[mail-desk-switch] say exactly one of -On or -Off
(exit 2)

$ pwsh -NoProfile -NonInteractive -File scripts/mail-desk-switch.ps1 -Account email@devthrottle.com -On -Off -Reason test -Gateway http://127.0.0.1:7996
[mail-desk-switch] say exactly one of -On or -Off
(exit 2)

$ pwsh -NoProfile -NonInteractive -File scripts/mail-desk-switch.ps1 -Account email@devthrottle.com -On -Reason "phase 1 local proof" -DryRun
[mail-desk-switch] the cc-secrets setting gateway-admin-url could not be read (exit 2); add it, or pass -Gateway
(exit 2)

$ pwsh -NoProfile -NonInteractive -File scripts/mail-desk-switch.ps1 -Account email@devthrottle.com -On -Reason "phase 1 local proof" -Gateway http://127.0.0.1:7996 -DryRun
[mail-desk-switch] DRY RUN - nothing is sent. Every request carries 'Authorization: Bearer <admin-service-token>'.
[mail-desk-switch] 1. GET  http://127.0.0.1:7996/gateway/admin/accounts?email=email%40devthrottle.com
[mail-desk-switch] 2. POST http://127.0.0.1:7996/gateway/admin/mail-desk
[mail-desk-switch]         {"enabled":true,"reason":"phase 1 local proof","account":"<account id from request 1>","actor":"soren on devlinux"}
[mail-desk-switch] 3. GET  http://127.0.0.1:7996/gateway/admin/mail-desk?account=<account id from request 1>
(exit 0)

$ curl GET /gateway/admin/mail-desk?account=<id>   (before: nothing recorded)
{"account":"b869a25d-d09a-401d-9b76-50c380188f4e","enabled":false,"decision":null}

$ pwsh -NoProfile -NonInteractive -File scripts/mail-desk-switch.ps1 -Account email@devthrottle.com -On -Reason "phase 1 local proof: on" -Gateway http://127.0.0.1:7996 -TokenSupplied
[mail-desk-switch] GET http://127.0.0.1:7996/gateway/admin/accounts?email=email%40devthrottle.com -> HTTP 200
[mail-desk-switch] account email@devthrottle.com is b869a25d-d09a-401d-9b76-50c380188f4e
[mail-desk-switch] POST http://127.0.0.1:7996/gateway/admin/mail-desk -> HTTP 200
[mail-desk-switch] GET http://127.0.0.1:7996/gateway/admin/mail-desk?account=b869a25d-d09a-401d-9b76-50c380188f4e -> HTTP 200
[mail-desk-switch] read back: {"account":"b869a25d-d09a-401d-9b76-50c380188f4e","enabled":true,"decision":{"enabled":true,"actor":"soren on devlinux","reason":"phase 1 local proof: on","recorded_at_utc":"2026-10-18T15:39:35.7683809Z"}}
[mail-desk-switch] Mail Desk is ON for email@devthrottle.com (b869a25d-d09a-401d-9b76-50c380188f4e), recorded by soren on devlinux at 10/18/2026 15:39:35: phase 1 local proof: on
(exit 0)

$ pwsh -NoProfile -NonInteractive -File scripts/mail-desk-switch.ps1 -Account email@devthrottle.com -Off -Reason "phase 1 local proof: off again" -Gateway http://127.0.0.1:7996 -TokenSupplied
[mail-desk-switch] GET http://127.0.0.1:7996/gateway/admin/accounts?email=email%40devthrottle.com -> HTTP 200
[mail-desk-switch] account email@devthrottle.com is b869a25d-d09a-401d-9b76-50c380188f4e
[mail-desk-switch] POST http://127.0.0.1:7996/gateway/admin/mail-desk -> HTTP 200
[mail-desk-switch] GET http://127.0.0.1:7996/gateway/admin/mail-desk?account=b869a25d-d09a-401d-9b76-50c380188f4e -> HTTP 200
[mail-desk-switch] read back: {"account":"b869a25d-d09a-401d-9b76-50c380188f4e","enabled":false,"decision":{"enabled":false,"actor":"soren on devlinux","reason":"phase 1 local proof: off again","recorded_at_utc":"2026-10-18T15:39:36.4356939Z"}}
[mail-desk-switch] Mail Desk is OFF for email@devthrottle.com (b869a25d-d09a-401d-9b76-50c380188f4e), recorded by soren on devlinux at 10/18/2026 15:39:36: phase 1 local proof: off again
(exit 0)

$ pwsh -NoProfile -NonInteractive -File scripts/mail-desk-switch.ps1 -Account nobody@example.com -On -Reason "an account the Gateway does not have" -Gateway http://127.0.0.1:7996 -TokenSupplied
[mail-desk-switch] GET http://127.0.0.1:7996/gateway/admin/accounts?email=nobody%40example.com -> HTTP 404
[mail-desk-switch] the account lookup was refused: HTTP 404 {"found":false,"searched_by":"email","message":"no account on this Gateway matches that. An account appears here once it has run something - a member who has only ever signed in to the website has no account here yet."}
(exit 1)

$ ADMIN_SERVICE_TOKEN=not-the-token pwsh -NoProfile -NonInteractive -File scripts/mail-desk-switch.ps1 -Account email@devthrottle.com -On -Reason "a wrong token" -Gateway http://127.0.0.1:7996 -TokenSupplied
[mail-desk-switch] GET http://127.0.0.1:7996/gateway/admin/accounts?email=email%40devthrottle.com -> HTTP 401
[mail-desk-switch] the account lookup was refused: HTTP 401 {"error":"a valid admin service token is required"}
(exit 1)

$ env -u ADMIN_SERVICE_TOKEN pwsh -NoProfile -NonInteractive -File scripts/mail-desk-switch.ps1 -Account email@devthrottle.com -On -Reason "no token given" -Gateway http://127.0.0.1:7996 -TokenSupplied
[mail-desk-switch] -TokenSupplied was given but ADMIN_SERVICE_TOKEN is empty; run without it, so cc-secrets supplies the token
(exit 2)

$ pwsh -NoProfile -NonInteractive -File scripts/mail-desk-switch.ps1 -Account email@devthrottle.com -On -Reason "phase 1 local proof: on again" -Gateway http://127.0.0.1:7996 -TokenSupplied
[mail-desk-switch] GET http://127.0.0.1:7996/gateway/admin/accounts?email=email%40devthrottle.com -> HTTP 200
[mail-desk-switch] account email@devthrottle.com is b869a25d-d09a-401d-9b76-50c380188f4e
[mail-desk-switch] POST http://127.0.0.1:7996/gateway/admin/mail-desk -> HTTP 200
[mail-desk-switch] GET http://127.0.0.1:7996/gateway/admin/mail-desk?account=b869a25d-d09a-401d-9b76-50c380188f4e -> HTTP 200
[mail-desk-switch] read back: {"account":"b869a25d-d09a-401d-9b76-50c380188f4e","enabled":true,"decision":{"enabled":true,"actor":"soren on devlinux","reason":"phase 1 local proof: on again","recorded_at_utc":"2026-10-18T15:39:56.8205985Z"}}
[mail-desk-switch] Mail Desk is ON for email@devthrottle.com (b869a25d-d09a-401d-9b76-50c380188f4e), recorded by soren on devlinux: phase 1 local proof: on again
(exit 0)
```

The `cc-secrets` hop, without sending the real token anywhere: `cc-secrets run admin-service-token` hands the entry to
the second run as `ADMIN_SERVICE_TOKEN`, the variable the script reads with `-TokenSupplied`:

```
$ cc-secrets run admin-service-token -- pwsh -NoProfile -Command 'if ([string]::IsNullOrWhiteSpace($env:ADMIN_SERVICE_TOKEN)) { "ADMIN_SERVICE_TOKEN is EMPTY" } else { "ADMIN_SERVICE_TOKEN is supplied to the child" }'
ADMIN_SERVICE_TOKEN is supplied to the child
[cc-secrets] command exited 0
```

## Not proven here

- The whole command without `-TokenSupplied`, which reads `gateway-admin-url` and runs itself under `cc-secrets`, was
  not run end to end: this machine has no `gateway-admin-url` setting, and that path's only real target is the hosted
  Gateway, which phase 6 switches. Its two halves were each run: the setting read (refused, exit 2, above) and the hop
  (above).
- The 409 on the wire was proven in a unit test through the real route, not against the running Gateway: that needs a
  registered Director and a published `mail-desk` factory, which phase 2 builds.
- A TIMED start (a schedule with a check) of `checks/screen.py` IS refused the same way, in `DueCheckService.Report`
  (review finding F1): `DueCheckServiceTests.Report_AMailDeskTimedStart_MailDeskOff_IsRefusedAndRecordsNothing`, which
  fails with that refusal switched off. Its 409 on the wire goes through the same `MailDeskOff` answer in
  `TriggerEndpoints` as a trigger's, but no unit test drives a due check through the route.
