# Phase 6, Track A, piece A1 - the license alarm

## Why

On 15 and 16 October a machine without a spare blocked every switch, and the one email that said so was sent once and
forgotten. A problem with licenses must be LOUD and must STAY until it is fixed. Three tracks raise such problems, so
there is exactly ONE alarm mechanism, and this is it. How another track raises one is in `contract.md` section 9; the
read route is in `cockpit-api.md` section 8.

## What was built

- **`Licenses/LicenseAlarmService.cs`** - the one mechanism. A check calls `RaiseAsync(tenant, kind, alarmsTrueNow)`
  with every alarm of its kind that is true now. New ones open (a row in `license_alarms`, an `alarm-opened` row in the
  Licenses log, one push, one owner email); ones still open stay open and send nothing; open ones no longer in the list
  close by themselves (an `alarm-closed` row in the Licenses log). The identity is tenant + kind + machine + account.
- **`Licenses/LicenseAlarmFold.cs`** - every word: the banner's lines and summary, the push sentence, the email, the
  log rows.
- **`Push/LicenseAlarmPushNotifier.cs`** - the push, over the existing Web Push transport and subscription store, in the
  tenant's own scope.
- **`GET /gateway/licenses/alarms`** - the open alarms with the banner's finished words. A read only; there is no route
  that raises or clears an alarm. In the session key guard's read list.
- **The red banner** - `packages/client-core/src/licenses/LicenseAlarmBanner.tsx`, mounted once in the Cockpit's frame
  (`AppShell.tsx`, above the routed page) and once in the phone's shell (`LicenseAlarmBar.tsx` in the gated layout). It
  renders the Gateway's words verbatim and draws nothing when no alarm is open.
- **The minute check** - an alarm whose owner email failed is tried again on the Gateway's once-a-minute license check.
- One new table, `license_alarms`, with its migration for SQLite and for Postgres.

### Choices made, and why

- **A table of its own, not more rows of the switch log.** The brief asked to reuse the no-room / renewal-failed
  pattern, or their table if it fits cleanly. The pattern is reused exactly (an open row is what makes the email go out
  once; the email state is stored and never changed back once sent; the same growing retry wait; the same
  remember-a-confirmed-send rule). The table did not fit cleanly: an alarm has two sentences, a kind word longer than
  the log's 16-character reason column, and an optional machine and account, and the log's columns would have had to
  be borrowed for meanings they do not have. The Licenses log still gets a row when an alarm opens and when it closes.
- **The push opens the Cockpit's Licenses page (`/licenses`).** The phone app has no Licenses screen of its own. The
  Cockpit page is served by the same Gateway at the site root and works at phone width, so it is the closest existing
  screen. The phone banner's link goes to the same page.
- **The push words come from the code, not from the check's sentences.** A kind is declared once with a short title
  ("Not ready to switch"); the push says "machine-a: Not ready to switch. Open Licenses to see what to do." The title is
  refused if it contains an email address, so no account address can reach a locked screen.
- **One email per check, not one per alarm.** Several alarms that open in the same minute (one cause often trips
  several machines) go in one email, as the failed-renewal email already does, so the channel is not taught to be
  ignored.
- **The push is sent once and not retried.** The banner and the email are the alarm's lasting voice; a push that
  failed is logged.

## The tests, in plain terms

Gateway, `src/CcDirector.Gateway.UnitTests/Licenses/`:

`LicenseAlarmServiceTests` (the real database on a throwaway SQLite file; the email and the push are recorders):
- **A new alarm** opens, has an `alarm-opened` row in the Licenses log with the exact sentence, is marked emailed, and
  exactly one email and one push went out.
- **The same alarm raised again** on the next tick sends nothing more; it keeps its opened time and only its sentence
  changes.
- **An alarm no longer raised** closes by itself and writes an `alarm-closed` row with the exact sentence; raised later,
  it is a NEW alarm (a new id), with a second email and a second push. The log reads opened, closed, opened.
- **A failed email** is marked failed, is NOT retried inside its one-minute wait, IS sent at one minute and marked
  emailed with that time, and is never sent again - not by a later check, not by raising the alarm again.
- **A sender that throws** still leaves the alarm open, with its email owed.
- **A push that fails** still leaves the alarm open and emailed.
- **Two alarms opening together** go in one email whose subject is pinned, and each is pushed.
- **One kind never closes another**: clearing `not-ready-at-80` leaves a `stopped-reporting` alarm on the same machine
  open.
- **Two tenants** never see each other's alarms, and one tenant clearing its alarms leaves the other's open.
- **An account in another case** ("Alice@Example.com ") is the same alarm, not a second one.
- **Bad input is refused** and changes nothing: a sentence with no full stop, an empty action, a sentence on two lines,
  two conditions with one identity, a machine id with no name, a kind word that is not lower-case words with hyphens,
  and a title that holds an email address.

`LicenseAlarmFoldTests` (the words, pinned):
- **The banner** for no alarm (count 0, no words at all), one alarm (one line "problem, then the one thing to do", no
  summary, link "Open Licenses"), and three alarms (the summary "3 license problems need you." and the three lines in
  order; an alarm about no machine or no account says null, never an empty name).
