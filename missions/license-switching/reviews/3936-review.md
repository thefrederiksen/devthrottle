# Review of pull request 3936 - the Licenses API writes

Reviewed at head `7579cfb43`, against its merge base with origin/main `30e468c18`. Reviewer seat; nothing in the
tree was edited, committed or pushed. Reproductions ran in a scratch project outside the repository.

## Verdict: changes needed

One must fix (finding 1), three should fix (findings 2 to 4), the rest are notes. Everything else I was asked to
check holds, and is listed under "What holds" with how I checked it.

## Findings

### 1. A Director's own device key can make both writes - must fix, or a written ruling that it may

`src/CcDirector.Gateway/Api/LicenseEndpoints.cs:165` and `:210` (the only caller check in each write is
`resolveTenant(ctx)`), mapped at `:58` and `:60`.

What is wrong: both writes accept ANY credential that resolves to a DevThrottle account and is not a session key.
That includes the device key a Director enrolled with (the "machine's own credential" of `cockpit-api.md` rule 1),
and on a self-hosted Gateway the shared machine token. Reproduced over real HTTP on a hosted host, with the key
`HostedTestEnrollment.Enroll` returns for the machine:

    PUT  gateway/licenses/accounts/renewal  -> 200, date stored
    POST gateway/licenses/switch-now        -> 200, one switch written

Why it matters: three places in this change say these are "the owner's own actions, from the Cockpit"
(`SessionKeyGuard.cs:367-371`, `GatewayHost.cs:5475-5478`, `cockpit-api.md` section 6 "Who may call the writes").
The session key guard only covers session keys. The Director's key lives on the same machines the agent sessions
run on. Other owner-only routes close this with one call that refuses a session key, a Director's key and the
shared token alike: `FleetManagerOwnerDevice.Require` (`Api/FleetManagerOwnerDevice.cs:24`), and the "people only"
checks in `PersonalMentorEndpoints` and `TeamRequestEndpoints`. Neither write uses it, and no test asks either
write with the machine's credential (the host tests ask with the owner's browser, a session key and no credential).

What I did not verify: whether an agent session can in practice read the Director's device key on its machine. If
the Tech Lead rules a machine credential may make these writes, the three comments and section 6 should say so and
a test should hold it.

### 2. A body with no `renewsOn` clears the stored date - should fix

`src/CcDirector.Gateway/Api/LicenseEndpoints.cs:173`; the request type at `LicenseCockpitDtos.cs:415`.

What is wrong: an absent property and an explicit `null` are the same thing to the handler. Reproduced: set
2026-10-17, then `PUT {"accountEmail":"account-a@example.com","renewOn":"2026-11-17"}` (one letter missing)
answers 200 `"renewsOn":null,"renewsText":"Renewal date not set"` and the stored date is gone.

Why it matters: the one thing a person sets on this page is erased by a misspelled field, and the answer is a 200.
`cockpit-api.md` says "or null to clear it"; it does not say that leaving the field out clears. Refuse a body that
does not carry `renewsOn` with 400 `invalid_body`, and add the case to the tests.

### 3. The owner email of contract 8.1 is not built, and nothing says so - should fix (in words)

`missions/license-switching/contract.md:551-554`, header at `:521`.

What is wrong: section 8.1 has two halves - the failed renewal is stored and shown on the page, and the owner gets
one email per holding per failure streak when the sign-in is within three days of expiring ("always seen twice").
This change builds the first half only. The description, the proof file and `cockpit-api.md` do not say the email
is still to come, and the section 8 header still reads "the Gateway side is not" built.

Why it matters: a reader of the contract after this merges will believe a failed renewal is emailed. Say in the
description and in the section 8 header which half is built. One thing for whoever builds the email: the store
keeps the failure TIME but drops a reason word it does not know (`LicenseStore.cs:201`), so the email rule must key
on `RenewalFailedAtUtc`, not on `RenewalError` as 8.1 is worded, or a failure with an unknown word is never emailed.

### 4. `accountEmail` has no length limit and is written to the log as typed - should fix

`src/CcDirector.Gateway/Licenses/LicenseStore.cs:213`; the same text is echoed in the refusal sentence of Switch now.

What is wrong: `SetRenewalDate` logs `account={email}` before it looks the account up. Reproduced: a 200,000
character `accountEmail` is accepted, logged whole, and answered 404; `"accountEmail":"x\ny"` to Switch now comes
back inside the sentence ("No machine on x\ny can be switched now"), and would put a line break in the log line of
the renewal route. The column is 320 characters.

Why it matters: one request writes a log line of any size, and a line break lets a caller write what looks like a
second log line. The caller is the signed-in owner, so this is small - but it is one comparison: refuse an
`accountEmail` or `targetAccountEmail` longer than 320 characters, or holding a control character, with 400.

### 5. While a Director is offline, every Switch now click writes another row - note

`src/CcDirector.Gateway/Licenses/LicenseSwitchService.cs:284-286`.

Reproduced with no Director connected: two requests at once and a third after answered 200, 200, 200; the log
holds three switch rows, two ended `failed` / `director-offline` and one open. Never two open rows for a machine,
so contract 5.4 holds. But the first answer of the two said "Waiting for the machine" for a row the second request
had already ended, and the page keeps saying `canSwitchNow: true` for a machine whose identical switch is already
waiting. This is what `switch-now-method.md` describes, so it is as designed; a repeat with the same source and
target could instead be answered with the waiting row. The page should disable the control while a call is out.

