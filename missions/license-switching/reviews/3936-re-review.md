# Re-review of pull request 3936 - the Licenses API writes

Reviewed at head `d79b73497` (the code is `8d9da5d39`, rebased onto main `8a62ffa29`), against the answers in
`3936-answers.md`. Reviewer seat; nothing in the tree was edited, committed or pushed. Reproductions ran in a
scratch project outside the repository, over real HTTP against a started Gateway.

## Verdict: approve

Every round 1 finding is answered and the answer holds. The one must fix is closed: I could not make either write
with anything but the owner's own signed-in browser. There are seven new findings; one is a should fix in words
(finding 1, the document's examples), the rest are notes. None of them needs another round of review.

## The round 1 findings

| | Round 1 finding | Verdict | How I checked |
|---|---|---|---|
| 1 | A Director's own device key can make both writes | Closed | Reproduced, see the table below |
| 2 | A body with no `renewsOn` clears the stored date | Closed | Sixteen bodies that do not name it, or are not the request, each 400 and the date kept; only an explicit null cleared it |
| 3 | The owner email of 8.1 is not built and nothing says so | Closed | Read `contract.md` section 8 and 8.1, `cockpit-api.md` section 6; the store keeps `not-known` and the time (`LicenseStore.cs:202-204`), and the fold shows no word for it |
| 4 | `accountEmail` has no limit and is logged as typed | Closed, with new finding 4 | 320 characters accepted, 321 and 200,000 refused 400; line feed, carriage return, null, tab, delete, escape and the C1 "next line" refused 400, on both routes and on the target |
| 5 | Every click writes another row while a Director is offline | Closed, with new finding 2 | Reproduced, see below |
| 6 | The page is told nothing about a bar | Closed, with new finding 1 | The two keys are the last two of every account, null when not barred, and another DevThrottle account sees none of it |
| 7 | "Renews today" lacks the date | Closed | Read `LicenseDisplayFold.cs:836`; the test |
| 8 | The PostgreSQL test alters no existing switch log row | Closed by reading | The test now stores a machine, a holding and a switch log row before the migration and reads them after, and after the Down. I did not rerun `scripts\test-database.ps1` |
| 9 | Entry and exit lines removed from two reads | Closed | Ruled, and both comments say so |
| 10 | No real-HTTP test with a raised session key | Closed | The host test raises through the real route; my own run did the same |

### Finding 1, reproduced: who can make the two writes

| Caller | `PUT` renewal | `POST` switch-now |
|---|---|---|
| The Director's own device key, as a bearer | 403 `owner_only` | 403 `owner_only` |
| The Director's own device key, as the cookie | 403 | 403 |
| A session key the owner raised through the real raise route | 403 | 403 |
| A session key not raised | 403 | 403 |
| A raised session key, as the cookie | 401 | 401 |
| The shared machine token, on a hosted Gateway | 401 | 401 |
| The shared machine token, on a self-hosted Gateway, as a bearer | 403 `owner_only` | 403 `owner_only` |
| The shared machine token, on a self-hosted Gateway, as the cookie | 403 `owner_only` | 403 `owner_only` |
| No credential | 401 | 401 |
| The owner's browser key, as a bearer | 200 | 200 |
| The owner's browser key, as the cookie | 200 | - |

After all the refused calls no date was stored and no switch row existed. One account still cannot write to
another's: `PUT` for the other's account 404, `POST` 409, the other's log empty.

### Finding 5, reproduced: the same Switch now again, and a different one

With no Director connected: Switch now A to B wrote one row. Three more at the same moment - B named, B named in
capitals, and no target (the Gateway chose B) - each answered 200 with the SAME switch identifier; the log still
held that one row, `pending` / `director-offline`, never sent. Then a different request, A to E: 200, a NEW
identifier, the answer names E, the B row ended `failed`, one open row and it is to E. Then A to B again: a third
identifier, the E row ended. A request to another target is never answered with the waiting row, and the expiry
test is the minute check's own (`now <= NotAfterUtc`, `LicenseSwitchService.cs:294` and `:452`).

## New findings

### 1. The three example answers do not carry the two new keys - should fix (in words)

`missions/license-switching/cockpit-api.md:234`, `:315`, `:377`.

What is wrong: `barredUntilUtc` and `barredText` are in the field table (`:95`), but each of the three whole example
accounts in section 2 still ends at `switchTargets`. The real answer ends `switchTargets`, `barredUntilUtc`,
`barredText` on every account, null when not barred (reproduced).

Why it matters: phase 3 builds to these examples, and the document says its field names are final. Add the two
keys as null to the three examples, and one example of a barred account.

### 2. The same Switch now again sends nothing, also when the Director has come back - note

`src/CcDirector.Gateway/Licenses/LicenseSwitchService.cs:288-301`. By reading, not reproduced.

The waiting switch is left for the minute check whether or not its Director is connected now. So an owner who
clicks again just after the machine came back is answered "pending", with the waiting row's sentence that the
Director was not connected, and the switch goes up to a minute later. Before this change that click sent at once.
It mends itself and it is what the ruling asked for. If it is wanted: send the waiting row again under its own
identifier, as the minute check does, in place of leaving it.

### 3. The two writes no longer look at the content type - note

`src/CcDirector.Gateway/Api/LicenseEndpoints.cs:292`.

The body is now read as text and parsed, so `text/plain` holding the JSON is accepted (reproduced: 200, the date
cleared; in round 1 this was 400). A browser may send `text/plain` to another site without asking first, and the
owner's key is accepted as a cookie on both writes. I see no way to use that: the cookie is `SameSite=Lax`
(`Api/GatewayTokenCookie.cs:43`), which a cross-site `POST` or `PUT` does not carry. Refusing a body that is not
`application/json` would keep the second lock the first version had. Smaller: the body is read whole before the
owner check, so a refused caller's body is still read.

### 4. Three characters that break a line or reverse text pass the control character check - note

`src/CcDirector.Gateway/Api/LicenseEndpoints.cs:282`.

`char.IsControl` does not cover the line separator (U+2028), the paragraph separator (U+2029) or the right-to-left
override (U+202E). Reproduced: each is accepted as an `accountEmail` and as a target, answered 404 or 409, and so
is logged and said back in the refusal sentence. The caller can only be the signed-in owner. Refusing the format
and separator categories as well would close it.

### 5. A person who opened a self-hosted Cockpit with the machine token cannot use either write - note

`missions/license-switching/cockpit-api.md:1045`.

On a self-hosted Gateway the login page stores the shared machine token as the cookie. With that cookie the page
reads the licenses and is told `canSwitchNow: true`, and both writes answer 403 `owner_only` (reproduced). This is
the same rule the Fleet Manager's owner-only routes have, so it is consistent; it should be one sentence in section
6, and phase 3 must show the sentence of a 403 `owner_only` and not treat it as an error of the page.

### 6. A comment landed on the wrong member - note

`src/CcDirector.Gateway/Licenses/LicenseStore.cs:208-222`.

The new constant `RenewalErrorNotKnown` was put between `SetRenewalDate` and its summary. The constant now has two
summaries, the first of them about setting a date, and `SetRenewalDate` has none. Also
`Data/Entities/LicenseEntities.cs:133` still says the column holds a word of the contract; it can now hold
`not-known`.

### 7. The filter in my brief does not run the self-hosted test - note

`--filter "FullyQualifiedName~LicenseHost"` matches `LicenseHostTests` only (17 tests). The test that holds the
shared machine token row, `LicenseWritesSelfHostedTests`, is not matched; I ran it by name. Whoever gates this
change with that filter should add it.

## What was run

| Run | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Licenses"` | 479 passed, 0 failed |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~LicenseHost\|FullyQualifiedName~LicenseWritesSelfHosted"` | 18 passed, 0 failed (17 and 1) |
| My six reproductions over real HTTP, out of tree | all ran; results above |
| `dotnet ef migrations has-pending-model-changes`, SQLite | no changes |
| The same with `CC_GATEWAY_EF_PROVIDER=postgres` and the PostgreSQL migrations project | no changes |
| Migration order after the rebase | `AddLicenseRenewal` is the last migration of both providers |

Not rerun by me: `scripts\test-database.ps1`, the default gate, the whole Gateway unit suite, and the eighteen
mutations of this round (a mutation edits tracked files, which this seat may not do).

## Other things looked at, with nothing to report

- A repeated `renewsOn` in one body: the last one wins, as the parser reads it. A date after a null stores the
  date; a null after a date clears. Either way the body named the field.
- `renewsOn` given as a number, `false`, an object, empty text, or with text after the closing brace: 400, date kept.
  A body that starts with a byte order mark is read as usual.
- Two bars on one account: the page gives the one that lasts longest, with its own window in the sentence.
- A waiting switch written by the minute check with the same source and target is answered the same way as one
  written by a click; the comparison is on the machine's row, so a row for another source account is never matched.
