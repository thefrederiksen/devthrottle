// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, cleanup, within, fireEvent } from "@testing-library/react";

// FORGET THIS MACHINE, AT THE WIRE (License Switching phase 6). Only the browser's fetch is replaced: what these tests
// see is what leaves the browser and what a person then reads. What they pin:
//   - the button is drawn exactly where the Gateway says canForget, and nowhere else;
//   - the confirmation shows the Gateway's own forgetText, and nothing is sent until it is confirmed;
//   - the one write is an application/json POST naming the machine, and the answer's sentence is shown as given;
//   - a refusal is shown in the Gateway's sentence and the confirmation stays open.
import { LicensesView } from "./LicensesView";
import { APPROVERS_EMPTY, FORECAST, LOG_PAGE, OVERVIEW, copyOf } from "./fixtures";

const FORGET = "POST /gateway/licenses/machines/forget";
const FORGET_TEXT =
  "Take the-windows-machine off this page, with what it holds, and end its open problems and their alarms. For a machine that is gone or renamed; if it reports again, it comes back.";
const FORGOTTEN =
  "the-windows-machine was forgotten: it is off the Licenses page, with what it held, and its open problems are over. If it reports again, it comes back.";

interface Sent {
  call: string;
  contentType: string | null;
  body: Record<string, unknown> | null;
}

type Answer = () => Response;

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

let sent: Sent[];
let answers: Record<string, Answer>;
let overview: typeof OVERVIEW;

const forgets = (): Sent[] => sent.filter((s) => s.call === FORGET);

/** The second machine has stopped reporting, and the Gateway offers to forget it. */
function silentWindowsMachine(): string {
  const machine = overview.machines.find((m) => m.machineName === "the-windows-machine")!;
  machine.status = "not-reporting";
  machine.statusText = "Not reporting since 2026-10-11 02:00 UTC";
  machine.statusTone = "warning";
  machine.canForget = true;
  machine.forgetText = FORGET_TEXT;
  return machine.machineId;
}

beforeEach(() => {
  sent = [];
  overview = copyOf(OVERVIEW);
  answers = {
    "GET /gateway/licenses": () => json(200, overview),
    "GET /gateway/licenses/forecast": () => json(200, FORECAST),
    "GET /gateway/licenses/log": () => json(200, LOG_PAGE),
    "GET /gateway/licenses/approvers": () => json(200, APPROVERS_EMPTY),
  };
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      const call = `${init?.method ?? "GET"} ${String(input).split("?")[0]}`;
      const text = typeof init?.body === "string" ? init.body : null;
      sent.push({
        call,
        contentType: new Headers(init?.headers).get("Content-Type"),
        body: text === null ? null : (JSON.parse(text) as Record<string, unknown>),
      });
      const answer = answers[call];
      // Anything else the Cockpit sends (its own error reports) is answered and ignored.
      return answer === undefined ? json(200, {}) : answer();
    }),
  );
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

async function openForget(): Promise<HTMLElement> {
  fireEvent.click(await screen.findByTestId("lic-machine-forget"));
  return screen.getByRole("alertdialog", { name: "Forget the-windows-machine?" });
}

describe("LicensesView: Forget this machine", () => {
  it("draws no button where the Gateway does not offer it", async () => {
    render(<LicensesView />);
    expect((await screen.findAllByTestId("lic-schedule-row")).length).toBe(2);
    expect(overview.machines.every((m) => !m.canForget)).toBe(true);
    expect(screen.queryByTestId("lic-machine-forget")).toBeNull();
  });

  it("confirms in the Gateway's sentence, sends one application/json POST naming the machine, and shows the answer", async () => {
    const machineId = silentWindowsMachine();
    answers[FORGET] = () => json(200, { text: FORGOTTEN, entry: { ...LOG_PAGE.entries[0], kind: "forgotten", kindText: "Machine forgotten", summary: FORGOTTEN } });
    render(<LicensesView />);

    const dialog = await openForget();
    expect(screen.getAllByTestId("lic-machine-forget").length).toBe(1);
    expect(within(dialog).getByText(FORGET_TEXT)).toBeTruthy();
    expect(forgets()).toEqual([]);

    fireEvent.click(within(dialog).getByRole("button", { name: "Forget" }));

    expect((await screen.findByTestId("lic-forget-answer")).textContent).toBe(FORGOTTEN);
    expect(forgets()).toEqual([{ call: FORGET, contentType: "application/json", body: { machineId } }]);
    expect(screen.queryByRole("alertdialog")).toBeNull();
  });

  it("shows a refusal in the Gateway's sentence and keeps the confirmation open", async () => {
    silentWindowsMachine();
    const why = "the-windows-machine is in the middle of a switch; it can be forgotten once the switch is over.";
    answers[FORGET] = () => json(409, { code: "switch_in_progress", error: why });
    render(<LicensesView />);

    const dialog = await openForget();
    fireEvent.click(within(dialog).getByRole("button", { name: "Forget" }));

    expect(await within(dialog).findByText(why)).toBeTruthy();
    expect(screen.getByRole("alertdialog")).toBeTruthy();
    expect(screen.queryByTestId("lic-forget-answer")).toBeNull();
    expect(forgets().length).toBe(1);
  });
});
