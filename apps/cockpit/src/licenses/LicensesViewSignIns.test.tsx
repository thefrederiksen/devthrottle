// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach, beforeEach } from "vitest";
import { render, screen, cleanup, waitFor, within, fireEvent } from "@testing-library/react";

// THE SIGN-INS OF THE LICENSES PAGE, AT THE WIRE: who approves an account (read, set, remove), Add a spare and Renew
// now.
//
// Nothing of the page or of its client is replaced here - only the browser's fetch. So what these tests see is what
// leaves the browser (the route, the method, the content type, the body) and what a person then reads, worded by the
// same code that words it in the Cockpit. The answers are the Gateway's own examples (fixtures.ts). What they pin:
//   - each of the four writes goes to its own route, by its own method; a write with a body is application/json and
//     its body is exactly the fields the Gateway reads, and the removal names its account in the query with no body;
//   - nothing is sent until the person confirms, and Add a spare cannot be confirmed before an account is typed;
//   - a refusal - 400, 403 owner_only, 404, 409 - is shown in the Gateway's sentence, word for word, where the person
//     is looking, and what they typed stays;
//   - whether Add a spare or Renew now is offered is the Gateway's, per machine and per sign-in, and its sentence is
//     shown whole whether it is offered or not;
//   - a started sign-in is shown in the Gateway's sentences, and how it ended is read from its row of the log, never
//     asked for again;
//   - after an approver is set or removed the page shows the Gateway's sentence and reads the approvers again: it
//     keeps no list of its own.
import { LicensesView } from "./LicensesView";
import {
  APPROVERS,
  APPROVERS_EMPTY,
  APPROVER_REFUSED,
  APPROVER_REMOVED,
  APPROVER_REMOVE_OWNER_ONLY,
  APPROVER_SET,
  APPROVER_SET_OWNER_ONLY,
  FORECAST,
  LOG_PAGE,
  OVERVIEW,
  OVERVIEW_SIGN_IN_FIELDS,
  SIGN_IN_ALREADY_HELD,
  SIGN_IN_DIRECTOR_OFFLINE,
  SIGN_IN_LOG,
  SIGN_IN_OWNER_ONLY,
  SIGN_IN_RUNNING,
  SIGN_IN_STARTED,
  SIGN_IN_UNKNOWN_MACHINE,
  copyOf,
} from "./fixtures";

const ACCOUNT_A = "account-a@example.com";
const ACCOUNT_B = "account-b@example.com";
const ACCOUNT_C = "account-c@example.com";
const WINDOWS = "the-windows-machine";
const MAC = "the-mac";
const APPROVERS_ROUTE = "/gateway/licenses/approvers";
const ADD_SPARE = "POST /gateway/licenses/sign-ins/add-spare";
const RENEW = "POST /gateway/licenses/sign-ins/renew";
const SET_APPROVER = `PUT ${APPROVERS_ROUTE}`;
const REMOVE_APPROVER = `DELETE ${APPROVERS_ROUTE}`;
const READ_APPROVERS = `GET ${APPROVERS_ROUTE}`;

// The Gateway's refusal of each write to a caller that is not the owner's own signed-in phone or browser.
const START_OWNER_ONLY = SIGN_IN_OWNER_ONLY;
const SET_OWNER_ONLY = APPROVER_SET_OWNER_ONLY;
const REMOVE_OWNER_ONLY = APPROVER_REMOVE_OWNER_ONLY;

interface Sent {
  call: string;
  url: string;
  contentType: string | null;
  text: string | null;
}

type Answer = () => Response | Promise<Response>;

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

let sent: Sent[];
let answers: Record<string, Answer>;
let overview: typeof OVERVIEW;

/** Every write the page sent to a sign-in route. */
function writes(): Sent[] {
  return sent.filter((s) => [ADD_SPARE, RENEW, SET_APPROVER, REMOVE_APPROVER].includes(s.call));
}

function reads(call: string): number {
  return sent.filter((s) => s.call === call).length;
}

/**
 * The example overview with the Gateway's real example of the sign-in fields laid over it, each on the account, the
 * machine and the sign-in it names. After it: the-windows-machine is offered Add a spare, is renewing its sign-in for
 * account A (so Renew now is not offered there), and is offered Renew now for its spare of account B; the-mac's
 * Director is not connected, so nothing is offered on it; accounts A and B have an approver and account C has none.
 */
