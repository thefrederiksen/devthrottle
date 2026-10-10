# The factory email - design (Factory Control mission, step 4, part A)

Written 10 October 2026 against origin/main at `fe2aaccc3` (product) and `ddfe5ba2` (devthrottle_internal).
Nothing here is built or switched on. Part B builds it once step 1 has merged and the owner has approved the
mockups.

Mockups (each file IS the email body that would be sent):

- `morning-0800.html` - the 08:00 report. Screenshot at phone width: `morning-0800-phone.png`.
- `check-1600.html` - the 16:00 check, sent only when something went wrong. The midnight check is the same
  email for the evening shift. Screenshot: `check-1600-phone.png`.

## 1. The send path

### What exists today

| Path | Who composes | Who decides when | Who sends | Recipient | Works on |
|---|---|---|---|---|---|
| The 07:00 daily report (`website/api/morning-report.js`) | Gateway folds JSON (`GET /gateway/reports/morning`, `Reports/MorningReportBuilder`); the WEBSITE renders the HTML | Website, Vercel cron every hour, mails accounts whose own clock reads 07:00 | Website, through Resend | Website reads `GET /gateway/reports/recipients` plus its own opt-out column | Hosted Gateway only (`GATEWAY_REPORT_URL` names one Gateway) |
| Team invitations (`Teams/TeamInvitationMailer`, `Core/Account/TeamInvitationMailClient`) | Gateway | On request | Website mailer | A named invitee - an addressed send, deliberately narrow | Both |
| "Email the account owner", self-host (`Core/Account/AccountNotifyClient`, `POST /api/v1/account/notify-owner`) | Gateway (any subject and HTML) | Gateway | Website, through Resend | Resolved by the website FROM the account access token. No recipient field exists | Self-host only: hosted holds no account token |
| "Email the account owner", hosted (`Core/Account/AccountNotifyByTenantClient`, `POST /api/v1/account/notify-owner-by-tenant`) | Gateway | Gateway | Website, through Resend | Resolved by the website FROM THE TENANT id through `gateway.tenants`. A recipient field is a hard 400 | Hosted only; needs `NOTIFY_OWNER_SERVICE_TOKEN` on the Gateway |

`Api/AccountEmailEndpoint.cs` already chooses between the last two by the Gateway's mode (folded once in
`AccountActingCredential`), and `Api/NetDiagAlertService.cs` is an existing piece of Gateway code that emails the
owner on a timer through `AccountNotifyClient`. So "Gateway code that emails the owner without an agent" is a
pattern that already runs.

### Recommendation: the owner-email primitive, driven by a Gateway sweep

**The Gateway composes the whole email (subject and HTML), decides when to send it, and hands it to the
"email the account owner" primitive** - `AccountNotifyByTenantClient` on hosted, `AccountNotifyClient` on
self-host, chosen exactly as `AccountEmailEndpoint` chooses today (lift that choice into one small
`OwnerMailer` both callers use rather than copying it).

Why this one and not the 07:00 report path:

- **The owner ruled the Gateway checks and sends it, as plain code.** The 07:00 path puts the timing and the
  HTML in the website, in JavaScript; the Gateway only supplies data. That is two places to change for every
  wording fix and a renderer the Gateway's tests cannot see.
- **It is the only path that works on both Gateways.** The 07:00 cron reads one hosted Gateway by a fixed URL;
  a self-host Gateway is never asked. The owner-email primitive has a hosted and a self-host form already.
- **It cannot reach the wrong person.** Neither form accepts an address. The Gateway names a tenant (hosted)
  or presents the account's own token (self-host), and the website resolves the address. That is the
  property "never a fixed address" needs, and it is enforced by the shape, not by a check.
- **Its limits fit.** Three sends a day per account at most; the hosted route allows 30 per tenant per hour.

What the website keeps doing: nothing new. No website change is needed for part B, and the factory email is
separate from the 07:00 daily report (which keeps its own opt-out and its own rules).

### When it sends, and how each Gateway does it

A new `FactoryEmailSweep : TenantScopedSweep` (the base every per-tenant timer uses; self-host runs the body
once under the local tenant, hosted fans out over the tenant census). It wakes every few minutes and, per
tenant:

1. Works out the account's local time in its display time zone (the same `TimeZone(tenant)` source step 1's
   shift helper uses; Toronto is the owner's).
