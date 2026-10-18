# Mail Desk, phase 1: review of the whole phase (the Gateway store and commands)

Date: 18 October 2026. Mission: `docs/missions/mail-desk-2026-10-18/MISSION.md` on origin/main (issue #4213),
sections 4, 5.6, 5.8, 5.10, 6 row 1 and 7. Contract and README: `docs/qa/mail-desk/phase-1/` on
`origin/mail-desk-phase-1`. Pull request 4235. The four pieces (4220, 4221, 4222, 4231) were each reviewed already;
this review judges the phase as a whole. I did not write any of it.

## Verdict

**Accept. No finding.** I looked for harm on each question the brief named and found none I could prove:

1. **Switch off.** Every Mail Desk route is mapped inside one gate that answers 404 with no body unless the calling
   account's own recorded decision says on. There is no machine switch. A Mail Desk check report (a trigger's or a
   timed start's) for an account that is off is refused 409 `mail_desk_off` before the per-trigger lock and before
   anything is written.
2. **Other accounts.** The switch key can be written only by `MailDeskSwitch.Set`, called only by the administrator
   route behind the admin service token, and that route names exactly one known account. No route lets an account
   write its own settings by key (the settings resolver has typed setters only). The five new tables are tenant
   scoped by the same global filter as every other table. The changes to shared code (`TriggerService`,
   `DueCheckService`, `FactoryDefinitionEndpoints`, `SessionKeyGuard`, `AuthMiddleware`) change nothing for a
   start that is not the Mail Desk check; the caller refactor in `FactoryDefinitionEndpoints` gives the same answer
   in every branch (compared line by line with origin/main). So D16's condition holds for this phase.
3. **A factory's own session** is refused 403 `factory_session_refused` on add channel, remove channel and set mode.
   It may change the rules, as D7 asks.
4. **Every change requires a why and records who.** All eight kinds of change refuse a missing or blank why (400
   `why_required`) before any store is asked to write. The stores also refuse a blank why themselves. Every channel
   add, remove and mode change, and every rules save, writes its change row (kind, who, the session's name, why) in
   the same `SaveChanges` as the change. The machine token and a Director are refused every change (403
   `not_an_author`).
5. **Migrations.** Both the SQLite and the PostgreSQL migration only create the five new tables and their indexes.
   Neither model snapshot loses a line. Nothing touches an existing table, so they are safe on an existing
   production database. The branch is level with origin/main (no commit behind), so no migration lands out of order.
6. **The command meets `docs/axi-standard.md`.** `--json` on every read. Every filter goes to the Gateway, so it
   applies to `--json` too. Unknown flags and a missing `--why` exit 2 before any request. List cells are printed in
   full (`_cell` never cuts). An empty list prints `count: 0`.

## Scope: what was read, what was run, what was not reached

**Read** from `origin/mail-desk-phase-1` in my own worktree (`../devthrottle-mail-desk-phase-review`, cut from that
branch at `cb7a0f957`), never the shared checkout:
`MailDeskGate.cs`, `MailDeskSwitch.cs`, `AdminMailDeskEndpoint.cs`, `MailDeskStart.cs`, `GatewayCaller.cs`,
`MailDeskWho.cs`, `MailDeskEndpoints.cs` (every line), the write paths of `MailDeskChannelStore.cs` and
`MailDeskRulesStore.cs`, the Mail Desk parts of `GatewayDbContext.cs`, both `AddMailDesk` migrations and the two
snapshot diffs, the diffs to `TriggerService`, `DueCheckService`, `TriggerEndpoints`, `FactoryDefinitionEndpoints`,
`GatewayHost`, `TenantSettingKeys`, `AuthMiddleware` and `SessionKeyGuard`, and in `tools/cc-devthrottle` the
`maildesk` wiring in `cli.py` and the reads and error path of `maildesk_ops.py`. The four piece reviews and the
phase README.

**Run** in that worktree, on devlinux (Linux):
- `dotnet test src/CcDirector.Gateway.UnitTests -p:EnableWindowsTargeting=true --filter 'Category=MailDesk|FullyQualifiedName~MailDesk'`:
  **262 passed, 0 failed, 0 skipped**.
- `tools/cc-devthrottle`: `python -m pytest tests/test_maildesk_ops.py -q` (uv, pytest, typer, rich, requests,
  cryptography, `FORCE_COLOR=1`): **95 passed**.

**Not run, on purpose:** the `-Parked` gate (the Tech Lead ran it; see the README), `scripts/test-database.ps1`
(the Tech Lead ran it: 79 of 79), anything on Windows or macOS, and anything against a real Gateway. I did not touch
the hosted Gateway. I did not read the store read paths, the rules validator or the channel fold line by line; their
piece reviews did.

## Observations (not findings: no harm proven)

- **The factory name `mail-desk` is now reserved for every account.** `MailDeskStart.Is` knows a Mail Desk report
  by the factory name `mail-desk` plus the check path `checks/screen.py`, whatever account it belongs to. If another
  account had already published its own factory with that exact name and that exact check path, its reports would
  now be refused 409 while its switch is off. That is the design (the switch is per account and the template owns
  the name), and the chance that such a factory exists is tiny. But I could not check production, because the
  brief keeps me off the hosted Gateway. The Delivery Lead may want one read-only query of the published factory
  definitions before the deploy to make this certain.
- **A wrong method shows the route exists.** For an account that is off, a request with a method no Mail Desk
  route takes (for example `PUT /gateway/mail-desk/status` from a person's device) is answered 405 by routing,
  before the gate's filter runs. So such an account can tell the path exists. Nothing is read or recorded, the
  route names are in a public repository anyway, and the factory agents gate behaves the same way. This is not harm
  to the grant.
- **Not proven by this phase, as its README already says:** Windows and macOS; the routes behind the real sign-in;
  any decision row actually written (phase 3 fills them); the template offer still has no caller (left to the
  Delivery Lead).