function withSignInFields(base: typeof OVERVIEW): typeof OVERVIEW {
  const laid = copyOf(base);
  for (const fields of OVERVIEW_SIGN_IN_FIELDS.accounts) {
    const account = laid.accounts.find((a) => a.accountEmail === fields.accountEmail);
    if (account === undefined) throw new Error(`the example overview has no ${fields.accountEmail}`);
    Object.assign(account, copyOf(fields));
  }
  for (const fields of OVERVIEW_SIGN_IN_FIELDS.machines) {
    const machine = laid.machines.find((m) => m.machineName === fields.machineName);
    if (machine === undefined) throw new Error(`the example overview has no ${fields.machineName}`);
    machine.canAddSpare = fields.canAddSpare;
    machine.addSpareText = fields.addSpareText;
    machine.signInsInProgress = copyOf(fields.signInsInProgress);
    const held = machine.active === null ? machine.spares : [machine.active, ...machine.spares];
    for (const one of fields.active === null ? fields.spares : [fields.active, ...fields.spares]) {
      const holding = held.find((h) => h.accountEmail === one.accountEmail);
      // The two pictures differ: the example's the-mac is on account C, which this overview's the-mac does not hold.
      if (holding !== undefined) Object.assign(holding, { canRenewNow: one.canRenewNow, renewNowText: one.renewNowText });
    }
  }
  return laid;
}

function machineId(machineName: string): string {
  const machine = overview.machines.find((m) => m.machineName === machineName);
  if (machine === undefined) throw new Error(`no ${machineName}`);
  return machine.machineId;
}

