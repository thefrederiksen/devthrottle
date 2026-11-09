// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, cleanup, waitFor, within, fireEvent } from "@testing-library/react";

// SWITCH NOW, AT THE WIRE, from the status card (the redesign of 8 November 2026 deleted the account cards, and with them
// the renewal date of a subscription, which nothing on the page sets any more).
//
// Nothing of the page or of its client is replaced here - only the browser's fetch. So what these tests see is what
// leaves the browser (the route, the method, the content type, the body) and what a person then reads, worded by the
// same code that words it in the Cockpit. The answers are the Gateway's own examples (fixtures.ts). What they pin:
//   - both writes are sent as application/json, to their own route, with exactly the body the Gateway reads;
//   - the page NEVER sends Switch now without a target where the Gateway chose none;
//   - a refusal - 403 owner_only, 409 - is shown in the Gateway's sentence, word for word, and nothing else;
//   - an accepted answer is shown in the Gateway's sentences, and a switch that had not finished is read from its
//     row of the switch log, never asked for again;
//   - a stored failed renewal is shown in the machine's drawer as given, or not at all.
import { LicensesView } from "./LicensesView";
import {
  APPROVERS_EMPTY,
  FORECAST,
  LOG_PAGE,
  OVERVIEW,
  RENEWAL_FAILED_FIELDS,
  SWITCH_NOW_ACCEPTED,
  SWITCH_NOW_OWNER_ONLY,
  SWITCH_NOW_REFUSED,
  copyOf,
} from "./fixtures";

const ACCOUNT_A = "account-a@example.com";
const ACCOUNT_B = "account-b@example.com";
const ACCOUNT_C = "account-c@example.com";
const SWITCH_NOW = "POST /gateway/licenses/switch-now";

interface Sent {
  call: string;
  contentType: string | null;
  text: string | null;
  body: Record<string, unknown> | null;
}

type Answer = () => Response | Promise<Response>;

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

let sent: Sent[];
let answers: Record<string, Answer>;
let overview: typeof OVERVIEW;

