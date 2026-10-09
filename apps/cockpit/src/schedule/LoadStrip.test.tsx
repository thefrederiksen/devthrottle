// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent } from "@testing-library/react";
import type { CronLoad, CronLoadHour, CronMachineLoad } from "@devthrottle/client-core/schedule/scheduleClient";
import { LoadStrip } from "./LoadStrip";

// The load strip lays out what the Gateway folded (GET /cron/load) and never works anything out itself: these pin
// that an hour over capacity is drawn as over, that tapping a bar hands its hour up to filter the list, that an
// empty hour cannot be tapped, and that the machine switch and the guess note appear only when they mean something.

afterEach(cleanup);

function hour(i: number, overrides: Partial<CronLoadHour> = {}): CronLoadHour {
  const label = `${String(i).padStart(2, "0")}:00`;
  return {
    startUtc: `2026-10-09T${String((i + 4) % 24).padStart(2, "0")}:00:00Z-${i}`,
    label,
    concurrent: 0,
    starts: 0,
    over: false,
    jobIds: [],
    ...overrides,
  };
}

function machine(name: string, overrides: Partial<CronMachineLoad> = {}): CronMachineLoad {
  const hours = Array.from({ length: 24 }, (_, i) => hour(i));
  hours[7] = hour(7, { concurrent: 8, starts: 5, over: true, jobIds: ["a", "b"] });
  hours[9] = hour(9, { concurrent: 1, starts: 1, jobIds: ["c"] });
  return {
    machine: name,
    timeZoneId: "America/Toronto",
    schedules: 3,
    peak: 8,
    hoursOver: 1,
    quietest: hours[0]!,
    summary: "peak 8 at 07:00 - 1 hour over 6 - quietest 00:00 (0 open)",
    hours,
    estimatedJobIds: [],
    estimateNote: "",
    ...overrides,
  };
}

function load(machines: CronMachineLoad[]): CronLoad {
  return { generatedUtc: "2026-10-09T04:10:00Z", capacity: 6, machines };
}

describe("LoadStrip", () => {
  it("draws 24 bars, shows the Gateway's summary, and marks the hour over capacity", () => {
    render(
      <LoadStrip load={load([machine("SOREN_NORTH")])} machine="" onMachine={vi.fn()} selectedHour={null} onSelectHour={vi.fn()} />,
    );

    expect(screen.getByText("peak 8 at 07:00 - 1 hour over 6 - quietest 00:00 (0 open)")).toBeTruthy();
    const bars = screen.getAllByRole("button", { name: /open, \d+ starting/ });
    expect(bars).toHaveLength(24);
    expect(bars[7]!.querySelector(".sched-load-bar.over")).not.toBeNull();
    expect(bars[9]!.querySelector(".sched-load-bar.over")).toBeNull();
    expect(screen.getByText("capacity 6")).toBeTruthy();
    // One machine needs no switch.
    expect(screen.queryByRole("group", { name: "Machine" })).toBeNull();
  });

  it("hands up the tapped hour, and an hour with nothing open cannot be tapped", () => {
    const onSelectHour = vi.fn();
    render(
      <LoadStrip load={load([machine("SOREN_NORTH")])} machine="" onMachine={vi.fn()} selectedHour={null} onSelectHour={onSelectHour} />,
    );

    fireEvent.click(screen.getByRole("button", { name: "07:00: at most 8 open, 5 starting" }));
    expect(onSelectHour).toHaveBeenCalledWith(expect.objectContaining({ label: "07:00", jobIds: ["a", "b"] }));

    const empty = screen.getByRole("button", { name: "03:00: at most 0 open, 0 starting" }) as HTMLButtonElement;
    expect(empty.disabled).toBe(true);
  });

  it("tapping the selected hour again clears the filter", () => {
    const onSelectHour = vi.fn();
    const strip = load([machine("SOREN_NORTH")]);
    render(
      <LoadStrip
        load={strip}
        machine=""
        onMachine={vi.fn()}
        selectedHour={strip.machines[0]!.hours[7]!.startUtc}
        onSelectHour={onSelectHour}
      />,
    );

    fireEvent.click(screen.getByRole("button", { name: "07:00: at most 8 open, 5 starting" }));
    expect(onSelectHour).toHaveBeenCalledWith(null);
  });

  it("offers a switch between machines and shows the one chosen, with its guess note", () => {
    const onMachine = vi.fn();
    const devlinux = machine("devlinux", {
      summary: "peak 1 at 09:00 - never over 6 - quietest 00:00 (0 open)",
      estimateNote: "1 schedule has never finished a run, so it is counted at 30 min",
    });
    render(
      <LoadStrip
        load={load([machine("SOREN_NORTH"), devlinux])}
        machine="devlinux"
        onMachine={onMachine}
        selectedHour={null}
        onSelectHour={vi.fn()}
      />,
    );

    expect(screen.getByText("peak 1 at 09:00 - never over 6 - quietest 00:00 (0 open)")).toBeTruthy();
    expect(screen.getByText("1 schedule has never finished a run, so it is counted at 30 min")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "SOREN_NORTH" }));
    expect(onMachine).toHaveBeenCalledWith("SOREN_NORTH");
  });
});
