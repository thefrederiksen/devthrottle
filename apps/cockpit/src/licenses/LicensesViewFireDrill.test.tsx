// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, cleanup, within, fireEvent } from "@testing-library/react";

// THE FIRE DRILL CARD, AT THE WIRE (mission "Licenses - Always Ready, Loud When Not", the follow-up to step 7). Only the
// browser's fetch is replaced, and every answer is the Gateway's own example (fixtures.ts, from cockpit-api.md section
// 8). What they pin:
//   - the drill's line is drawn exactly as the Gateway sent it: not set up, passed with its run of passes, failed;
//   - choosing the drill machine is one application/json PUT naming it, and "No drill machine" sends null;
//   - Run now is offered only when the Gateway names a drill machine, confirms first, and sends one POST;
//   - every refusal, the owner-only ones included, is shown in the Gateway's own sentence.
import { LicensesView } from "./LicensesView";
import {
  APPROVERS_EMPTY,
  FIRE_DRILL_FAILED,
  FIRE_DRILL_MACHINE_OWNER_ONLY,
  FIRE_DRILL_NOT_SET_UP,
  FIRE_DRILL_PASSED,
  FIRE_DRILL_RUN_OWNER_ONLY,
  FIRE_DRILL_UNKNOWN_MACHINE,
  FORECAST,
  LOG_PAGE,
  OVERVIEW,
  copyOf,
} from "./fixtures";

const READ = "GET /gateway/licenses/fire-drill";
const CHOOSE = "PUT /gateway/licenses/fire-drill/machine";
const RUN = "POST /gateway/licenses/fire-drill/run";

interface Sent {
  call: string;
  contentType: string | null;
  body: Record<string, unknown> | null;
}

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

let sent: Sent[];
let answers: Record<string, () => Response>;

const writes = (call: string): Sent[] => sent.filter((s) => s.call === call);

/** The fixtures name the-windows-machine as the drill machine, by its identifier in OVERVIEW. */
const WINDOWS = OVERVIEW.machines.find((m) => m.label === "the-windows-machine")!;