2. Finds the most recent of 08:00, 16:00 and 00:00 that has passed, and checks a per-tenant "last slot sent"
   record (a tenant setting, the way `DictationEmailCadenceState` records the dictionary email). If that slot
   is already recorded, it does nothing. This makes a restart, a deploy, or a missed wake safe: the slot is
   sent once, late rather than twice. A slot more than 2 hours old is recorded as skipped, not sent - a
   Gateway that was down all morning does not send the 08:00 report at 15:00.
3. Asks the fold (section 4) for the slot's content, renders it, sends it, and records the slot ONLY when the
   send succeeded. A failed send is logged with the website's reason verbatim and retried on the next wake,
   within the same 2-hour window.

- **Hosted Gateway**: `AccountNotifyByTenantClient.SendOwnerForTenantAsync(serviceToken, tenantId, accountSubject, subject, text, html, null)`. Needs
  `NOTIFY_OWNER_SERVICE_TOKEN` set on the hosted site. NOT CHECKED: whether it is set there today (part B
  checks it first; if it is missing the sweep must fail loud in the log and on the factory page, not skip
  quietly).
- **Self-host Gateway**: `AccountNotifyClient.SendOwnerAsync(token, subject, text, html, null)` with the token from
  `Account.GetAccessTokenForForwarding()`. If the Gateway is not signed in, the send fails with "not signed in"
  and the factory page shows it, the same answer `NetDiagAlertService` gives.

Daylight saving: slots are computed in local time, so 08:00 stays 08:00 across the change. On the autumn day
the 00:00 slot still happens once; the sweep's "last slot sent" key is the local date plus the slot, so no
slot is sent twice.

## 2. The recipient

**What the registry records.** `FactoryRegistryEntity.RegisteredBy` is a free-text string: `session <id>` or
`the owner (<credential>)`. It is overwritten on every re-registration. It is not an account, so it cannot be
resolved to an email address. Nothing else on a factory names an account.

**What does resolve.** A factory row belongs to exactly one tenant, and the owner-email primitive resolves a
tenant to an address:

- A personal account's tenant has a row in `tenants` (`TenantRegistry.EmailForTenant`, and the website's
  `gateway.tenants` lookup). For a personal account, the account that set up the factory IS that tenant's
  account - no one else can register a factory there. **So for every personal account, sending to the tenant
  owner is exactly the owner's rule, and it works today with no new field.** All of the owner's factories are
  in his personal account (not checked one by one; part B verifies before switching on).
- **A team is also a tenant, but has no row in `tenants`** (`Teams/TeamRegistry.cs`: team rows live in the
  team tables). The hosted primitive would answer `tenant_not_found` for a team factory, and even if it
  resolved the team's Owner, that may not be the member who set the factory up.

**What is missing, and the recommendation.**

1. Record the account that set the factory up, once, at its first registration: a new
   `SetUpByAccountSubject` column on the registry row, taken from the calling credential's account, NOT
   overwritten when the factory is registered again (a session re-registering it must not move the email).
   Existing rows are backfilled with their tenant's account subject - for personal tenants that is exact.
2. For part B, send only for factories in a personal tenant, which is every factory that exists today. A
   factory in a team tenant is shown on its factory page as "the factory email is not available for team
   factories yet" - stated, not silently skipped.