- **The push sentence** with and without a machine, and **the whole push message** as the phone reads it - its five
  fields, the tag per alarm, the `/licenses` target - with no `@` anywhere in it.
- **The email** for one alarm, subject and body word for word, and the subject of an alarm about no one machine.
- **The Licenses log** shows the two alarm rows as "Alarm opened" and "Alarm closed", with the row's sentence as the
  summary, and neither reads as open.

`LicenseAlarmsEndpointTests`: the route answers the caller's own alarms in the banner's words and an empty list to
another account; a request with no account bound is refused with 403 `no_account`.

`LicenseGuardTests`: a session key may GET `/gateway/licenses/alarms` and may not POST, PUT or DELETE it.

Also in `LicenseAlarmServiceTests`, after the review: the minute check's own method (`TickEveryTenantAsync`) retries a
failed email for each tenant in its own scope, skips a pass with no tenant in scope, and sends nothing more once each
email has gone out; and a kind title ending in a full stop followed by a space is refused.

Postgres, `src/CcDirector.Gateway.DatabaseTests/Data/` (run by `scripts\test-database.ps1` against a throwaway
PostgreSQL in Docker):
- `AddLicenseAlarmsPostgresTests` (added after the review): the migration applies right after the one before it and
  leaves a Licenses log row stored earlier untouched; the real service then opens an alarm (its email time lands in the
  `timestamp with time zone` column as stored), finds it again when the account is written in another case, closes it
  (the log holds the opened and closed rows beside the earlier one), and opens a new one with a new email; Down removes
  the table and keeps every log row; migrating up again restores it.
- `PostgresProviderProofTests` lists the two exact-key columns of `license_alarms` (kind and account), declared with the
  byte-ordinal collation. It also now lists the three `api_mode_receipts` columns, which were declared that way on main
  and missing from the list, so this test was already failing on main and could not reach the new columns.

Web:
- **Cockpit** (`apps/cockpit/src/AppShell.test.tsx`, "Cockpit license alarm banner"): no banner when the Gateway has
  no alarm open; one alarm shows exactly the Gateway's line and the link "Open Licenses" to `/licenses`, as an alert,
  inside the main pane and not in the rail; several show the summary and every line in order.
- **Phone** (`apps/mobile/src/components/licenseAlarmBar.test.tsx`): no banner with no alarm; one alarm shows the
  Gateway's line and a link to `/licenses`.
- **Phone push handler** (`apps/mobile/src/push/pushSw.test.ts`): a license alarm push draws its own audible
  notification in the Gateway's words, leaves the "needs you" dot alone, and a tap steers the open app to `/licenses`.

## The screenshots

Taken by `layout_check.py` in this folder: the Cockpit and the phone app on their own development servers, with
`GET /gateway/licenses/alarms` answered by example answers in the pinned words, and every other Gateway call refused
with 503 (so the pages behind the banner show their own "not part of this layout check" errors - that is expected and
not part of what is checked). Made-up names only.

- `cockpit-history-one-alarm.png` - the History page with one alarm: one red line and the "Open Licenses" link, above
  the page, in the main pane.
- `cockpit-directors-several-alarms.png` - a different page, Directors, with three alarms: the summary line and three
  lines.
- `cockpit-history-no-alarm.png` - the History page with no alarm open: no banner. The script fails if one is drawn.
- `phone-home-one-alarm.png` - the phone's home screen, 390 pixels wide, with one alarm, pinned at the top above the
  app bar.
- `phone-settings-several-alarms.png` - the phone's Settings screen with three alarms.

## How it was proved

- `.\scripts\test-local.ps1` and `.\scripts\test-local.ps1 -Parked` - results in the pull request.
- The Cockpit, phone and client-core web suites, run in full, and `npm run typecheck`.
- `python missions/license-switching/phase-6/track-a/alarm/layout_check.py` for the screenshots.

## Review findings and answers

The review is `review.md` in this folder (a separate review session, at commit 33d00d817). All four findings accepted.

1. **The Postgres migration had no test of its own (should-fix) - accepted, fixed.** Added
   `AddLicenseAlarmsPostgresTests`, the sibling of the earlier License Switching migration tests, described above. Run
   under `scripts\test-database.ps1` with the collation proof: 7 executed, 7 passed, none skipped. Running it found that
   `PostgresProviderProofTests` was already red on main (three `api_mode_receipts` columns missing from its list); those
   three lines are added so the proof covers the whole list, including the new columns.
2. **The Cockpit banner was off while a team's pages were on screen (should-fix) - accepted, fixed.** The banner is now
   enabled whenever the shell is not still learning which team is on screen (`enabled={!team.resolving}`), so the
   email's promise "shown in red at the top of every page of the Cockpit" is true. New Cockpit test "still shows the
   banner while a team's pages are on screen"; it fails with the old condition and passes with the new one.
3. **The kind's title was checked before it was trimmed (note) - accepted, fixed.** Trimmed first, then checked. New
   test: "Not ready. " is refused.
4. **The minute tick's retry was proven at the service, not through the tick (note) - accepted, fixed.** New test
   drives `TickEveryTenantAsync` with a two-tenant pass (and a pass with no tenant in scope) and shows each tenant's
   failed email is sent once, in its own scope, and never again. The line in `GatewayHost` that calls it each minute is
   still covered only by reading it.
