# Worker report - phase 3b, the Gateway: one link that lands where you are, and the way back named for a human

Issue #3025 (child of #2936). Branch `mission/dev-reports-p3b-gateway`, worktree
`D:\ReposFred\devthrottle-p3b-gateway`. Nothing here was merged anywhere; the Manager merges the branch in.

---

## What changed

Four files of product code and two test files.

### C. `/r/{reportId}` - one address, routed by device, landing inside the report

**New: `src/CcDirector.Gateway/Api/DevReportLinkRoute.cs`.**

`GET|HEAD /r/{reportId}` resolves the report in the CALLER'S OWN account and 302s to the app for this device:

| device | where it lands |
| --- | --- |
| a phone User-Agent (`MobileRedirect.IsPhoneUserAgent`) | `/mobile/session/{sessionId}/reports/{reportId}` |
| anything else | `/session/{sessionId}?tab=reports&report={reportId}` |

- Both identifiers are `Uri.EscapeDataString`-escaped into the path and the query.
- The device decision reuses `MobileRedirect.IsPhoneUserAgent`, so this server has ONE phone/desktop policy
  and not two that can disagree.
- The Cockpit target is exactly the route the brief named, which is what the Cockpit Worker is building. It
  was not changed, and nothing here diverged from it.
- A report that is not in this account is `404` with the sentence `There is no dev report {id} here.` -
  never a redirect to a guess. Another account's report gets that same 404, so its existence does not leak.
- No account bound to the request is `403` with `No account is bound to this request.` - the same sentence
  `DevReportEndpoints` uses. That is a different condition from "not found" and it says so.

**It is MIDDLEWARE, not a mapped endpoint, and that is the whole route working on a phone.**
`MobileRedirect.UseMobileRedirect` sends EVERY phone HTML navigation to `/mobile/`, and it runs long before
the endpoint middleware at the end of the pipeline. A mapped route would never be reached by a phone: every
printed link would land on the mobile home screen. So `GatewayHost` registers it immediately BEFORE the
mobile front door, and `LinkRoute_PhoneNavigation_AnswersBeforeTheMobileFrontDoor` fails the moment a later
edit reorders the two.

**Signed out: nothing new was built.** `AuthMiddleware` already runs first and redirects a signed-out HTML
navigation to `/signin?next=<the requested route>`. `/r/{id}` is not on the public allow list, so it is
already covered: the round trip comes back to `/r/{id}` itself and THEN routes by device. There is
deliberately no second sign-in path, and the test asserts the exact `next` value rather than its presence.

### D. The way back, named for a human - the Gateway supplies the words

**New: `src/CcDirector.Gateway/DevReports/DevReportSessionLabel.cs`** - a pure fold, plus the record
`DevReportSessionNaming(int? Number, string? Name)`.

Every report record - the LIST (`GET /dev-reports`, `GET /sessions/{sid}/dev-reports`) and the DETAIL
(`GET /dev-reports/{id}`, `GET /sessions/{sid}/dev-reports/{id}`) and the publish answer - now carries two
new finished strings beside `title` and `sessionEnded`. Clients render them verbatim (repository rule 7).

**The exact shape, as JSON on the report object:**

```json
{
  "id": "…", "sessionId": "…", "key": "…", "title": "…", "status": "…",
  "version": 1, "publishedAtUtc": "…", "updatedAtUtc": "…",
  "sessionEnded": false, "openItems": 0,
  "sessionLabel": "121 devthrottle - tool not working on linux",
  "backLabel": "back to 121 devthrottle - tool not working on linux"
}
```

Both are plain strings, never null, never absent.

**The fold's rules, and they are the whole job:**

| what the Gateway knows | `sessionLabel` | `backLabel` |
| --- | --- | --- |
| number and name | `121 devthrottle - tool not working on linux` | `back to ` + that |
| number only | `121` | `back to 121` |
| name only | `the gateway worker` | `back to the gateway worker` |
| neither | `the session` | `back to the session` |

- **NO INTERNAL IDENTIFIER, BY CONSTRUCTION.** The fold is handed a number and a name and nothing else - it
  is never given a session id, so it cannot emit one. When it knows neither it says one plain true sentence
  rather than a ten-character hexadecimal string. This is asserted, not assumed: the unit tests and the
  hosted test both check the finished strings for the session id and for its first eight characters.
- The name is whitespace-collapsed to one line and cut at `DevReportTitle.MaxLength` (200) - the existing
  convention reused, not a second one invented.
- The number is rendered as the Gateway holds it. The allocator's band is 100-999, so it is normally three
  digits already; a number outside the band is printed as it is and never zero-padded into a shape the
  allocator never issued.

