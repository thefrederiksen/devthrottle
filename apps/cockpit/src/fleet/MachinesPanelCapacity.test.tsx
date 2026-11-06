// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { CAPACITY_DASH, type FleetMachines, type MachineResourceHistory } from "@devthrottle/client-core/fleet/machinesClient";
import { MachinesPanel } from "./MachinesPanel";
import { HOT, OFFLINE, OFFLINE_WHY, ONLINE, history, machineWith, window } from "./machineCapacityFixtures";

// THE CAPACITY COLUMNS AND THE EXPANDED MACHINE, AT THE WIRE (Mission: Resource Monitor, sections 5j and 5k). Only the
// browser's fetch is replaced. What these pin is CLAUDE.md rule 7 and the defect class of this mission:
//   - every label and tone is the Gateway's, drawn verbatim;
//   - an OFFLINE machine shows the Gateway's dashes, never a zero, and says why;
//   - Spare sorts with the unknown last;
//   - the expanded machine draws the Gateway's factory labels - average AND peak - and its empty message as given.

const view: FleetMachines = {
  newestRelease: { label: "0.9.41", version: "0.9.41" },
  highlights: [],
  machines: [
    machineWith("devthrottle-mac-mini", ONLINE),
    machineWith("devlinux", HOT),
    machineWith("demo-win11", OFFLINE),
  ],
};

let answers: Record<string, () => Response>;
let calls: string[];

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

beforeEach(() => {
  calls = [];
  answers = {};
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      const path = new URL(String(input), "http://cockpit.test").pathname;
      const call = `${init?.method ?? "GET"} ${path}`;
      calls.push(call);
      const answer = Object.entries(answers).find(([key]) => call.endsWith(key))?.[1];
      return answer === undefined ? json(200, {}) : answer();
    }),
  );
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function draw() {
  render(
    <MemoryRouter>
      <MachinesPanel view={view} error={null} onChanged={() => undefined} />
    </MemoryRouter>,
  );
}

function row(machine: string): HTMLElement {
  return screen.getAllByTestId("fm-machine-row").find((r) => r.dataset.machine === machine)!;
}

describe("MachinesPanel: capacity columns", () => {
  it("draws the Gateway's labels verbatim for an online machine", () => {
    draw();
    const mac = within(row("devthrottle-mac-mini"));

    expect(mac.getByTestId("fm-cpu").textContent).toBe("28%");
    expect(mac.getByText("peak 71%")).toBeTruthy();
    expect(mac.getByTestId("fm-memory").textContent).toBe("55%");
    expect(mac.getByText("8.9 / 16 GB")).toBeTruthy();
    expect(mac.getByTestId("fm-spare").textContent).toBe("7.1 GB");
    expect(mac.queryByTestId("fm-capacity-status")).toBeNull();
  });

  it("draws a hot machine's status in the Gateway's tone", () => {
    draw();
    const linux = within(row("devlinux"));

    const status = linux.getByTestId("fm-capacity-status");
    expect(status.textContent).toBe("CPU hot");
    expect(status.className).toContain("dstat-warn");
    expect(linux.getByTestId("fm-cpu").className).toContain("dstat-warn");
  });

  it("draws an OFFLINE machine with the Gateway's dashes and its reason - never a zero", () => {
    draw();
    const offline = row("demo-win11");
    const cells = within(offline);

    expect(cells.getByTestId("fm-cpu").textContent).toBe(CAPACITY_DASH);
    expect(cells.getByTestId("fm-memory").textContent).toBe(CAPACITY_DASH);
    expect(cells.getByTestId("fm-spare").textContent).toBe(CAPACITY_DASH);
    expect(cells.getByTestId("fm-capacity-status").textContent).toBe("Offline");
    expect(cells.getByTestId("fm-capacity-why").textContent).toBe(OFFLINE_WHY);
    // No zero, no percent and no gigabyte figure anywhere in the offline row: offline is not idle.
    expect(offline.textContent).not.toMatch(/\b0(\.0)?\s*(%|GB)/);
    expect(offline.textContent).not.toMatch(/\d+%/);
  });

  it("sorts by Spare - most room first, then least, then as sent - with the unknown last", () => {
    draw();
    const order = () => screen.getAllByTestId("fm-machine-row").map((r) => r.dataset.machine);
    const sort = screen.getByTestId("fm-sort-spare");

    expect(order()).toEqual(["devthrottle-mac-mini", "devlinux", "demo-win11"]);
    fireEvent.click(sort);
    expect(order()).toEqual(["devlinux", "devthrottle-mac-mini", "demo-win11"]);
    fireEvent.click(sort);
    expect(order()).toEqual(["devthrottle-mac-mini", "devlinux", "demo-win11"]);
    fireEvent.click(sort);
    expect(order()).toEqual(["devthrottle-mac-mini", "devlinux", "demo-win11"]);
  });
});