beforeEach(() => {
  sent = [];
  answers = {
    "GET /gateway/licenses": () => json(200, OVERVIEW),
    "GET /gateway/licenses/forecast": () => json(200, FORECAST),
    "GET /gateway/licenses/log": () => json(200, LOG_PAGE),
    "GET /gateway/licenses/approvers": () => json(200, APPROVERS_EMPTY),
    [READ]: () => json(200, FIRE_DRILL_NOT_SET_UP),
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

async function line(): Promise<HTMLElement> {
  return screen.findByTestId("lic-fire-drill-line");
}

async function picker(): Promise<HTMLSelectElement> {
  return (await screen.findByTestId("lic-fire-drill-machine")) as HTMLSelectElement;
}

describe("LicensesView: the fire drill card", () => {
  it("fixtures: the drill machine of the examples is the-windows-machine of the overview", () => {
    expect(FIRE_DRILL_PASSED.machineId).toBe(WINDOWS.machineId);
    expect(FIRE_DRILL_FAILED.machineId).toBe(WINDOWS.machineId);
  });

  it("not set up: shows the Gateway's line, chooses no machine, and offers no Run now", async () => {
    render(<LicensesView />);

    const drawn = await line();
    expect(drawn.textContent).toBe(FIRE_DRILL_NOT_SET_UP.line!.text);
    expect(drawn.className).toContain("lic-tone-warning");
    expect((await picker()).value).toBe("");
    expect(within(await picker()).getAllByRole("option").map((o) => o.textContent)).toEqual([
      "No drill machine",
      ...OVERVIEW.machines.map((m) => m.label),
    ]);
    expect(screen.queryByTestId("lic-fire-drill-run")).toBeNull();
  });

  it("passed with a streak: shows the Gateway's line as given, in the good tone, with its machine chosen", async () => {
    answers[READ] = () => json(200, FIRE_DRILL_PASSED);
    render(<LicensesView />);

    const drawn = await line();
    expect(drawn.textContent).toBe(FIRE_DRILL_PASSED.line!.text);
    expect(drawn.textContent).toContain("2 drills passed in a row.");
    expect(drawn.className).toContain("lic-tone-good");
    expect((await picker()).value).toBe(WINDOWS.machineId);
    expect(screen.getByTestId("lic-fire-drill-run")).toBeTruthy();
  });

  it("failed: shows the Gateway's line as given, not in the good tone", async () => {
    answers[READ] = () => json(200, FIRE_DRILL_FAILED);
    render(<LicensesView />);

    const drawn = await line();
    expect(drawn.textContent).toBe(FIRE_DRILL_FAILED.line!.text);
    expect(drawn.getAttribute("data-passed")).toBe("false");
    expect(drawn.className).toContain("lic-tone-warning");
  });

  it("choosing a machine sends one application/json PUT naming it and shows the answer", async () => {
    answers[CHOOSE] = () => json(200, FIRE_DRILL_PASSED);
    render(<LicensesView />);

    fireEvent.change(await picker(), { target: { value: WINDOWS.machineId } });

    expect(await screen.findByText(FIRE_DRILL_PASSED.line!.text)).toBeTruthy();
    expect(writes(CHOOSE)).toEqual([{ call: CHOOSE, contentType: "application/json", body: { machineId: WINDOWS.machineId } }]);
    expect((await picker()).value).toBe(WINDOWS.machineId);
  });

  it("No drill machine sends null, which clears the choice", async () => {
    answers[READ] = () => json(200, FIRE_DRILL_PASSED);
    answers[CHOOSE] = () => json(200, FIRE_DRILL_NOT_SET_UP);
    render(<LicensesView />);
    await screen.findByTestId("lic-fire-drill-run");

    fireEvent.change(await picker(), { target: { value: "" } });

    expect(await screen.findByText(FIRE_DRILL_NOT_SET_UP.line!.text)).toBeTruthy();
    expect(writes(CHOOSE).map((s) => s.body)).toEqual([{ machineId: null }]);
    expect(screen.queryByTestId("lic-fire-drill-run")).toBeNull();
  });

  it.each([
    ["owner only", 403, FIRE_DRILL_MACHINE_OWNER_ONLY],
    ["a machine not on the page", 409, FIRE_DRILL_UNKNOWN_MACHINE],
  ])("a refused choice (%s) is shown in the Gateway's sentence, and the choice on screen stays the Gateway's", async (_name, status, refusal) => {
    answers[CHOOSE] = () => json(status, refusal);
    render(<LicensesView />);

    fireEvent.change(await picker(), { target: { value: WINDOWS.machineId } });

    expect((await screen.findByTestId("lic-fire-drill-error")).textContent).toBe(refusal.error);
    expect((await picker()).value).toBe("");
    expect((await line()).textContent).toBe(FIRE_DRILL_NOT_SET_UP.line!.text);
  });

  it("Run now confirms that it switches the machine out and back, sends one POST, and shows the drill's answer", async () => {
    answers[READ] = () => json(200, FIRE_DRILL_FAILED);
    answers[RUN] = () => json(200, FIRE_DRILL_PASSED);
    render(<LicensesView />);

    fireEvent.click(await screen.findByTestId("lic-fire-drill-run"));
    const dialog = screen.getByRole("alertdialog", { name: "Run the fire drill on the-windows-machine now?" });
    expect(within(dialog).getByText(/switches the-windows-machine out to a spare account and back again/)).toBeTruthy();
    expect(writes(RUN)).toEqual([]);

    fireEvent.click(within(dialog).getByRole("button", { name: "Run the drill" }));

    expect(await screen.findByText(FIRE_DRILL_PASSED.line!.text)).toBeTruthy();
    expect(writes(RUN).length).toBe(1);
    expect(screen.queryByRole("alertdialog")).toBeNull();
  });

  it("a refused run (owner only) is shown in the Gateway's sentence and the confirmation stays open", async () => {
    answers[READ] = () => json(200, FIRE_DRILL_PASSED);
    answers[RUN] = () => json(403, FIRE_DRILL_RUN_OWNER_ONLY);
    render(<LicensesView />);

    fireEvent.click(await screen.findByTestId("lic-fire-drill-run"));
    const dialog = screen.getByRole("alertdialog");
    fireEvent.click(within(dialog).getByRole("button", { name: "Run the drill" }));

    expect(await within(dialog).findByText(FIRE_DRILL_RUN_OWNER_ONLY.error)).toBeTruthy();
    expect(screen.getByRole("alertdialog")).toBeTruthy();
    expect((await line()).textContent).toBe(FIRE_DRILL_PASSED.line!.text);
  });

  it("a drill machine that has left the page is still shown as chosen, by its identifier", async () => {
    const gone = copyOf(FIRE_DRILL_PASSED);
    gone.machineId = "0123456789abcdef0123456789abcdef";
    answers[READ] = () => json(200, gone);
    render(<LicensesView />);

    expect((await screen.findByTestId("lic-fire-drill-run")).textContent).toBe("Run the drill now");
    expect((await picker()).value).toBe(gone.machineId);
  });
});
