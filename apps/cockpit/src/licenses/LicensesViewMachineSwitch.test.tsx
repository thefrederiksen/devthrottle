// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, cleanup, within, fireEvent } from "@testing-library/react";

// SWITCH THIS MACHINE TO... AND THE TILE'S PACE, AT THE WIRE (the follow-up to the 8 November 2026 redesign). Only the
// browser's fetch is replaced: what these see is what leaves the browser and what a person then reads. What they pin:
//   - the drawer's button can be pressed exactly where the Gateway says canSwitchNow, with its sentence when it cannot;
//   - the confirmation lists the Gateway's targets for that machine, starts on the Gateway's choice, keeps a target it
//     cannot take shut, and sends nothing until it is confirmed;
//   - the one write is an application/json POST naming the machine and the target picked, and the answer is shown;
//   - a refusal is shown in the Gateway's sentence and the confirmation stays open;
//   - a license's tile shows the Gateway's pace sentence, and nothing where the Gateway sends none.
import { LicensesView } from "./LicensesView";
import { APPROVERS_EMPTY, FORECAST, LOG_PAGE, OVERVIEW, SWITCH_NOW_ACCEPTED, copyOf } from "./fixtures";

const SWITCH = "POST /gateway/licenses/machines/switch-now";
const ACCOUNT_A = "account-a@example.com";
const ACCOUNT_B = "account-b@example.com";
const ACCOUNT_C = "account-c@example.com";

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
let overview: typeof OVERVIEW;

const switches = (): Sent[] => sent.filter((s) => s.call === SWITCH);
const mac = () => overview.machines.find((m) => m.machineName === "the-mac")!;

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
      return answer === undefined ? json(200, {}) : answer();
    }),
  );
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

async function openDrawer(label: string): Promise<HTMLElement> {
  fireEvent.click(await screen.findByRole("button", { name: `More about ${label}` }));
  return screen.getByTestId("lic-drawer");
}

async function openSwitch(): Promise<HTMLElement> {
  fireEvent.click(within(await openDrawer("the-mac")).getByRole("button", { name: "Switch this machine to..." }));
  return screen.getByRole("alertdialog", { name: "Switch the-mac now?" });
}

describe("LicensesView: Switch this machine to..., in the machine's drawer", () => {
  it("cannot be pressed where the Gateway does not offer it, and shows the Gateway's reason", async () => {
    const why = "the-mac cannot be switched now: a switch sent to it has not finished.";
    Object.assign(mac(), { canSwitchNow: false, switchNowText: why, switchTargets: [] });
    render(<LicensesView />);

    const drawer = await openDrawer("the-mac");
    expect((within(drawer).getByRole("button", { name: "Switch this machine to..." }) as HTMLButtonElement).disabled).toBe(true);
    expect(within(drawer).getByTestId("lic-drawer-switch-text").textContent).toBe(why);
  });

  it("starts on no target where the Gateway names none, and cannot be confirmed until one is picked", async () => {
    mac().switchNowTargetAccountEmail = null;
    render(<LicensesView />);

    const dialog = await openSwitch();
    expect(within(dialog).getAllByRole("radio").some((r) => (r as HTMLInputElement).checked)).toBe(false);
    expect((within(dialog).getByRole("button", { name: "Switch now" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("lists the Gateway's targets for the machine, starts on the Gateway's choice, and sends nothing when cancelled", async () => {
    render(<LicensesView />);

    const dialog = await openSwitch();
    expect(within(dialog).getByText(`Moves the-mac off ${ACCOUNT_A}. The other machines stay where they are.`)).toBeTruthy();
    const choice = within(dialog).getByRole("radio", { name: new RegExp(ACCOUNT_B.replace(/\./g, "\\.")) }) as HTMLInputElement;
    const full = within(dialog).getByRole("radio", { name: new RegExp(ACCOUNT_C.replace(/\./g, "\\.")) }) as HTMLInputElement;
    expect(choice.checked).toBe(true);
    expect(full.disabled).toBe(true);
    expect(within(dialog).getByText(`${ACCOUNT_C} has no room: it is at 99% of its 5-hour window.`)).toBeTruthy();

    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("alertdialog")).toBeNull();
    expect(switches()).toEqual([]);
  });

  it("on confirmation sends one application/json POST naming the machine and the target, and shows the Gateway's answer", async () => {
    answers[SWITCH] = () => json(200, SWITCH_NOW_ACCEPTED);
    render(<LicensesView />);

    fireEvent.click(within(await openSwitch()).getByRole("button", { name: "Switch now" }));

    expect((await screen.findByTestId("lic-switch-answer-text")).textContent).toBe(SWITCH_NOW_ACCEPTED.text);
    expect(switches()).toEqual([
      {
        call: SWITCH,
        contentType: "application/json",
        body: { machineId: mac().machineId, expectedAccountEmail: ACCOUNT_A, targetAccountEmail: ACCOUNT_B },
      },
    ]);
    expect(screen.queryByRole("alertdialog")).toBeNull();
  });

  it("shows a refusal in the Gateway's sentence and keeps the confirmation open", async () => {
    const why = `${ACCOUNT_B} has no room: it is at 98% of its weekly limit.`;
    answers[SWITCH] = () => json(409, { code: "target-full", error: why });
    render(<LicensesView />);

    const dialog = await openSwitch();
    fireEvent.click(within(dialog).getByRole("button", { name: "Switch now" }));

    expect(await within(dialog).findByText(why)).toBeTruthy();
    expect(screen.getByRole("alertdialog")).toBeTruthy();
    expect(screen.queryByTestId("lic-switch-answer")).toBeNull();
  });
});

describe("LicensesView: the pace on a license's tile", () => {
  it("shows the Gateway's pace sentence on the tile that has one, and none on the others", async () => {
    render(<LicensesView />);

    const tiles = await screen.findAllByTestId("lic-tile");
    const tileOf = (email: string) => tiles.find((t) => within(t).queryByText(email) !== null)!;
    expect(within(tileOf(ACCOUNT_A)).getByTestId("lic-tile-pace").textContent?.trim()).toBe("+2.5% an hour");
    expect(within(tileOf(ACCOUNT_B)).queryByTestId("lic-tile-pace")).toBeNull();
    expect(within(tileOf(ACCOUNT_C)).queryByTestId("lic-tile-pace")).toBeNull();
  });
});