/** Every write the page sent to a Licenses route. */
function writes(): Sent[] {
  return sent.filter((s) => s.call === SWITCH_NOW);
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
      let body: Record<string, unknown> | null = null;
      try {
        body = text === null ? null : (JSON.parse(text) as Record<string, unknown>);
      } catch {
        body = null;
      }
      sent.push({ call, contentType: new Headers(init?.headers).get("Content-Type"), text, body });
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

/** Open the Switch now confirmation of account A, from the status card, and return it. */
async function openSwitchNow(): Promise<HTMLElement> {
  fireEvent.click(within(await screen.findByTestId("lic-status")).getByRole("button", { name: "Switch now" }));
  return screen.getByRole("alertdialog", { name: "Switch account-a@example.com now?" });
}

function confirmButton(dialog: HTMLElement): HTMLButtonElement {
  return within(dialog).getByRole("button", { name: "Switch now" }) as HTMLButtonElement;
}

/** The Gateway chose no target for account A: Switch now is offered only because one can be named by hand. */
function noTargetChosen(): void {
  const a = overview.accounts[0];
  a.switchNowTargetAccountEmail = null;
  a.switchNowText = "No other account has room for the-mac and the-windows-machine. A target can still be chosen by hand.";
  for (const option of a.switchTargets) {
    option.isAutomaticChoice = false;
    option.canChoose = true;
    option.unavailableText = null;
  }
}

describe("LicensesView: Switch now at the wire", () => {
  it("sends one application/json POST naming the account, with no target for the Gateway's own choice, and shows the Gateway's answer", async () => {
    answers[SWITCH_NOW] = () => json(200, SWITCH_NOW_ACCEPTED);
    render(<LicensesView />);
    const dialog = await openSwitchNow();
    expect(writes()).toEqual([]);

    fireEvent.click(confirmButton(dialog));

    const answer = await screen.findByTestId("lic-switch-answer");
    expect(writes().length).toBe(1);
    expect(writes()[0].call).toBe(SWITCH_NOW);
    expect(writes()[0].contentType).toBe("application/json");
    // The Gateway's choice is sent as no target: the automatic moves the card's sentence describes.
    expect(writes()[0].body).toEqual({ accountEmail: ACCOUNT_A, targetAccountEmail: null });
    // The whole outcome in the Gateway's one sentence, then the machine's row in the Gateway's words and tone.
    expect(within(answer).getByTestId("lic-switch-answer-text").textContent).toBe(SWITCH_NOW_ACCEPTED.text);
    // The WHOLE row is the Gateway's label, a space and the Gateway's sentence: the page adds no word anywhere in it.
    const machine = within(answer).getByTestId("lic-switch-answer-machine");
    expect(machine.textContent).toBe(`Waiting for the machine ${SWITCH_NOW_ACCEPTED.switches[0].summary}`);
    expect(machine.querySelector(".lic-pill")?.className).toContain("lic-tone-warning");
    expect(screen.queryByRole("alertdialog")).toBeNull();
  });

  it("shows a machine that was left out in the Gateway's own sentence, and adds no name of its own to it", async () => {
    const why = "the-linux-machine (second) is not switched: its last report is too old.";
    const accepted = copyOf(SWITCH_NOW_ACCEPTED);
    accepted.skipped = [
      { machineName: "the-linux-machine", osUser: "second", label: "the-linux-machine (second)", why },
      { machineName: "the-linux-machine", osUser: "owner", label: "the-linux-machine (owner)", why: "the-linux-machine (owner) is not switched: a switch sent to it has not finished." },
    ];
    answers[SWITCH_NOW] = () => json(200, accepted);
    render(<LicensesView />);
    fireEvent.click(confirmButton(await openSwitchNow()));

    await screen.findByTestId("lic-switch-answer");
    // Two users of one computer are two machines: both are listed, each with exactly its own sentence.
    expect(screen.getAllByTestId("lic-switch-answer-skipped").map((row) => row.textContent)).toEqual([why, accepted.skipped[1].why]);
  });

  it("reads how a switch ended from its row of the switch log, and never sends the same Switch now again", async () => {
    answers[SWITCH_NOW] = () => json(200, SWITCH_NOW_ACCEPTED);
    render(<LicensesView />);
    const dialog = await openSwitchNow();
    // By the time the page reads the log again, the Gateway's once-a-minute check has ended the switch.
    const ended = copyOf(LOG_PAGE);
    ended.entries[0] = {
      ...ended.entries[0],
      entryId: SWITCH_NOW_ACCEPTED.switches[0].entryId,
      state: "switched",
      stateText: "Switched",
      stateTone: "good",
      summary: "machine-of-one switched from account-a@example.com to account-b@example.com.",
    };
    answers["GET /gateway/licenses/log"] = () => json(200, ended);

    fireEvent.click(confirmButton(dialog));

    const answer = await screen.findByTestId("lic-switch-answer");
    await waitFor(() => expect(within(answer).getByTestId("lic-switch-answer-machine").textContent).toContain("Switched"));
    const machine = within(answer).getByTestId("lic-switch-answer-machine");
    expect(machine.textContent).toBe("Switched machine-of-one switched from account-a@example.com to account-b@example.com.");
    expect(machine.querySelector(".lic-pill")?.className).toContain("lic-tone-good");
    expect(writes().length).toBe(1);
  });

  it("shows a 403 owner_only in the Gateway's sentence, word for word, and keeps the confirmation open", async () => {
    answers[SWITCH_NOW] = () => json(403, SWITCH_NOW_OWNER_ONLY);
    render(<LicensesView />);
    const dialog = await openSwitchNow();

    fireEvent.click(confirmButton(dialog));

    await waitFor(() => expect(dialog.querySelector(".ui-confirm-error")).not.toBeNull());
    expect(dialog.querySelector(".ui-confirm-error")?.textContent).toBe(SWITCH_NOW_OWNER_ONLY.error);
    expect(SWITCH_NOW_OWNER_ONLY.error).toContain("may switch the machines of a subscription to another one");
    expect(screen.getByRole("alertdialog")).toBeTruthy();
    expect(screen.queryByTestId("lic-switch-answer")).toBeNull();
    expect(writes().length).toBe(1);
  });

  it("shows a 409 refusal in the Gateway's sentence, word for word", async () => {
    answers[SWITCH_NOW] = () => json(409, SWITCH_NOW_REFUSED);
    render(<LicensesView />);
    const dialog = await openSwitchNow();

    fireEvent.click(confirmButton(dialog));

    await waitFor(() => expect(dialog.querySelector(".ui-confirm-error")).not.toBeNull());
    expect(dialog.querySelector(".ui-confirm-error")?.textContent).toBe(SWITCH_NOW_REFUSED.error);
    expect(screen.queryByTestId("lic-switch-answer")).toBeNull();
  });
});

describe("LicensesView: Switch now when the Gateway chose no target", () => {
  it("cannot be confirmed and sends nothing until a target is picked by hand, then sends that target", async () => {
    noTargetChosen();
    answers[SWITCH_NOW] = () => json(200, { ...copyOf(SWITCH_NOW_ACCEPTED), targetAccountEmail: ACCOUNT_C });
    render(<LicensesView />);
    const dialog = await openSwitchNow();

    // The Gateway's sentence says a target can be chosen by hand; none is selected, and the confirmation is shut.
    expect(within(dialog).getByText(overview.accounts[0].switchNowText)).toBeTruthy();
    expect((within(dialog).getAllByRole("radio") as HTMLInputElement[]).some((radio) => radio.checked)).toBe(false);
    expect(confirmButton(dialog).disabled).toBe(true);
    fireEvent.click(confirmButton(dialog));
    await Promise.resolve();
    expect(writes()).toEqual([]);
    expect(screen.getByRole("alertdialog")).toBeTruthy();

    fireEvent.click(within(dialog).getByRole("radio", { name: /account-c@example\.com/ }));
    expect(confirmButton(dialog).disabled).toBe(false);
    fireEvent.click(confirmButton(dialog));

    await screen.findByTestId("lic-switch-answer");
    expect(writes().length).toBe(1);
    expect(writes()[0].body).toEqual({ accountEmail: ACCOUNT_A, targetAccountEmail: ACCOUNT_C });
  });

  it("can be confirmed at once where the Gateway did choose a target", async () => {
    render(<LicensesView />);
    expect(confirmButton(await openSwitchNow()).disabled).toBe(false);
  });
});

describe("LicensesView: a stored failed renewal", () => {
  it("shows a stored failed renewal in the machine's drawer, on the one sign-in it is about, in the Gateway's sentence and tone", async () => {
    // The sign-in for account B on the-windows-machine; the-mac holds one for the same account that renewed.
    const windows = overview.machines.find((m) => m.machineName === "the-windows-machine");
    const spare = windows?.spares.find((h) => h.accountEmail === ACCOUNT_B);
    if (spare === undefined) throw new Error("the example overview has no spare for account B on the-windows-machine");
    Object.assign(spare, RENEWAL_FAILED_FIELDS);
    render(<LicensesView />);

    fireEvent.click(await screen.findByRole("button", { name: "More about the-windows-machine" }));
    const failed = within(screen.getByTestId("lic-drawer")).getAllByTestId("lic-drawer-renewal-failed");
    expect(failed.length).toBe(1);
    expect(failed[0].textContent).toBe(RENEWAL_FAILED_FIELDS.renewalFailedText);
    expect(failed[0].className).toContain("lic-tone-bad");
    expect(failed[0].closest("[data-testid=lic-drawer-signin]")?.textContent).toContain(ACCOUNT_B);
    fireEvent.click(within(screen.getByTestId("lic-drawer")).getByRole("button", { name: "Close" }));

    fireEvent.click(screen.getByRole("button", { name: "More about the-mac" }));
    expect(within(screen.getByTestId("lic-drawer")).queryByTestId("lic-drawer-renewal-failed")).toBeNull();
  });
});