3. Team factories need a website change (a "notify a member of this team" form of the primitive, still with no
   address field, resolving `SetUpByAccountSubject` against the team's membership). That is its own issue,
   raised in part B, not built here.

If one account set up several factories, they get ONE email covering all of them - one 08:00 email per
account, never one per factory.

## 3. What the email says

### 08:00 report - every day, even when all is clear

- **Subject**: `Factories: 3 problems still open - Saturday 11 October`. When there are none:
  `Factories: all clear - Saturday 11 October`.
- **Still not fixed**: every open problem from the problems fold, whenever it started (a problem from two days
  ago that is still open is listed every morning, by the owner's ruling). Each shows: factory (a link to its
  factory page, `#problems`), seat role word and schedule name, what went wrong in one line, when it started
  (or was due), its shift, and how long it has been open. Oldest-open last, newest first.
  A problem that was resolved, or cleared by a later successful run, is NOT shown - not even as "fixed
  yesterday".
- **Last 24 hours**: one row per factory of the account: runs done, runs that did not run, seats left open.
  Factories with a problem are links; others are plain text. A factory with no schedules is left out.
- **Footer**: read-only notice, how a problem leaves the email, the send time and zone.
- No buttons. The only links are factory names.

### 16:00 and midnight checks - only when something went wrong

- Sent only when the shift that just ended raised at least one problem that is still open at the check
  (morning shift for 16:00, evening shift for midnight). Problems from earlier shifts are not repeated - they
  are one line, "Also still open from before: N problems", because they are already in the 08:00 report.
- **Subject**: `Factories: 1 new problem in the morning shift - Saturday 11 October`.
- The night shift has no separate check: the 08:00 report covers it.

This is my reading of "the same check, sent only when it finds something": if it repeated every open problem,
a single unresolved problem would send three emails a day for as long as it stayed open. Raised with the
Implementation Lead to confirm with the owner when the mockups go for approval.

### Plain email-safe HTML

Inline styles and tables only, no scripts, no web fonts, no images, ASCII text. The palette is the 07:00 daily
report's (`#0B1628` header, `#16181D` text, `#5A616B` muted, `#E6E8EC` rules) so both emails look like one
product, with `#B42318` for problems. Width capped at 560 pixels, single column, so it reads the same on a
phone and a desktop. The links use `GatewayPublicUrl.ResolveBase()` - the same base the dictionary email uses,
because the Cockpit's router is mounted at the root (`{base}/factories/<factory>`). A self-host Gateway with no
reachable public address renders the factory name without a link and says "open DevThrottle, Factories" in the
footer, the way `SuggestionEmailComposer` already handles it.

## 4. The data it reads

The email does no ruling of its own: it renders one Gateway fold, the same way every client does
(CLAUDE.md rule 7). Part B adds `FactoryEmailFold.Compose(tenant, slot, nowUtc)` returning a finished DTO,
and a renderer that turns it into HTML. The fold reads two sources.

### From the open-problems fold (Developer brief 1, item 6)

The email needs, per open problem, everything already in brief 1's list - and these specific things:

| Field | Why the email needs it |
|---|---|
| problem id | to count and to de-duplicate across the 16:00 and 08:00 emails |
| factory id and title, or none | grouping and the link; a problem with no factory goes in an "Not in a factory" group |
| seat role word and schedule name | the "Sender - Morning send" line. Role word, never a person's name |
| kind (`problem`, `did-not-report`, `ran-past-shift`, `did-not-run`) and the one-line reason | the red line. The fold should also supply the finished sentence ("Did not run: its machine was offline when it was due.") so every surface says it the same way |
| started time, or due time for `did-not-run` | "Started Fri 10 Oct 09:00" / "Due Sat 11 Oct 02:00" |
| shift of that time | "morning shift" - from brief 1's shift helper |
| when the problem was raised | to decide whether it is NEW in the shift a 16:00 or midnight check covers |
| resolved or cleared, and when | open problems only are listed; resolved/cleared ones are excluded by the fold, not by the email |

Ask of brief 1: the fold should take an "as of" instant and a tenant, and expose `RaisedAtUtc` on each problem.
Everything else in the list above is already in its brief.

### From the run record, per factory, for the last 24 hours

Not in brief 1's fold, so part B adds a small per-factory count over `cron_runs` (with the schedule-to-factory
link in `Factory/Registry`) for the window `[slot - 24h, slot)`:

| Count | Defined as |
|---|---|
| runs done | runs that started in the window and recorded result `ok` |
| did not run | runs recorded with problem kind `did-not-run`, due in the window |
| left open | the factory's seats whose run session is still open now (`CronRunEndings.StillOpen`), whatever the run's age - the same fact step 2's "stayed open" marker shows on the Factories list |

The headline totals ("4 factories, 19 runs done, 1 run did not run") are sums of those rows, computed in the
fold.

### Part B, then, is

`OwnerMailer` (lift the existing hosted/self-host choice), `FactoryEmailFold` plus its counts, an HTML
renderer matching these mockups, `FactoryEmailSweep` with the last-slot record, the `SetUpByAccountSubject`
column and backfill, and tests: one email per slot across a restart, nothing at 16:00 when the shift was
clean, a resolved problem absent next morning, a problem still listed on the second morning, Toronto
daylight-saving days, a hosted send with no service token failing loud, a team factory stated rather than
skipped.

## What I did not check

- Whether `NOTIFY_OWNER_SERVICE_TOKEN` is set on the hosted Gateway today.
- That every one of the owner's factories is in his personal tenant (I believe so; part B verifies).
- Any email client rendering beyond a 375-pixel-wide browser frame (no Outlook or Gmail test send - nothing
  was sent).