describe("MachinesPanel: one machine expanded", () => {
  const factories: MachineResourceHistory["factories"] = [
    { factory: "Biographies", sessions: 1, processorLabel: "31% / 97%", memoryLabel: "1.9 / 6.2 GB", processorPeakPercent: 97 },
    { factory: "Marketing", sessions: 4, processorLabel: "at least 22% / 61%", memoryLabel: "3.1 / 5.8 GB", processorPeakPercent: 61 },
    { factory: "No factory", sessions: 1, processorLabel: "4% / 12%", memoryLabel: "0.6 / 0.9 GB", processorPeakPercent: 12 },
  ];

  it("reads the machine's history only when expanded, and draws the factories' labels verbatim", async () => {
    const hot = {
      ...window("2026-11-06T11:50:00Z", [88, 99], [34, 36]),
      crossings: [{ startedUtc: "2026-11-06T11:51:00Z", resource: "CPU", label: "CPU red for 90 seconds, peak 99%", tone: "bad" as const }],
    };
    answers["GET /machines/devlinux/resources"] = () =>
      json(200, history([window("2026-11-06T11:45:00Z", [40, 70], [33, 35]), hot], factories));
    draw();
    expect(calls.filter((c) => c.includes("/resources"))).toEqual([]);

    fireEvent.click(within(row("devlinux")).getByRole("button", { name: "Show devlinux's last 24 hours" }));

    const table = await screen.findByTestId("fmr-factories");
    const rows = within(table).getAllByRole("row").slice(1).map((r) => [...r.querySelectorAll("td")].map((td) => td.textContent));
    expect(rows).toEqual([
      ["Biographies", "1", "31% / 97%", "1.9 / 6.2 GB"],
      ["Marketing", "4", "at least 22% / 61%", "3.1 / 5.8 GB"],
      ["No factory", "1", "4% / 12%", "0.6 / 0.9 GB"],
    ]);
    expect(screen.getByTestId("fmr-chart-CPU")).toBeTruthy();
    expect(screen.getByTestId("fmr-chart-Memory")).toBeTruthy();
    expect(screen.getAllByTestId("fmr-mark")).toHaveLength(1);
    expect(within(screen.getByTestId("fmr-events")).getByText(/CPU red for 90 seconds, peak 99%/)).toBeTruthy();
    expect(calls.filter((c) => c.includes("/resources"))).toEqual(["GET /machines/devlinux/resources"]);
  });

  it("shows the Gateway's empty message instead of an empty chart", async () => {
    answers["GET /machines/demo-win11/resources"] = () =>
      json(200, { machine: "demo-win11", hours: 24, emptyMessage: "No resource summaries from demo-win11 in the last 24 hours.", windows: [], factories: [] });
    draw();

    fireEvent.click(within(row("demo-win11")).getByRole("button", { name: "Show demo-win11's last 24 hours" }));

    expect((await screen.findByTestId("fmr-empty")).textContent).toBe("No resource summaries from demo-win11 in the last 24 hours.");
    expect(screen.queryByTestId("fmr-chart-CPU")).toBeNull();
    expect(screen.queryByTestId("fmr-factories")).toBeNull();
  });

  it("shows a failed read in the Gateway's words, not an empty chart", async () => {
    answers["GET /machines/devlinux/resources"] = () => json(503, { reason: "The statistics store is busy." });
    draw();

    fireEvent.click(within(row("devlinux")).getByRole("button", { name: "Show devlinux's last 24 hours" }));

    expect((await screen.findByRole("alert")).textContent).toContain("The statistics store is busy.");
    expect(screen.queryByTestId("fmr-chart-CPU")).toBeNull();
  });
});
