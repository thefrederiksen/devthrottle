import { describe, expect, it } from "vitest";
import type { CronJob } from "@devthrottle/client-core/schedule/scheduleClient";
import { groupRows } from "../components/DataTable";
import {
  compareScheduleGroups,
  isFactorySchedule,
  NO_FACTORY_GROUP,
  repeatsLabel,
  scheduleGroupOf,
  scheduleGroupTitle,
} from "./scheduleFormat";

// The owner's layout A (2026-10-09): the Active list grouped by factory, plain scheduled jobs in their own group at
// the bottom, and every row saying whether it repeats or runs once.

function job(overrides: Partial<CronJob> = {}): CronJob {
  return {
    id: "job",
    name: "A job",
    enabled: true,
    scheduleKind: "recurring",
    cronExpression: "0 7 * * *",
    runAt: null,
    timeZoneId: "America/Toronto",
    target: { machine: "SOREN_NORTH" },
    action: { repoPath: "D:\\repo", seed: "/help", workListName: null },
    preventOverlap: true,
    notifyOn: "none",
    ...overrides,
  };
}

const titles: Record<string, string> = { clickfunnels: "ClickFunnels", devthrottle: "DevThrottle", warmforward: "WarmForward" };
const titleOf = (key: string) => titles[key] ?? key;

describe("scheduleGroupOf", () => {
  it("groups a factory schedule under its factory", () => {
    expect(scheduleGroupOf(job({ factory: "clickfunnels", seat: "builder" }))).toBe("clickfunnels");
  });

  it("puts a schedule with no factory among the plain scheduled jobs, whatever its name says", () => {
    // The name is never read to guess a factory: that would be a fallback hiding the missing field.
    expect(scheduleGroupOf(job({ name: "WarmForward Factory - Nora Hale - morning run", factory: null }))).toBe(
      NO_FACTORY_GROUP,
    );
    expect(scheduleGroupOf(job({ factory: "  " }))).toBe(NO_FACTORY_GROUP);
  });
});

describe("compareScheduleGroups", () => {
  it("puts the owner's own jobs first and orders factories by title", () => {
    const keys = ["warmforward", NO_FACTORY_GROUP, "clickfunnels", "devthrottle"];
    keys.sort((a, b) => compareScheduleGroups(a, b, titleOf));
    expect(keys).toEqual([NO_FACTORY_GROUP, "clickfunnels", "devthrottle", "warmforward"]);
  });

  it("treats a schedule as a factory one only when the Gateway gave it a factory page", () => {
    expect(isFactorySchedule(job({ factory: "clickfunnels", factoryHref: "/factories/clickfunnels/seats" }))).toBe(true);
    // Its factory is not registered: no page to edit it on, so it stays an ordinary row.
    expect(isFactorySchedule(job({ factory: "gone", factoryHref: null }))).toBe(false);
    expect(isFactorySchedule(job({ factory: null }))).toBe(false);
  });
});

describe("scheduleGroupTitle", () => {
  it("uses the registered title, the factory id when it is not registered, and Personal for the plain ones", () => {
    expect(scheduleGroupTitle("clickfunnels", [job({ factory: "clickfunnels", factoryTitle: "ClickFunnels" })])).toBe(
      "ClickFunnels",
    );
    expect(scheduleGroupTitle("unregistered", [job({ factory: "unregistered", factoryTitle: null })])).toBe(
      "unregistered",
    );
    expect(scheduleGroupTitle(NO_FACTORY_GROUP, [job()])).toBe("Personal");
  });
});

describe("repeatsLabel", () => {
  it("says ONCE for a one-off and REPEATS for recurring and random schedules", () => {
    expect(repeatsLabel(job({ scheduleKind: "oneOff" }))).toBe("ONCE");
    expect(repeatsLabel(job({ scheduleKind: "recurring" }))).toBe("REPEATS");
    expect(repeatsLabel(job({ scheduleKind: "random" }))).toBe("REPEATS");
  });
});

describe("groupRows", () => {
  it("keeps the table's sort inside each group and orders the groups", () => {
    const sorted = [
      job({ id: "a", factory: null }),
      job({ id: "b", factory: "devthrottle" }),
      job({ id: "c", factory: "clickfunnels" }),
      job({ id: "d", factory: "devthrottle" }),
    ];

    const groups = groupRows(sorted, {
      groupOf: scheduleGroupOf,
      compareGroups: (a, b) => compareScheduleGroups(a, b, titleOf),
    });

    expect(groups.map((g) => [g.groupKey, g.rows.map((r) => r.id)])).toEqual([
      [NO_FACTORY_GROUP, ["a"]],
      ["clickfunnels", ["c"]],
      ["devthrottle", ["b", "d"]],
    ]);
  });
});
