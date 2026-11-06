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

function draw(shown: FleetMachines = view) {
  return render(
    <MemoryRouter>
      <MachinesPanel view={shown} error={null} onChanged={() => undefined} />
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
    // One status per machine: the reach line owns reachability, so the capacity fold sends no second pill.
    expect(cells.queryByTestId("fm-capacity-status")).toBeNull();
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
    const crossing = { startedUtc: "2026-11-06T11:51:00Z", resource: "CPU", label: "CPU red for 90 seconds, peak 99%", tone: "bad" as const };
    const hot = { ...window("2026-11-06T11:50:00Z", [88, 99], [34, 36]), crossings: [crossing] };
    answers["GET /machines/devlinux/resources"] = () =>
      json(200, history([window("2026-11-06T11:45:00Z", [40, 70], [33, 35]), hot], factories, {
        events: [{ atUtc: crossing.startedUtc, label: crossing.label, tone: crossing.tone }],
      }));
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

  // Review of pull request 4646: a refused window was covered only by the geometry test. On screen it must be a stripe
  // carrying the Gateway's reason, and the reason must appear in the events list - never a zero on the line.
  // Review finding 3: the stripe and the event list carry the Gateway's words and tone, not ones the client wrote.
  it("draws a refused window with the Gateway's note and tone, and lists the Gateway's event", async () => {
    const note = { label: "Figures refused: its processor peak was 412%, above the 100% a whole machine can reach", tone: "bad" as const };
    const refused = { ...window("2026-11-06T11:50:00Z", null, null), refusedReason: "its processor peak was 412%", processorNote: note, memoryNote: note };
    answers["GET /machines/devlinux/resources"] = () =>
      json(200, history([window("2026-11-06T11:45:00Z", [40, 70], [33, 35]), refused, window("2026-11-06T11:55:00Z", [41, 72], [33, 35])], factories, {
        events: [{ atUtc: refused.windowStartUtc, label: note.label, tone: "bad" }],
      }));
    draw();

    fireEvent.click(within(row("devlinux")).getByRole("button", { name: "Show devlinux's last 24 hours" }));

    await screen.findByTestId("fmr-factories");
    const cpu = within(screen.getByTestId("fmr-chart-CPU"));
    const stripe = cpu.getByTestId("fmr-stripe-note");
    expect(stripe.textContent).toContain(note.label);
    expect(stripe.getAttribute("class")).toContain("dstat-err");
    expect(cpu.getAllByTestId("fmr-run")).toHaveLength(2);
    const listed = within(screen.getByTestId("fmr-events")).getByText(note.label);
    expect(listed.closest("li")!.className).toContain("dstat-err");
  });

  // Review findings 1 and 2: a machine silent since 02:00 shows that silence as the Gateway's gap, up to its now.
  it("draws the Gateway's gap up to the right edge for a machine that went silent", async () => {
    const gap = { startUtc: "2026-11-06T02:05:00Z", endUtc: "2026-11-06T12:00:00Z", label: "No resource summary has arrived for 9 hours 55 minutes", tone: "idle" as const };
    answers["GET /machines/devlinux/resources"] = () =>
      json(200, history([window("2026-11-06T02:00:00Z", [40, 70], [33, 35])], factories, {
        gaps: [gap],
        events: [{ atUtc: gap.startUtc, label: gap.label, tone: gap.tone }],
      }));
    draw();

    fireEvent.click(within(row("devlinux")).getByRole("button", { name: "Show devlinux's last 24 hours" }));

    await screen.findByTestId("fmr-factories");
    for (const chart of ["fmr-chart-CPU", "fmr-chart-Memory"]) {
      const stripe = within(screen.getByTestId(chart)).getByTestId("fmr-stripe-gap");
      expect(stripe.textContent).toContain(gap.label);
      expect(Number(stripe.getAttribute("x")) + Number(stripe.getAttribute("width"))).toBeCloseTo(712, 0);
    }
    expect(within(screen.getByTestId("fmr-events")).getByText(gap.label)).toBeTruthy();
  });

  it("shows the Gateway's sentence when no session was recorded", async () => {
    answers["GET /machines/devlinux/resources"] = () =>
      json(200, history([window("2026-11-06T11:45:00Z", [40, 70], [33, 35])], [], { noFactoriesMessage: "No sessions were recorded on this machine in this span." }));
    draw();

    fireEvent.click(within(row("devlinux")).getByRole("button", { name: "Show devlinux's last 24 hours" }));

    expect((await screen.findByTestId("fmr-no-factories")).textContent).toBe("No sessions were recorded on this machine in this span.");
  });

  // Review finding 7: the expanded history re-reads on the table's own refresh, so it never sits stale under a live row.
  it("reads the expanded history again when the table refreshes", async () => {
    answers["GET /machines/devlinux/resources"] = () => json(200, history([window("2026-11-06T11:45:00Z", [40, 70], [33, 35])], factories));
    const shown = draw();
    fireEvent.click(within(row("devlinux")).getByRole("button", { name: "Show devlinux's last 24 hours" }));
    await screen.findByTestId("fmr-factories");
    expect(calls.filter((c) => c.includes("/resources"))).toHaveLength(1);

    shown.rerender(
      <MemoryRouter>
        <MachinesPanel view={{ ...view, machines: [...view.machines] }} error={null} onChanged={() => undefined} />
      </MemoryRouter>,
    );

    await vi.waitFor(() => expect(calls.filter((c) => c.includes("/resources"))).toHaveLength(2));
    // The chart already drawn stays up while the next read is in flight.
    expect(screen.getByTestId("fmr-factories")).toBeTruthy();
  });

  // Second review, finding 4: a Gateway slower than the table's poll still delivers - the next refresh is skipped while
  // a read is in flight, never allowed to abort it.
  it("never aborts a slow read on the table's refresh; the chart arrives", async () => {
    let answer: (r: Response) => void = () => undefined;
    let aborted = false;
    vi.stubGlobal(
      "fetch",
      vi.fn((input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
        const path = new URL(String(input), "http://cockpit.test").pathname;
        calls.push(`${init?.method ?? "GET"} ${path}`);
        if (!path.endsWith("/resources")) return Promise.resolve(json(200, {}));
        init?.signal?.addEventListener("abort", () => (aborted = true));
        return new Promise<Response>((resolve) => (answer = resolve));
      }),
    );
    const shown = draw();
    fireEvent.click(within(row("devlinux")).getByRole("button", { name: "Show devlinux's last 24 hours" }));
    await vi.waitFor(() => expect(calls.filter((c) => c.includes("/resources"))).toHaveLength(1));

    shown.rerender(
      <MemoryRouter>
        <MachinesPanel view={{ ...view, machines: [...view.machines] }} error={null} onChanged={() => undefined} />
      </MemoryRouter>,
    );
    answer(json(200, history([window("2026-11-06T11:45:00Z", [40, 70], [33, 35])], factories)));

    expect(await screen.findByTestId("fmr-factories")).toBeTruthy();
    expect(aborted).toBe(false);
    expect(calls.filter((c) => c.includes("/resources"))).toHaveLength(1);
  });

  it("refuses a window with no figure and no reason instead of wording it", async () => {
    const broken = { ...window("2026-11-06T11:45:00Z", [40, 70], [33, 35]), processorPeakPercent: null };
    answers["GET /machines/devlinux/resources"] = () => json(200, history([broken], factories));
    draw();

    fireEvent.click(within(row("devlinux")).getByRole("button", { name: "Show devlinux's last 24 hours" }));

    expect((await screen.findByRole("alert")).textContent).toContain("could not read the machine's resource history");
    expect(screen.queryByTestId("fmr-chart-CPU")).toBeNull();
  });

  it("refuses a history without the Gateway's span instead of drawing it against an invented clock", async () => {
    answers["GET /machines/devlinux/resources"] = () =>
      json(200, { ...history([window("2026-11-06T11:45:00Z", [40, 70], [33, 35])], factories), fromUtc: undefined, toUtc: undefined });
    draw();

    fireEvent.click(within(row("devlinux")).getByRole("button", { name: "Show devlinux's last 24 hours" }));

    expect((await screen.findByRole("alert")).textContent).toContain("could not read the machine's resource history");
    expect(screen.queryByTestId("fmr-chart-CPU")).toBeNull();
  });

  it("shows the Gateway's empty message instead of an empty chart", async () => {
    answers["GET /machines/demo-win11/resources"] = () =>
      json(200, history([], [], { machine: "demo-win11", emptyMessage: "No resource summaries from demo-win11 in the last 24 hours.", noFactoriesMessage: null }));
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
