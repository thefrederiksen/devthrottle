# Mission document review

## Verdict

Changes are needed before the document can direct implementation without ambiguity.

## Finding

### 1. The goal treats factories and Missions as the same thing, but the product stores them as separate identities

Goal item 5 requires the Cockpit to show “every factory (Mission)” with its average and peak cost. Phase 4 repeats “factories (Missions)”. Elsewhere, the mission and its why consistently describe factory placement and factory cost as the reason for collecting these measurements.

On `origin/main`, these are not two names for one record:

- `src/CcDirector.Gateway/Data/Entities/SessionHistoryEntity.cs` stores a nullable `MissionId` and a separate nullable `Factory` on the same session history row.
- `src/CcDirector.Gateway.Contracts/MissionDto.cs` defines a Mission as a named unit of work with its own identifier.
- `src/CcDirector.Gateway/Data/Entities/FactoryRegistryEntity.cs` defines a registered factory with a string factory identifier, computer, seats and goal.

This ambiguity can produce the wrong aggregation key. Grouping resource summaries by `MissionId` would report units of work, while grouping them by `Factory` would report the automation factories that the why says must be placed according to machine capacity. A session can carry either or both identities, so one grouping cannot stand in for the other. An implementation that follows the parenthetical wording could split one factory across several Missions, omit factory sessions that have no Mission, and leave the owner without the factory cost required for placement.

The goal, design and phase 4 should name the intended grouping explicitly. If the intended result is factory cost, they should consistently say registered factory and identify the session's `Factory` value as the grouping key. If Mission cost is also required, it should be a separate result with its own grouping rule and proof.

## Scope

I read the complete proposed `missions/resource-monitor.html`, the added `missions/README.md` row and the pull request file list from `origin/docs/mission-resource-monitor` after fetching `origin`.

I checked the goal, design, phases, check, merge plan and ending against one another. I also checked every requested foundation claim against `origin/main` by reading or searching:

- `src/CcDirector.Core/Machine/MachineMemoryProbe.cs`, `MemoryPressure.cs` and their unit test references;
- the right-hand toolbar group in `src/CcDirector.Avalonia/MainWindow.axaml`;
- `PushSnapshot` and `PushDelta` use in `src/CcDirector.ControlApi/GatewayStreamClient.cs`;
- the `gateway_stats` model and current PostgreSQL model snapshot, including their `tenant` columns;
- `src/CcDirector.DeliveryQualification/CpuLoad.cs` and its project;
- the `apps/cockpit` tree and package definition;
- the separate Mission and factory records named in the finding.

Those requested foundation claims are supported by `origin/main`. The statistics store has tenant columns on its tenant-scoped tables; its two session-membership tables are explicitly partitioned indirectly and do not have such a column, which does not conflict with the document's plan to key the new resource tables by tenant.

I ran only `git fetch`, `git show`, `git grep`, `git diff`, `git ls-tree` and `git log` inspections. I did not build, run tests or run the application, as this was a documentation review. I did not independently verify issue 2811, the linked external design report, live machine measurements, or the proposed Windows and macOS diagnostic signals. Those surfaces were outside the requested code-claim check and are also identified by the mission document as not yet proved.
