// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, cleanup, waitFor, within, fireEvent } from "@testing-library/react";

// THE TWO WRITES OF THE LICENSES PAGE, AT THE WIRE: Switch now and the renewal date of a subscription.
//
// Nothing of the page or of its client is replaced here - only the browser's fetch. So what these tests see is what
// leaves the browser (the route, the method, the content type, the body) and what a person then reads, worded by the
// same code that words it in the Cockpit. The answers are the Gateway's own examples (fixtures.ts). What they pin:
//   - both writes are sent as application/json, to their own route, with exactly the body the Gateway reads;
//   - the page NEVER sends Switch now without a target where the Gateway chose none;
//   - a refusal - 403 owner_only, 409 - is shown in the Gateway's sentence, word for word, and nothing else;
//   - an accepted answer is shown in the Gateway's sentences, and a switch that had not finished is read from its
//     row of the switch log, never asked for again;
//   - the renewal date shown after a save is the Gateway's answer, and clearing sends an explicit null;
//   - the two last fields of an account (barred) and a stored failed renewal are shown as given, or not at all.
import { LicensesView } from "./LicensesView";
import {
  BARRED_FIELDS,
  FORECAST,
  LOG_PAGE,
  OVERVIEW,
  OWNER_ONLY,
  RENEWAL_CLEARED,
  RENEWAL_FAILED_FIELDS,
  RENEWAL_SET,
  SWITCH_NOW_ACCEPTED,
  SWITCH_NOW_REFUSED,
  copyOf,
} from "./fixtures";

const ACCOUNT_A = "account-a@example.com";
const ACCOUNT_B = "account-b@example.com";
const ACCOUNT_C = "account-c@example.com";
const SWITCH_NOW = "POST /gateway/licenses/switch-now";
const RENEWAL = "PUT /gateway/licenses/accounts/renewal";

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
  return sent.filter((s) => s.call === SWITCH_NOW || s.call === RENEWAL);
}