### 6. A barred account can be named as the target, and the page is told nothing about the bar - note

`src/CcDirector.Gateway/Licenses/LicenseDisplayFold.cs:243` and `:249`.

My brief lists "barred" among the named targets that are refused. It is not refused, by the ruling on pull request
3865 (finding 7 there: the bar stops the automatic choice only). Reproduced with account B barred: the page says
`canSwitchNow: true`, `switchNowTargetAccountEmail: null`, "No other account has room ... A target can still be
chosen by hand.", and B is `canChoose: true` with `unavailableText: null`. A call with no target is refused 409
`no-target`; a call naming B is accepted. The page and the route agree, provided the page sends the target it
offers. Two things follow for phase 3: the page must never send the default call when
`switchNowTargetAccountEmail` is null, and the 3865 ruling asked that the bar be shown beside the account - no
field carries it.

### 7. "Renews today" is the one sentence without the date - note

`src/CcDirector.Gateway/Licenses/LicenseDisplayFold.cs:812` and `:820`.

The date is compared with the Gateway's date in Coordinated Universal Time, so near midnight "today" and "has
passed" can be a day off for the viewer. The comment accepts that because "the date itself is always given" - true
of every sentence but "Renews today". "Renews today, 17 Oct" would make the comment true. The stored date itself
never moves: it is a `DateOnly`, a `date` column on PostgreSQL and "yyyy-MM-dd" text on SQLite, and the database
test reads it back as text.

### 8. The PostgreSQL migration test alters no existing switch log row - note

`src/CcDirector.Gateway.DatabaseTests/Data/AddLicenseRenewalPostgresTests.cs:82`.

The test stores an account before the migration, but no switch log row and no holding. The collation change on
`license_switch_log.FromAccountEmail` is the one step that touches existing data. I judge it safe by reading: no
index includes that column (`GatewayDbContext.cs`, the switch log's three indexes), and equality is the same under
"C" as under the default collation, so no row can change meaning. A row inserted before the migration and read
back after would make the test say so.

### 9. Entry and exit lines removed from two public reads - note, for the Tech Lead to confirm

`src/CcDirector.Gateway/Licenses/LicenseReadModel.cs:119`, `LicenseSnapshotSource.cs:46`.

`CLAUDE.md` rule 2 asks every public method to log entry and exit. Both now log only a failure, with the reason
written in place (read every minute for every account). I think the reason is good; it is a departure from a
written rule and should be a ruling, not only a comment.

### 10. What the proof claims that I did not rerun - note

The 41 mutations and the `-Parked` gate run were not repeated: a mutation edits tracked files, which this seat may
not do. No real-HTTP test asks either write with a RAISED session key (the unit test of the guard does).

## What holds, and how I checked

| Claim | Checked by | Result |
|---|---|---|
| 456 license unit tests pass | `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Licenses"` | 456 passed |
| Real-HTTP host tests pass | `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~LicenseHost"` | 13 passed |
| Database run green, 71 of 71 | `scripts\test-database.ps1` on a throwaway PostgreSQL | 71 passed, none skipped |
| Both providers agree with the model | `dotnet ef migrations has-pending-model-changes`, SQLite and `CC_GATEWAY_EF_PROVIDER=postgres` | no changes, both |
| Tenant from the credential, never the body | Read: `GatewayEndpoints.ResolveReadTenant`, as every other Cockpit route; the scope check at `LicenseEndpoints.cs:218` | holds |
| One account cannot write to another's | Reproduced over real HTTP: PUT for the other's account 404, POST 409, the other's date and log untouched | holds |
| Both writes refused to every session key | Read `SessionKeyGuard.cs:372-374` (three reads only) and the middleware; `LicenseGuardTests` raised and unraised; host tests 403 | holds |
| The owner's browser can call them | Host tests and my own run with a `browser` device key | holds |
| Switch now has one path | Read: the route calls only `LicenseSwitchService.SwitchNowAsync`; no other writer of a manual switch | holds |
| A refusal is 409, a word, a sentence, nothing written | Read: every refusal returns before the first write; tests assert no row and no send | holds |
| No second open switch for a machine | Read `Classify` and `CreatePendingSwitch`; reproduced with two requests at once | one open row |
| The request does not wait minutes | Read: sends start together, each waits at most 60 seconds | holds |
| Stale, full, not held, expiring targets refused | Read `PreviewManual`; tests | holds (barred: finding 6) |
| The page and the route decide on one picture | Read `SnapshotSwitchNowDecidesOn` against `Classify`; they differ only while a send is in flight | holds |
| Finished words from one fold | Read: `RenewsText`, `RenewalFailedText`, the tone and the Switch now sentences are all in `LicenseDisplayFold` | holds |
| Nonsense input is 400, never 500 | Reproduced: empty, `null`, `[]`, a number for the date, full-width digits, `text/plain`, an object for the email | all 400 |
| A failed renewal is stored as 8.1 says and cleared | Read `CopyReading`; store tests; the Director sends only its own fixed sentences (`LicenseUsageReporter.cs:296`) | holds |
| No other license route drops its answer | Read: the three reads are plain lambdas that return the result, and the host tests read their bodies | holds |
| `Down` works | The database test migrates down and up again | holds |