**The lookup: `GatewayHost.DevReportSessionNaming`**, written immediately beside `DevReportSessionLiveness`
and answering the same way - `PushedSessions.TryLocate` first (`SessionDto.Number` / `SessionDto.Name`),
then `_sessionHistory.Get` inside the tenant scope (`WorkHistorySessionDto.SessionNumber` / `SessionName`).
It hands back facts only; the fold stays pure and is testable with no host. `DevReportEndpoints.Map` takes it
as a `Func<TenantId, string, DevReportSessionNaming>` and calls it ONCE PER SESSION per answer, not once per
report.

### Files touched

| file | what |
| --- | --- |
| `src/CcDirector.Gateway/Api/DevReportLinkRoute.cs` | new - the `/r/{id}` route |
| `src/CcDirector.Gateway/DevReports/DevReportSessionLabel.cs` | new - the fold and `DevReportSessionNaming` |
| `src/CcDirector.Gateway/Api/DevReportEndpoints.cs` | `Map` takes the naming lookup; both labels on the record |
| `src/CcDirector.Gateway/GatewayHost.cs` | registers the route before the mobile front door; the roster-then-history lookup |
| `src/CcDirector.Gateway.Tests/DevReportLinkRouteTests.cs` | new - the hosted route and record tests |
| `src/CcDirector.Gateway.UnitTests/DevReports/DevReportSessionLabelTests.cs` | new - the fold |
| `src/CcDirector.Gateway.UnitTests/Fleet/FleetManagerEventServiceTests.cs` | a join break fixed - see below |

**One repair outside the phase's subject.** `CcDirector.Gateway.UnitTests` DID NOT COMPILE on `origin/main`
when this work started: #3001 added `FleetManagerEventServiceTests` and #3018 added a required
`narrationPlan` parameter to `GatewayTurnVerdictEnvironment`, and neither pull request saw the other. Both
commits are ancestors of `origin/main`, so this was not introduced here. One line added,
`narrationPlan: _ => NarrationPlan.Allowed`, the value every other wingman test uses; nothing in that test
reads it. Without it no unit test in the project could run at all.

---

## The tests

### `DevReportSessionLabelTests` (Gateway.UnitTests, 12 tests) - the fold

| test | what it holds down |
| --- | --- |
| `Session_NumberAndName_ReadsAsTheOwnerWouldSayIt` | `121 devthrottle - tool not working on linux` |
| `Back_NumberAndName_IsTheSameWordsWithTheWayBackInFront` | `back to ` + the same words |
| `Session_NameUnknown_IsTheNumberAlone` | the number alone, for null and for whitespace |
| `Session_NumberUnknown_IsTheNameAlone` | the name alone - say what you honestly know |
| `Session_NeitherKnown_IsOnePlainTrueSentence` | `the session` / `back to the session` |
| `Session_WhateverIsKnown_NeverContainsASessionIdentifier` (4 cases) | no id and no 8-character prefix, in every state of knowledge |
| `Session_NameWithNewlinesAndRuns_IsOneLineWithSingleSpaces` | one line, single spaces |
| `Session_NameLongerThanTheCeiling_IsCutAtTheTitleCeiling` | cut at `DevReportTitle.MaxLength` |
| `Session_NumberOutsideTheAllocatorsBand_IsRenderedAsTheGatewayHoldsIt` | `7`, not `007` |

### `DevReportLinkRouteTests` (Gateway.Tests, PARKED) - the route and the record

Pure policy, no host: `Target_PhoneUserAgent_…`, `Target_DesktopUserAgent_…`,
`Target_IdentifiersNeedingEscaping_AreEscapedIntoThePathAndQuery`,
`ReadReportId_OnlyMatchesOneSegmentUnderTheLinkPrefix` (6 cases).

On a real hosted Gateway, over HTTP, with real device keys and a Director really on the tunnel:

| test | the brief's proof |
| --- | --- |
| `LinkRoute_PhoneUserAgent_RedirectsToThePhoneReportScreenWithBothIdentifiers` | 1 |
| `LinkRoute_DesktopUserAgent_RedirectsToTheCockpitReportsTabWithTheReportOpen` | 2 |
| `LinkRoute_PhoneNavigation_AnswersBeforeTheMobileFrontDoor` | 3 |
| `LinkRoute_UnknownReportId_IsA404WithASentenceAndNoRedirect` | 4 |
| `LinkRoute_ReportOfAnotherAccount_IsTheSame404AndDoesNotLeakThatItExists` | 5 |
| `LinkRoute_SignedOutHtmlNavigation_ReachesTheSignInGateCarryingTheLinkItself` | 6 |
| `ReportRecord_SessionOnTheRoster_CarriesTheNumberAndNameFromTheRoster` | 7 (roster) and 8 (list AND detail) |
| `ReportRecord_SessionGoneFromTheRoster_CarriesTheNumberAndNameFromTheHistoryRow` | 7 (history) |
| `ReportRecord_SessionTheGatewayKnowsNothingAbout_SaysOnePlainTrueSentenceAndNoIdentifier` | 7 (neither known, and no identifier) |

