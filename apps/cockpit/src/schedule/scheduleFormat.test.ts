import { describe, expect, it } from "vitest";
import type { CronJob } from "@devthrottle/client-core/schedule/scheduleClient";
import {
  absoluteUtc,
  actionShortLabel,
  actionType,
  cronToEnglish,
  epochOrMax,
  lastOutcome,
  nextRunInstant,
  nextRunLabel,
  promptBody,
  relativeUntil,
  scheduleListOf,
} from "./scheduleFormat";

// A minimal CronJob builder for the display tests - only the fields these pure helpers read.
function job(
  overrides: Omit<Partial<CronJob>, "action"> & { action?: Partial<CronJob["action"]> } = {},
): CronJob {
  const { action: actionOverrides, ...rest } = overrides;
  return {
    id: "job-1",
    name: "Test job",
    enabled: true,
    scheduleKind: "recurring",
    cronExpression: "0 0 * * *",
    runAt: null,
    timeZoneId: "America/Chicago",
    target: { machine: "SOREN_NORTH" },
    preventOverlap: true,
    notifyOn: "none",
    notifyWebhookUrl: null,
    nextRunUtc: null,
    lastFiredUtc: null,
    lastStatus: null,
    ...rest,
    action: {
      repoPath: "C:\\repos\\devthrottle",
      seed: "",
      workListName: null,
      ...(actionOverrides ?? {}),
    },
  };
}

describe("actionType", () => {
  it("classifies a slash command as a Skill", () => {
    expect(actionType(job({ action: { seed: "/inbound-watch" } }))).toBe("Skill");
  });

  it("classifies free text as a Prompt", () => {
    expect(actionType(job({ action: { seed: "You are a marketing analyst..." } }))).toBe("Prompt");
  });

  it("classifies a named work list as a Work list", () => {
    expect(actionType(job({ action: { seed: "", workListName: "Tonight" } }))).toBe("Work list");
  });
});

describe("actionShortLabel", () => {
  it("never glues the raw word 'skill' onto the prompt", () => {
    const label = actionShortLabel(job({ action: { seed: "/implementation-loop 312" } }));
    expect(label).toBe("/implementation-loop 312");
    expect(label.startsWith("skill ")).toBe(false);
  });

  it("uses only the first line of a multi-paragraph prompt", () => {
    const label = actionShortLabel(
      job({ action: { seed: "First line summary\n\nSecond paragraph with detail." } }),
    );
    expect(label).toBe("First line summary");
  });

  it("truncates a long single line to the maximum length with an ellipsis", () => {
    const long = "a".repeat(200);
    const label = actionShortLabel(job({ action: { seed: long } }), 60);
    expect(label.length).toBe(60);
    expect(label.endsWith("...")).toBe(true);
  });

  it("uses the work list name for a work-list job", () => {
    expect(actionShortLabel(job({ action: { seed: "", workListName: "Tonight" } }))).toBe("Tonight");
  });
});

describe("promptBody", () => {
  it("returns the full prompt untouched, with no type prefix", () => {
    const body = "You are a run.\n\nStep 1: do the thing.";
    expect(promptBody(job({ action: { seed: body } }))).toBe(body);
  });
});

describe("cronToEnglish", () => {
  it("reads a daily midnight schedule", () => {
    expect(cronToEnglish("0 0 * * *")).toBe("At 12:00 AM");
  });

  it("reads twice-daily on weekdays", () => {
    expect(cronToEnglish("13 8,14 * * 1-5")).toBe("At 8:13 AM and 2:13 PM, Monday through Friday");
  });

  it("reads a step-minute schedule", () => {
    expect(cronToEnglish("*/15 * * * *")).toBe("Every 15 minutes");
  });

  it("reads a day-of-month schedule", () => {
    expect(cronToEnglish("0 0 1 * *")).toBe("At 12:00 AM, on the 1st of the month");
  });

  it("falls back to a spelled-out field description when there are too many times to list", () => {
    expect(cronToEnglish("0,30 8,12,16,20 * * *")).toBe("At minute 0 and 30, hour 8 and 12 and 16 and 20");
  });

  it("returns the raw string when it is not five fields", () => {
    expect(cronToEnglish("not a cron")).toBe("not a cron");
  });

  it("names an empty schedule plainly", () => {
    expect(cronToEnglish("")).toBe("(no schedule)");
    expect(cronToEnglish(null)).toBe("(no schedule)");
  });
});

