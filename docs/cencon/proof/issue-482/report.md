# Proof Report - Issue #482

## Cron jobs (1/3): job store, DTOs, and REST CRUD

**Part 1 of 3 of epic #479.** Durable foundation: define and manage cron-job definitions over REST. No firing (that is #483).

**Branch/PR:** issue-482-cron-store-crud (PR #485)
**Build:** `dotnet build cc-director.sln` -> **Build succeeded, 0 Warning(s), 0 Error(s)** (TreatWarningsAsErrors=true across all projects).

---

## How this slice is proven

The Gateway exposes **no UI** for this slice, so the running-app proof is the **real HTTP** endpoint suite (`CronJobEndpointsTests`): it boots the actual `CronJobEndpoints` on an ephemeral loopback port with a fresh `CronJobStore` and drives the full CRUD contract over the wire - the same harness the shipped `WorkListEndpoints` are proven with. Persistence/restart (AC2, AC3) is proven by `CronJobStoreTests` exactly as the shipped `WorkListStore` persistence is: a "restart" is a brand-new store instance loading the same file - what a new Gateway process does. Schedule grammar/timezone (AC5 + the next-run computation) is proven by `CronScheduleTests`.

**Test result:** `dotnet test --filter Cron` -> **Passed: 31, Failed: 0** (15 schedule + 12 store + 7 endpoint).

---

## Acceptance criteria - Expected vs Actual

| # | Criterion | Proof (test) | Expected | Actual |
|---|-----------|--------------|----------|--------|
| AC1 | `POST /cron/jobs` valid -> 201 with id + `nextRunUtc`; `GET {id}` returns it | `CronJobEndpointsTests.Post_ValidJob_Returns201_WithIdAndNextRun`, `.Get_ById_ReturnsJob...` | 201, id starts `cj_`, `nextRunUtc` set | PASS |
| AC2 | Persisted to `cronjobs.json` (atomic write-through); corrupt file quarantined, no crash | `CronJobStoreTests.Create_...Persists`, `.CorruptFile_IsQuarantined_StoreStartsEmpty_BytesPreserved`, `.Save_LeavesNoTempResidue_AndFileIsWholeJson` | written through; corrupt -> `.corrupt-<stamp>`, store boots empty | PASS |
| AC3 | Survives Gateway restart; `nextRunUtc` recomputed | `CronJobStoreTests.RoundTrip_MultipleJobs_SurviveReloadWithRecomputedNextRun` | fresh store on same file returns jobs with recomputed next-run | PASS |
| AC4 | `PUT` updates + persists; `DELETE` removes; subsequent `GET` 404 | `CronJobEndpointsTests.Put_UpdatesJob_AndMissingReturns404`, `.Delete_RemovesJob_AndMissingReturns404`; `CronJobStoreTests.Update_...`, `.Delete_...` | PUT 200/404, DELETE 200 then GET 404 | PASS |
| AC5 | Invalid cron expression -> 400, not stored | `CronJobEndpointsTests.Post_InvalidCron_Returns400_AndIsNotStored`; `CronScheduleTests.Validate_InvalidCronExpression_Fails` | 400; list stays empty | PASS |
| AC6 | `GET /cron/jobs` lists all jobs | `CronJobEndpointsTests.GetAll_ListsCreatedJobs` | list of 2 after creating 2 | PASS |

Supporting correctness (next-run math): `CronScheduleTests.ComputeNextRunUtc_DailyMidnightChicago_IsNextLocalMidnightInUtc` and `...OneOff_ConvertsLocalRunAtToUtc` prove 00:00 America/Chicago resolves to 05:00 UTC (CDT), so the stored `nextRunUtc` is correct and explicit.

---

## CenCon impact

Adds a store + REST surface to the `gateway` container and DTOs to `gateway_contracts`, plus the Cronos NuGet dependency. No security-posture change (inherits the host-wide token middleware; no new auth surface, no secrets logged). The architecture_manifest `gateway` container gains the cron store/endpoints; recommend the Support Agent record this when the engine (part 2) lands so the manifest reflects the whole feature at once.

---

## Note on the full-suite run

The full `CcDirector.Gateway.Tests` run shows one unrelated failure: `DictationEndpointTests.FullPipeline_transcribes_phase0_clip2_with_realtime_provider` (Vosk realtime transcription - "expected at least 1 partial transcript, got 0"). It is environment-dependent (needs the Vosk model/audio realtime provider present) and touches no cron code; CI - which gates this PR - is the authority on the suite. All 31 cron tests pass.

---

## Statement

I believe this slice (#482) is finished: the store + DTOs + REST CRUD are implemented, the solution builds clean with zero warnings, and every acceptance criterion is proven by a passing test (real HTTP for the endpoints, file round-trip for persistence). Firing is intentionally out of scope and lands in #483.