beforeEach(() => {
  sent = [];
  overview = copyOf(OVERVIEW);
  answers = {
    "GET /gateway/licenses": () => json(200, overview),
    "GET /gateway/licenses/forecast": () => json(200, FORECAST),
    "GET /gateway/licenses/log": () => json(200, LOG_PAGE),
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

async function card(accountEmail: string): Promise<HTMLElement> {
  return await screen.findByRole("region", { name: accountEmail });
}

/** Open the Switch now confirmation of account A and return it. */
async function openSwitchNow(): Promise<HTMLElement> {
  fireEvent.click(within(await card(ACCOUNT_A)).getByRole("button", { name: "Switch now" }));
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
  it("sends one application/json POST naming the account and the target, and shows the Gateway's answer", async () => {
    answers[SWITCH_NOW] = () => json(200, SWITCH_NOW_ACCEPTED);
    render(<LicensesView />);
    const dialog = await openSwitchNow();
    expect(writes()).toEqual([]);

    fireEvent.click(confirmButton(dialog));

    const answer = await screen.findByTestId("lic-switch-answer");
    expect(writes().length).toBe(1);
    expect(writes()[0].call).toBe(SWITCH_NOW);
    expect(writes()[0].contentType).toBe("application/json");
    expect(writes()[0].body).toEqual({ accountEmail: ACCOUNT_A, targetAccountEmail: ACCOUNT_B });
    // The whole outcome in the Gateway's one sentence, then the machine's row in the Gateway's words and tone.
    expect(within(answer).getByTestId("lic-switch-answer-text").textContent).toBe(SWITCH_NOW_ACCEPTED.text);
    const machine = within(answer).getByTestId("lic-switch-answer-machine");
    expect(machine.textContent).toContain("Waiting for the machine");
    expect(machine.textContent).toContain(SWITCH_NOW_ACCEPTED.switches[0].summary);
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
    expect(machine.textContent).toContain("machine-of-one switched from account-a@example.com to account-b@example.com.");
    expect(machine.textContent).not.toContain("Waiting for the machine");
    expect(machine.querySelector(".lic-pill")?.className).toContain("lic-tone-good");
    expect(writes().length).toBe(1);
  });

  it("shows a 403 owner_only in the Gateway's sentence, word for word, and keeps the confirmation open", async () => {
    answers[SWITCH_NOW] = () => json(403, OWNER_ONLY);
    render(<LicensesView />);
    const dialog = await openSwitchNow();

    fireEvent.click(confirmButton(dialog));

    await waitFor(() => expect(dialog.querySelector(".ui-confirm-error")).not.toBeNull());
    expect(dialog.querySelector(".ui-confirm-error")?.textContent).toBe(OWNER_ONLY.error);
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

describe("LicensesView: the renewal date of a subscription", () => {
  /** Open the editor on a card and return the card. */
  async function edit(accountEmail: string): Promise<HTMLElement> {
    const drawn = await card(accountEmail);
    fireEvent.click(within(drawn).getByRole("button", { name: "Set renewal date" }));
    return drawn;
  }

  function dateField(drawn: HTMLElement, accountEmail: string): HTMLInputElement {
    return within(drawn).getByLabelText(`Renewal date of ${accountEmail}`) as HTMLInputElement;
  }

  it("sends one application/json PUT with the account and the date, and shows the Gateway's sentence at once", async () => {
    answers[RENEWAL] = () => json(200, RENEWAL_SET);
    render(<LicensesView />);
    const drawn = await edit(ACCOUNT_A);
    expect(within(drawn).getByTestId("lic-account-renewal").textContent).toBe("Max - Renewal date not set");
    // Nothing can be saved before a date is given, and there is nothing to clear.
    expect((within(drawn).getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(true);
    expect(within(drawn).queryByRole("button", { name: "Clear date" })).toBeNull();
    // From here the overview is not answered again, so what the card shows can only be the answer to the write.
    answers["GET /gateway/licenses"] = () => new Promise<Response>(() => undefined);

    fireEvent.change(dateField(drawn, ACCOUNT_A), { target: { value: "2026-10-17" } });
    fireEvent.click(within(drawn).getByRole("button", { name: "Save" }));

    await waitFor(() => expect(within(drawn).getByTestId("lic-account-renewal").textContent).toBe("Max - Renews 17 Oct"));
    expect(writes().length).toBe(1);
    expect(writes()[0].call).toBe(RENEWAL);
    expect(writes()[0].contentType).toBe("application/json");
    expect(writes()[0].body).toEqual({ accountEmail: ACCOUNT_A, renewsOn: "2026-10-17" });
    expect(within(drawn).queryByTestId("lic-renewal-editor")).toBeNull();
    // Another account's date is not touched.
    expect(within(await card(ACCOUNT_B)).getByTestId("lic-account-renewal").textContent).toBe("Max - Renewal date not set");
  });

  it("clears a date with an explicit null, which is the only thing the Gateway takes as clearing", async () => {
    overview.accounts[0].renewsOn = RENEWAL_SET.renewsOn;
    overview.accounts[0].renewsText = RENEWAL_SET.renewsText;
    answers[RENEWAL] = () => json(200, RENEWAL_CLEARED);
    render(<LicensesView />);
    const drawn = await edit(ACCOUNT_A);
    // The editor opens on the date that is stored.
    expect(dateField(drawn, ACCOUNT_A).value).toBe("2026-10-17");
    answers["GET /gateway/licenses"] = () => new Promise<Response>(() => undefined);

    fireEvent.click(within(drawn).getByRole("button", { name: "Clear date" }));

    await waitFor(() => expect(within(drawn).getByTestId("lic-account-renewal").textContent).toBe("Max - Renewal date not set"));
    expect(writes().length).toBe(1);
    expect(writes()[0].text).toBe(JSON.stringify({ accountEmail: ACCOUNT_A, renewsOn: null }));
  });

  it("shows a 403 owner_only in the Gateway's sentence, word for word, and leaves the date as it was", async () => {
    answers[RENEWAL] = () => json(403, OWNER_ONLY);
    render(<LicensesView />);
    const drawn = await edit(ACCOUNT_A);

    fireEvent.change(dateField(drawn, ACCOUNT_A), { target: { value: "2026-10-17" } });
    fireEvent.click(within(drawn).getByRole("button", { name: "Save" }));

    const refusal = await within(drawn).findByTestId("lic-renewal-error");
    expect(refusal.textContent).toBe(OWNER_ONLY.error);
    expect(within(drawn).getByTestId("lic-account-renewal").textContent).toBe("Max - Renewal date not set");
    // The editor stays open with what was typed, and can be used again.
    expect(dateField(drawn, ACCOUNT_A).value).toBe("2026-10-17");
    expect((within(drawn).getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(false);
  });

  it("sends nothing when the editor is cancelled", async () => {
    render(<LicensesView />);
    const drawn = await edit(ACCOUNT_A);
    fireEvent.change(dateField(drawn, ACCOUNT_A), { target: { value: "2026-10-17" } });

    fireEvent.click(within(drawn).getByRole("button", { name: "Cancel" }));

    expect(within(drawn).queryByTestId("lic-renewal-editor")).toBeNull();
    expect(writes()).toEqual([]);
  });
});

describe("LicensesView: an account barred from the automatic choice, and a stored failed renewal", () => {
  it("shows the Gateway's barred sentence and its time on that account, and nothing on an account that is not barred", async () => {
    Object.assign(overview.accounts[1], BARRED_FIELDS);
    render(<LicensesView />);

    const barred = within(await card(ACCOUNT_B)).getByTestId("lic-account-barred");
    expect(barred.textContent).toContain(BARRED_FIELDS.barredText as string);
    expect(barred.querySelector("time")?.getAttribute("datetime")).toBe(BARRED_FIELDS.barredUntilUtc);
    // The Gateway sends no tone for it, so the page gives it none.
    expect(barred.className).not.toContain("lic-tone-");
    expect(within(await card(ACCOUNT_A)).queryByTestId("lic-account-barred")).toBeNull();
    expect(within(await card(ACCOUNT_C)).queryByTestId("lic-account-barred")).toBeNull();
  });

  it("shows a stored failed renewal on the one sign-in it is about, in the Gateway's sentence and tone", async () => {
    // The sign-in for account B on the-windows-machine; the-mac holds one for the same account that renewed.
    const windows = overview.machines.find((m) => m.machineName === "the-windows-machine");
    const spare = windows?.spares.find((h) => h.accountEmail === ACCOUNT_B);
    if (spare === undefined) throw new Error("the example overview has no spare for account B on the-windows-machine");
    Object.assign(spare, RENEWAL_FAILED_FIELDS);
    render(<LicensesView />);

    const failed = within(await card(ACCOUNT_B)).getAllByTestId("lic-holding-renewal-failed");
    expect(failed.length).toBe(1);
    expect(failed[0].textContent).toBe(RENEWAL_FAILED_FIELDS.renewalFailedText);
    expect(failed[0].className).toContain("lic-tone-bad");
    expect(failed[0].closest("[data-testid=lic-holding]")?.textContent).toContain("the-windows-machine");
    // It is about the sign-in, not the subscription: the account's renewal line is untouched.
    expect(within(await card(ACCOUNT_B)).getByTestId("lic-account-renewal").textContent).toBe("Max - Renewal date not set");
  });
});