describe("relativeUntil", () => {
  const now = Date.parse("2026-07-11T12:00:00Z");

  it("phrases a time 46 minutes out", () => {
    expect(relativeUntil("2026-07-11T12:46:00Z", now)).toBe("in 46m");
  });

  it("phrases a time hours out", () => {
    expect(relativeUntil("2026-07-11T15:00:00Z", now)).toBe("in 3h");
  });

  it("phrases a time days out", () => {
    expect(relativeUntil("2026-07-13T12:00:00Z", now)).toBe("in 2d");
  });

  it("says a past time is overdue", () => {
    expect(relativeUntil("2026-07-11T11:00:00Z", now)).toBe("overdue");
  });

  it("returns a dash for an absent or unparseable time", () => {
    expect(relativeUntil(null, now)).toBe("-");
    expect(relativeUntil("nonsense", now)).toBe("-");
  });
});

describe("epochOrMax", () => {
  it("parses a valid time to its epoch", () => {
    expect(epochOrMax("2026-07-11T12:00:00Z")).toBe(Date.parse("2026-07-11T12:00:00Z"));
  });

  it("sinks an absent time to the maximum so it sorts last, not first", () => {
    expect(epochOrMax(null)).toBe(Number.MAX_SAFE_INTEGER);
    expect(epochOrMax("")).toBe(Number.MAX_SAFE_INTEGER);
  });
});

describe("lastOutcome", () => {
  it("marks a success", () => {
    expect(lastOutcome("OK")).toEqual({ kind: "ok", label: "OK" });
  });

  it("marks a failure and keeps the reason", () => {
    expect(lastOutcome("failed: gateway timeout")).toEqual({
      kind: "err",
      label: "failed: gateway timeout",
    });
  });

  it("shows an unknown status verbatim as a neutral badge", () => {
    expect(lastOutcome("started")).toEqual({ kind: "warn", label: "started" });
  });

  it("reports no badge when nothing ran", () => {
    expect(lastOutcome(null)).toEqual({ kind: "none", label: "" });
  });
});

describe("absoluteUtc", () => {
  it("formats a UTC instant", () => {
    expect(absoluteUtc("2026-07-11T12:14:00Z")).toBe("2026-07-11 12:14 UTC");
  });

  it("returns a dash when absent", () => {
    expect(absoluteUtc(null)).toBe("-");
  });
});

// The owner, 2026-10-09: the page lists only schedules that will still run, and a one-off that already ran must
// never read "overdue". The lifecycle comes from the Gateway; these check the page files each one correctly.
describe("scheduleListOf", () => {
  it("lists an active schedule as active", () => {
    expect(scheduleListOf(job({ lifecycle: "active" }))).toBe("active");
  });

  it("lists a switched-off repeating schedule as paused, never as active", () => {
    expect(scheduleListOf(job({ enabled: false, lifecycle: "paused" }))).toBe("paused");
  });

  it("lists a spent one-off and one switched off before it ran as historical", () => {
    expect(scheduleListOf(job({ scheduleKind: "oneOff", enabled: false, lifecycle: "spent" }))).toBe("historical");
    expect(scheduleListOf(job({ scheduleKind: "oneOff", enabled: false, lifecycle: "off" }))).toBe("historical");
  });

  it("lists a job with no lifecycle as active, so nothing can vanish from the default view", () => {
    expect(scheduleListOf(job({ lifecycle: undefined }))).toBe("active");
  });
});

describe("nextRunLabel", () => {
  const now = Date.parse("2026-10-09T12:00:00Z");

  it("reads 'ran <date>' for a spent one-off, never 'overdue'", () => {
    const spent = job({
      scheduleKind: "oneOff",
      enabled: false,
      lifecycle: "spent",
      nextRunUtc: "2026-08-03T14:30:00Z",
      lastFiredUtc: "2026-08-03T14:30:05Z",
    });
    expect(nextRunLabel(spent, now)).toBe("ran 2026-08-03");
    expect(nextRunInstant(spent)).toBe("2026-08-03T14:30:05Z");
  });

  it("reads 'paused' and 'off' for the switched-off kinds", () => {
    expect(nextRunLabel(job({ enabled: false, lifecycle: "paused", nextRunUtc: "2026-10-10T07:00:00Z" }), now)).toBe(
      "paused",
    );
    expect(nextRunLabel(job({ scheduleKind: "oneOff", enabled: false, lifecycle: "off" }), now)).toBe("off");
  });

  it("counts down to the next run for an active schedule", () => {
    const active = job({ lifecycle: "active", nextRunUtc: "2026-10-09T15:00:00Z" });
    expect(nextRunLabel(active, now)).toBe("in 3h");
    expect(nextRunInstant(active)).toBe("2026-10-09T15:00:00Z");
  });
});