beforeEach(() => {
  sent = [];
  overview = withSignInFields(OVERVIEW);
  answers = {
    "GET /gateway/licenses": () => json(200, overview),
    "GET /gateway/licenses/forecast": () => json(200, FORECAST),
    "GET /gateway/licenses/log": () => json(200, LOG_PAGE),
    [READ_APPROVERS]: () => json(200, APPROVERS),
  };
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
      const url = String(input);
      const call = `${init?.method ?? "GET"} ${url.split("?")[0]}`;
      const text = typeof init?.body === "string" ? init.body : null;
      sent.push({ call, url, contentType: new Headers(init?.headers).get("Content-Type"), text });
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

/** The line of one machine's sign-in on the card of an account. */
async function holding(accountEmail: string, machineLabel: string): Promise<HTMLElement> {
  const lines = within(await card(accountEmail)).getAllByTestId("lic-holding");
  const line = lines.find((l) => l.querySelector(".lic-holding-machine")?.textContent === machineLabel);
  if (line === undefined) throw new Error(`no sign-in for ${accountEmail} on ${machineLabel}`);
  return line;
}

/** The row of one machine in the Sign-ins table. */
async function machineRow(label: string): Promise<HTMLElement> {
  const rows = await screen.findAllByTestId("lic-sign-in-machine");
  const row = rows.find((r) => r.querySelector(".lic-strong")?.textContent === label);
  if (row === undefined) throw new Error(`no row for ${label}`);
  return row;
}

function refusalIn(dialog: HTMLElement): string | null {
  return dialog.querySelector(".ui-confirm-error")?.textContent ?? null;
}

describe("LicensesView: what the overview says of sign-ins", () => {
  it("shows on each card who approves the account, in the Gateway's sentence and tone, and adds no word", async () => {
    render(<LicensesView />);

    const a = within(await card(ACCOUNT_A)).getByTestId("lic-account-approver");
    expect(a.textContent).toBe(OVERVIEW_SIGN_IN_FIELDS.accounts[0].approverText);
    expect(a.className).toContain("lic-tone-warning");
    const c = within(await card(ACCOUNT_C)).getByTestId("lic-account-approver");
    expect(c.textContent).toBe("No machine is set to approve sign-ins for this account. A machine that has its own browser profile for it approves there.");
    expect(c.className).toContain("lic-tone-neutral");
    expect(c.className).not.toContain("lic-tone-warning");
  });

  it("offers Renew now only on the sign-in the Gateway offers it for, and shows its sentence on every sign-in", async () => {
    render(<LicensesView />);

    // The spare of account B on the-windows-machine: offered, with what it does.
    const offered = await holding(ACCOUNT_B, WINDOWS);
    expect(within(offered).getByRole("button", { name: "Renew now" })).toBeTruthy();
    expect(within(offered).getByTestId("lic-holding-renew-text").textContent).toBe(
      "Signs the-windows-machine in to account-b@example.com again, replacing the sign-in it holds.",
    );
    // The same account on the-mac, whose Director is not connected: not offered, with why.
    const offline = await holding(ACCOUNT_B, MAC);
    expect(within(offline).queryByRole("button", { name: "Renew now" })).toBeNull();
    expect(within(offline).getByTestId("lic-holding-renew-text").textContent).toBe(
      "The Director on the-mac is not connected, so a sign-in cannot be started there.",
    );
    // Account A on the-windows-machine, where a renewal is already running: not offered, with why.
    const running = await holding(ACCOUNT_A, WINDOWS);
    expect(within(running).queryByRole("button", { name: "Renew now" })).toBeNull();
    expect(within(running).getByTestId("lic-holding-renew-text").textContent).toBe(
      "A sign-in for account-a@example.com on the-windows-machine is already running.",
    );
    expect(screen.getAllByRole("button", { name: "Renew now" }).length).toBe(1);
  });

  it("lists every machine with the Gateway's Add a spare sentence, the button only where it is offered, and the sign-ins that have not ended", async () => {
    render(<LicensesView />);

    const windows = await machineRow(WINDOWS);
    expect(within(windows).getByRole("button", { name: "Add a spare" })).toBeTruthy();
    expect(within(windows).getByTestId("lic-add-spare-text").textContent).toBe(
      "Signs the-windows-machine in to another account, to be held there as a spare.",
    );
    expect(within(windows).getAllByTestId("lic-sign-in-progress").map((line) => line.textContent)).toEqual([
      "the-windows-machine is renewing its sign-in for account-a@example.com.",
    ]);
    const mac = await machineRow(MAC);
    expect(within(mac).queryByRole("button", { name: "Add a spare" })).toBeNull();
    expect(within(mac).getByTestId("lic-add-spare-text").textContent).toBe(
      "The Director on the-mac is not connected, so a sign-in cannot be started there.",
    );
    expect(within(mac).queryByTestId("lic-sign-in-progress")).toBeNull();
    expect(screen.getAllByTestId("lic-sign-in-machine").length).toBe(2);
    expect(writes()).toEqual([]);
  });

  it("calls a machine by the Gateway's label in the Sign-ins table and in the confirmation", async () => {
    // Two users of one computer are two machines; the Gateway tells them apart in the label.
    const windows = overview.machines.find((m) => m.machineName === WINDOWS);
    if (windows === undefined) throw new Error("no the-windows-machine");
    windows.label = "the-windows-machine (owner)";
    render(<LicensesView />);

    fireEvent.click(within(await machineRow("the-windows-machine (owner)")).getByRole("button", { name: "Add a spare" }));

    expect(screen.getByRole("alertdialog", { name: "Add a spare on the-windows-machine (owner)?" })).toBeTruthy();
  });
});

describe("LicensesView: Renew now at the wire", () => {
  /** Open the Renew now confirmation of account B's spare on the-windows-machine and return it. */
  async function openRenew(): Promise<HTMLElement> {
    fireEvent.click(within(await holding(ACCOUNT_B, WINDOWS)).getByRole("button", { name: "Renew now" }));
    return screen.getByRole("alertdialog", { name: "Renew the sign-in for account-b@example.com on the-windows-machine?" });
  }

  function confirm(dialog: HTMLElement): HTMLButtonElement {
    return within(dialog).getByRole("button", { name: "Renew now" }) as HTMLButtonElement;
  }

  it("sends nothing until confirmed, then one application/json POST naming the account and the machine, and shows the Gateway's answer", async () => {
    answers[RENEW] = () => json(202, SIGN_IN_STARTED);
    render(<LicensesView />);
    const dialog = await openRenew();
    // The confirmation says what will happen in the Gateway's sentence for that sign-in.
    expect(dialog.querySelector(".ui-confirm-message")?.textContent).toBe(
      "Signs the-windows-machine in to account-b@example.com again, replacing the sign-in it holds.",
    );
    expect(writes()).toEqual([]);

    fireEvent.click(confirm(dialog));

    const answer = await screen.findByTestId("lic-sign-in-answer");
    expect(writes().length).toBe(1);
    expect(writes()[0].call).toBe(RENEW);
    expect(writes()[0].contentType).toBe("application/json");
    expect(writes()[0].text).toBe(JSON.stringify({ accountEmail: ACCOUNT_B, machineId: machineId(WINDOWS) }));
    // The whole outcome so far in the Gateway's one sentence, then the sign-in's row: the Gateway's label, a space and
    // the Gateway's sentence. The page adds no word anywhere in it.
    expect(within(answer).getByTestId("lic-sign-in-answer-text").textContent).toBe(SIGN_IN_STARTED.text);
    const row = within(answer).getByTestId("lic-sign-in-answer-row");
    expect(row.textContent).toBe("Signing in the-windows-machine is renewing its sign-in for account-a@example.com.");
    expect(row.querySelector(".lic-pill")?.className).toContain("lic-tone-neutral");
    expect(within(answer).queryByTestId("lic-sign-in-answer-reason")).toBeNull();
    expect(screen.queryByRole("alertdialog")).toBeNull();
  });

  it("reads how the sign-in ended from its row of the log, with its reason, and never starts it again", async () => {
    answers[RENEW] = () => json(202, SIGN_IN_STARTED);
    render(<LicensesView />);
    const dialog = await openRenew();
    // By the time the page reads the log again the sign-in has failed: the Gateway's own example of a failed
    // sign-in, on the row the answer named.
    const ended = copyOf(SIGN_IN_LOG);
    ended.entries = [{ ...ended.entries[2], entryId: SIGN_IN_STARTED.entry.entryId }];
    answers["GET /gateway/licenses/log"] = () => json(200, ended);

    fireEvent.click(confirm(dialog));

    const answer = await screen.findByTestId("lic-sign-in-answer");
    await waitFor(() => expect(within(answer).getByTestId("lic-sign-in-answer-row").textContent).toContain("Failed"));
    const row = within(answer).getByTestId("lic-sign-in-answer-row");
    expect(row.textContent).toBe("Failed The sign-in for account-b@example.com on the-windows-machine was not renewed.");
    expect(row.querySelector(".lic-pill")?.className).toContain("lic-tone-bad");
    expect(within(answer).getByTestId("lic-sign-in-answer-reason").textContent).toBe(
      "The machine set to approve this account had no Director connected.",
    );
    // What was started is still said as the Gateway said it.
    expect(within(answer).getByTestId("lic-sign-in-answer-text").textContent).toBe(SIGN_IN_STARTED.text);
    expect(writes().length).toBe(1);
  });

  it.each([
    ["409 sign_in_running", 409, SIGN_IN_RUNNING],
    ["409 director_offline", 409, SIGN_IN_DIRECTOR_OFFLINE],
    ["404 unknown_machine", 404, SIGN_IN_UNKNOWN_MACHINE],
    ["403 owner_only", 403, START_OWNER_ONLY],
  ])("shows a %s in the Gateway's sentence, word for word, and keeps the confirmation open", async (_name, status, refusal) => {
    answers[RENEW] = () => json(status, refusal);
    render(<LicensesView />);
    const dialog = await openRenew();

    fireEvent.click(confirm(dialog));

    await waitFor(() => expect(refusalIn(dialog)).not.toBeNull());
    expect(refusalIn(dialog)).toBe(refusal.error);
    expect(screen.getByRole("alertdialog")).toBeTruthy();
    expect(screen.queryByTestId("lic-sign-in-answer")).toBeNull();
    expect(writes().length).toBe(1);
  });

  it("takes the answer to a started sign-in off the screen when it is dismissed", async () => {
    answers[RENEW] = () => json(202, SIGN_IN_STARTED);
    render(<LicensesView />);
    fireEvent.click(confirm(await openRenew()));
    const answer = await screen.findByTestId("lic-sign-in-answer");

    fireEvent.click(within(answer).getByRole("button", { name: "Dismiss" }));

    expect(screen.queryByTestId("lic-sign-in-answer")).toBeNull();
    expect(writes().length).toBe(1);
  });

  it("sends nothing when the confirmation is cancelled", async () => {
    render(<LicensesView />);
    const dialog = await openRenew();

    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByRole("alertdialog")).toBeNull();
    expect(writes()).toEqual([]);
  });
});

describe("LicensesView: Add a spare at the wire", () => {
  /** Open the Add a spare confirmation of the-windows-machine and return it. */
  async function openAddSpare(): Promise<HTMLElement> {
    fireEvent.click(within(await machineRow(WINDOWS)).getByRole("button", { name: "Add a spare" }));
    return screen.getByRole("alertdialog", { name: "Add a spare on the-windows-machine?" });
  }

  function confirm(dialog: HTMLElement): HTMLButtonElement {
    return within(dialog).getByRole("button", { name: "Add a spare" }) as HTMLButtonElement;
  }

  function accountField(dialog: HTMLElement): HTMLInputElement {
    return within(dialog).getByLabelText("Claude account to sign in to") as HTMLInputElement;
  }

  it("cannot be confirmed and sends nothing before an account is typed, then sends that account and the machine", async () => {
    answers[ADD_SPARE] = () => json(202, SIGN_IN_STARTED);
    render(<LicensesView />);
    const dialog = await openAddSpare();
    // The Gateway's sentence for the machine, and an empty field: which account is the person's own choice.
    expect(within(dialog).getByText("Signs the-windows-machine in to another account, to be held there as a spare.")).toBeTruthy();
    expect(accountField(dialog).value).toBe("");
    expect(confirm(dialog).disabled).toBe(true);
    fireEvent.click(confirm(dialog));
    // Space alone is not an account.
    fireEvent.change(accountField(dialog), { target: { value: "   " } });
    expect(confirm(dialog).disabled).toBe(true);
    fireEvent.click(confirm(dialog));
    await Promise.resolve();
    expect(writes()).toEqual([]);

    fireEvent.change(accountField(dialog), { target: { value: " account-d@example.com " } });
    expect(confirm(dialog).disabled).toBe(false);
    fireEvent.click(confirm(dialog));

    const answer = await screen.findByTestId("lic-sign-in-answer");
    expect(writes().length).toBe(1);
    expect(writes()[0].call).toBe(ADD_SPARE);
    expect(writes()[0].contentType).toBe("application/json");
    // The account as typed, less the space around it; the machine by the identifier the overview gave.
    expect(writes()[0].text).toBe(JSON.stringify({ accountEmail: "account-d@example.com", machineId: machineId(WINDOWS) }));
    expect(within(answer).getByTestId("lic-sign-in-answer-text").textContent).toBe(SIGN_IN_STARTED.text);
    expect(screen.queryByRole("alertdialog")).toBeNull();
  });

  it("shuts the account field while the call is out, and opens it again with what was typed when the Gateway refuses", async () => {
    let answerNow: (response: Response) => void = () => undefined;
    answers[ADD_SPARE] = () => new Promise<Response>((resolve) => (answerNow = resolve));
    render(<LicensesView />);
    const dialog = await openAddSpare();
    expect(accountField(dialog).disabled).toBe(false);
    fireEvent.change(accountField(dialog), { target: { value: ACCOUNT_A } });

    fireEvent.click(confirm(dialog));

    await waitFor(() => expect(accountField(dialog).disabled).toBe(true));
    expect(accountField(dialog).value).toBe(ACCOUNT_A);
    answerNow(json(409, SIGN_IN_ALREADY_HELD));
    await waitFor(() => expect(accountField(dialog).disabled).toBe(false));
    expect(accountField(dialog).value).toBe(ACCOUNT_A);
    expect(writes().length).toBe(1);
  });

  it("opens empty every time: the account typed for one machine is not carried to the next", async () => {
    render(<LicensesView />);
    const first = await openAddSpare();
    fireEvent.change(accountField(first), { target: { value: "account-d@example.com" } });
    fireEvent.click(within(first).getByRole("button", { name: "Cancel" }));

    const second = await openAddSpare();

    expect(accountField(second).value).toBe("");
    expect(confirm(second).disabled).toBe(true);
    expect(writes()).toEqual([]);
  });

  it.each([
    ["409 already_held", 409, SIGN_IN_ALREADY_HELD],
    ["409 director_offline", 409, SIGN_IN_DIRECTOR_OFFLINE],
    ["403 owner_only", 403, START_OWNER_ONLY],
  ])("shows a %s in the Gateway's sentence, word for word, and keeps what was typed", async (_name, status, refusal) => {
    answers[ADD_SPARE] = () => json(status, refusal);
    render(<LicensesView />);
    const dialog = await openAddSpare();
    fireEvent.change(accountField(dialog), { target: { value: ACCOUNT_A } });

    fireEvent.click(confirm(dialog));

    await waitFor(() => expect(refusalIn(dialog)).not.toBeNull());
    expect(refusalIn(dialog)).toBe(refusal.error);
    expect(accountField(dialog).value).toBe(ACCOUNT_A);
    expect(screen.queryByTestId("lic-sign-in-answer")).toBeNull();
    expect(writes().length).toBe(1);
  });
});

describe("LicensesView: who approves the sign-ins of an account", () => {
  function editor(): HTMLElement {
    return screen.getByTestId("lic-approver-editor");
  }

  function field(label: string): HTMLInputElement {
    return within(editor()).getByLabelText(label) as HTMLInputElement;
  }

  function type(account: string, machine: string, user: string, profile: string): void {
    fireEvent.change(field("Claude account"), { target: { value: account } });
    fireEvent.change(field("Approving machine"), { target: { value: machine } });
    fireEvent.change(field("Operating system user on that machine"), { target: { value: user } });
    fireEvent.change(field("Browser profile on that machine"), { target: { value: profile } });
  }

  function saveButton(): HTMLButtonElement {
    return within(editor()).getByRole("button", { name: "Save" }) as HTMLButtonElement;
  }

  it("shows the Gateway's sentence in place of the list when no approver is set", async () => {
    answers[READ_APPROVERS] = () => json(200, APPROVERS_EMPTY);
    render(<LicensesView />);

    expect((await screen.findByTestId("lic-approvers-empty")).textContent).toBe(APPROVERS_EMPTY.emptyText);
    expect(screen.queryByTestId("lic-approvers")).toBeNull();
    expect(screen.queryByTestId("lic-approver")).toBeNull();
  });

  it("takes whether the list is empty from the Gateway's own word for it, not from counting the rows", async () => {
    // The Gateway never sends an answer whose word and rows disagree; these two are made to, so that which of the
    // two the page goes by can be seen.
    const saidEmpty = { ...copyOf(APPROVERS), isEmpty: true, emptyText: APPROVERS_EMPTY.emptyText };
    answers[READ_APPROVERS] = () => json(200, saidEmpty);
    const first = render(<LicensesView />);
    expect((await screen.findByTestId("lic-approvers-empty")).textContent).toBe(APPROVERS_EMPTY.emptyText);
    expect(screen.queryByTestId("lic-approver")).toBeNull();
    first.unmount();

    const saidNotEmpty = { ...copyOf(APPROVERS_EMPTY), isEmpty: false, emptyText: null };
    answers[READ_APPROVERS] = () => json(200, saidNotEmpty);
    render(<LicensesView />);
    expect(await screen.findByTestId("lic-approvers")).not.toBeNull();
    expect(screen.queryByTestId("lic-approvers-empty")).toBeNull();
    expect(screen.queryByTestId("lic-approver")).toBeNull();
  });

  it("lists each approver with the Gateway's label, status in its tone, and sentence", async () => {
    render(<LicensesView />);

    const rows = await screen.findAllByTestId("lic-approver");
    expect(rows.length).toBe(2);
    const cells = (row: HTMLElement) => [...row.querySelectorAll("td")].slice(0, 3).map((cell) => cell.textContent);
    expect(cells(rows[0])).toEqual([ACCOUNT_A, "the-mac", "work"]);
    expect(within(rows[0]).getByTestId("lic-approver-status").textContent).toBe("Ready");
    expect(within(rows[0]).getByTestId("lic-approver-status").className).toContain("lic-tone-good");
    expect(within(rows[0]).getByTestId("lic-approver-text").textContent).toBe(APPROVERS.approvers[0].text);
    expect(cells(rows[1])).toEqual([ACCOUNT_B, "the-laptop", "personal"]);
    expect(within(rows[1]).getByTestId("lic-approver-status").textContent).toBe("Not reporting yet");
    expect(within(rows[1]).getByTestId("lic-approver-status").className).toContain("lic-tone-warning");
    expect(within(rows[1]).getByTestId("lic-approver-text").textContent).toBe(APPROVERS.approvers[1].text);
    expect(screen.queryByTestId("lic-approvers-empty")).toBeNull();
    expect(screen.queryByTestId("lic-approver-answer")).toBeNull();
  });

  it("calls an approving machine by the Gateway's label, not by its machine name", async () => {
    // Two users of one computer are two machines; the Gateway tells them apart in the label.
    const labelled = copyOf(APPROVERS);
    labelled.approvers[0].label = "the-mac (owner)";
    answers[READ_APPROVERS] = () => json(200, labelled);
    render(<LicensesView />);

    const rows = await screen.findAllByTestId("lic-approver");
    expect(rows[0].querySelectorAll("td")[1].textContent).toBe("the-mac (owner)");
  });

  it("sets an approver with one application/json PUT of the four fields, shows the Gateway's sentence, and reads the approvers again", async () => {
    answers[READ_APPROVERS] = () => json(200, APPROVERS_EMPTY);
    answers[SET_APPROVER] = () => json(200, APPROVER_SET);
    render(<LicensesView />);
    fireEvent.click(await screen.findByRole("button", { name: "Set an approver" }));
    // Nothing can be saved before all four are given.
    expect(saveButton().disabled).toBe(true);
    type(` ${ACCOUNT_A} `, "the-mac", "owner", "");
    expect(saveButton().disabled).toBe(true);
    type(` ${ACCOUNT_A} `, " the-mac", "owner ", " work ");
    expect(saveButton().disabled).toBe(false);
    expect(writes()).toEqual([]);
    // From here the Gateway lists the approver it stored.
    answers[READ_APPROVERS] = () => json(200, APPROVERS);
    const readsBefore = reads(READ_APPROVERS);

    fireEvent.click(saveButton());

    expect((await screen.findByTestId("lic-approver-answer")).textContent).toBe(APPROVER_SET.text);
    expect(writes().length).toBe(1);
    expect(writes()[0].call).toBe(SET_APPROVER);
    expect(writes()[0].contentType).toBe("application/json");
    // Each field as typed, less the space around it, and no other field.
    expect(writes()[0].text).toBe(JSON.stringify({ accountEmail: ACCOUNT_A, machineName: "the-mac", osUser: "owner", browserProfile: "work" }));
    expect(screen.queryByTestId("lic-approver-editor")).toBeNull();
    // The list on screen is the Gateway's next answer, not one the page made up from what was typed.
    await waitFor(() => expect(screen.getAllByTestId("lic-approver").length).toBe(2));
    expect(reads(READ_APPROVERS)).toBe(readsBefore + 1);
    expect(screen.queryByTestId("lic-approvers-empty")).toBeNull();
  });

  it("keeps the list as the Gateway last gave it when the read after a set has not answered: it adds no row of its own", async () => {
    answers[READ_APPROVERS] = () => json(200, APPROVERS_EMPTY);
    answers[SET_APPROVER] = () => json(200, APPROVER_SET);
    render(<LicensesView />);
    fireEvent.click(await screen.findByRole("button", { name: "Set an approver" }));
    type(ACCOUNT_A, "the-mac", "owner", "work");
    answers[READ_APPROVERS] = () => new Promise<Response>(() => undefined);

    fireEvent.click(saveButton());

    expect((await screen.findByTestId("lic-approver-answer")).textContent).toBe(APPROVER_SET.text);
    expect(screen.queryByTestId("lic-approver")).toBeNull();
    expect(screen.getByTestId("lic-approvers-empty").textContent).toBe(APPROVERS_EMPTY.emptyText);
  });

  it.each([
    ["400 invalid_browser_profile", 400, APPROVER_REFUSED],
    ["403 owner_only", 403, SET_OWNER_ONLY],
  ])("shows a %s in the Gateway's sentence, word for word, and keeps the editor open with what was typed", async (_name, status, refusal) => {
    answers[SET_APPROVER] = () => json(status, refusal);
    render(<LicensesView />);
    fireEvent.click(await screen.findByRole("button", { name: "Set an approver" }));
    type(ACCOUNT_C, "the-mac", "owner", "a/b");

    fireEvent.click(saveButton());

    expect((await within(editor()).findByTestId("lic-approver-error")).textContent).toBe(refusal.error);
    expect(field("Browser profile on that machine").value).toBe("a/b");
    expect(saveButton().disabled).toBe(false);
    expect(screen.queryByTestId("lic-approver-answer")).toBeNull();
    expect(writes().length).toBe(1);
  });

  it("takes the sentence of the answer before off the screen when the next write is refused: it is not that write's outcome", async () => {
    answers[SET_APPROVER] = () => json(200, APPROVER_SET);
    render(<LicensesView />);
    fireEvent.click(await screen.findByRole("button", { name: "Set an approver" }));
    type(ACCOUNT_A, "the-mac", "owner", "work");
    fireEvent.click(saveButton());
    expect((await screen.findByTestId("lic-approver-answer")).textContent).toBe(APPROVER_SET.text);

    answers[REMOVE_APPROVER] = () => json(403, REMOVE_OWNER_ONLY);
    const dialog = await openRemove();
    fireEvent.click(within(dialog).getByRole("button", { name: "Remove" }));

    await waitFor(() => expect(refusalIn(dialog)).toBe(REMOVE_OWNER_ONLY.error));
    expect(screen.queryByTestId("lic-approver-answer")).toBeNull();
  });

  it("changes an approver from its own row: the editor opens on what is stored and keeps its account", async () => {
    answers[SET_APPROVER] = () => json(200, APPROVER_SET);
    render(<LicensesView />);
    const rows = await screen.findAllByTestId("lic-approver");

    fireEvent.click(within(rows[1]).getByRole("button", { name: "Change" }));

    expect(field("Claude account").value).toBe(ACCOUNT_B);
    expect(field("Claude account").disabled).toBe(true);
    expect(field("Approving machine").value).toBe("the-laptop");
    expect(field("Operating system user on that machine").value).toBe("owner");
    expect(field("Browser profile on that machine").value).toBe("personal");
    fireEvent.change(field("Browser profile on that machine"), { target: { value: "work" } });
    fireEvent.click(saveButton());

    await screen.findByTestId("lic-approver-answer");
    expect(writes().length).toBe(1);
    expect(writes()[0].text).toBe(JSON.stringify({ accountEmail: ACCOUNT_B, machineName: "the-laptop", osUser: "owner", browserProfile: "work" }));
  });

  it("sends nothing when the editor is cancelled", async () => {
    render(<LicensesView />);
    fireEvent.click(await screen.findByRole("button", { name: "Set an approver" }));
    type(ACCOUNT_C, "the-mac", "owner", "work");

    fireEvent.click(within(editor()).getByRole("button", { name: "Cancel" }));

    expect(screen.queryByTestId("lic-approver-editor")).toBeNull();
    expect(writes()).toEqual([]);
  });

  /** Open the removal confirmation of account B's approver and return it. */
  async function openRemove(): Promise<HTMLElement> {
    const rows = await screen.findAllByTestId("lic-approver");
    fireEvent.click(within(rows[1]).getByRole("button", { name: "Remove" }));
    return screen.getByRole("alertdialog", { name: "Remove the approver of account-b@example.com?" });
  }

  it("removes an approver only when confirmed: one DELETE naming the account in the query, no body, then the Gateway's sentence and a new read", async () => {
    answers[REMOVE_APPROVER] = () => json(200, APPROVER_REMOVED);
    render(<LicensesView />);
    const dialog = await openRemove();
    // The confirmation says what is being removed in the Gateway's sentence for that approver.
    expect(dialog.querySelector(".ui-confirm-message")?.textContent).toBe(APPROVERS.approvers[1].text);
    expect(writes()).toEqual([]);
    // From here the Gateway lists what is left.
    const left = copyOf(APPROVERS);
    left.approvers = [left.approvers[0]];
    answers[READ_APPROVERS] = () => json(200, left);
    const readsBefore = reads(READ_APPROVERS);

    fireEvent.click(within(dialog).getByRole("button", { name: "Remove" }));

    expect((await screen.findByTestId("lic-approver-answer")).textContent).toBe(APPROVER_REMOVED.text);
    expect(writes().length).toBe(1);
    expect(writes()[0].call).toBe(REMOVE_APPROVER);
    expect(writes()[0].url).toBe("/gateway/licenses/approvers?accountEmail=account-b%40example.com");
    expect(writes()[0].text).toBeNull();
    expect(writes()[0].contentType).toBeNull();
    expect(screen.queryByRole("alertdialog")).toBeNull();
    await waitFor(() => expect(screen.getAllByTestId("lic-approver").length).toBe(1));
    expect(reads(READ_APPROVERS)).toBe(readsBefore + 1);
  });

  it("shows a refused removal in the Gateway's sentence, word for word, and removes nothing from the screen", async () => {
    answers[REMOVE_APPROVER] = () => json(403, REMOVE_OWNER_ONLY);
    render(<LicensesView />);
    const dialog = await openRemove();

    fireEvent.click(within(dialog).getByRole("button", { name: "Remove" }));

    await waitFor(() => expect(refusalIn(dialog)).not.toBeNull());
    expect(refusalIn(dialog)).toBe(REMOVE_OWNER_ONLY.error);
    expect(screen.getAllByTestId("lic-approver").length).toBe(2);
    expect(screen.queryByTestId("lic-approver-answer")).toBeNull();
  });

  it("says so when the approvers cannot be read, and still draws the machines", async () => {
    answers[READ_APPROVERS] = () => json(403, { code: "no_account", error: "no account is bound to this request" });
    render(<LicensesView />);

    expect(await screen.findByText("no account is bound to this request")).toBeTruthy();
    expect(screen.getAllByTestId("lic-sign-in-machine").length).toBe(2);
    expect(screen.queryByTestId("lic-approvers")).toBeNull();
    expect(screen.queryByTestId("lic-approvers-empty")).toBeNull();
  });
});