---

## The revert evidence

Each fix was COMMITTED first (commit `7ef02aa5d`), so `git checkout -- <file>` restores the fix rather than
eating it. Each revert was built, run, and restored; the restore leg was rebuilt, never run `--no-build`.

### The fold - `DevReportSessionLabel.cs` (all five run and watched)

| # | the line reverted | the test that went red | the symptom it reported |
| --- | --- | --- | --- |
| R1 | deleted `if (cleaned.Length > 0) return cleaned;` | `Session_NumberUnknown_IsTheNameAlone` | `Expected: "the gateway worker"` / `Actual: "the session"` |
| R2 | deleted `if (digits.Length > 0) return digits;` | `Session_NameUnknown_IsTheNumberAlone` | `Expected: "121"` / `Actual: "the session"` |
| R3 | `UnknownSession = "the session"` -> `""` | `Session_NeitherKnown_IsOnePlainTrueSentence` | `Expected: "the session"` / `Actual: ""` |
| R4 | replaced the collapse-and-cut in `Clean` with a bare `Trim()` | `Session_NameWithNewlinesAndRuns_IsOneLineWithSingleSpaces` AND `Session_NameLongerThanTheCeiling_IsCutAtTheTitleCeiling` | `Expected: "121 a name that wrapped"` / `Actual: "121 a name that \n\t wrapped"`, and the 250-character name came back uncut |
| R5 | `n.ToString(CultureInfo.InvariantCulture)` -> `n.ToString("D3", …)` | `Session_NumberOutsideTheAllocatorsBand_IsRenderedAsTheGatewayHoldsIt` | `Expected: "7 a session"` / `Actual: "007 a session"` |

Restore leg after all five: rebuilt (not `--no-build`) and 12/12 green.

### The route and the record - PENDING, see "What is not proven" below

---

## The runs

| run | result |
| --- | --- |
| `.\scripts\test-local.ps1` (the default gate) | GREEN. 2,057 tests across 8 projects, every suite `outcome=Completed`. It printed the COVERAGE GAP warning naming `CcDirector.Gateway.Tests` and `CcDirector.Gateway.UnitTests`, which is exactly why the two below are run explicitly. |
| `dotnet test CcDirector.Gateway.UnitTests --filter FullyQualifiedName~DevReportSessionLabel` | GREEN, 12/12. This suite is PARKED out of the default run, so it was run explicitly. |
| `.\scripts\test-local.ps1 -Gateway -Filter "FullyQualifiedName~DevReport"` | PENDING - blocked on the machine-wide lock, see below. |

---

## What I did NOT prove

- **The PARKED `CcDirector.Gateway.Tests` suite has not run.** It serializes machine-wide on
  `GatewayTestSuiteLock`, and that lock has been held since 20:25 UTC by testhost pid 69604 in the worktree
  `D:\ReposFred\devthrottle-dev-reports-p4-director` (session `6b9b9276`, "Dev Reports - Worker - Director
  reports pane"). The holder is alive and burning processor time, so it is a real run, not a stale file;
  another run was already queued behind it before mine. So `DevReportLinkRouteTests` is written, compiles,
  and is queued, but **no route test in this report has been observed green or red yet, and its revert
  proofs have not been done.** Nothing about `/r/{id}` or about the two labels reaching a client over HTTP
  is proven until that run happens. Treat every row in the route table above as a claim about what the test
  asserts, not as a result.
- **The tenant revert is one revert covering two tests, not two.** `DevReportStore` has no cross-tenant
  accessor, so there is no honest one-line mutation that looks a report up outside the caller's account. The
  revert planned for tests 4 and 5 removes the ownership lookup and redirects on the id alone, which turns
  both into a 302. That proves the lookup is load-bearing; it does not separately exercise a
  wrong-tenant read path, because none exists to exercise.
- **Nothing was checked in a browser.** The phone and Cockpit targets are asserted as strings. That those
  routes exist and render the report is the apps Worker's half; if `/session/{id}?tab=reports&report=…`
  changes, this route changes with it.
- **`-Parked` and `-Configuration Release` were not run.** Only the default gate, plus the two explicit
  filters above.
- **The message to the Manager may not have landed.** The first `cc-devthrottle message send` answered
  `Not delivered: unknown error`; the retry was refused by the rate limiter, which implies the first one did
  reach the throttle. It is repeated here so it is on the record either way.
